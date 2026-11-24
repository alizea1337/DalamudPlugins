using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Provenance.Core.Tribes;
using Xunit;

namespace Provenance.Tests;

/// <summary>
/// Which class does what in an Ixal daily with a fight (accepted as a crafter, fought on the combat job): the talks
/// and hand-overs as the crafter, at whoever is talked to; everything that calls the quest's enemies, and every trip
/// in a sequence with a fight, on the combat job; no class change in combat. The cases are 1505 "Of Rodents and
/// Rigging" (the hand-over shares its sequence with an ambush on the way), 1516 "Hot Cross Buns" (a talk and the
/// item on a field mob share a sequence) and 1502 "Getting into Gear" (NPCs who turn on you), with their listener,
/// Level and to-do rows as the game data has them (7.58).
/// </summary>
public class IxalFightClassTests
{
    private static readonly DateTime T0 = new(2026, 11, 24, 15, 0, 0, DateTimeKind.Utc);
    private const uint Shroud = 154, Coerthas = 155;
    private const uint Pld = 19, Crp = 8;

    private static ListenerRow Row(uint listener, byte type, uint value, byte kind, byte seq) => new(listener, type, value, kind, seq);

    private static Func<uint, ScriptSpot?> Levels(params (uint Row, uint Zone, float X, float Y, float Z, uint Object)[] rows)
        => id => rows.Where(r => r.Row == id).Select(r => new ScriptSpot(r.Row, r.Zone, new Vector3(r.X, r.Y, r.Z), r.Object, r.Object == 0)).FirstOrDefault();

    private static StepEnemy Mob(ulong id, uint baseId, uint name, float x, float z, EnemyTarget target = EnemyTarget.None, bool held = false)
        => new(id, baseId, name, $"mob {baseId}", new Vector3(x, 0, z)) { Hostile = true, Target = target, InCombat = target != EnemyTarget.None, Held = held };

    private static StepObject Npc(ulong id, uint baseId, float x, float z, string name) => new(id, baseId, StepObjectKind.Npc, name, new Vector3(x, 0, z), 71223, true, true);

    private static StepSnapshot Snap(QuestScript q, byte seq, uint zone, Vector3 me, uint job, IEnumerable<StepEnemy>? mobs = null, IEnumerable<StepObject>? objects = null,
        IEnumerable<StepMarker>? markers = null, Dictionary<uint, int>? items = null, bool combat = false, bool read = true, bool remembered = false,
        IEnumerable<TodoProgress>? todos = null, int hp = 100, HashSet<uint>? statuses = null)
        => new()
        {
            Quest = q.Quest, Accepted = true, Sequence = seq, AcceptJob = Crp, Job = job, Fighter = !StepRules.HandOrLand(job), Territory = zone, Player = me,
            Hostiles = mobs?.ToList() ?? new List<StepEnemy>(), Objects = objects?.ToList() ?? new List<StepObject>(), Markers = markers?.ToList() ?? new List<StepMarker>(),
            Items = items ?? new Dictionary<uint, int>(), InCombat = combat, ScriptRead = read, Remembered = remembered, Todos = todos?.ToList() ?? new List<TodoProgress>(),
            Hp = hp, Statuses = statuses ?? new HashSet<uint>(),
        };

    private static StepPlan Decide(QuestScript q, StepSnapshot s, StepMemory? m = null)
    {
        m ??= new StepMemory();
        if (s.ScriptRead) m.Observe(s.Signature(), T0);
        return StepRules.Decide(q, s, m, T0, combatJob: Pld);
    }

    // ------------------------------------------------------------------ 1505 "Of Rodents and Rigging"

