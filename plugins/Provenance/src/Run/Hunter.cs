using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using Provenance.Combat;
using Provenance.Data;
using Provenance.Integrations;

namespace Provenance.Run;

/// <summary>A monster drop to farm: how many, and where monsters that drop it spawn.</summary>
internal sealed record HuntOrder(uint ItemId, int Amount, IReadOnlyList<MobSpot> Spots);

/// <summary>
/// Farms monster drops with the chosen combat job: its best gearset, the chocobo if wanted, travel to
/// a spawn area, then one monster at a time — never one someone else is fighting, but always whatever
/// attacks us — with the job's basic combo, a heal or defensive action on low health, until the bags
/// hold enough. Knock-outs are handled by the run (it raises and calls this again). Framework thread.
/// </summary>
internal sealed unsafe class Hunter
{
    private enum Step { Idle, Gear, Travel, Search, Approach, Fight, Loot, Done, Failed }

    private static readonly TimeSpan SearchFor = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan FightFor = TimeSpan.FromSeconds(120);

    private readonly Plugin plugin;
    private readonly Action<string, bool> log;
    private readonly Travel travel = new();
    private readonly Queue<HuntOrder> orders = new();
    private readonly HashSet<ulong> givenUp = new();
    private Dictionary<uint, int> startCounts = new();
    private Step step = Step.Idle;
    private uint classJob;
    private BasicCombo.Kit? kit;
    private int spotIndex;
    private ulong targetId;
    private DateTime stepSince;
    private DateTime waitUntil;
    private DateTime nextMove;
    private bool stanceSet;
    private bool pauseRequested;
    private int kills;

    public Hunter(Plugin plugin, Action<string, bool> log)
    {
        this.plugin = plugin;
        this.log = log;
    }

    public string Status { get; private set; } = "";
    public string? Failure { get; private set; }
    public bool Finished => step is Step.Done or Step.Failed;
    public bool IsPaused { get; private set; }

    public void Begin(IEnumerable<HuntOrder> hunts, uint combatJob)
    {
        orders.Clear();
        foreach (var h in hunts.Where(h => h.Amount > 0 && h.Spots.Count > 0)) orders.Enqueue(h);
        classJob = combatJob;
        kit = BasicCombo.For(combatJob);
        Failure = null;
        IsPaused = false;
        pauseRequested = false;
        givenUp.Clear();
        kills = 0;
        startCounts = orders.Select(o => o.ItemId).Distinct().ToDictionary(id => id, GameState.BagCount);
        if (orders.Count == 0) { step = Step.Done; return; }
        if (kit == null) { Fail("Provenance has no combo for that job."); return; }
        spotIndex = 0;
        Go(Step.Gear);
    }

    public void RequestPause() => pauseRequested = true;

    public void Resume()
    {
        pauseRequested = false;
        if (!IsPaused) return;
        IsPaused = false;
        Go(Step.Gear);
    }

    public void Cancel()
    {
        travel.Cancel();
        Vnav.Stop();
        if (!Finished) step = Step.Idle;
    }

    private HuntOrder Current => orders.Peek();
    private MobSpot Spot => Current.Spots[spotIndex % Current.Spots.Count];
    private int Got(uint item) => GameState.BagCount(item) - startCounts.GetValueOrDefault(item);
    private string Name(uint item) => plugin.Plans.Catalog?.ItemName(item) ?? "item " + item;

    public void Tick()
    {
        if (Finished || IsPaused || step == Step.Idle || DateTime.UtcNow < waitUntil) return;
        var me = Services.Objects.LocalPlayer;
        if (me == null || me.IsDead || Services.Condition[ConditionFlag.BetweenAreas]) return;

        // Whatever attacks us comes first, wherever we are.
        if (step is Step.Travel or Step.Search && Attacker(me) is { } attacker)
        {
            travel.Cancel();
            Engage(attacker);
            return;
        }

        switch (step)
        {
            case Step.Gear: Gear(); return;
            case Step.Travel:
                travel.Tick();
                Status = travel.Status;
                if (travel.Current == Travel.State.Failed)
                {
                    log($"Couldn't get to {Spot.Name} in {Spot.PlaceName}: {travel.Failure}", true);
                    NextSpot(me);
                    return;
                }
                if (travel.Current == Travel.State.Arrived) Go(Step.Search);
                return;
            case Step.Search: Search(me); return;
            case Step.Approach: Approach(me); return;
            case Step.Fight: Fight(me); return;
            case Step.Loot:
                // Solo kills drop straight into the bags; give the game a moment.
                if (DateTime.UtcNow - stepSince < TimeSpan.FromSeconds(1.5)) return;
                AfterKill();
                return;
        }
    }

