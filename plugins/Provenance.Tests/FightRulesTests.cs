using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Provenance.Core.Tribes;
using Xunit;

namespace Provenance.Tests;

/// <summary>
/// The step rules around fights: fighting back before the wait while in combat, a class that doesn't fight carrying
/// on, the quest's enemies before the next trigger, where to look for them, the Ixal's change to the combat job and
/// back, the quest's item kept for the enemy, and the quest mount's action on enemies. Cases from the same dailies as
/// <see cref="EnemyRulesTests"/>.
/// </summary>
public class FightRulesTests
{
    private static readonly DateTime T0 = new(2026, 11, 24, 15, 0, 0, DateTimeKind.Utc);
    private const uint Thanalan = 146, Forelands = 398, Shroud = 154, Fringes = 612;
    private const uint Pld = 19, Crp = 8;

    private static ListenerRow Row(uint listener, byte type, uint value, byte kind, byte seq) => new(listener, type, value, kind, seq);

    private static Func<uint, ScriptSpot?> Levels(params (uint Row, uint Zone, float X, float Y, float Z, uint Object)[] rows)
        => id => rows.Where(r => r.Row == id).Select(r => new ScriptSpot(r.Row, r.Zone, new Vector3(r.X, r.Y, r.Z), r.Object, r.Object == 0)).FirstOrDefault();

    private static StepEnemy Mob(ulong id, uint baseId, uint name, float x, float z, EnemyTarget target = EnemyTarget.None, uint drawn = 0, bool weakened = false, bool hostile = true)
        => new(id, baseId, name, $"mob {baseId}", new Vector3(x, 0, z)) { Hostile = hostile, Target = target, InCombat = target != EnemyTarget.None, Drawn = drawn, Weakened = weakened };

    private static StepObject Obj(ulong id, uint baseId, float x, float z, StepObjectKind kind = StepObjectKind.Object, uint icon = 0, string name = "thing")
        => new(id, baseId, kind, name, new Vector3(x, 0, z), icon, true, true);

    private static StepSnapshot Snap(QuestScript q, byte seq, uint zone, Vector3 me, IEnumerable<StepEnemy>? mobs = null, IEnumerable<StepObject>? objects = null,
        IEnumerable<StepMarker>? markers = null, Dictionary<uint, int>? items = null, uint job = Pld, uint accept = Pld, bool? fighter = null, bool combat = false,
        bool read = true, bool busy = false, bool remembered = false, HashSet<uint>? statuses = null, bool mounted = false, uint mount = 0, int hp = 100,
        IEnumerable<TodoProgress>? todos = null)
        => new()
        {
            Quest = q.Quest, Accepted = true, Sequence = seq, AcceptJob = accept, Job = job, Fighter = fighter ?? !StepRules.HandOrLand(job), Territory = zone, Player = me,
            Hp = hp, Todos = todos?.ToList() ?? new List<TodoProgress>(),
            Hostiles = mobs?.ToList() ?? new List<StepEnemy>(), Objects = objects?.ToList() ?? new List<StepObject>(), Markers = markers?.ToList() ?? new List<StepMarker>(),
            Items = items ?? new Dictionary<uint, int>(), InCombat = combat, ScriptRead = read, Busy = busy, Remembered = remembered,
            Statuses = statuses ?? new HashSet<uint>(), Mounted = mounted, Mount = mount,
        };

    private static StepPlan Decide(QuestScript q, StepSnapshot s, StepMemory? m = null, uint combatJob = 0)
    {
        m ??= new StepMemory();
        if (s.ScriptRead) m.Observe(s.Signature(), T0);
        return StepRules.Decide(q, s, m, T0, combatJob: combatJob);
    }

    // ------------------------------------------------------------------ 1242 "Elite Beating": field mobs

    private static readonly ScriptSpot EliteArea = new(4505574, Thanalan, new Vector3(128.9f, -0.3f, -9.6f), 0, true);
    private static readonly ScriptSpot EliteGiver = new(4496979, Thanalan, new Vector3(85.6f, 15.3f, -355.7f), 1005552, false);
    private static readonly StepMarker EliteMarker = new(Thanalan, new Vector3(128.9f, -0.3f, -9.6f), 125, 60493, 4505574, 0);
    private static readonly Vector3 InZaharak = new(120, 0, 0);

    private static QuestScript Elite() => new()
    {
        Quest = 65536 + 1242, Name = "Elite Beating", Giver = 1005552, TargetEnd = 1005552, GiverSpot = EliteGiver,
        Enemies = EnemyRules.Read(new[] { ("ENEMY0", 739u), ("ENEMY1", 741u) }, new[] { Row(739, 1, 1837, 5, 1), Row(741, 1, 1838, 5, 1) }, _ => null),
        Todos = new[]
        {
            new ScriptTodo(0, 1, 0, new[] { EliteArea }, "Slay Zahar'ak lancers."),
            new ScriptTodo(1, 1, 0, new[] { EliteArea }, "Slay Zahar'ak archers."),
            new ScriptTodo(2, 255, 0, new[] { EliteGiver }, "Report to Yadovv Gah."),
        },
    };

    [Fact]
    public void AFieldMobInViewIsFought()
    {
        var q = Elite();
        var lancer = Mob(1, 739, 1837, 150, 20);
        var plan = Decide(q, Snap(q, 1, Thanalan, InZaharak, new[] { lancer }, markers: new[] { EliteMarker }));
        Assert.Equal(StepKind.Fight, plan.Kind);
        Assert.Equal(1ul, plan.Enemy!.Id);
        Assert.Equal(StepRules.EngageWithin, plan.Within);
        Assert.Equal(lancer.Position, plan.Spot);
        Assert.Contains("mob 739", plan.ToString());
    }

