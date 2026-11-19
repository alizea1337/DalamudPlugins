// Ported from GatherBuddy Reborn's AutoGather/Helpers/RotationSolver.cs (Apache-2.0,
// https://github.com/FFXIV-CombatReborn/GatherBuddyReborn). Changed: pure (no game access; the caller
// passes the node, the player and the learned actions in), plain C# instead of preview syntax, and
// the Diadem branch left out.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Provenance.Core.Gathering;

/// <summary>Gathering actions the solver and the node logic know about (job-neutral; the game side maps them to MIN/BTN ids).</summary>
public enum GatherAction
{
    Gather,
    TwelvesBounty,
    SolidAge,
    Yield1,
    Yield2,
    Gift1,
    Gift2,
    Tidings,
    Bountiful,
    GivingLand,
    Wise,
    Luck,
    Collect,
    Scour,
    Brazen,
    Meticulous,
    Scrutiny,
}

/// <summary>What the solver needs to know about the node slot, the player and the actions they have.</summary>
public sealed record GatherSolverInput
{
    public int PlayerLevel { get; init; }
    public int MaxGp { get; init; }
    public int Gp { get; init; }
    public int MaxIntegrity { get; init; }
    public int Integrity { get; init; }
    /// <summary>The slot's yield as the window shows it, before this node's buffs (see GBR: minus Bountiful, plus Giving Land, minus a bonus).</summary>
    public int Yield { get; init; }
    public int BoonChance { get; init; }
    /// <summary>Boon chance the item has on a node without bonuses (for "best nodes only").</summary>
    public int BaseBoonChance { get; init; }
    public int BoonYield { get; init; } = 1;
    public int BountifulYield { get; init; } = 1;
    public bool IsCrystal { get; init; }
    public bool IsRare { get; init; }
    /// <summary>Regular nodes optimise yield per GP; timed nodes spend everything on the best total.</summary>
    public bool RegularNode { get; init; }
    public bool SpendGpOnBestNodesOnly { get; init; } = true;
    public int GpRegenPerHit { get; init; } = 5;
    public int GpRegenPerTick { get; init; } = 5;
    /// <summary>Seconds until The Giving Land can be used again (0 = ready).</summary>
    public float GivingLandCooldown { get; init; }
    /// <summary>Actions the character has learned (level and quest), with their GP cost.</summary>
    public IReadOnlyDictionary<GatherAction, int> Learned { get; init; } = new Dictionary<GatherAction, int>();
    /// <summary>Buffs already on the player (Bountiful, King's Yield, …), by the action that gives them.</summary>
    public IReadOnlySet<GatherAction> Active { get; init; } = new HashSet<GatherAction>();
}

/// <summary>
/// Plans the actions for one node: on timed nodes the sequence with the best total yield; on regular
/// nodes the best yield per GP (only spending GP when this node is about as good as the best a node
/// can be), with Bountiful or Twelve's Bounty as the filler. Searches every sequence; nodes are small.
/// </summary>
public static class GatherSolver
{
    private const uint MaxYieldDiffPercent = 10;
    private const int GivingLandYield = 20;
    private const int KeepGpBelowMaxMinus = 50;

    [Flags]
    private enum Fx : ushort
    {
        None = 0, TwelvesBounty = 1, Bountiful = 2, Yield1 = 4, Yield2 = 8, GivingLand = 16, Gift1 = 32, Gift2 = 64, Tidings = 128,
    }

    private sealed class Global
    {
        public int Level;
        public uint MaxGp;
        public uint InitialGp;
        public int MaxIntegrity;
        public int BountifulYield;
        public int BountyYield;
        public bool OptimizeForCost;
        public uint GpRegenPerHit;
        public List<Step> Available = new();
        public Stack<GatherAction> Used = new();
        public uint BestYield;
        public uint BaselineYield;
        public int BestGp;
        public List<GatherAction> Best = new();

        public Global Copy(Action<Global>? change = null)
        {
            var g = (Global)MemberwiseClone();
            g.Used = new Stack<GatherAction>();
            g.Best = new List<GatherAction>();
            g.BestYield = 0;
            g.BestGp = 0;
            change?.Invoke(g);
            return g;
        }
    }

    private readonly record struct State(Global G, uint TotalYield, int Gp, int Integrity, int BoonChance, int Yield, int BoonYield, Fx Effects, int Restored);