    private const uint Methuli = 1009212, Hildeyerd = 1009218, Pot = 2004464, Qiqirn = 170;
    private static readonly ScriptSpot AlderSprings = new(4890859, Shroud, new Vector3(20.0f, -52.0f, 443.2f), 0, true);
    private static readonly ScriptSpot AtHildeyerd = new(4890285, Shroud, new Vector3(-4.1f, -41.0f, 176.8f), Hildeyerd, false);
    private static readonly ScriptSpot AtMethuli = new(4889078, Shroud, new Vector3(151.3f, -9.8f, 79.7f), Methuli, false);
    private static readonly StepMarker HildeyerdMarker = new(Shroud, new Vector3(-4.1f, -41.0f, 176.8f), 1, 71223, 4890285, 0);
    /// <summary>Where the ambush is sprung (the area that calls the Qiqirn), on the way from the pot.</summary>
    private static readonly Vector3 OnTheWay = new(-41.8f, -44.7f, 400.7f);
    private static readonly Vector3 ByHildeyerd = new(-2, -41, 178);

    private static QuestScript Rigging()
    {
        var listeners = new[]
        {
            Row(Pot, 0, 0, 1, 1), Row(Methuli, 0, 0, 1, 1), Row(2004463, 0, 0, 1, 1), Row(Hildeyerd, 0, 0, 1, 2), Row(Methuli, 0, 0, 1, 2), Row(2004463, 0, 0, 1, 2),
            Row(5000000, 3, 4890860, 10, 2), Row(Qiqirn, 3, 4890861, 5, 2), Row(Methuli, 0, 0, 1, 255), Row(Hildeyerd, 0, 0, 1, 255),
        };
        var args = new[]
        {
            ("ACTOR0", Methuli), ("EOBJECT0", Pot), ("ITEM0", 2001382u), ("EOBJECT1", 2004463u), ("ACTOR1", Hildeyerd), ("RITEM0", 8057u), ("STATUS0", 549u),
            ("EVENTRANGE0", 4890860u), ("ENEMY0", 4890861u), ("RITEM1", 8096u),
        };
        return new QuestScript
        {
            Quest = 65536 + 1505, Name = "Of Rodents and Rigging", Giver = Methuli, TargetEnd = Methuli, GiverSpot = AtMethuli, Product = 8096, Materials = 8057, CraftStatus = 549,
            Enemies = EnemyRules.Read(args, listeners, Levels((4890861, Shroud, -40.2f, -47.3f, 404.9f, Qiqirn))),
            ItemObjects = EnemyRules.ItemObjects(listeners),
            // The crate is handed over by talking (its item has no action).
            KeyItems = new[] { new KeyItemFact(2001382, "Rigging Component Materials", 0, false, false, 3, 0, new[] { "Rigging Component Materials" }) },
            Todos = new[]
            {
                new ScriptTodo(0, 1, 1, new[] { AlderSprings }, "Obtain rigging component materials from the snatched storage pot in Alder Springs."),
                new ScriptTodo(1, 2, 1, new[] { AtHildeyerd }, "Present the rigging component materials to Hildeyerd at Fallgourd Float."),
                new ScriptTodo(2, 255, 1, new[] { AtMethuli, AtHildeyerd }, "Synthesize rigging components."),
            },
        };
    }

    private static readonly Dictionary<uint, int> HasCrate = new() { [2001382] = 1 };

    [Fact]
    public void TheDataOf1505PutsTheAmbushInTheHandOversSequence()
    {
        var q = Rigging();
        var qiqirn = Assert.Single(q.Enemies);
        Assert.Equal((Qiqirn, (byte)2, false, EnemyTask.Defeat), (qiqirn.Base, qiqirn.Sequence, qiqirn.Field, qiqirn.Task));
        Assert.Equal(4890861u, qiqirn.Spawn!.Level);
        Assert.False(EnemyRules.Fights(q, 1));
        Assert.True(EnemyRules.Fights(q, 2));
        Assert.False(EnemyRules.Fights(q, 255));
    }