    [Fact]
    public void WithNoFieldMobInViewTheMarkedAreaIsGoneToThenItsSurroundingsThenThePlayerIsAsked()
    {
        var q = Elite();
        var s = Snap(q, 1, Thanalan, new Vector3(85, 15, -355), markers: new[] { EliteMarker });
        var m = new StepMemory();
        m.Observe(s.Signature(), T0);
        var first = StepRules.Decide(q, s, m, T0);
        Assert.Equal(StepKind.Go, first.Kind);
        Assert.Equal(4505574u, first.Level);
        m.Looked.Add(first.Level);
        for (var i = 0; i < EnemyRules.RingPoints * EnemyRules.RingRounds; i++)
        {
            var around = StepRules.Decide(q, s, m, T0);
            Assert.Equal(StepKind.Go, around.Kind);
            Assert.Contains("looking for the quest's enemies", around.What);
            m.Looked.Add(around.Level);
        }
        var ask = StepRules.Decide(q, s, m, T0);
        Assert.Equal(StepKind.AskUser, ask.Kind);
        Assert.Contains("Couldn't find the quest's enemies", ask.What);
    }

    [Fact]
    public void AtTheReportTheQuestsFieldMobsAreLeftAlone()
    {
        var q = Elite();
        var lancer = Mob(1, 739, 1837, 150, 20);
        var giver = Obj(9, 1005552, 86, -355, StepObjectKind.Npc, 71225, "Yadovv Gah");
        var plan = Decide(q, Snap(q, 255, Thanalan, InZaharak, new[] { lancer }, new[] { giver }, new[] { EliteMarker }));
        Assert.Equal(StepKind.Talk, plan.Kind);
        Assert.Equal(1005552u, plan.Target!.BaseId);
    }

    // ------------------------------------------------------------------ no stall in combat

    [Fact]
    public void OnACombatJobWhateverAttacksIsFoughtBeforeTheWaitWhileInCombat()
    {
        // In combat the quest's script isn't asked (nothing is read): the fight is still decided, from the object table.
        var q = Elite();
        var stranger = Mob(7, 9999, 1, 122, 2, EnemyTarget.You);
        var plan = Decide(q, Snap(q, 255, Thanalan, InZaharak, new[] { stranger }, combat: true, read: false));
        Assert.Equal(StepKind.Fight, plan.Kind);
        Assert.Equal(7ul, plan.Enemy!.Id);
        Assert.StartsWith("fight back", plan.What);
    }

    [Fact]
    public void AnEventOrACastStillWaitsEvenWhenAttacked()
    {
        var q = Elite();
        var stranger = Mob(7, 9999, 1, 122, 2, EnemyTarget.You);
        Assert.Equal(StepKind.Wait, Decide(q, Snap(q, 1, Thanalan, InZaharak, new[] { stranger }, combat: true, read: false, busy: true)).Kind);
    }

    [Fact]
    public void OnACombatJobInCombatWithNothingOfTheRunsToFightWaits()
    {
        // The tail of a fight, or someone else's mob: no step is started from a stale reading.
        var q = Elite();
        var theirs = Mob(7, 739, 1837, 122, 2, EnemyTarget.OtherPlayer);
        var plan = Decide(q, Snap(q, 1, Thanalan, InZaharak, new[] { theirs }, markers: new[] { EliteMarker }, combat: true, read: false));
        Assert.Equal(StepKind.Wait, plan.Kind);
        Assert.Equal("in combat", plan.What);
    }

    private static QuestScript Errand() => new()
    {
        Quest = 65536 + 3902, Name = "Lakeland Neighbors", Giver = 1033712, TargetEnd = 1033712,
        Todos = new[] { new ScriptTodo(0, 1, 1, new[] { new ScriptSpot(8368200, 813, new Vector3(40, 0, 0), 1034103, false) }, "Speak with the dwarven tinkerer.") },
    };

    [Fact]
    public void ACrafterAttackedCarriesOnToTheNextMarkedPlace()
    {
        // The script can't be asked in combat; the map's marker still says where the step is. Not a wait.
        var q = Errand();
        var mob = Mob(7, 9999, 1, 2, 2, EnemyTarget.You);
        var marker = new StepMarker(813, new Vector3(40, 0, 0), 1, 71223, 8368200, 0);
        var plan = Decide(q, Snap(q, 1, 813, Vector3.Zero, new[] { mob }, markers: new[] { marker }, job: Crp, accept: Crp, combat: true, read: false));
        Assert.Equal(StepKind.Go, plan.Kind);
        Assert.Equal(8368200u, plan.Level);
        Assert.StartsWith("carry on while attacked", plan.What);
    }

    [Fact]
    public void ACrafterAttackedGoesOnWithItsStepByTheLastCalmLook()
    {
        // The quest hasn't moved since the last calm look: its lines and objects are remembered, and the step goes on.
        var q = Errand();
        var mob = Mob(7, 9999, 1, 2, 2, EnemyTarget.You);
        var tinkerer = Obj(1, 1034103, 40, 0, StepObjectKind.Npc, 71223, "dwarven tinkerer");
        var plan = Decide(q, Snap(q, 1, 813, Vector3.Zero, new[] { mob }, new[] { tinkerer }, job: Crp, accept: Crp, combat: true, remembered: true));
        Assert.Equal(StepKind.Talk, plan.Kind);
        Assert.Equal(1034103u, plan.Target!.BaseId);
    }

    [Fact]
    public void ACrafterAttackedWithNowhereToGoWaitsForItToPass()
    {
        var q = Errand();
        var mob = Mob(7, 9999, 1, 2, 2, EnemyTarget.You);
        var s = Snap(q, 1, 813, Vector3.Zero, new[] { mob }, job: Crp, accept: Crp, combat: true, read: false);
        var m = new StepMemory();
        m.Looked.Add(8368200);
        var plan = StepRules.Decide(q, s, m, T0);
        Assert.Equal(StepKind.Wait, plan.Kind);
        Assert.Equal("in combat", plan.What);
    }