    private void Gear()
    {
        Status = "Getting ready to fight";
        if (GameState.CurrentJob != classJob)
        {
            var set = GameState.BestGearset(classJob);
            if (set < 0) { Fail($"No gearset saved for {Core.Character.ClassJobs.Abbreviation(classJob)}."); return; }
            if (DateTime.UtcNow - stepSince > TimeSpan.FromSeconds(8)) { Fail("Couldn't change into the combat gearset."); return; }
            if (!Services.Condition[ConditionFlag.InCombat]) GameState.EquipGearset(set);
            Wait(1.5);
            return;
        }
        if (Chocobos()) return;
        StartTravel();
    }

    /// <summary>Summons the chocobo and sets its stance. True while that is still going on.</summary>
    private bool Chocobos()
    {
        var c = plugin.Config.Combat;
        if (!c.Chocobo) return false;
        if (!Chocobo.Out)
        {
            stanceSet = false;
            if (!Chocobo.CanSummon) return false;
            Status = "Calling your chocobo";
            Chocobo.Summon();
            Wait(2.5);
            return true;
        }
        if (!stanceSet)
        {
            stanceSet = Chocobo.SetStance(c.ChocoboStance);
            Wait(1);
            return true;
        }
        return false;
    }

    private void StartTravel()
    {
        var spot = Spot;
        travel.GoTo($"{spot.Name} in {spot.PlaceName}", spot.TerritoryId, spot.Position, Math.Clamp(spot.Radius * 0.5f, 10f, 40f), null);
        Go(Step.Travel);
    }