    [Fact]
    public void ThePotOf1505IsOpenedAsTheCrafter()
    {
        // Sequence 1 has no fight: the quest goes on as the class it was accepted as.
        var q = Rigging();
        var pot = new StepObject(1, Pot, StepObjectKind.Object, "snatched storage pot", new Vector3(22, 0, 440), 0, true, true);
        Assert.Equal(StepKind.Talk, Decide(q, Snap(q, 1, Shroud, new Vector3(20, -52, 443), Crp, objects: new[] { pot })).Kind);
        var back = Decide(q, Snap(q, 1, Shroud, new Vector3(20, -52, 443), Pld, objects: new[] { pot }));
        Assert.Equal(StepKind.SwitchJob, back.Kind);
        Assert.Equal(Crp, back.Id);
    }

    [Fact]
    public void TheWayToHildeyerdIsMadeOnTheCombatJob()
    {
        // Sequence 2: the crate goes to Hildeyerd, and a Qiqirn waits on the way. The trip is the combat job's.
        var q = Rigging();
        var change = Decide(q, Snap(q, 2, Shroud, new Vector3(20, -52, 443), Crp, markers: new[] { HildeyerdMarker }, items: HasCrate));
        Assert.Equal(StepKind.SwitchJob, change.Kind);
        Assert.Equal(Pld, change.Id);
        var trip = Decide(q, Snap(q, 2, Shroud, new Vector3(20, -52, 443), Pld, markers: new[] { HildeyerdMarker }, items: HasCrate));
        Assert.Equal(StepKind.Go, trip.Kind);
        Assert.Equal(4890285u, trip.Level);
        // Not held up for health: nothing here calls the enemies on purpose, and it's a long way.
        Assert.Equal(StepKind.Go, Decide(q, Snap(q, 2, Shroud, new Vector3(20, -52, 443), Pld, markers: new[] { HildeyerdMarker }, items: HasCrate, hp: 50)).Kind);
    }

    [Fact]
    public void TheAmbushOf1505IsFoughtOnTheWay()
    {
        var q = Rigging();
        // The Qiqirn the area called: the quest's handler holds it where the quest puts it, and then it attacks.
        var waiting = Mob(5, Qiqirn, 2989, -40, 405, held: true);
        var seen = Decide(q, Snap(q, 2, Shroud, OnTheWay, Pld, new[] { waiting }, markers: new[] { HildeyerdMarker }, items: HasCrate));
        Assert.Equal(StepKind.Fight, seen.Kind);
        var attacking = waiting with { Target = EnemyTarget.You, InCombat = true };
        var fight = Decide(q, Snap(q, 2, Shroud, OnTheWay, Pld, new[] { attacking }, items: HasCrate, combat: true, read: false));
        Assert.Equal(StepKind.Fight, fight.Kind);
        Assert.StartsWith("fight back", fight.What);
        // A Qiqirn of the open world, of the same kind, elsewhere: not the quest's.
        var wild = Mob(6, Qiqirn, 2989, 100, 100);
        Assert.Equal(StepKind.Go, Decide(q, Snap(q, 2, Shroud, new Vector3(90, -40, 110), Pld, new[] { wild }, markers: new[] { HildeyerdMarker }, items: HasCrate)).Kind);
    }

    [Fact]
    public void AtHildeyerdTheCrafterGoesBackOnForTheHandOver()
    {
        var q = Rigging();
        var hildeyerd = Npc(1, Hildeyerd, -4.1f, 176.8f, "Hildeyerd");
        // In view from a way off, on the combat job: the trip goes on as it is. The step is the crafter's.
        var approach = Decide(q, Snap(q, 2, Shroud, new Vector3(-4, -41, 215), Pld, objects: new[] { hildeyerd }, items: HasCrate));
        Assert.Equal(StepKind.Talk, approach.Kind);
        Assert.Equal(Crp, approach.Job);
        Assert.True(StepRules.HandOver(q, Snap(q, 2, Shroud, ByHildeyerd, Pld, objects: new[] { hildeyerd }), approach));
        // Arrived, out of combat, no enemy of the quest's in view: the crafter.
        var change = Decide(q, Snap(q, 2, Shroud, ByHildeyerd, Pld, objects: new[] { hildeyerd }, items: HasCrate));
        Assert.Equal(StepKind.SwitchJob, change.Kind);
        Assert.Equal(Crp, change.Id);
        Assert.Contains("accepted as", change.What);
        // As the crafter: the hand-over. No wait for health or for a knock-out's weakness: it's not a fight.
        var talk = Decide(q, Snap(q, 2, Shroud, ByHildeyerd, Crp, objects: new[] { hildeyerd }, items: HasCrate, hp: 30, statuses: new HashSet<uint> { 43 }));
        Assert.Equal(StepKind.Talk, talk.Kind);
        Assert.Equal(Hildeyerd, talk.Target!.BaseId);
        Assert.Equal(Crp, talk.Job);
    }