    [Fact]
    public void InCombatNoCraftingOrClassChangeIsStarted()
    {
        var craft = new QuestScript
        {
            Quest = 65536 + 3902, Giver = 1033712, TargetEnd = 1033712, Product = 31147, Materials = 31146,
            Todos = new[] { new ScriptTodo(1, 2, 1, new[] { new ScriptSpot(8368201, 813, new Vector3(21, 0, 0), 1034104, false) }, "Synthesize .") },
        };
        var mob = Mob(7, 9999, 1, 2, 2, EnemyTarget.You);
        var s = new StepSnapshot
        {
            Quest = craft.Quest, Accepted = true, Sequence = 2, AcceptJob = Crp, Job = Crp, Territory = 813, InCombat = true, Remembered = true,
            Hostiles = new[] { mob }, Todos = new[] { new TodoProgress(1, false, 0, 3, 31147) }, Items = new Dictionary<uint, int> { [31146] = 3 },
        };
        var plan = Decide(craft, s);
        Assert.Equal(StepKind.Wait, plan.Kind);
        Assert.Contains("crafting waits", plan.What);

        // On another class than the quest's: no change in combat (the game refuses it). The way to the step is made
        // meanwhile (standing where one is attacked helps nothing); there, the wait, and the class once that's over.
        var other = new StepSnapshot
        {
            Quest = craft.Quest, Accepted = true, Sequence = 2, AcceptJob = Crp, Job = 9, Territory = 813, InCombat = true, Remembered = true, Hostiles = new[] { mob },
        };
        var walkOn = Decide(craft, other);
        Assert.Equal(StepKind.Go, walkOn.Kind);
        Assert.Equal(8368201u, walkOn.Level);
        Assert.StartsWith("on the way while attacked", walkOn.What);
        Assert.Equal(Crp, walkOn.Job);
        var there = new StepSnapshot
        {
            Quest = craft.Quest, Accepted = true, Sequence = 2, AcceptJob = Crp, Job = 9, Territory = 813, Player = new Vector3(20, 0, 0), InCombat = true, Remembered = true,
            Hostiles = new[] { mob },
        };
        var wait = Decide(craft, there);
        Assert.Equal(StepKind.Wait, wait.Kind);
        Assert.Contains("class can't be changed", wait.What);
        // The attack over: the class.
        var calm = new StepSnapshot { Quest = craft.Quest, Accepted = true, Sequence = 2, AcceptJob = Crp, Job = 9, Territory = 813, Player = new Vector3(20, 0, 0) };
        Assert.Equal(StepKind.SwitchJob, Decide(craft, calm).Kind);
    }

    // ------------------------------------------------------------------ never standing in a fight without defending

    [Fact]
    public void WhatAttacksIsFoughtBeforeAnyReasonToAskThePlayer()
    {
        // Fights given up, defeats that didn't count, nothing changed for long: each would hand the step to the player,
        // and a run that asks stops. With something on the character, the fight comes first.
        var q = Shaft();
        var melia = Mob(5, 5550, 4100, 296, 174, EnemyTarget.You);
        var s = Snap(q, 1, Forelands, AtSurvey, new[] { melia }, combat: true, read: false);
        Assert.Equal(StepKind.Fight, StepRules.Decide(q, s, new StepMemory { GiveUps = StepRules.MaxGiveUps }, T0).Kind);
        Assert.Equal(StepKind.Fight, StepRules.Decide(q, s, new StepMemory { Kills = StepRules.MaxIdleKills }, T0).Kind);
        var stale = new StepMemory();
        stale.Observe("some state", T0 - StepRules.NoProgressLimit - TimeSpan.FromSeconds(1));
        Assert.Equal(StepKind.Fight, StepRules.Decide(q, s, stale, T0).Kind);
        // An enemy given up on that attacks is fought too.
        var gaveUp = new StepMemory();
        gaveUp.GivenUp.Add(5);
        Assert.Equal(StepKind.Fight, StepRules.Decide(q, s, gaveUp, T0).Kind);
        // Nothing attacks: the player is asked.
        var idle = Mob(6, 5550, 4100, 297, 176, drawn: 71224);
        var calm = Snap(q, 1, Forelands, AtSurvey, new[] { idle });
        Assert.Equal(StepKind.AskUser, StepRules.Decide(q, calm, stale, T0).Kind);
        Assert.Equal(StepKind.AskUser, StepRules.Decide(q, calm, new StepMemory { GiveUps = StepRules.MaxGiveUps }, T0).Kind);
    }

    [Fact]
    public void AnAttackerThatCantBeHitDoesntKeepACombatJobStanding()
    {
        // The stepper found it couldn't reach or hurt what attacks. Nothing read (in combat): on to the next marked place.
        var q = Errand();
        var mob = Mob(7, 9999, 1, 2, 2, EnemyTarget.You);
        var marker = new StepMarker(813, new Vector3(40, 0, 0), 1, 71223, 8368200, 0);
        var m = new StepMemory();
        var unread = Snap(q, 1, 813, Vector3.Zero, new[] { mob }, markers: new[] { marker }, combat: true, read: false);
        Assert.Equal(StepKind.Fight, StepRules.Decide(q, unread, m, T0).Kind);
        m.CantHit.Add(7);
        var on = StepRules.Decide(q, unread, m, T0);
        Assert.Equal(StepKind.Go, on.Kind);
        Assert.StartsWith("carry on while attacked", on.What);
        // By the last calm look, when the quest hasn't moved since: the step itself.
        var tinkerer = Obj(1, 1034103, 40, 0, StepObjectKind.Npc, 71223, "dwarven tinkerer");
        var remembered = Snap(q, 1, 813, Vector3.Zero, new[] { mob }, new[] { tinkerer }, combat: true, remembered: true);
        Assert.Equal(StepKind.Talk, StepRules.Decide(q, remembered, m, T0).Kind);
        // In combat with nothing known to attack (the tail of a fight): still the wait.
        var tail = Snap(q, 1, 813, Vector3.Zero, new[] { mob with { Target = EnemyTarget.None } }, markers: new[] { marker }, combat: true, read: false);
        Assert.Equal(StepKind.Wait, StepRules.Decide(q, tail, m, T0).Kind);
    }

