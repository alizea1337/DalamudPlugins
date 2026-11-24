using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Provenance.Combat;
using Provenance.Core.Tribes;
using Provenance.Integrations;

namespace Provenance.Run.Tribes;

/// <summary>
/// Fights one enemy for the quest stepper, the way <see cref="Hunter"/> farms monster drops: the chocobo out and in
/// its stance when the settings want it, up to the reach of the job's basic combo, the combo until the enemy is
/// down, the job's heal or defensive action on low health. It also uses the quest's key item on the enemy when told
/// to (one that was subdued takes no more damage and waits for it). Which enemy, and when the item, is the stepper's
/// to say (<see cref="EnemyRules"/>); this only fights the one it's given. Every wait is bounded: an enemy that can't
/// be reached, can't be hurt or takes too long is given up on, with the reason. Ticked on the framework thread.
/// </summary>
internal sealed unsafe class QuestFighter
{
    public enum Step { Idle, Ready, Approach, Fight, Item }

    public enum End
    {
        None,
        /// <summary>Its health ran out.</summary>
        Defeated,
        /// <summary>It's gone from view (a subdued one leaves once the item was used; a dead one is cleared away).</summary>
        Gone,
        /// <summary>Left alone: <see cref="Why"/> says why.</summary>
        GaveUp,
    }

    /// <summary>A fight with one enemy over this long is given up (the order run's monster farming gives its fights two minutes).</summary>
    public static readonly TimeSpan FightFor = TimeSpan.FromMinutes(3);
    /// <summary>Not within reach of it after this long: given up.</summary>
    private static readonly TimeSpan ReachFor = TimeSpan.FromSeconds(45);
    /// <summary>Nothing has hurt it for this long: the fight has stalled (the stepper tries the quest's item then, when it has one for it).</summary>
    private static readonly TimeSpan StalledAfter = TimeSpan.FromSeconds(10);
    /// <summary>Nothing has hurt it for this long: given up.</summary>
    private static readonly TimeSpan UnhurtFor = TimeSpan.FromSeconds(30);
    /// <summary>Subdued with no item coming for this long: given up.</summary>
    private static readonly TimeSpan SubduedFor = TimeSpan.FromSeconds(8);
    /// <summary>Times in a row the game may refuse the quest's item before the fight goes on without it.</summary>
    private const int ItemRefusals = 12;
    /// <summary>Tries at the chocobo (the summon, the stance) before the fight goes on without it.</summary>
    private const int ChocoboTries = 4;

    private readonly Plugin plugin;
    private BasicCombo.Kit? kit;
    private Step step = Step.Idle;
    private ulong targetId;
    private uint targetBase;
    private DateTime since;
    private DateTime stepSince;
    private DateTime waitUntil;
    private DateTime nextMove;
    private DateTime hurtAt;
    private DateTime subduedSince;
    private uint lowestHp;
    private bool stanceSet;
    private int chocoboTries;
    private KeyItemFact? item;
    private int refusals;
    private uint lastRefusal;
    private DateTime itemUntil;
    private bool itemCast;
    private string? note;
    /// <summary>A walk towards the enemy is under way (stopped once in reach, not every frame).</summary>
    private bool walking;

    public QuestFighter(Plugin plugin) => this.plugin = plugin;

    /// <summary>An enemy is being fought (or got ready for, or closed in on).</summary>
    public bool Active => step != Step.Idle;
    public Step Now => step;
    /// <summary>The enemy's game object id (the last one's once the fight is over).</summary>
    public ulong Target => targetId;
    public string Name { get; private set; } = "";
    /// <summary>How the last fight ended; <see cref="End.None"/> while one is under way.</summary>
    public End Outcome { get; private set; }
    /// <summary>Why the enemy was given up on, in plain words.</summary>
    public string Why { get; private set; } = "";
    public string Status { get; private set; } = "";
    /// <summary>Enemies defeated since the stepper started on its quest.</summary>
    public int Defeated { get; private set; }
    /// <summary>The quest's item is being used on the enemy (walking up to it, or its cast).</summary>
    public bool UsingItem => step == Step.Item;
    /// <summary>Fighting, and nothing has hurt the enemy for a while.</summary>
    public bool Stalled => step == Step.Fight && DateTime.UtcNow - hurtAt > StalledAfter;

    /// <summary>The job on now has a basic combo to fight with.</summary>
    public static bool CanFight => BasicCombo.For(GameState.CurrentJob) != null;