    [Fact]
    public void AtHildeyerdWithTheQiqirnStillOnTheCharacterTheClassIsntChanged()
    {
        var q = Rigging();
        var hildeyerd = Npc(1, Hildeyerd, -4.1f, 176.8f, "Hildeyerd");
        var qiqirn = Mob(5, Qiqirn, 2989, -3, 180, EnemyTarget.You);
        // In combat nothing is read: the fight.
        Assert.Equal(StepKind.Fight, Decide(q, Snap(q, 2, Shroud, ByHildeyerd, Pld, new[] { qiqirn }, items: HasCrate, combat: true, read: false)).Kind);
        // The quest's Qiqirn in view, not attacking yet: fought before the class is changed.
        var waiting = Mob(5, Qiqirn, 2989, -40, 405, held: true);
        Assert.Equal(StepKind.Fight, Decide(q, Snap(q, 2, Shroud, OnTheWay, Pld, new[] { waiting }, new[] { hildeyerd with { Position = new Vector3(-40, 0, 398) } }, items: HasCrate)).Kind);
        // In combat with nothing the run can fight (the tail of the fight): the wait, no class change.
        var tail = Decide(q, Snap(q, 2, Shroud, ByHildeyerd, Pld, objects: new[] { hildeyerd }, items: HasCrate, combat: true, remembered: true));
        Assert.Equal(StepKind.Wait, tail.Kind);
    }

    [Fact]
    public void AttackedAsTheCrafterBeforeTheClassChangeTheTripGoesOn()
    {
        // The crafter is attacked at the pot, before the change to the combat job could happen: it walks on (the class
        // can't be changed in combat, and standing there helps nothing).
        var q = Rigging();
        var boar = Mob(9, 9999, 1, 22, 441, EnemyTarget.You);
        var hildeyerd = Npc(1, Hildeyerd, -4.1f, 176.8f, "Hildeyerd");
        // The quest hasn't moved since the last calm look: on with the step's trip.
        var remembered = Decide(q, Snap(q, 2, Shroud, new Vector3(20, -52, 443), Crp, new[] { boar }, new[] { hildeyerd }, items: HasCrate, combat: true, remembered: true));
        Assert.Equal(StepKind.Talk, remembered.Kind);
        Assert.StartsWith("on the way while attacked", remembered.What);
        Assert.Equal(Crp, remembered.Job);
        // Nothing read: on to the marked place.
        var unread = Decide(q, Snap(q, 2, Shroud, new Vector3(20, -52, 443), Crp, new[] { boar }, markers: new[] { HildeyerdMarker }, items: HasCrate, combat: true, read: false));
        Assert.Equal(StepKind.Go, unread.Kind);
        Assert.StartsWith("carry on while attacked", unread.What);
        // The attack over: the combat job for the rest of the way.
        var calm = Decide(q, Snap(q, 2, Shroud, new Vector3(0, -45, 300), Crp, objects: new[] { hildeyerd }, items: HasCrate));
        Assert.Equal(StepKind.SwitchJob, calm.Kind);
        Assert.Equal(Pld, calm.Id);
    }

    [Fact]
    public void AttackedAsTheCrafterAtHildeyerdTheHandOverIsTriedAllTheSame()
    {
        var q = Rigging();
        var boar = Mob(9, 9999, 1, -3, 180, EnemyTarget.You);
        var hildeyerd = Npc(1, Hildeyerd, -4.1f, 176.8f, "Hildeyerd");
        var plan = Decide(q, Snap(q, 2, Shroud, ByHildeyerd, Crp, new[] { boar }, new[] { hildeyerd }, items: HasCrate, combat: true, remembered: true));
        Assert.Equal(StepKind.Talk, plan.Kind);
        Assert.Equal(Crp, plan.Job);
    }