    [Fact]
    public void NoFightIsPickedOnLowHealthButWhatAttacksIsFoughtAtOnce()
    {
        var q = Shaft();
        var idle = Mob(6, 5550, 4100, 297, 176, drawn: 71224);
        var m = new StepMemory();
        var hurt = Snap(q, 1, Forelands, AtSurvey, new[] { idle }, hp: 40);
        m.Observe(hurt.Signature(), T0);
        var rest = StepRules.Decide(q, hurt, m, T0);
        Assert.Equal(StepKind.Wait, rest.Kind);
        Assert.Contains("health", rest.What);
        // Still waiting a little under the limit; past it the fight is picked as it is.
        Assert.Equal(StepKind.Wait, StepRules.Decide(q, hurt, m, T0 + StepRules.RestFor - TimeSpan.FromSeconds(1)).Kind);
        Assert.Equal(StepKind.Fight, StepRules.Decide(q, hurt, m, T0 + StepRules.RestFor + TimeSpan.FromSeconds(1)).Kind);
        // Health back: the fight, and the clock starts over for the next time.
        var fine = Snap(q, 1, Forelands, AtSurvey, new[] { idle }, hp: StepRules.FightFromHp);
        Assert.Equal(StepKind.Fight, StepRules.Decide(q, fine, m, T0 + TimeSpan.FromSeconds(5)).Kind);
        Assert.Equal(default, m.RestSince);
        // What attacks is fought whatever the health.
        var attacker = idle with { Target = EnemyTarget.You, InCombat = true };
        Assert.Equal(StepKind.Fight, StepRules.Decide(q, Snap(q, 1, Forelands, AtSurvey, new[] { attacker }, combat: true, read: false, hp: 10), new StepMemory(), T0).Kind);
    }

    [Fact]
    public void NothingThatCallsTheQuestsEnemiesIsDoneOnLowHealthWhenItsCloseBy()
    {
        var q = Shaft();
        var destination = Obj(1, 2006882, 296, 172, name: "destination");
        var m = new StepMemory();
        var hurt = Snap(q, 1, Forelands, AtSurvey, objects: new[] { destination }, hp: 55);
        m.Observe(hurt.Signature(), T0);
        Assert.Equal(StepKind.Wait, StepRules.Decide(q, hurt, m, T0).Kind);
        Assert.Equal(StepKind.Talk, StepRules.Decide(q, hurt, m, T0 + StepRules.RestFor + TimeSpan.FromSeconds(1)).Kind);
        // A long way off the health comes back on the way: no wait.
        var far = Snap(q, 1, Forelands, new Vector3(0, -70, 0), hp: 55);
        var m2 = new StepMemory();
        m2.Observe(far.Signature(), T0);
        Assert.Equal(StepKind.Go, StepRules.Decide(q, far, m2, T0).Kind);
        // The report has no fight ahead: no wait there either.
        var keeneye = Obj(2, 1016803, 57, -174, StepObjectKind.Npc, 71225, "Vath keeneye");
        Assert.Equal(StepKind.Talk, Decide(q, Snap(q, 255, Forelands, new Vector3(57, -47, -170), objects: new[] { keeneye }, hp: 20)).Kind);
    }

    [Fact]
    public void APhraseOrEmoteIsNotSentWhileInCombat()
    {
        // A crafter attacked carries on with its step, but what's sent once only waits for the fight to be over.
        var spot = new ScriptSpot(9100001, 813, new Vector3(40, 0, 0), 1034103, false);
        var q = new QuestScript
        {
            Quest = 65536 + 3903, Name = "A Word", Giver = 1033712, TargetEnd = 1033712,
            Todos = new[] { new ScriptTodo(0, 1, 1, new[] { spot }, "Say “Lali-ho” to the dwarven tinkerer.") },
            Say = new Dictionary<int, string> { [0] = "Lali-ho" },
        };
        var tinkerer = Obj(1, 1034103, 40, 0, StepObjectKind.Npc, 71223, "dwarven tinkerer");
        var mob = Mob(7, 9999, 1, 2, 2, EnemyTarget.You);
        // A way off: the way there is made.
        var far = Decide(q, Snap(q, 1, 813, Vector3.Zero, new[] { mob }, new[] { tinkerer }, job: Crp, accept: Crp, combat: true, remembered: true));
        Assert.Equal(StepKind.Say, far.Kind);
        // There: not said while attacked.
        var there = Decide(q, Snap(q, 1, 813, new Vector3(38, 0, 0), new[] { mob }, new[] { tinkerer }, job: Crp, accept: Crp, combat: true, remembered: true));
        Assert.Equal(StepKind.Wait, there.Kind);
        Assert.Contains("phrase waits", there.What);
        Assert.Equal(StepKind.Say, Decide(q, Snap(q, 1, 813, new Vector3(38, 0, 0), objects: new[] { tinkerer }, job: Crp, accept: Crp)).Kind);
    }

    // ------------------------------------------------------------------ 2261 "Getting the Shaft": survey, then what appears

    private static QuestScript Shaft() => new()
    {
        Quest = 65536 + 2261, Name = "Getting the Shaft", Giver = 1016803, TargetEnd = 1016803,
        GiverSpot = new ScriptSpot(6209461, Forelands, new Vector3(57.3f, -47.8f, -174.6f), 1016803, false),
        Enemies = EnemyRules.Read(new[] { ("ENEMY0", 6211294u), ("ENEMY1", 6211295u) }, new[] { Row(5550, 3, 6211294, 9, 1), Row(5550, 3, 6211295, 9, 1) },
            Levels((6211294, Forelands, 295.2f, -70.6f, 173.7f, 5550), (6211295, Forelands, 26.7f, -66.0f, 128.3f, 5550))),
        Todos = new[]
        {
            new ScriptTodo(0, 1, 3, new[]
            {
                new ScriptSpot(6211297, Forelands, new Vector3(309.7f, -72.7f, 158.0f), 0, true),
                new ScriptSpot(6211298, Forelands, new Vector3(38.8f, -72.3f, 121.3f), 0, true),
            }, "Survey the designated spots and slay meliae for their branches."),
            new ScriptTodo(1, 255, 1, new[] { new ScriptSpot(6209461, Forelands, new Vector3(57.3f, -47.8f, -174.6f), 1016803, false) }, "Deliver the melia branches to the Vath keeneye."),
        },
    };

    private static readonly Vector3 AtSurvey = new(300, -70, 170);