    private sealed class Step(GatherAction action, int cost, Func<State, bool> check, Func<State, State> execute, Fx effect, bool singleUse)
    {
        public GatherAction Action => action;
        public int Cost => cost;
        public Fx Effect => effect;

        public bool Check(State s)
            => cost <= s.Gp
               && (effect & s.Effects) == 0
               // Only on the first step, or when the GP weren't there on the first step.
               && (singleUse || s.TotalYield == 0 || s.Gp - s.Restored < cost)
               && check(s);

        public State Execute(State s) => execute(s with { Gp = s.Gp - cost, Effects = s.Effects | effect });
    }

    /// <summary>The planned sequence; <see cref="GatherAction.Gather"/> entries are gathers. Empty for rare items (just gather).</summary>
    public static async Task<IReadOnlyList<GatherAction>> SolveAsync(GatherSolverInput input)
    {
        if (input.IsRare) return Array.Empty<GatherAction>();
        var g = new Global
        {
            Level = input.PlayerLevel,
            MaxGp = (uint)input.MaxGp,
            InitialGp = (uint)input.Gp,
            MaxIntegrity = input.MaxIntegrity,
            BountifulYield = input.BountifulYield,
            BountyYield = input.PlayerLevel >= 71 ? 3 : 2,
            OptimizeForCost = input.RegularNode,
            GpRegenPerHit = (uint)input.GpRegenPerHit,
        };
        g.Available = Steps(input, g).ToList();
        var state = new State(g, 0, input.Gp, input.Integrity, Math.Max(0, input.BoonChance), input.Yield, input.BoonYield, ActiveEffects(input.Active), 0);

        if (!input.RegularNode) await Task.Run(() => Search(state));
        else await SolveRegular(state, input);
        return g.Best.AsEnumerable().Reverse().ToList();
    }

    private static Fx ActiveEffects(IReadOnlySet<GatherAction> active)
    {
        var fx = Fx.None;
        foreach (var a in active)
            fx |= a switch
            {
                GatherAction.TwelvesBounty => Fx.TwelvesBounty, GatherAction.Bountiful => Fx.Bountiful, GatherAction.Yield1 => Fx.Yield1,
                GatherAction.Yield2 => Fx.Yield2, GatherAction.GivingLand => Fx.GivingLand, GatherAction.Gift1 => Fx.Gift1,
                GatherAction.Gift2 => Fx.Gift2, GatherAction.Tidings => Fx.Tidings, _ => Fx.None,
            };
        return fx;
    }

    private static IEnumerable<Step> Steps(GatherSolverInput input, Global g)
    {
        bool Has(GatherAction a) => input.Learned.ContainsKey(a);
        int Cost(GatherAction a) => input.Learned.GetValueOrDefault(a);
        var boon = input.BoonChance > 0;
        if (Has(GatherAction.TwelvesBounty) && input.IsCrystal)
            yield return new Step(GatherAction.TwelvesBounty, Cost(GatherAction.TwelvesBounty), _ => true, s => s with { Yield = s.Yield + s.G.BountyYield }, Fx.TwelvesBounty, false);
        if (Has(GatherAction.SolidAge))
            yield return new Step(GatherAction.SolidAge, Cost(GatherAction.SolidAge), s => s.Integrity < s.G.MaxIntegrity - 1, SolidAge, Fx.None, true);
        if (Has(GatherAction.Yield1))
            yield return new Step(GatherAction.Yield1, Cost(GatherAction.Yield1), s => (s.Effects & Fx.Yield2) == 0, s => s with { Yield = s.Yield + 1 }, Fx.Yield1, false);
        if (Has(GatherAction.Yield2))
            yield return new Step(GatherAction.Yield2, Cost(GatherAction.Yield2), s => (s.Effects & Fx.Yield1) == 0, s => s with { Yield = s.Yield + 2 }, Fx.Yield2, false);
        if (Has(GatherAction.Gift1) && boon)
            yield return new Step(GatherAction.Gift1, Cost(GatherAction.Gift1), _ => true, s => s with { BoonChance = Math.Min(s.BoonChance + 10, 100) }, Fx.Gift1, false);
        if (Has(GatherAction.Gift2) && boon)
            yield return new Step(GatherAction.Gift2, Cost(GatherAction.Gift2), _ => true, s => s with { BoonChance = Math.Min(s.BoonChance + 30, 100) }, Fx.Gift2, false);
        if (Has(GatherAction.Tidings) && boon)
            yield return new Step(GatherAction.Tidings, Cost(GatherAction.Tidings), _ => true, s => s with { BoonYield = 2 }, Fx.Tidings, false);
        if (Has(GatherAction.Bountiful))
            yield return new Step(GatherAction.Bountiful, Cost(GatherAction.Bountiful), _ => true, s => s, Fx.Bountiful, true);
        if (Has(GatherAction.GivingLand) && input.IsCrystal && input.GivingLandCooldown <= 0)
            yield return new Step(GatherAction.GivingLand, Cost(GatherAction.GivingLand), _ => true, s => s with { Yield = s.Yield + GivingLandYield }, Fx.GivingLand, false);
        yield return new Step(GatherAction.Gather, 0, _ => true, Gather, Fx.None, true);
    }