    [Fact]
    public void AfterTheHandOverOf1505TheReportIsTheCraftersAsEver()
    {
        var q = Rigging();
        var s = Snap(q, 255, Shroud, ByHildeyerd, Pld, items: new() { [8057] = 3 });
        var back = Decide(q, s);
        Assert.Equal(StepKind.SwitchJob, back.Kind);
        Assert.Equal(Crp, back.Id);
    }

    // ------------------------------------------------------------------ 1516 "Hot Cross Buns"

    private const uint Rozol = 1009213, Duvicauroix = 1006385, Templeton = 1009219, Croc = 395, CrocName = 784;
    private static readonly ScriptSpot AtDuvicauroix = new(4262326, Coerthas, new Vector3(241.5f, 222.3f, 325.5f), Duvicauroix, false);
    private static readonly ScriptSpot SkyfireLocks = new(4891498, Coerthas, new Vector3(311.3f, 237.2f, 148.5f), 0, true);
    private static readonly ScriptSpot AtTempleton = new(4889199, Coerthas, new Vector3(251.1f, 222.0f, 366.2f), Templeton, false);
    private static readonly ScriptSpot AtRozol = new(4889079, Shroud, new Vector3(159.3f, -3.7f, 64.3f), Rozol, false);
    private static readonly KeyItemFact Knife = new(2001403, "Shearing Knife", 1, false, false, 3, 3, new[] { "Shearing Knife", "shearing knife", "shearing knives" });
    private static readonly Dictionary<uint, int> HasKnife = new() { [2001403] = 1 };
    private static readonly StepMarker LocksMarker = new(Coerthas, new Vector3(311.3f, 237.2f, 148.5f), 100, 60493, 4891498, 0);
    private static readonly StepMarker DuvicauroixMarker = new(Coerthas, new Vector3(241.5f, 222.3f, 325.5f), 1, 71223, 4262326, 0);
    private static readonly Vector3 ByDuvicauroix = new(243, 222, 327);
    private static readonly Vector3 InTheLocks = new(305, 237, 150);

    private static QuestScript Buns()
    {
        var listeners = new[]
        {
            Row(Duvicauroix, 0, 0, 1, 1), Row(Croc, 1, CrocName, 8, 1), Row(Rozol, 0, 0, 1, 1), Row(Templeton, 0, 0, 1, 2), Row(Rozol, 0, 0, 1, 2), Row(Duvicauroix, 0, 0, 1, 2),
            Row(Rozol, 0, 0, 1, 255), Row(Templeton, 0, 0, 1, 255),
        };
        var args = new[]
        {
            ("ACTOR0", Rozol), ("ITEM0", 2001403u), ("ACTOR1", Duvicauroix), ("ITEM1", 2001401u), ("ENEMY0", Croc), ("ITEM2", 2001402u), ("ACTOR2", Templeton), ("RITEM0", 8063u),
            ("STATUS0", 550u), ("RITEM1", 8102u),
        };
        return new QuestScript
        {
            Quest = 65536 + 1516, Name = "Hot Cross Buns", Giver = Rozol, TargetEnd = Rozol, GiverSpot = AtRozol, Product = 8102, Materials = 8063, CraftStatus = 550,
            Enemies = EnemyRules.Read(args, listeners, _ => null),
            ItemObjects = EnemyRules.ItemObjects(listeners),
            KeyItems = new[]
            {
                Knife,
                new KeyItemFact(2001401, "Ballast Component Materials", 0, false, false, 3, 0, new[] { "Ballast Component Materials" }),
                new KeyItemFact(2001402, "Feral Croc Beard", 0, false, false, 3, 0, new[] { "Feral Croc Beard" }),
            },
            Todos = new[]
            {
                new ScriptTodo(0, 1, 1, new[] { AtDuvicauroix }, "Speak with Duvicauroix at the Observatorium."),
                new ScriptTodo(1, 1, 1, new[] { SkyfireLocks }, "Use the shearing knife to cut a beard from a feral croc in Skyfire Locks."),
                new ScriptTodo(2, 2, 1, new[] { AtTempleton }, "Present the ballast component materials and the feral croc beard to Templeton at the Observatorium."),
                new ScriptTodo(3, 255, 1, new[] { AtRozol, AtTempleton }, "Synthesize a ballast component."),
            },
        };
    }