    /// <summary>Starts on an enemy (dropping the one before).</summary>
    public void Engage(StepEnemy enemy)
    {
        var now = DateTime.UtcNow;
        kit = BasicCombo.For(GameState.CurrentJob);
        targetId = enemy.Id;
        targetBase = enemy.BaseId;
        Name = enemy.Name;
        Outcome = End.None;
        Why = "";
        since = now;
        hurtAt = now;
        subduedSince = default;
        lowestHp = uint.MaxValue;
        item = null;
        refusals = 0;
        itemCast = false;
        itemUntil = default;
        chocoboTries = 0;
        waitUntil = default;
        note = null;
        if (kit == null)
        {
            Finish(End.GaveUp, "this class has no actions to fight with");
            return;
        }
        Go(Step.Ready);
    }

    /// <summary>The quest's item on the enemy now: the attacks stop, the character walks into the item's reach and uses it.</summary>
    public void UseItem(KeyItemFact keyItem)
    {
        if (step is not (Step.Ready or Step.Approach or Step.Fight)) return;
        item = keyItem;
        refusals = 0;
        itemCast = false;
        itemUntil = default;
        Go(Step.Item);
    }

    /// <summary>Ends the fight at once (the walk too); nothing is reported.</summary>
    public void Stop()
    {
        StopWalking();
        step = Step.Idle;
        Outcome = End.None;
        Status = "";
    }

    /// <summary>A line for the debug log the fight left since the last call (an item sent or refused), or null.</summary>
    public string? TakeNote()
    {
        var text = note;
        note = null;
        return text;
    }

    /// <summary>Forgets the count of defeated enemies (a new quest).</summary>
    public void Reset()
    {
        Stop();
        Defeated = 0;
        stanceSet = false;
        targetId = 0;
        Name = "";
    }

    public void Tick()
    {
        var now = DateTime.UtcNow;
        if (step == Step.Idle || now < waitUntil) return;
        var me = Services.Objects.LocalPlayer;
        if (me == null || me.IsDead || Services.Condition[ConditionFlag.BetweenAreas]) return;

        var mob = QuestSteps.FindEnemy(targetId);
        if (mob == null) { Finish(End.Gone, "it's gone"); return; }
        if (mob.IsDead || mob.CurrentHp == 0) { Finish(End.Defeated, ""); return; }
        if (now - since > FightFor) { Finish(End.GaveUp, "the fight took too long"); return; }
        if (mob.CurrentHp < lowestHp)
        {
            lowestHp = mob.CurrentHp;
            hurtAt = now;
        }

        switch (step)
        {
            case Step.Ready: Ready(now); return;
            case Step.Approach: Approach(me, mob, now); return;
            case Step.Fight: Fight(me, mob, now); return;
            case Step.Item: Item(me, mob, now); return;
        }
    }

    /// <summary>The chocobo out and in its stance, when the settings want it and nothing attacks yet.</summary>
    private void Ready(DateTime now)
    {
        var c = plugin.Config.Combat;
        if (!Services.Condition[ConditionFlag.InCombat] && chocoboTries < ChocoboTries)
        {
            var wait = Chocobo.Ready(c.Chocobo, c.ChocoboStance, ref stanceSet);
            if (wait > 0)
            {
                chocoboTries++;
                Status = "Calling your chocobo";
                Wait(wait);
                return;
            }
        }
        hurtAt = now;
        Go(Step.Approach);
    }

    private float Reach(IGameObject mob) => kit!.Reach + mob.HitboxRadius;

    private void Approach(IGameObject me, IBattleNpc mob, DateTime now)
    {
        Status = $"Going for {mob.Name}";
        if (Vector3.Distance(me.Position, mob.Position) <= Reach(mob))
        {
            StopWalking();
            // The stepper gets off the mount before a fight; should the character still ride, off first.
            if (Services.Condition[ConditionFlag.Mounted]) { QuestSteps.GetOff(); Wait(1); return; }
            hurtAt = now;
            Go(Step.Fight);
            return;
        }
        if (now - stepSince > ReachFor) { Finish(End.GaveUp, "couldn't reach it"); return; }
        if (now >= nextMove)
        {
            Vnav.MoveCloseTo(mob.Position, false, Math.Max(1f, Reach(mob) - 1f));
            walking = true;
            nextMove = now.AddSeconds(1);
        }
    }

    private void Fight(IGameObject me, IBattleNpc mob, DateTime now)
    {
        Status = $"Fighting {mob.Name} ({mob.CurrentHp * 100 / Math.Max(1, mob.MaxHp)}%)";
        // Subdued: it takes no more damage and waits for the quest's item (the stepper sends it). With none coming, on.
        if (QuestSteps.Subdued(mob))
        {
            if (subduedSince == default) subduedSince = now;
            Status = $"{mob.Name} is subdued";
            if (now - subduedSince > SubduedFor) Finish(End.GaveUp, "it's subdued, and the quest's item couldn't be used on it");
            return;
        }
        subduedSince = default;
        if (now - hurtAt > UnhurtFor) { Finish(End.GaveUp, "nothing hurt it"); return; }
        if (Vector3.Distance(me.Position, mob.Position) > Reach(mob) + 2) { Go(Step.Approach); return; }
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
        if (next != 0 && BasicCombo.Use(next, mob.GameObjectId)) Wait(0.3);
        else Wait(0.1);
    }

