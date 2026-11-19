using System;
using System.Collections.Generic;

namespace Provenance.Core.Fishing;

/// <summary>The lure a cast needs: lure fish only come to the hook after Ambitious or Modest Lure.</summary>
public enum LureKind { None, Ambitious, Modest }

/// <summary>Why a fish is hooked.</summary>
public enum CatchRole
{
    /// <summary>The fish the order wants.</summary>
    Target,
    /// <summary>A link of a mooch chain: hooked to be mooched.</summary>
    Chain,
    /// <summary>Caught for Fisher's Intuition.</summary>
    Predator,
    /// <summary>The Surface Slap fish: hooked so it can be slapped away from the next cast.</summary>
    Slap,
}

/// <summary>
/// What a cast went out with: tackle (<see cref="MoochFish"/> 0) or a fish being mooched, and whether
/// Fisher's Intuition was up at the cast. The hook rules are looked up by this.
/// </summary>
public readonly record struct CastContext(uint MoochFish, bool Intuition)
{
    public bool Mooch => MoochFish != 0;
    public static CastContext Tackle(bool intuition) => new(0, intuition);
}

/// <summary>
/// A fish one cast context hooks. A bite whose tug matches (<see cref="Tug.Unknown"/> matches any tug)
/// inside the bite window is hooked with <see cref="HookSet"/>; every other bite gets Rest.
/// </summary>
/// <param name="Route">The route the fish belongs to: 0 the target's, 1 and up the predators' in plan order.</param>
/// <param name="From">Earliest bite in seconds after the cast (the data's floored minimum); null when unknown (any time).</param>
/// <param name="Until">Latest bite in seconds after the cast (the floored maximum + 1, as AutoHook does); null when unknown.</param>
/// <param name="OnlyOneWithTug">No other fish at the fishing hole pulls with this tug, so a bite with it is this fish at any time.</param>
/// <param name="Lure">The lure the fish needs on the cast.</param>
public sealed record HookRule(uint FishId, CatchRole Role, int Route, Tug Tug, HookSet HookSet, double? From, double? Until, bool OnlyOneWithTug, LureKind Lure);

/// <summary>
/// How one fish is caught (a sub-plan): cast with <see cref="Bait"/>, mooch along <see cref="Chain"/> (in our
/// data's order: the fish caught on the tackle first, the fish's direct predecessor last), hook the fish.
/// </summary>
/// <param name="Index">0 for the target, 1 and up for the predators in the plan's order.</param>
/// <param name="Bait">The tackle to cast with; 0 when unknown (the fallback bait is used).</param>
/// <param name="Count">Predators: catches Fisher's Intuition needs. The target: 0 (as many as the order wants).</param>
/// <param name="Snag">Snagging on for this route's casts (some fish on it need it).</param>
/// <param name="Lure">The lure the route's fish needs.</param>
public sealed record CatchRoute(int Index, FishInfo Fish, uint Bait, IReadOnlyList<FishInfo> Chain, int Count, bool Snag, LureKind Lure)
{
    public bool Mooches => Chain.Count > 0;

    /// <summary>The fish a cast with the route's tackle hooks: the chain's first fish, or the fish itself.</summary>
    public FishInfo First => Chain.Count > 0 ? Chain[0] : Fish;

    /// <summary>The fish a mooch with <paramref name="fish"/> hooks on this route; null when the fish isn't a link of the chain.</summary>
    public FishInfo? After(uint fish)
    {
        for (var i = 0; i < Chain.Count; i++)
            if (Chain[i].ItemId == fish) return i + 1 < Chain.Count ? Chain[i + 1] : Fish;
        return null;
    }

    /// <summary>The route's fish with a Precision or Powerful hookset (worth 50 GP for the hookset under Patience).</summary>
    public bool NeedsHookset
    {
        get
        {
            if (Fish.HookSet is HookSet.Precise or HookSet.Powerful) return true;
            foreach (var c in Chain)
                if (c.HookSet is HookSet.Precise or HookSet.Powerful) return true;
            return false;
        }
    }
}

/// <summary>
/// The buffs a plan uses (fish-solver §5.11 and §7.6, with §7.9's defects fixed). Every action named here
/// is one the character has learned; the decider still checks GP, cooldowns and the game's own rules.
/// </summary>
/// <param name="Patience">Patience II then Patience (the learned ones), for routes that mooch: large fish can be mooched.</param>
/// <param name="PrizeCatch">Prize Catch for mooch routes when Patience is out of reach (guarantees a large fish).</param>
/// <param name="MakeshiftBait">Makeshift Bait (5 Angler's Art) for mooch routes: average-sized fish can be mooched too.</param>
/// <param name="Chum">Chum before tackle casts when the GP the plan needs stays covered.</param>
/// <param name="SlapFish">The fish to Surface Slap after catching it (GatherBuddy Reborn's curated slap fish); 0 for none.</param>
/// <param name="IdenticalCastPredators">Identical Cast after the next-to-last catch of a predator caught on tackle.</param>
/// <param name="IdenticalCastTarget">Identical Cast after a target catch while more are wanted (straight, not a big fish).</param>
/// <param name="ThaliaksFavor">Thaliak's Favor (3 Angler's Art for 150 GP) when the GP isn't full.</param>
/// <param name="Cordials">Cordials between casts, up to <see cref="GpTarget"/>.</param>
/// <param name="GpTarget">GP the plan likes to have before a cast (its dearest buff and the GP kept for hooking); 0 when it spends none.</param>
/// <param name="MoochII">Mooch II for average-sized chain fish.</param>
public sealed record BuffPolicy(
    IReadOnlyList<uint> Patience,
    bool PrizeCatch,
    bool MakeshiftBait,
    bool Chum,
    uint SlapFish,
    bool IdenticalCastPredators,
    bool IdenticalCastTarget,
    bool ThaliaksFavor,
    bool Cordials,
    int GpTarget,
    bool MoochII)
{
    public static readonly BuffPolicy None = new(Array.Empty<uint>(), false, false, false, 0, false, false, false, false, 0, false);
}