    private void Search(IGameObject me)
    {
        var spot = Spot;
        Status = $"Looking for {spot.Name} ({Got(Current.ItemId)}/{Current.Amount} {Name(Current.ItemId)})";
        if (Services.ClientState.TerritoryType != spot.TerritoryId) { StartTravel(); return; }
        if (Chocobos()) return;
        var mob = Services.Objects.OfType<IBattleNpc>()
            .Where(n => n.NameId == spot.NameId && Fightable(n, me) && !n.StatusFlags.HasFlag(StatusFlags.InCombat))
            .Where(n => Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), spot.Position) <= spot.Radius + 60)
            .OrderBy(n => Vector3.Distance(n.Position, me.Position))
            .FirstOrDefault();
        if (mob != null) { Engage(mob); return; }
        if (DateTime.UtcNow - stepSince > SearchFor) NextSpot(me);
    }

    private void Engage(IBattleNpc mob)
    {
        targetId = mob.GameObjectId;
        Services.Targets.Target = mob;
        Go(Step.Approach);
    }

    private float Range(IGameObject mob) => (kit!.Melee ? 2.6f : 20f) + mob.HitboxRadius;

    private void Approach(IGameObject me)
    {
        var mob = Target();
        if (mob == null) { Vnav.Stop(); Go(Step.Search); return; }
        Status = $"Going for {mob.Name}";
        var distance = Vector3.Distance(me.Position, mob.Position);
        if (distance <= Range(mob))
        {
            Vnav.Stop();
            if (Services.Condition[ConditionFlag.Mounted]) { ActionManager.Instance()->UseAction(ActionType.Mount, 0); Wait(1); return; }
            Go(Step.Fight);
            return;
        }
        if (DateTime.UtcNow - stepSince > TimeSpan.FromSeconds(45)) { GiveUp(mob, "couldn't reach it"); return; }
        if (DateTime.UtcNow >= nextMove)
        {
            Vnav.MoveCloseTo(mob.Position, false, Math.Max(1f, Range(mob) - 1f));
            nextMove = DateTime.UtcNow.AddSeconds(1);
        }
    }

    private void Fight(IGameObject me)
    {
        var mob = Target();
        if (mob == null || mob.IsDead || mob.CurrentHp == 0)
        {
            kills++;
            Go(Step.Loot);
            return;
        }
        Status = $"Fighting {mob.Name} ({mob.CurrentHp * 100 / Math.Max(1, mob.MaxHp)}%)";
        if (DateTime.UtcNow - stepSince > FightFor) { GiveUp(mob, "the fight took too long"); return; }
        if (Vector3.Distance(me.Position, mob.Position) > Range(mob) + 2) { Go(Step.Approach); return; }
        if (Services.Targets.Target?.GameObjectId != mob.GameObjectId) Services.Targets.Target = mob;
        if (Services.Condition[ConditionFlag.Casting]) return;

        // Low on health: the job's heal or defensive action first.
        var c = plugin.Config.Combat;
        if (me is IBattleChara self && self.MaxHp > 0 && self.CurrentHp * 100 / self.MaxHp < c.HealBelow && kit!.Heal != 0
            && BasicCombo.Learned(kit.Heal, GameState.JobLevel(GameState.CurrentJob)))
        {
            var healTarget = me.GameObjectId;
            if (BasicCombo.Ready(kit.Heal, healTarget) is var heal && heal != 0 && BasicCombo.Use(heal, healTarget)) { Wait(0.3); return; }
        }
        var next = BasicCombo.Next(kit!, mob.GameObjectId);
        // Out of sight (or range) from here, which the game's action status doesn't tell: closer, to melee distance.
        if (next != 0 && BasicCombo.OutOfPlace(BasicCombo.Placement(next, me, mob)))
        {
            if (DateTime.UtcNow >= nextMove)
            {
                Vnav.MoveCloseTo(mob.Position, false, Math.Max(1f, 2f + mob.HitboxRadius - 0.5f));
                nextMove = DateTime.UtcNow.AddSeconds(1);
            }
            Wait(0.5);
            return;
        }
        if (next != 0 && BasicCombo.Use(next, mob.GameObjectId)) Wait(0.3);
        else Wait(0.1);
    }

    private void AfterKill()
    {
        var order = Current;
        if (Got(order.ItemId) >= order.Amount)
        {
            log($"Got {Got(order.ItemId)} {Name(order.ItemId)} from {kills} {(kills == 1 ? "fight" : "fights")}.", false);
            orders.Dequeue();
            kills = 0;
            spotIndex = 0;
            if (orders.Count == 0) { step = Step.Done; Status = "All monster drops in"; return; }
            StartTravel();
            return;
        }
        if (pauseRequested) { IsPaused = true; pauseRequested = false; Status = "Paused"; return; }
        Go(Step.Search);
    }

    private void NextSpot(IGameObject me)
    {
        spotIndex++;
        if (spotIndex >= Current.Spots.Count * 3)
        {
            Fail($"Couldn't find {Current.Spots[0].Name} for {Name(Current.ItemId)}; get the rest yourself.");
            return;
        }
        StartTravel();
    }

    private void GiveUp(IGameObject mob, string why)
    {
        givenUp.Add(mob.GameObjectId);
        Vnav.Stop();
        log($"Left {mob.Name} alone: {why}.", true);
        Go(Step.Search);
    }

    /// <summary>An enemy fighting us or our chocobo, nearest first.</summary>
    private IBattleNpc? Attacker(IGameObject me)
    {
        var buddy = Services.Buddies.CompanionBuddy?.GameObject?.GameObjectId ?? 0;
        return Services.Objects.OfType<IBattleNpc>()
            .Where(n => Fightable(n, me) && n.StatusFlags.HasFlag(StatusFlags.InCombat) && (n.TargetObjectId == me.GameObjectId || (buddy != 0 && n.TargetObjectId == buddy)))
            .OrderBy(n => Vector3.Distance(n.Position, me.Position))
            .FirstOrDefault();
    }

    private bool Fightable(IBattleNpc n, IGameObject me)
        => n.BattleNpcKind == BattleNpcSubKind.Combatant && n.StatusFlags.HasFlag(StatusFlags.Hostile) && !n.IsDead && n.CurrentHp > 0 && n.IsTargetable && !givenUp.Contains(n.GameObjectId)
           && Vector3.Distance(n.Position, me.Position) < 120;

    private IBattleNpc? Target() => Services.Objects.SearchById(targetId) as IBattleNpc;

    private void Go(Step next)
    {
        step = next;
        stepSince = DateTime.UtcNow;
        nextMove = default;
    }

    private void Fail(string message)
    {
        Failure = message;
        Status = message;
        step = Step.Failed;
        travel.Cancel();
        Vnav.Stop();
    }

    private void Wait(double seconds) => waitUntil = DateTime.UtcNow.AddSeconds(seconds);
}