    private static readonly TodoProgress[] BothOpen = { new(0, false, 0, 1, 0), new(1, false, 0, 1, 0) };
    private static readonly TodoProgress[] SpokenTo = { new(0, true, 1, 1, 0), new(1, false, 0, 1, 0) };

    [Fact]
    public void TheDataOf1516PutsTheTalkAndTheCrocInOneSequence()
    {
        var q = Buns();
        var croc = Assert.Single(q.Enemies);
        Assert.Equal((Croc, CrocName, (byte)1, true, EnemyTask.UseItem), (croc.Base, croc.Name, croc.Sequence, croc.Field, croc.Task));
        Assert.True(EnemyRules.Fights(q, 1));
        Assert.False(EnemyRules.Fights(q, 2));
        Assert.Equal(2, q.TodosAt(1).Count());
    }

    [Fact]
    public void TheTripsOf1516AreTheCombatJobsAndTheTalkWithDuvicauroixTheCrafters()
    {
        var q = Buns();
        var duvicauroix = Npc(1, Duvicauroix, 241.5f, 325.5f, "Duvicauroix");
        // Just accepted, as the crafter, in the North Shroud: the sequence has a fight, so the way is the combat job's.
        var start = Decide(q, Snap(q, 1, Shroud, new Vector3(159, -4, 66), Crp, items: HasKnife, todos: BothOpen));
        Assert.Equal(StepKind.SwitchJob, start.Kind);
        Assert.Equal(Pld, start.Id);
        Assert.Equal(StepKind.Go, Decide(q, Snap(q, 1, Shroud, new Vector3(159, -4, 66), Pld, items: HasKnife, todos: BothOpen)).Kind);
        // At Duvicauroix (a line names him): the crafter goes on for the talk.
        var change = Decide(q, Snap(q, 1, Coerthas, ByDuvicauroix, Pld, objects: new[] { duvicauroix }, markers: new[] { LocksMarker, DuvicauroixMarker }, items: HasKnife, todos: BothOpen));
        Assert.Equal(StepKind.SwitchJob, change.Kind);
        Assert.Equal(Crp, change.Id);
        var talk = Decide(q, Snap(q, 1, Coerthas, ByDuvicauroix, Crp, objects: new[] { duvicauroix }, markers: new[] { LocksMarker, DuvicauroixMarker }, items: HasKnife, todos: BothOpen));
        Assert.Equal(StepKind.Talk, talk.Kind);
        Assert.Equal(Duvicauroix, talk.Target!.BaseId);
        // Spoken to: the crocs are next, a trip again, so the combat job goes back on.
        var next = Decide(q, Snap(q, 1, Coerthas, ByDuvicauroix, Crp, markers: new[] { LocksMarker }, items: HasKnife, todos: SpokenTo));
        Assert.Equal(StepKind.SwitchJob, next.Kind);
        Assert.Equal(Pld, next.Id);
        var ride = Decide(q, Snap(q, 1, Coerthas, ByDuvicauroix, Pld, markers: new[] { LocksMarker }, items: HasKnife, todos: SpokenTo));
        Assert.Equal(StepKind.Go, ride.Kind);
        Assert.Equal(4891498u, ride.Level);
    }