    /// <summary>The quest's item on the enemy: into its reach, stand still, use it, let its cast play out, then back to the fight.</summary>
    private void Item(IGameObject me, IBattleNpc mob, DateTime now)
    {
        var it = item!;
        Status = $"Using the {it.Name} on {mob.Name}";
        if (now < itemUntil) return;
        if (itemCast)
        {
            // The cast is over: back to the fight (the stepper sends the item again if it's still wanted).
            itemCast = false;
            hurtAt = now;
            subduedSince = default;
            Go(Step.Fight);
            return;
        }
        var reach = Math.Max(1.5f, it.Range - 0.5f) + mob.HitboxRadius;
        if (Vector3.Distance(me.Position, mob.Position) > reach)
        {
            if (now - stepSince > ReachFor) { Finish(End.GaveUp, "couldn't get close enough to use the quest's item on it"); return; }
            if (now >= nextMove)
            {
                Vnav.MoveCloseTo(mob.Position, false, Math.Max(1f, reach - 1f));
                walking = true;
                nextMove = now.AddSeconds(1);
            }
            return;
        }
        StopWalking();
        if (Services.Condition[ConditionFlag.Casting] || Services.Condition[ConditionFlag.Casting87]) return;
        if (QuestSteps.UseKeyItem(it.Id, ItemUse.Target, mob, null, out var why))
        {
            itemCast = true;
            refusals = 0;
            itemUntil = now.AddSeconds(it.CastSeconds + 1.5);
            note = $"item {it.Id} sent on {mob.Name} ({mob.BaseId}) {Vector3.Distance(me.Position, mob.Position):0.0} yalms away, subdued {QuestSteps.Subdued(mob)}, health {mob.CurrentHp * 100 / Math.Max(1, mob.MaxHp)}%";
            return;
        }
        lastRefusal = why;
        if (++refusals >= ItemRefusals)
        {
            // The game won't take the item on it now: on with the fight.
            note = $"item {it.Id} refused on {mob.Name} ({mob.BaseId}) {refusals} times, status {why}, subdued {QuestSteps.Subdued(mob)}; fighting on";
            refusals = 0;
            hurtAt = now;
            Go(Step.Fight);
            return;
        }
        Wait(0.4);
    }

    /// <summary>What the fight is doing, for a bug report.</summary>
    public IEnumerable<string> Describe()
    {
        var now = DateTime.UtcNow;
        var mob = QuestSteps.FindEnemy(targetId);
        var me = Services.Objects.LocalPlayer;
        yield return $"{step}, enemy {(targetId == 0 ? "none" : $"\"{Name}\" (base {targetBase})")}"
                     + (mob != null ? $", health {mob.CurrentHp * 100 / Math.Max(1, mob.MaxHp)}%, {(me != null ? Vector3.Distance(me.Position, mob.Position).ToString("0.0") : "?")} yalms away, subdued {QuestSteps.Subdued(mob)}" : targetId != 0 ? ", not in view" : "")
                     + (step != Step.Idle ? $", for {(now - since).TotalSeconds:0} s, last hurt {(now - hurtAt).TotalSeconds:0} s ago" : "")
                     + $", last outcome {Outcome}{(Why.Length > 0 ? $" ({Why})" : "")}, defeated {Defeated}";
        var c = plugin.Config.Combat;
        yield return $"job {GameState.CurrentJob} (basic combo: {(BasicCombo.For(GameState.CurrentJob) is { } k ? (k.Melee ? "melee" : "ranged") : "none")}), heal below {c.HealBelow}%, "
                     + $"chocobo wanted {c.Chocobo} (stance {c.ChocoboStance}), out {Chocobo.Out}, stance set {stanceSet}"
                     + (item != null ? $", quest item {item.Id} (range {item.Range}, cast {item.CastSeconds} s), last refusal status {lastRefusal}" : "");
    }

    private void Finish(End end, string why)
    {
        if (end == End.Defeated) Defeated++;
        Outcome = end;
        Why = why;
        step = Step.Idle;
        StopWalking();
    }

    private void StopWalking()
    {
        if (!walking) return;
        walking = false;
        Vnav.Stop();
    }

    private void Go(Step next)
    {
        step = next;
        stepSince = DateTime.UtcNow;
        nextMove = default;
    }

    private void Wait(double seconds) => waitUntil = DateTime.UtcNow.AddSeconds(seconds);
}