    [Fact]
    public void TheSurveySpotIsUsedAndThenWhatAppearsIsFought()
    {
        var q = Shaft();
        var destination = Obj(1, 2006882, 296, 172, name: "destination");
        // Nothing has appeared yet: the trigger is an ordinary step.
        var before = Decide(q, Snap(q, 1, Forelands, AtSurvey, objects: new[] { destination }));
        Assert.Equal(StepKind.Talk, before.Kind);
        Assert.Equal(2006882u, before.Target!.BaseId);
        // A melia appeared and attacks (in combat, nothing read): fought.
        var melia = Mob(5, 5550, 4100, 296, 174, EnemyTarget.You);
        var attacked = Decide(q, Snap(q, 1, Forelands, AtSurvey, new[] { melia }, combat: true, read: false));
        Assert.Equal(StepKind.Fight, attacked.Kind);
        // A second one stands by with the quest's icon: fought before the next spot is surveyed.
        var idle = Mob(6, 5550, 4100, 297, 176, drawn: 71224);
        var next = Decide(q, Snap(q, 1, Forelands, AtSurvey, new[] { idle }, new[] { destination }));
        Assert.Equal(StepKind.Fight, next.Kind);
        Assert.Equal(6ul, next.Enemy!.Id);
    }

    [Fact]
    public void AnUnmarkedMobOfTheQuestsKindDoesntKeepTheStepFromGoingOn()
    {
        var q = Shaft();
        var destination = Obj(1, 2006882, 296, 172, name: "destination");
        var wild = Mob(6, 5550, 4100, 297, 176);
        var plan = Decide(q, Snap(q, 1, Forelands, AtSurvey, new[] { wild }, new[] { destination }));
        Assert.Equal(StepKind.Talk, plan.Kind);
    }

    [Fact]
    public void WithNothingMarkedLeftTheSpawnPointsAreGoneTo()
    {
        var q = Shaft();
        var s = Snap(q, 1, Forelands, AtSurvey);
        var m = new StepMemory();
        m.Observe(s.Signature(), T0);
        m.Looked.Add(6211297);
        m.Looked.Add(6211298);
        var plan = StepRules.Decide(q, s, m, T0);
        Assert.Equal(StepKind.Go, plan.Kind);
        Assert.Equal(6211294u, plan.Level);
        Assert.Equal(EnemyRules.SpawnWithin, plan.Within);
        Assert.Equal("where the quest's enemies appear", plan.What);
    }

    [Fact]
    public void FightsGivenUpAndDefeatsThatDontCountAskThePlayer()
    {
        var q = Shaft();
        var melia = Mob(5, 5550, 4100, 296, 174, drawn: 71224);
        var s = Snap(q, 1, Forelands, AtSurvey, new[] { melia });
        var gaveUp = new StepMemory { GiveUps = StepRules.MaxGiveUps };
        var ask = StepRules.Decide(q, s, gaveUp, T0);
        Assert.Equal(StepKind.AskUser, ask.Kind);
        Assert.Contains("given up", ask.What);

        // At the fifteenth defeat that didn't count, not the sixteenth.
        Assert.Equal(StepKind.Fight, StepRules.Decide(q, s, new StepMemory { Kills = StepRules.MaxIdleKills - 1 }, T0).Kind);
        var idle = new StepMemory { Kills = StepRules.MaxIdleKills };
        Assert.Equal(StepKind.AskUser, StepRules.Decide(q, s, idle, T0).Kind);
        // A defeat that counts moves the quest on, which starts the count over.
        idle.Observe("moved on", T0);
        Assert.Equal(0, idle.Kills);
        Assert.Equal(StepKind.Fight, StepRules.Decide(q, s, idle, T0).Kind);
    }

    [Fact]
    public void AFightOnAClassTheBasicCombatDoesntKnowIsThePlayers()
    {
        // A battle daily already in the journal, accepted as a class without a combo: asked before the spot is surveyed.
        var q = Shaft();
        var destination = Obj(1, 2006882, 296, 172, name: "destination");
        var plan = Decide(q, Snap(q, 1, Forelands, AtSurvey, objects: new[] { destination }, job: 36, accept: 36, fighter: false));
        Assert.Equal(StepKind.AskUser, plan.Kind);
        Assert.Contains("no actions for the class", plan.What);
        // The report has no fight: handed in as usual.
        var keeneye = Obj(2, 1016803, 57, -174, StepObjectKind.Npc, 71225, "Vath keeneye");
        Assert.Equal(StepKind.Talk, Decide(q, Snap(q, 255, Forelands, new Vector3(57, -47, -170), objects: new[] { keeneye }, job: 36, accept: 36, fighter: false)).Kind);
    }

    [Fact]
    public void WeakFromAKnockOutNoFightIsStartedButAttackersAreFought()
    {
        var q = Shaft();
        var weak = new HashSet<uint> { 43 };
        var idle = Mob(6, 5550, 4100, 297, 176, drawn: 71224);
        Assert.Equal(StepKind.Wait, Decide(q, Snap(q, 1, Forelands, AtSurvey, new[] { idle }, statuses: weak)).Kind);
        // Nor is the survey spot used (it would call the next ones).
        var destination = Obj(1, 2006882, 296, 172, name: "destination");
        Assert.Equal(StepKind.Wait, Decide(q, Snap(q, 1, Forelands, AtSurvey, objects: new[] { destination }, statuses: weak)).Kind);
        var attacker = idle with { Target = EnemyTarget.You, InCombat = true };
        Assert.Equal(StepKind.Fight, Decide(q, Snap(q, 1, Forelands, AtSurvey, new[] { attacker }, statuses: weak, combat: true, read: false)).Kind);
        // At the report there's no fight ahead: the quest is handed in.
        var keeneye = Obj(2, 1016803, 57, -174, StepObjectKind.Npc, 71225, "Vath keeneye");
        Assert.Equal(StepKind.Talk, Decide(q, Snap(q, 255, Forelands, new Vector3(57, -47, -170), objects: new[] { keeneye }, statuses: weak)).Kind);
    }

    // ------------------------------------------------------------------ 1228 "Losing One's Tempered": the item is the enemy's