    [Fact]
    public void TheKnifeOf1516GoesOnAFeralCrocOnTheCombatJobWithoutAFight()
    {
        var q = Buns();
        var croc = Mob(5, Croc, CrocName, 300, 152);
        var s = Snap(q, 1, Coerthas, InTheLocks, Pld, new[] { croc }, markers: new[] { LocksMarker }, items: HasKnife, todos: SpokenTo);
        var plan = Decide(q, s);
        Assert.Equal(StepKind.Fight, plan.Kind);
        Assert.Equal(5ul, plan.Enemy!.Id);
        // The knife at once (the quest wants nothing else of the croc), and no attack unless it attacks.
        Assert.Equal(Knife, EnemyRules.ItemNow(q, s, croc, new StepMemory()));
        Assert.True(EnemyRules.ItemOnly(q, s, croc));
        // A croc under another name (the same kind) isn't the quest's.
        var other = Mob(6, Croc, 999, 300, 152);
        Assert.Equal(StepKind.Go, Decide(q, Snap(q, 1, Coerthas, new Vector3(250, 230, 250), Pld, new[] { other }, markers: new[] { LocksMarker }, items: HasKnife, todos: SpokenTo)).Kind);
        // As the crafter the croc isn't touched: the combat job first.
        var asCrafter = Decide(q, Snap(q, 1, Coerthas, InTheLocks, Crp, new[] { croc }, markers: new[] { LocksMarker }, items: HasKnife, todos: SpokenTo));
        Assert.Equal(StepKind.SwitchJob, asCrafter.Kind);
        Assert.Equal(Pld, asCrafter.Id);
    }

    [Fact]
    public void WithoutTheKnifeWhoeverHandsItOutComesBeforeTheCrocs()
    {
        // Should the knife not be in the bags yet, the crocs are nobody's business, and the area isn't gone to first
        // though it's nearer.
        var q = Buns();
        var croc = Mob(5, Croc, CrocName, 300, 152);
        var none = new Dictionary<uint, int>();
        var s = Snap(q, 1, Coerthas, InTheLocks, Pld, new[] { croc }, markers: new[] { LocksMarker, DuvicauroixMarker }, items: none, todos: BothOpen);
        var plan = Decide(q, s);
        Assert.Equal(StepKind.Go, plan.Kind);
        Assert.Equal(4262326u, plan.Level);
        // With the knife, the croc in view is gone for.
        Assert.Equal(StepKind.Fight, Decide(q, Snap(q, 1, Coerthas, InTheLocks, Pld, new[] { croc }, markers: new[] { LocksMarker, DuvicauroixMarker }, items: HasKnife, todos: BothOpen)).Kind);
    }

    [Fact]
    public void TheHandOverOf1516IsInASequenceWithoutAFight()
    {
        var q = Buns();
        var templeton = Npc(2, Templeton, 251.1f, 366.2f, "Templeton");
        var back = Decide(q, Snap(q, 2, Coerthas, InTheLocks, Pld, objects: new[] { templeton }));
        Assert.Equal(StepKind.SwitchJob, back.Kind);
        Assert.Equal(Crp, back.Id);
        Assert.Equal(StepKind.Talk, Decide(q, Snap(q, 2, Coerthas, new Vector3(250, 222, 365), Crp, objects: new[] { templeton })).Kind);
    }

    // ------------------------------------------------------------------ 1502 "Getting into Gear": NPCs who turn on you

    private const uint Tazel = 1009214, Rat = 1009638;

