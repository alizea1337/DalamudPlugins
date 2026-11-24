using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using Provenance.Combat;
using Provenance.Core.Tribes;
using Provenance.Integrations;

namespace Provenance.Run.Tribes;

/// <summary>
/// Fights one enemy for the quest stepper, the way <see cref="Hunter"/> farms monster drops: the chocobo out and in
/// its stance when the settings want it, up to the reach of the job's basic combo, the combo until the enemy is
/// down, the job's heal or defensive action on low health. It also uses the quest's key item on the enemy when told
/// to (one that was subdued takes no more damage and waits for it). Which enemy, and when the item, is the stepper's
/// to say (<see cref="EnemyRules"/>); this only fights the one it's given. Every wait is bounded, and how it ends
/// depends on whether the enemy attacks: one that doesn't is given up on when it can't be reached, can't be hurt or
/// takes too long; one that attacks the character or the chocobo is fought for as long as it can be hit, and when it
/// can't be reached or hurt the fight ends as <see cref="End.CantHit"/>, so the stepper can go on with its step instead
/// of the character standing there. Ticked on the framework thread.
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
        /// <summary>It wasn't attacking, and is left alone: <see cref="Why"/> says why.</summary>
        GaveUp,
        /// <summary>It attacks, but couldn't be reached or hurt (<see cref="Why"/>): the step goes on, and it's tried again later.</summary>
        CantHit,
        /// <summary>The game doesn't let the character attack it (another player's quest enemy): not the run's.</summary>
        NotOurs,
    }

    /// <summary>
    /// A fight over this long with an enemy that isn't attacking is given up (the order run's monster farming gives its
    /// fights two minutes). One that attacks is fought on: <see cref="Overtime"/> tells the stepper, which tells the player.
    /// </summary>
    public static readonly TimeSpan FightFor = TimeSpan.FromMinutes(3);
    /// <summary>Not within reach of it after this long: the fight ends.</summary>
    private static readonly TimeSpan ReachFor = TimeSpan.FromSeconds(45);
    /// <summary>Nothing has hurt it for this long: the fight has stalled (the stepper tries the quest's item then, when it has one for it).</summary>
    private static readonly TimeSpan StalledAfter = TimeSpan.FromSeconds(10);
    /// <summary>Nothing has hurt it for this long: the fight ends.</summary>
    private static readonly TimeSpan UnhurtFor = TimeSpan.FromSeconds(30);
    /// <summary>Subdued with no item coming for this long: the fight ends.</summary>
    private static readonly TimeSpan SubduedFor = TimeSpan.FromSeconds(8);
    /// <summary>The run's attack counts as "just gone out" for this long (the game takes a moment to show whom a mob turned on).</summary>
    private static readonly TimeSpan StrikeShows = TimeSpan.FromSeconds(4);
    /// <summary>Times in a row the game may refuse the quest's item before the fight goes on without it.</summary>
    private const int ItemRefusals = 12;
    /// <summary>Tries at the chocobo (the summon, the stance) before the fight goes on without it.</summary>
    private const int ChocoboTries = 4;
    /// <summary>Tries (a second apart) at getting off the mount within reach of the enemy before the fight ends.</summary>
    private const int GetOffTries = 15;
    /// <summary>
    /// Refusals in a row for where the character stands (no line of sight, out of range, not facing) before it turns to
    /// the enemy and closes in to melee distance, trying again only twice a second.
    /// </summary>
    private const int CloseInAfter = 3;
    /// <summary>How close to the enemy's edge the character closes in when its actions are refused from where it stands.</summary>
    private const float CloseInTo = 2f;

    /// <summary>The game's "Invalid target." (LogMessage 563), as <c>ActionManager.GetActionStatus</c> returns it.</summary>
    private const uint InvalidTarget = 563;

    /// <summary>
    /// Refusals for where the character stands (LogMessage rows): 562 "Target not in line of sight.", 565 and 566
    /// "Target is not in range.", 597 and 598 "Target out of range.", 599 "Cannot see target."
    /// </summary>
    private static bool Positional(uint status) => status is 562 or 565 or 566 or 597 or 598 or 599;

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
    /// <summary>A walk towards the enemy is under way (stopped once in reach, not every frame), and where it was aimed.</summary>
    private bool walking;
    private Vector3 walkingTo;
    /// <summary>The quest wants only its item used on this kind: it isn't attacked unless it attacks.</summary>
    private bool itemOnly;
    /// <summary>The target was asked about once (whether the game lets the character attack it at all).</summary>
    private bool asked;
    private int getOffTries;
    /// <summary>The run's attacks on this enemy: when the last one went out, and the enemy's health when the first did.</summary>
    private DateTime struckAt;
    private uint hpAtStrike;
    /// <summary>Actions refused in a row for where the character stands.</summary>
    private int refusedHere;

    public QuestFighter(Plugin plugin) => this.plugin = plugin;

    /// <summary>An enemy is being fought (or got ready for, or closed in on).</summary>
    public bool Active => step != Step.Idle;
    public Step Now => step;
    /// <summary>The enemy's game object id (the last one's once the fight is over).</summary>
    public ulong Target => targetId;
    public string Name { get; private set; } = "";
    /// <summary>How the last fight ended; <see cref="End.None"/> while one is under way.</summary>
    public End Outcome { get; private set; }
    /// <summary>Why the fight ended without the enemy down, in plain words.</summary>
    public string Why { get; private set; } = "";
    public string Status { get; private set; } = "";
    /// <summary>Enemies defeated since the stepper started on its quest.</summary>
    public int Defeated { get; private set; }
    /// <summary>The quest's item is being used on the enemy (walking up to it, or its cast).</summary>
    public bool UsingItem => step == Step.Item;
    /// <summary>Fighting, and nothing has hurt the enemy for a while.</summary>
    public bool Stalled => step == Step.Fight && DateTime.UtcNow - hurtAt > StalledAfter;
    /// <summary>The fight is over its time and goes on, because the enemy attacks and can still be hit.</summary>
    public bool Overtime => Active && DateTime.UtcNow - since > FightFor;
    /// <summary>One of the run's attacks has just gone out on the enemy.</summary>
    public bool Striking => Active && struckAt != default && DateTime.UtcNow - struckAt < StrikeShows;
    /// <summary>The enemy lost health since the run's first attack on it went out.</summary>
    public bool HurtIt => struckAt != default && lowestHp < hpAtStrike;

    /// <summary>The job on now has a basic combo to fight with.</summary>
    public static bool CanFight => BasicCombo.For(GameState.CurrentJob) != null;

    /// <summary>Starts on an enemy (dropping the one before).</summary>
    /// <param name="onlyItem">The quest wants only its item used on this kind: it isn't attacked unless it attacks.</param>
    public void Engage(StepEnemy enemy, bool onlyItem = false)
    {
        var now = DateTime.UtcNow;
        StopWalking();
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
        itemOnly = onlyItem;
        asked = false;
        getOffTries = 0;
        struckAt = default;
        hpAtStrike = 0;
        refusedHere = 0;
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
        if (step == Step.Idle) return;
        // The wait for the chocobo ends when the fight starts on its own (the enemy, or another, attacks meanwhile).
        if (now < waitUntil && !(step == Step.Ready && Services.Condition[ConditionFlag.InCombat])) return;
        var me = Services.Objects.LocalPlayer;
        if (me == null || me.IsDead || Services.Condition[ConditionFlag.BetweenAreas]) return;

        var mob = QuestSteps.FindEnemy(targetId);
        if (mob == null) { Finish(End.Gone, "it's gone"); return; }
        if (mob.IsDead || mob.CurrentHp == 0) { Finish(End.Defeated, ""); return; }
        // Over its time: one that isn't attacking is left; one that attacks is fought on while it can be hit.
        if (now - since > FightFor && !QuestSteps.AttacksMe(mob)) { Finish(End.GaveUp, "the fight took too long"); return; }
        if (mob.CurrentHp < lowestHp)
        {
            lowestHp = mob.CurrentHp;
            hurtAt = now;
        }
        // Once, before anything is done to it: does the game let the character attack it at all? (Not asked of one
        // that was subdued or isn't hostile any more: that one takes no attack anyway, and the item's own answer counts.)
        if (!asked)
        {
            asked = true;
            if (mob.StatusFlags.HasFlag(StatusFlags.Hostile) && !QuestSteps.Subdued(mob) && Refusal(mob.GameObjectId) == InvalidTarget) { NotOurs(mob); return; }
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
        waitUntil = default;
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
            // The stepper gets off the mount before a fight; should the character still ride, off first (a few tries).
            if (Services.Condition[ConditionFlag.Mounted])
            {
                if (++getOffTries > GetOffTries) { EndUndone(mob, "couldn't get off the mount to fight it"); return; }
                QuestSteps.GetOff();
                Wait(1);
                return;
            }
            hurtAt = now;
            Go(Step.Fight);
            return;
        }
        if (now - stepSince > ReachFor) { EndUndone(mob, "couldn't reach it"); return; }
        Walk(mob.Position, Math.Max(1f, Reach(mob) - 1f), now);
    }

    private void Fight(IGameObject me, IBattleNpc mob, DateTime now)
    {
        Status = $"Fighting {mob.Name} ({mob.CurrentHp * 100 / Math.Max(1, mob.MaxHp)}%)";
        // Subdued: it takes no more damage and waits for the quest's item (the stepper sends it). With none coming, on.
        if (QuestSteps.Subdued(mob))
        {
            if (subduedSince == default) subduedSince = now;
            Status = $"{mob.Name} is subdued";
            if (now - subduedSince > SubduedFor) EndUndone(mob, "it's subdued, and the quest's item couldn't be used on it");
            return;
        }
        subduedSince = default;
        if (now - hurtAt > UnhurtFor) { EndUndone(mob, itemOnly && struckAt == default ? "the quest's item did nothing to it" : "nothing hurt it"); return; }
        // Only the quest's item is wanted of it (the stepper sends it): it isn't attacked unless it attacks.
        if (itemOnly && !QuestSteps.AttacksMe(mob))
        {
            Status = $"Waiting to see what the item did to {mob.Name}";
            Wait(0.25);
            return;
        }
        if (Vector3.Distance(me.Position, mob.Position) > Reach(mob) + 2) { refusedHere = 0; Go(Step.Approach); return; }
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
        if (next != 0 && BasicCombo.Use(next, mob.GameObjectId))
        {
            if (struckAt == default) hpAtStrike = mob.CurrentHp;
            struckAt = now;
            if (refusedHere >= CloseInAfter) StopWalking();
            refusedHere = 0;
            Wait(0.3);
            return;
        }
        // Not used. Between two actions that's the recast, and the next try comes at once. Refused for where the
        // character stands (no line of sight, not facing), it turns to the enemy and closes in, and tries less often.
        var why = Refusal(mob.GameObjectId);
        if (why == InvalidTarget) { NotOurs(mob); return; }
        if (!Positional(why))
        {
            Wait(0.1);
            return;
        }
        lastRefusal = why;
        if (++refusedHere < CloseInAfter)
        {
            Wait(0.2);
            return;
        }
        if (refusedHere == CloseInAfter)
            note = $"actions on {mob.Name} ({mob.BaseId}) refused {refusedHere} times from {Vector3.Distance(me.Position, mob.Position):0.0} yalms away, status {why}; turning to it and closing in";
        QuestSteps.Face(mob.Position);
        var close = CloseInTo + mob.HitboxRadius;
        if (Vector3.Distance(me.Position, mob.Position) > close + 0.5f) Walk(mob.Position, Math.Max(1f, close - 0.5f), now);
        Wait(0.5);
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
            if (now - stepSince > ReachFor) { EndUndone(mob, "couldn't get close enough to use the quest's item on it"); return; }
            Walk(mob.Position, Math.Max(1f, reach - 1f), now);
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
        // A subdued one the game doesn't take as a target for the item at all: someone else's (it was subdued by them).
        if (why == InvalidTarget && QuestSteps.Subdued(mob)) { NotOurs(mob); return; }
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

    /// <summary>
    /// Why the game wouldn't take the kit's first action on the target now, leaving out the recast and a cast under way
    /// (its action status; 0: it would). Asked at the start of a fight and whenever an action wasn't used.
    /// </summary>
    private uint Refusal(ulong target)
    {
        var am = ActionManager.Instance();
        if (am == null || kit == null) return 0;
        var level = GameState.JobLevel(GameState.CurrentJob);
        foreach (var action in kit.Combo)
        {
            if (!BasicCombo.Learned(action, level)) continue;
            return am->GetActionStatus(ActionType.Action, am->GetAdjustedActionId(action), target, false, false, null);
        }
        return 0;
    }

    /// <summary>
    /// The fight ends without the enemy down. One that attacks couldn't be hit (the stepper goes on with its step and
    /// tries again later); one that doesn't is given up on.
    /// </summary>
    private void EndUndone(IBattleNpc mob, string why) => Finish(QuestSteps.AttacksMe(mob) ? End.CantHit : End.GaveUp, why);

    /// <summary>
    /// The game says the enemy isn't a target for the character ("Invalid target"): another player's quest enemy, which
    /// the filter can't tell from the run's own at the same place. Left alone, without counting as a fight given up.
    /// Should it attack all the same, it's one that can't be hit (the step goes on).
    /// </summary>
    private void NotOurs(IBattleNpc mob)
    {
        note = $"the game says {mob.Name} ({mob.BaseId}) can't be attacked or have the item used on it (status {InvalidTarget}, \"Invalid target\")";
        if (QuestSteps.AttacksMe(mob)) Finish(End.CantHit, "the game doesn't let you attack it");
        else Finish(End.NotOurs, "the game doesn't let you attack it (it's someone else's)");
    }

    /// <summary>What the fight is doing, for a bug report.</summary>
    public IEnumerable<string> Describe()
    {
        var now = DateTime.UtcNow;
        var mob = QuestSteps.FindEnemy(targetId);
        var me = Services.Objects.LocalPlayer;
        yield return $"{step}, enemy {(targetId == 0 ? "none" : $"\"{Name}\" (base {targetBase})")}"
                     + (mob != null ? $", health {mob.CurrentHp * 100 / Math.Max(1, mob.MaxHp)}%, {(me != null ? Vector3.Distance(me.Position, mob.Position).ToString("0.0") : "?")} yalms away, subdued {QuestSteps.Subdued(mob)}, attacks you {QuestSteps.AttacksMe(mob)}" : targetId != 0 ? ", not in view" : "")
                     + (step != Step.Idle ? $", for {(now - since).TotalSeconds:0} s, last hurt {(now - hurtAt).TotalSeconds:0} s ago, struck {(struckAt == default ? "not yet" : $"{(now - struckAt).TotalSeconds:0.0} s ago")}{(itemOnly ? ", only the item is wanted of it" : "")}" : "")
                     + $", last outcome {Outcome}{(Why.Length > 0 ? $" ({Why})" : "")}, defeated {Defeated}";
        var c = plugin.Config.Combat;
        yield return $"job {GameState.CurrentJob} (basic combo: {(BasicCombo.For(GameState.CurrentJob) is { } k ? (k.Melee ? "melee" : "ranged") : "none")}), heal below {c.HealBelow}%, "
                     + $"chocobo wanted {c.Chocobo} (stance {c.ChocoboStance}), out {Chocobo.Out}, stance set {stanceSet}, last refusal status {lastRefusal}"
                     + (item != null ? $", quest item {item.Id} (range {item.Range}, cast {item.CastSeconds} s)" : "");
    }

    private void Finish(End end, string why)
    {
        if (end == End.Defeated) Defeated++;
        Outcome = end;
        Why = why;
        step = Step.Idle;
        StopWalking();
    }

    /// <summary>
    /// Walks towards the enemy: a new walk only when none is under way or the enemy has moved more than 3 yalms from
    /// where the last one was aimed (as the trips do), and not more often than once a second.
    /// </summary>
    private void Walk(Vector3 to, float within, DateTime now)
    {
        if (walking && Vnav.Busy && Vector3.Distance(walkingTo, to) <= 3f) return;
        if (now < nextMove) return;
        Vnav.MoveCloseTo(to, false, within);
        walking = true;
        walkingTo = to;
        nextMove = now.AddSeconds(1);
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