    private static readonly KeyItemFact SacredAsh = new(2001191, "Sacred Ash", 1843, false, false, 3, 3, new[] { "Sacred Ash", "sacred ash" });

    private static QuestScript Tempered()
    {
        var listeners = new[]
        {
            Row(2003166, 0, 0, 1, 1), Row(79, 3, 4553358, 5, 1), Row(79, 0, 0, 8, 1), Row(80, 0, 0, 8, 1), Row(2003167, 0, 0, 1, 1), Row(80, 3, 4553360, 5, 1),
        };
        return new QuestScript
        {
            Quest = 65536 + 1228, Name = "Losing One's Tempered", Giver = 1005550, TargetEnd = 1005550,
            Enemies = EnemyRules.Read(Array.Empty<(string, uint)>(), listeners, Levels((4553358, Thanalan, -76.7f, -2.4f, -105.7f, 79), (4553360, Thanalan, -160.2f, 4.3f, -136.9f, 80))),
            ItemObjects = EnemyRules.ItemObjects(listeners),
            KeyItems = new[] { SacredAsh },
            Todos = new[]
            {
                new ScriptTodo(0, 1, 3, new[] { new ScriptSpot(4553373, Thanalan, new Vector3(-75.1f, -10.6f, -105.3f), 0, true) },
                    "Head to the tempering ground in northern Zanr'ak, and use the sacred ash on subdued tempered."),
            },
        };
    }

    [Fact]
    public void AnItemMeantForTheEnemyIsNotUsedOnTheObjectThatCallsIt()
    {
        // The line names the sacred ash, and a tempering ground is in view: it's used (a talk), the ash isn't thrown at it.
        var q = Tempered();
        var ground = Obj(1, 2003166, -76, -104, name: "tempering ground");
        var plan = Decide(q, Snap(q, 1, Thanalan, new Vector3(-70, 0, -100), objects: new[] { ground }, items: new() { [2001191] = 3 }));
        Assert.Equal(StepKind.Talk, plan.Kind);
        Assert.Equal(2003166u, plan.Target!.BaseId);
    }

    [Fact]
    public void ASubduedEnemyIsStillTheFightsForItsItem()
    {
        var q = Tempered();
        var subdued = Mob(5, 79, 2000, -77, -105, EnemyTarget.You, weakened: true);
        var s = Snap(q, 1, Thanalan, new Vector3(-70, 0, -100), new[] { subdued }, items: new() { [2001191] = 3 }, combat: true, read: false);
        var plan = Decide(q, s);
        Assert.Equal(StepKind.Fight, plan.Kind);
        Assert.Equal(SacredAsh, EnemyRules.ItemNow(q, s, plan.Enemy!, new StepMemory()));
    }

    // ------------------------------------------------------------------ 1494 "Trust, but Verify": the Ixal's fight

    private const uint Dametta = 1000656, Yazel = 1009211;
    private static readonly KeyItemFact Beans = new(2001374, "Ripe Mun-Tuy Beans", 1, false, false, 3, 3, new[] { "Ripe Mun-Tuy Beans", "ripe Mun-Tuy beans" });

    private static QuestScript Trust()
    {
        var listeners = new[]
        {
            Row(Dametta, 0, 0, 1, 1), Row(Yazel, 0, 0, 1, 1), Row(2004452, 0, 0, 1, 2), Row(2004452, 0, 0, 8, 2), Row(3239, 3, 4890723, 9, 2),
            Row(2004453, 0, 0, 1, 2), Row(2004453, 0, 0, 8, 2), Row(3239, 3, 4890724, 9, 2), Row(Dametta, 0, 0, 1, 3), Row(Yazel, 0, 0, 1, 255),
        };
        var dametta = new ScriptSpot(1146918, Shroud, new Vector3(400.7f, -5.9f, -104.9f), Dametta, false);
        var lair = new ScriptSpot(4890731, Shroud, new Vector3(315.6f, -24.1f, 193.1f), 0, true);
        var yazel = new ScriptSpot(4889077, Shroud, new Vector3(153.9f, -9.0f, 77.5f), Yazel, false);
        return new QuestScript
        {
            Quest = 65536 + 1494, Name = "Trust, but Verify", Giver = Yazel, TargetEnd = Yazel, GiverSpot = yazel, Product = 8091, Materials = 8052, CraftStatus = 547,
            Enemies = EnemyRules.Read(new[] { ("ENEMY0", 4890723u), ("ENEMY1", 4890724u) }, listeners,
                Levels((4890723, Shroud, 325.1f, -13.9f, 180.2f, 3239), (4890724, Shroud, 303.3f, -18.9f, 190.2f, 3239))),
            ItemObjects = EnemyRules.ItemObjects(listeners),
            KeyItems = new[] { Beans },
            Todos = new[]
            {
                new ScriptTodo(0, 1, 1, new[] { dametta }, "Speak with Dametta."),
                new ScriptTodo(1, 2, 1, new[] { lair }, "Use the ripe Mun–Tuy beans to lure out the salivating simian."),
                new ScriptTodo(2, 3, 1, new[] { dametta }, "Present the simian claw and the bladder component materials to Dametta."),
                new ScriptTodo(3, 255, 1, new[] { yazel, dametta }, "Synthesize a bladder component."),
            },
        };
    }

    private static readonly Vector3 AtLair = new(316, -20, 190);
    private static readonly Dictionary<uint, int> HasBeans = new() { [2001374] = 1 };

    [Fact]
    public void BeforeTheFightTheIxalQuestGoesOnAsTheCrafter()
    {
        var q = Trust();
        var dametta = Obj(1, Dametta, 400, -105, StepObjectKind.Npc, 71223, "Dametta");
        var plan = Decide(q, Snap(q, 1, Shroud, new Vector3(398, -6, -100), objects: new[] { dametta }, job: Crp, accept: Crp), combatJob: Pld);
        Assert.Equal(StepKind.Talk, plan.Kind);
        // On the combat job at a sequence without a fight: back to the crafter first.
        var back = Decide(q, Snap(q, 1, Shroud, new Vector3(398, -6, -100), objects: new[] { dametta }, job: Pld, accept: Crp), combatJob: Pld);
        Assert.Equal(StepKind.SwitchJob, back.Kind);
        Assert.Equal(Crp, back.Id);
    }