/// <summary>Why a plan can't work; the runtime words it with item and action names.</summary>
public enum CatchProblemKind
{
    /// <summary>A spearfishing fish (not caught with a rod).</summary>
    Spear,
    /// <summary>Only caught on ocean fishing voyages.</summary>
    Ocean,
    /// <summary>No bait is known for <see cref="CatchProblem.Item"/>.</summary>
    NoBait,
    /// <summary><see cref="CatchProblem.Item"/> (a chain fish or a predator) isn't in the fish data.</summary>
    UnknownFish,
    /// <summary><see cref="CatchProblem.Action"/> isn't learned yet.</summary>
    MissingAction,
    /// <summary>The predator <see cref="CatchProblem.Item"/> itself needs Fisher's Intuition.</summary>
    NestedIntuition,
    /// <summary><see cref="CatchProblem.Item"/> doesn't live at this fishing hole.</summary>
    NotAtSpot,
}

/// <summary>Why a plan can't work, with the fish (and action) it is about.</summary>
public sealed record CatchProblem(CatchProblemKind Kind, uint Item = 0, uint Action = 0);

/// <summary>Whether a fish bites now: open, or how long until it opens; how long it stays open.</summary>
/// <param name="OpensIn">Zero when open; null when it doesn't open within the forecast horizon.</param>
/// <param name="Left">Time left while open (<see cref="TimeSpan.MaxValue"/> for fish without a window).</param>
public readonly record struct CatchWindowState(bool Open, TimeSpan? OpensIn, TimeSpan Left)
{
    public static readonly CatchWindowState Always = new(true, TimeSpan.Zero, TimeSpan.MaxValue);
}

/// <summary>A fish's time and weather window at a spot (its zone's weather table), through <see cref="FishWindow"/>.</summary>
public sealed record CatchWindow(FishInfo Fish, IReadOnlyList<(uint Weather, int Rate)> Table)
{
    /// <summary>How far ahead the forecast is searched for the next opening.</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromHours(12);

    public bool Restricted => Fish.Restricted;

    /// <summary>Open now, or how long until it opens (and how long it stays open).</summary>
    public CatchWindowState At(DateTimeOffset now)
    {
        if (!Fish.Restricted) return CatchWindowState.Always;
        return FishWindow.IsOpen(Fish, Table, now)
            ? new CatchWindowState(true, TimeSpan.Zero, FishWindow.Left(Fish, Table, now, Horizon))
            : new CatchWindowState(false, FishWindow.UntilOpen(Fish, Table, now, Horizon), TimeSpan.Zero);
    }
}

/// <summary>
/// Everything needed to catch one fish at one fishing hole: the target's route (tackle and mooch chain),
/// the predators for Fisher's Intuition with their counts and own routes, the hook rules per cast
/// context, the buffs, and the target's window. Built by <see cref="CatchPlanner"/>; read every tick by
/// <see cref="CatchDecider"/>. The intent of AutoHook's FishSolver and SolverPresetBuilder (BSD-3,
/// Puni.sh), rebuilt from our own fish data.
/// </summary>
public sealed record CatchPlan(
    FishInfo Target,
    CatchRoute TargetRoute,
    IReadOnlyList<CatchRoute> Predators,
    int IntuitionSeconds,
    IReadOnlyDictionary<CastContext, IReadOnlyList<HookRule>> Rules,
    BuffPolicy Buffs,
    CatchWindow Window,
    IReadOnlyDictionary<uint, CatchWindow> PredatorWindows,
    TimeSpan PrepLead,
    TimeSpan EarlyLimit,
    CatchProblem? Problem)
{
    /// <summary>Predators must be caught for Fisher's Intuition before the target bites.</summary>
    public bool NeedsIntuition => Predators.Count > 0;

    /// <summary>Every route: the target's first, then the predators'.</summary>
    public IEnumerable<CatchRoute> Routes
    {
        get
        {
            yield return TargetRoute;
            foreach (var p in Predators) yield return p;
        }
    }

    public CatchRoute Route(int index) => index == 0 ? TargetRoute : Predators[index - 1];

    /// <summary>The hook rules of a cast context, most important first; empty when it hooks nothing (every bite is Rested).</summary>
    public IReadOnlyList<HookRule> RulesFor(CastContext context)
        => Rules.TryGetValue(context, out var rules) ? rules : Array.Empty<HookRule>();
}