    private static State SolidAge(State s)
    {
        s = s with { Integrity = s.Integrity + 1 };
        if (s.G.Level >= 90) s = s with { TotalYield = s.TotalYield + (uint)(s.Yield * 1000 + s.BoonYield * s.BoonChance * 10) / 2 };
        return s;
    }

    private static State Gather(State s)
    {
        var regen = (int)Math.Min(s.G.GpRegenPerHit, s.G.MaxGp - (uint)Math.Min(s.Gp, (int)s.G.MaxGp));
        s = s with
        {
            TotalYield = s.TotalYield + (uint)(s.Yield * 1000 + s.BoonYield * s.BoonChance * 10),
            Integrity = s.Integrity - 1,
            Gp = (int)Math.Min((uint)s.Gp + s.G.GpRegenPerHit, s.G.MaxGp),
            Restored = s.Restored + regen,
        };
        if ((s.Effects & Fx.Bountiful) != 0)
            s = s with { TotalYield = s.TotalYield + (uint)s.G.BountifulYield * 1000, Effects = s.Effects ^ Fx.Bountiful };
        return s;
    }

    private static void Search(State s)
    {
        var g = s.G;
        if (s.Integrity <= 0)
        {
            if (g.OptimizeForCost)
            {
                var extra = s.TotalYield - Math.Min(s.TotalYield, g.BaselineYield);
                var used = (uint)Math.Max(0, (int)g.InitialGp + s.Restored - s.Gp);
                var perGp = used == 0 ? 0u : (uint)((long)extra * 100 / used);
                if (g.BestYield == 0 || perGp > g.BestYield || perGp == g.BestYield && used > g.BestGp)
                {
                    g.BestYield = perGp;
                    g.BestGp = (int)used;
                    g.Best.Clear();
                    g.Best.AddRange(g.Used);
                }
            }
            else if (s.TotalYield > g.BestYield || s.TotalYield == g.BestYield && s.Gp > g.BestGp)
            {
                g.BestYield = s.TotalYield;
                g.BestGp = s.Gp;
                g.Best.Clear();
                g.Best.AddRange(g.Used);
            }
            return;
        }
        foreach (var step in g.Available)
        {
            if (!step.Check(s)) continue;
            g.Used.Push(step.Action);
            Search(step.Execute(s));
            g.Used.Pop();
        }
    }