    private static QuestScript Gear()
    {
        var listeners = new[]
        {
            Row(1006406, 0, 0, 1, 1), Row(Tazel, 0, 0, 1, 1), Row(Rat, 0, 0, 1, 2), Row(Qiqirn, 3, 4893688, 9, 2), Row(1009694, 0, 0, 1, 2), Row(Qiqirn, 3, 4893689, 9, 2),
            Row(Hildeyerd, 0, 0, 1, 3), Row(Tazel, 0, 0, 1, 255),
        };
        return new QuestScript
        {
            Quest = 65536 + 1502, Name = "Getting into Gear", Giver = Tazel, TargetEnd = Tazel, Product = 8109, Materials = 8070, CraftStatus = 549,
            Enemies = EnemyRules.Read(new[] { ("ENEMY0", 4893688u), ("ENEMY1", 4893689u) }, listeners,
                Levels((4893688, Shroud, 4.8f, 0.3f, -87.2f, Qiqirn), (4893689, Shroud, 49.0f, 6.8f, -48.8f, Qiqirn))),
            ItemObjects = EnemyRules.ItemObjects(listeners),
            Todos = new[]
            {
                new ScriptTodo(0, 1, 1, new[] { new ScriptSpot(4257001, Shroud, new Vector3(63.2f, -13.4f, 140.9f), 1006406, false) }, "Speak with Miah Molkot at Eugenia's Spire."),
                new ScriptTodo(1, 2, 3, new[] { new ScriptSpot(4893693, Shroud, new Vector3(79.7f, -11.9f, -49.5f), 0, true) },
                    "Speak with Qiqirn rats at Proud Creek, and take back the transmission component materials by force."),
                new ScriptTodo(2, 3, 1, new[] { AtHildeyerd }, "Present the transmission component materials to Hildeyerd at Fallgourd Float."),
            },
        };
    }

    [Fact]
    public void AnNpcWhoTurnsOnYouIsTalkedToOnTheCombatJob()
    {
        // The Qiqirn rats are the quest's NPCs, but no to-do line names them: talking to one calls the fight.
        var q = Gear();
        var rat = Npc(1, Rat, 5, -86, "Qiqirn rat");
        var at = new Vector3(6, 0, -85);
        var asCrafter = Decide(q, Snap(q, 2, Shroud, at, Crp, objects: new[] { rat }));
        Assert.Equal(StepKind.SwitchJob, asCrafter.Kind);
        Assert.Equal(Pld, asCrafter.Id);
        var talk = Decide(q, Snap(q, 2, Shroud, at, Pld, objects: new[] { rat }));
        Assert.Equal(StepKind.Talk, talk.Kind);
        Assert.Equal(Pld, talk.Job);
        Assert.False(StepRules.HandOver(q, Snap(q, 2, Shroud, at, Pld, objects: new[] { rat }), talk));
        // Not on low health, nor weak from a knock-out: it calls a fight.
        Assert.Equal(StepKind.Wait, Decide(q, Snap(q, 2, Shroud, at, Pld, objects: new[] { rat }, hp: 40)).Kind);
        Assert.Equal(StepKind.Wait, Decide(q, Snap(q, 2, Shroud, at, Pld, objects: new[] { rat }, statuses: new HashSet<uint> { 43 })).Kind);
        // The rat turned into a Qiqirn that attacks: fought.
        var qiqirn = Mob(5, Qiqirn, 2989, 5, -87, EnemyTarget.You);
        Assert.Equal(StepKind.Fight, Decide(q, Snap(q, 2, Shroud, at, Pld, new[] { qiqirn }, combat: true, read: false)).Kind);
    }

    [Fact]
    public void WithoutACombatJobTheStepIsThePlayersAndACombatJobWithoutActionsToo()
    {
        var q = Gear();
        var rat = Npc(1, Rat, 5, -86, "Qiqirn rat");
        var s = Snap(q, 2, Shroud, new Vector3(6, 0, -85), Crp, objects: new[] { rat });
        var m = new StepMemory();
        m.Observe(s.Signature(), T0);
        Assert.Equal(StepKind.AskUser, StepRules.Decide(q, s, m, T0, combatJob: 0).Kind);
        // On a combat job the basic combat has no actions for (blue mage): the player's, before the rat is talked to.
        var blue = new StepSnapshot
        {
            Quest = q.Quest, Accepted = true, Sequence = 2, AcceptJob = Crp, Job = 36, Fighter = false, Territory = Shroud, Player = new Vector3(6, 0, -85), Objects = new[] { rat },
        };
        var ask = StepRules.Decide(q, blue, m, T0, combatJob: 36);
        Assert.Equal(StepKind.AskUser, ask.Kind);
        Assert.Contains("no actions for the class", ask.What);
    }
}