    [Fact]
    public void TheIxalFightIsDoneOnTheCombatJobChangedToBeforeTheEnemyIsCalled()
    {
        var q = Trust();
        var territory = Obj(1, 2004452, 320, 185, name: "marked territory");
        // As the crafter at the fight's sequence: the combat job first, before the beans call the opo-opo.
        var change = Decide(q, Snap(q, 2, Shroud, AtLair, objects: new[] { territory }, items: HasBeans, job: Crp, accept: Crp), combatJob: Pld);
        Assert.Equal(StepKind.SwitchJob, change.Kind);
        Assert.Equal(Pld, change.Id);
        Assert.Contains("combat job", change.What);
        // On the combat job: the beans on the marked territory.
        var lure = Decide(q, Snap(q, 2, Shroud, AtLair, objects: new[] { territory }, items: HasBeans, job: Pld, accept: Crp), combatJob: Pld);
        Assert.Equal(StepKind.UseItem, lure.Kind);
        Assert.Equal(2001374u, lure.Id);
        Assert.Equal(2004452u, lure.Target!.BaseId);
        // The opo-opo appears and attacks: fought, on the combat job (the class isn't changed back for it).
        var opo = Mob(5, 3239, 3300, 322, 184, EnemyTarget.You);
        var fight = Decide(q, Snap(q, 2, Shroud, AtLair, new[] { opo }, items: HasBeans, job: Pld, accept: Crp, combat: true, read: false), combatJob: Pld);
        Assert.Equal(StepKind.Fight, fight.Kind);
    }

    [Fact]
    public void WithoutACombatJobPickedTheIxalFightIsThePlayers()
    {
        var q = Trust();
        var territory = Obj(1, 2004452, 320, 185, name: "marked territory");
        var s = Snap(q, 2, Shroud, AtLair, objects: new[] { territory }, items: HasBeans, job: Crp, accept: Crp);
        var none = Decide(q, s, combatJob: 0);
        Assert.Equal(StepKind.AskUser, none.Kind);
        Assert.Contains("Monster drops", none.What);
        // A crafter isn't a combat job either.
        Assert.Equal(StepKind.AskUser, Decide(q, s, combatJob: 9).Kind);
    }

    [Fact]
    public void AfterTheIxalFightTheCrafterIsBackBeforeAnyoneIsTalkedTo()
    {
        var q = Trust();
        var dametta = Obj(1, Dametta, 400, -105, StepObjectKind.Npc, 71223, "Dametta");
        var plan = Decide(q, Snap(q, 3, Shroud, new Vector3(398, -6, -100), objects: new[] { dametta }, job: Pld, accept: Crp), combatJob: Pld);
        Assert.Equal(StepKind.SwitchJob, plan.Kind);
        Assert.Equal(Crp, plan.Id);
        // Once the crafter again: the claw goes to Dametta.
        Assert.Equal(StepKind.Talk, Decide(q, Snap(q, 3, Shroud, new Vector3(398, -6, -100), objects: new[] { dametta }, job: Crp, accept: Crp), combatJob: Pld).Kind);
    }

    [Fact]
    public void StillAttackedAfterTheIxalFightTheClassIsntChangedYet()
    {
        // Sequence 3 as the combat job with a straggler on the character: fought first (the game changes no class in combat).
        var q = Trust();
        var straggler = Mob(5, 3239, 3300, 399, -101, EnemyTarget.You);
        var plan = Decide(q, Snap(q, 3, Shroud, new Vector3(398, -6, -100), new[] { straggler }, job: Pld, accept: Crp, combat: true, read: false), combatJob: Pld);
        Assert.Equal(StepKind.Fight, plan.Kind);
    }

    [Fact]
    public void AsTheCrafterAnIxalQuestsEnemyIsNotFought()
    {
        // Attacked as the crafter (the change didn't happen): this class doesn't fight; on, away from it.
        var q = Trust();
        var opo = Mob(5, 3239, 3300, 322, 184, EnemyTarget.You);
        var plan = Decide(q, Snap(q, 2, Shroud, AtLair, new[] { opo }, job: Crp, accept: Crp, combat: true, read: false), combatJob: Pld);
        Assert.NotEqual(StepKind.Fight, plan.Kind);
        Assert.NotEqual(StepKind.SwitchJob, plan.Kind);
    }

    [Fact]
    public void InAFightsSequenceTheItemGoesOnlyOnWhatTheQuestNamesForIt()
    {
        // Something else of the quest's is in view (a marked NPC): the beans aren't used on it.
        var q = Trust();
        var dametta = Obj(1, Dametta, 318, 188, StepObjectKind.Npc, 71223, "Dametta");
        var plan = Decide(q, Snap(q, 2, Shroud, AtLair, objects: new[] { dametta }, items: HasBeans, job: Pld, accept: Crp), combatJob: Pld);
        // On to the lair instead, where the marked territories are.
        Assert.Equal(StepKind.Go, plan.Kind);
        Assert.Equal(4890731u, plan.Level);
    }

    // ------------------------------------------------------------------ 3061 "Ants in the Pants": the quest mount's action on enemies

    private const uint Marid = 147, Trample = 4585;

    private static QuestScript Ants() => new()
    {
        Quest = 65536 + 3061, Name = "Ants in the Pants", Giver = 1024773, TargetEnd = 1024773,
        // The script names no action: the marid's own (Trample: around the rider, 7 yalms).
        Mount = new MountFact(Marid, Trample, 0, 7, false, true),
        Enemies = EnemyRules.Read(new[] { ("ENEMY0", 7232848u), ("ENEMY1", 7232850u) }, new[] { Row(8593, 3, 7232848, 5, 2), Row(8593, 3, 7232850, 5, 2) },
            Levels((7232848, Fringes, 482.5f, 42.5f, 351.4f, 8593), (7232850, Fringes, 485.2f, 42.3f, 349.8f, 8593))),
        Todos = new[]
        {
            new ScriptTodo(0, 1, 1, new[] { new ScriptSpot(7186927, Fringes, new Vector3(-41.6f, 56.0f, 211.4f), 1024777, false) }, "Speak with Dakshina."),
            new ScriptTodo(1, 2, 8, new[]
            {
                new ScriptSpot(7232992, Fringes, new Vector3(474.1f, 40.2f, 348.2f), 0, true),
                new ScriptSpot(7186927, Fringes, new Vector3(-41.6f, 56.0f, 211.4f), 1024777, false),
            }, "Use the marid's Trample ability on Velodyna ants in Virdjala."),
        },
    };