    private static async Task SolveRegular(State state, GatherSolverInput input)
    {
        var g = state.G;
        var givingLand = g.Available.FirstOrDefault(x => x.Action == GatherAction.GivingLand);
        if (givingLand?.Check(state) == true && state.Integrity >= 4)
        {
            // The Giving Land is the only action with a cooldown: use it whenever it's up.
            g.Best.Add(GatherAction.GivingLand);
            return;
        }

        var bountiful = g.Available.FirstOrDefault(x => x.Action == GatherAction.Bountiful);
        var bountifulYield = bountiful != null ? (uint)g.BountifulYield * 1000u : 0u;
        var bounty = g.Available.FirstOrDefault(x => x.Action == GatherAction.TwelvesBounty);
        var bountyYield = bounty != null && bounty.Cost > 0 ? (uint)g.BountyYield * 1000u * (uint)state.Integrity * 100 / (uint)bounty.Cost : 0u;
        var fillerYield = Math.Max(bountifulYield, bountyYield);
        var bestSimulated = 0u;

        // Bountiful +3 on a non-crystal can't be beaten; skip the search.
        if (bountifulYield < 3000 || bounty != null)
        {
            g.Available.RemoveAll(x => x.Action is GatherAction.Bountiful or GatherAction.GivingLand);
            g.BaselineYield = (uint)(state.Yield * 1000 + state.BoonChance * 10 * state.BoonYield) * (uint)state.Integrity;

            if (input.SpendGpOnBestNodesOnly)
            {
                // Solve the best possible regular nodes (+2 integrity, +3 yield, +100% boon) and only spend GP
                // here when this node comes within MaxYieldDiffPercent of the best of them.
                var startGp = (int)g.MaxGp;
                var delta = KeepGpBelowMaxMinus + (bounty?.Cost ?? bountiful?.Cost ?? 0);
                if (startGp > delta) startGp -= delta;
                var tasks = new List<State> { state };
                var boonBase = input.BaseBoonChance;
                tasks.Add(new State(g.Copy(c => { c.MaxIntegrity = 6; c.InitialGp = (uint)startGp; c.BaselineYield = (uint)(1000 + boonBase * 10) * 6; }), 0, startGp, 6, boonBase, 1, 1, Fx.None, 0));
                tasks.Add(new State(g.Copy(c => { c.MaxIntegrity = 4; c.InitialGp = (uint)startGp; c.BaselineYield = (uint)(4000 + boonBase * 10) * 4; }), 0, startGp, 4, boonBase, 4, 1, Fx.None, 0));
                if (state.BoonChance != 0)
                    tasks.Add(new State(g.Copy(c => { c.MaxIntegrity = 4; c.InitialGp = (uint)startGp; c.BaselineYield = (1000 + 100 * 10) * 4; }), 0, startGp, 4, 100, 1, 1, Fx.None, 0));
                await Task.WhenAll(tasks.Select(t => Task.Run(() => Search(t))));
                bestSimulated = tasks.Skip(1).Select(t => t.G.BestYield).DefaultIfEmpty(0u).Max();
            }
            else
            {
                await Task.Run(() => Search(state));
            }
        }

        if (g.Best.Count == 0 || g.BestYield <= fillerYield || g.BestYield < bestSimulated * (100 - MaxYieldDiffPercent) / 100)
        {
            // Filler: Bountiful (or Twelve's Bounty on crystals) when GP allow, keeping GP for The Giving
            // Land (crystals) or Tidings, and not letting GP overcap when a better node may come.
            var canGiving = input.IsCrystal && input.Learned.ContainsKey(GatherAction.GivingLand);
            var givingCost = input.Learned.GetValueOrDefault(GatherAction.GivingLand);
            var keepGp = canGiving ? givingCost - Math.Max((int)((input.GivingLandCooldown - 10 - state.Integrity * 2 - 2) * input.GpRegenPerTick / 3), 0) : 0;
            if (fillerYield <= 1000 && input.Learned.TryGetValue(GatherAction.Tidings, out var tidingsCost)) keepGp = Math.Max(keepGp, tidingsCost);
            var overcap = bestSimulated > fillerYield ? (int)g.MaxGp - KeepGpBelowMaxMinus : 0;
            keepGp = Math.Clamp(keepGp, 0, (int)g.MaxGp);
            overcap = Math.Clamp(overcap, 0, (int)g.MaxGp);
            var gather = new Step(GatherAction.Gather, 0, _ => true, Gather, Fx.None, true);
            g.Used.Clear();
            if (bounty != null && bounty.Check(state)
                && state.Gp - bounty.Cost + (int)g.GpRegenPerHit * state.Integrity >= keepGp
                && state.Gp + (int)g.GpRegenPerHit * state.Integrity > overcap)
            {
                g.Used.Push(bounty.Action);
                state = bounty.Execute(state);
            }
            while (state.Integrity > 0)
            {
                if (bountiful != null && bounty == null && bountiful.Check(state)
                    && state.Gp - bountiful.Cost + (int)g.GpRegenPerHit >= keepGp
                    && state.Gp + (int)g.GpRegenPerHit > overcap)
                {
                    g.Used.Push(bountiful.Action);
                    state = bountiful.Execute(state);
                }
                g.Used.Push(gather.Action);
                state = gather.Execute(state);
            }
            g.Best.Clear();
            g.Best.AddRange(g.Used);
        }
    }
}