    [Fact]
    public void OnTheQuestMountItsActionIsUsedOnTheQuestsEnemies()
    {
        var q = Ants();
        var ant = Mob(5, 8593, 7100, 483, 352, drawn: 71224);
        var plan = Decide(q, Snap(q, 2, Fringes, new Vector3(470, 42, 345), new[] { ant }, mounted: true, mount: Marid));
        Assert.Equal(StepKind.MountAction, plan.Kind);
        Assert.Equal(5ul, plan.Enemy!.Id);
        Assert.Null(plan.Target);
        Assert.Equal(Trample, plan.Id);
        Assert.Equal(5f, plan.Within);
        // Attacked while riding (in combat, nothing read): still the mount's action, not a fight on foot.
        var biting = ant with { Target = EnemyTarget.You, InCombat = true };
        var attacked = Decide(q, Snap(q, 2, Fringes, new Vector3(470, 42, 345), new[] { biting }, mounted: true, mount: Marid, combat: true, read: false));
        Assert.Equal(StepKind.MountAction, attacked.Kind);
    }

    [Fact]
    public void OnTheQuestMountWhateverElseAttacksIsRiddenPast()
    {
        // A field mob of the Fringes bites the marid: Trample is for the ants, and getting off would lose the mount.
        var q = Ants();
        var stranger = Mob(9, 9999, 1, 471, 346, EnemyTarget.You);
        var marker = new StepMarker(Fringes, new Vector3(474.1f, 40.2f, 348.2f), 30, 60491, 7232992, 0);
        var plan = Decide(q, Snap(q, 2, Fringes, new Vector3(400, 42, 345), new[] { stranger }, markers: new[] { marker }, mounted: true, mount: Marid, combat: true, read: false));
        Assert.Equal(StepKind.Go, plan.Kind);
        Assert.Null(plan.Enemy);
        // With an ant in view as well, the ant gets the action.
        var ant = Mob(5, 8593, 7100, 483, 352, drawn: 71224);
        var both = Decide(q, Snap(q, 2, Fringes, new Vector3(470, 42, 345), new[] { stranger, ant }, mounted: true, mount: Marid, combat: true, read: false));
        Assert.Equal(StepKind.MountAction, both.Kind);
        Assert.Equal(5ul, both.Enemy!.Id);
    }

    [Fact]
    public void OnTheQuestMountWithNoAntsInViewTheirSpawnPointsAreRiddenTo()
    {
        var q = Ants();
        var s = Snap(q, 2, Fringes, new Vector3(470, 42, 345), mounted: true, mount: Marid);
        var m = new StepMemory();
        m.Observe(s.Signature(), T0);
        m.Looked.Add(7232992);
        m.Looked.Add(7186927);
        var plan = StepRules.Decide(q, s, m, T0);
        Assert.Equal(StepKind.Go, plan.Kind);
        Assert.Equal(7232848u, plan.Level);
    }

    [Fact]
    public void AQuestMountWithoutAnActionDoesntFight()
    {
        var q = Ants();
        var horse = new QuestScript { Quest = q.Quest, Giver = q.Giver, Mount = new MountFact(169, 0, 0, 0, false, false), Enemies = q.Enemies, Todos = q.Todos };
        var ant = Mob(5, 8593, 7100, 483, 352, EnemyTarget.You);
        var marker = new StepMarker(Fringes, new Vector3(474.1f, 40.2f, 348.2f), 30, 60491, 7232992, 0);
        var plan = Decide(horse, Snap(horse, 2, Fringes, new Vector3(400, 42, 345), new[] { ant }, markers: new[] { marker }, mounted: true, mount: 169, combat: true, read: false));
        Assert.Equal(StepKind.Go, plan.Kind);
        Assert.True(plan.NoFly);
    }

    [Fact]
    public void AMountWithSeveralActionsTriesTheNextOnATargetTheLastDidNothingTo()
    {
        // The Arkasodara's hippo cart: red, blue and yellow gulal, thrown at the target's place.
        var cart = new MountFact(296, 29382, 10, 2, false, true) { Others = new uint[] { 29384, 29383 }, Ground = true };
        Assert.Equal(29382u, cart.ActionFor(0));
        Assert.Equal(29384u, cart.ActionFor(1));
        Assert.Equal(29383u, cart.ActionFor(2));
        Assert.Equal(29382u, cart.ActionFor(3));
        Assert.Equal(4585u, new MountFact(Marid, Trample, 0, 7, false, true).ActionFor(2));

        var q = new QuestScript
        {
            Quest = 65536 + 4557, Giver = 1042301, Mount = cart,
            Todos = new[] { new ScriptTodo(1, 2, 3, new[] { new ScriptSpot(9000001, 957, new Vector3(50, 0, 0), 1042400, false) }, "Use gulal on the townsfolk.") },
        };
        var target = Obj(1, 1042400, 50, 0, StepObjectKind.Npc, 71223, "anxious provider");
        var s = Snap(q, 2, 957, Vector3.Zero, objects: new[] { target }, mounted: true, mount: 296);
        var m = new StepMemory();
        m.Observe(s.Signature(), T0);
        var first = StepRules.Decide(q, s, m, T0);
        Assert.Equal(StepKind.MountAction, first.Kind);
        Assert.Equal(29382u, first.Id);
        Assert.Equal(ItemUse.Ground, first.Use);
        m.Note(first.Key, T0);
        Assert.Equal(29384u, StepRules.Decide(q, s, m, T0).Id);
    }
}
