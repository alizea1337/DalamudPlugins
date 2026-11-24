using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Provenance.Core.Tribes;
using Xunit;

namespace Provenance.Tests;

/// <summary>
/// A quest's enemies from its data, and the filter for which mobs are a quest's to fight. The cases are real dailies
/// (game data 7.58, their listener rows and Level rows as the sheets have them): a field-mob daily (1242 "Elite
/// Beating"), a survey-and-spawn daily (2261 "Getting the Shaft"), an item-on-subdued daily (1228 "Losing One's
/// Tempered") and an Ixal fight (1494 "Trust, but Verify").
/// </summary>
public class EnemyRulesTests
{
    private const uint Thanalan = 146, Forelands = 398, Shroud = 154;
    private const uint Me = 0x1000_0001;

    private static ListenerRow Row(uint listener, byte type, uint value, byte kind, byte seq) => new(listener, type, value, kind, seq);

    private static Func<uint, ScriptSpot?> Levels(params (uint Row, uint Zone, float X, float Y, float Z, uint Object)[] rows)
        => id => rows.Where(r => r.Row == id).Select(r => new ScriptSpot(r.Row, r.Zone, new Vector3(r.X, r.Y, r.Z), r.Object, r.Object == 0)).FirstOrDefault();

    // ------------------------------------------------------------------ the data of the four quests

    // 1242 "Elite Beating" (Amalj'aa): slay Zahar'ak lancers and archers, field mobs named by their BNpcName.
    private static readonly (string, uint)[] EliteArgs = { ("ACTOR0", 1005552), ("ENEMY0", 739), ("ENEMY1", 741) };
    private static readonly ListenerRow[] EliteListeners =
    {
        Row(739, 1, 1837, 5, 1), Row(741, 1, 1838, 5, 1), Row(1005552, 0, 0, 1, 1), Row(1005552, 0, 0, 1, 255),
    };
    private static readonly ScriptSpot EliteArea = new(4505574, Thanalan, new Vector3(128.9f, -0.3f, -9.6f), 0, true);
    private static readonly ScriptSpot EliteGiver = new(4496979, Thanalan, new Vector3(85.6f, 15.3f, -355.7f), 1005552, false);

    private static QuestScript Elite() => new()
    {
        Quest = 65536 + 1242, Name = "Elite Beating", Giver = 1005552, TargetEnd = 1005552, GiverSpot = EliteGiver,
        Enemies = EnemyRules.Read(EliteArgs, EliteListeners, _ => null),
        Todos = new[]
        {
            new ScriptTodo(0, 1, 0, new[] { EliteArea }, "Slay Zahar'ak lancers."),
            new ScriptTodo(1, 1, 0, new[] { EliteArea }, "Slay Zahar'ak archers."),
            new ScriptTodo(2, 255, 0, new[] { EliteGiver }, "Report to Yadovv Gah."),
        },
    };

    // 2261 "Getting the Shaft" (Vath): survey the destinations, slay the meliae that appear for their branches.
    private static readonly (string, uint)[] ShaftArgs =
    {
        ("ACTOR0", 1016803), ("EOBJECT0", 2006881), ("EOBJECT1", 2006882), ("ENEMY0", 6211294), ("ITEM0", 2001951), ("EOBJECT2", 2006883), ("ENEMY1", 6211295),
    };
    private static readonly ListenerRow[] ShaftListeners =
    {
        Row(2006881, 0, 0, 1, 1), Row(2006882, 0, 0, 1, 1), Row(5550, 3, 6211294, 9, 1), Row(2006883, 0, 0, 1, 1), Row(5550, 3, 6211295, 9, 1),
        Row(1016803, 0, 0, 1, 255),
    };
    private static readonly Func<uint, ScriptSpot?> ShaftLevels = Levels(
        (6211294, Forelands, 295.2f, -70.6f, 173.7f, 5550), (6211295, Forelands, 26.7f, -66.0f, 128.3f, 5550));

    private static QuestScript Shaft() => new()
    {
        Quest = 65536 + 2261, Name = "Getting the Shaft", Giver = 1016803, TargetEnd = 1016803,
        GiverSpot = new ScriptSpot(6209461, Forelands, new Vector3(57.3f, -47.8f, -174.6f), 1016803, false),
        Enemies = EnemyRules.Read(ShaftArgs, ShaftListeners, ShaftLevels),
        ItemObjects = EnemyRules.ItemObjects(ShaftListeners),
        Todos = new[]
        {
            new ScriptTodo(0, 1, 3, new[]
            {
                new ScriptSpot(6211296, Forelands, new Vector3(340.0f, -58.5f, -44.1f), 0, true),
                new ScriptSpot(6211297, Forelands, new Vector3(309.7f, -72.7f, 158.0f), 0, true),
                new ScriptSpot(6211298, Forelands, new Vector3(38.8f, -72.3f, 121.3f), 0, true),
            }, "Survey the designated spots and slay meliae for their branches."),
            new ScriptTodo(1, 255, 1, new[] { new ScriptSpot(6209461, Forelands, new Vector3(57.3f, -47.8f, -174.6f), 1016803, false) }, "Deliver the melia branches to the Vath keeneye."),
        },
    };

    // 1228 "Losing One's Tempered" (Amalj'aa): use the tempering grounds, subdue the tempered that appear, use the
    // sacred ash on them. The item rows (kind 8) are on the enemies' kinds only.
    private static readonly (string, uint)[] TemperedArgs =
    {
        ("ACTOR0", 1005550), ("ITEM0", 2001191), ("EOBJECT0", 2003166), ("ENEMY0", 4553358), ("ENEMY1", 4553359), ("ENEMY2", 4553360), ("ENEMY3", 4553361),
        ("ENEMY4", 4553362), ("ENEMY5", 4553363), ("ENEMY6", 4553365), ("EOBJECT1", 2003167), ("EOBJECT2", 2003168),
    };
    private static readonly ListenerRow[] TemperedListeners =
    {
        Row(2003166, 0, 0, 1, 1), Row(79, 3, 4553358, 5, 1), Row(79, 0, 0, 8, 1), Row(79, 0, 0, 8, 1), Row(80, 0, 0, 8, 1), Row(80, 0, 0, 8, 1), Row(80, 0, 0, 8, 1),
        Row(81, 0, 0, 8, 1), Row(81, 0, 0, 8, 1), Row(2003167, 0, 0, 1, 1), Row(79, 3, 4553359, 5, 1), Row(2003168, 0, 0, 1, 1), Row(80, 3, 4553360, 5, 1),
        Row(2003169, 0, 0, 1, 1), Row(80, 3, 4553361, 5, 1), Row(2003170, 0, 0, 1, 1), Row(80, 3, 4553362, 5, 1), Row(2003171, 0, 0, 1, 1), Row(81, 3, 4553363, 5, 1),
        Row(2003172, 0, 0, 1, 1), Row(81, 3, 4553365, 5, 1), Row(1005550, 0, 0, 1, 1), Row(1005550, 0, 0, 1, 255),
    };
    private static readonly Func<uint, ScriptSpot?> TemperedLevels = Levels(
        (4553358, Thanalan, -76.7f, -2.4f, -105.7f, 79), (4553359, Thanalan, -84.2f, -4.8f, -106.1f, 79), (4553360, Thanalan, -160.2f, 4.3f, -136.9f, 80),
        (4553361, Thanalan, -198.6f, 4.4f, -156.7f, 80), (4553362, Thanalan, -152.7f, 6.7f, -51.8f, 80), (4553363, Thanalan, -21.7f, 1.4f, -28.1f, 81),
        (4553365, Thanalan, -19.7f, 1.0f, 19.8f, 81));
    private static readonly KeyItemFact SacredAsh = new(2001191, "Sacred Ash", 1843, false, false, 3, 3, new[] { "Sacred Ash", "sacred ash" });

    private static QuestScript Tempered() => new()
    {
        Quest = 65536 + 1228, Name = "Losing One's Tempered", Giver = 1005550, TargetEnd = 1005550,
        Enemies = EnemyRules.Read(TemperedArgs, TemperedListeners, TemperedLevels),
        ItemObjects = EnemyRules.ItemObjects(TemperedListeners),
        KeyItems = new[] { SacredAsh },
        Todos = new[]
        {
            new ScriptTodo(0, 1, 3, new[] { new ScriptSpot(4553373, Thanalan, new Vector3(-75.1f, -10.6f, -105.3f), 0, true) },
                "Head to the tempering ground in northern Zanr'ak, and use the sacred ash on subdued tempered."),
            new ScriptTodo(1, 255, 1, new[] { new ScriptSpot(4496854, Thanalan, new Vector3(105.3f, 15.4f, -357.4f), 1005550, false) }, "Report to Fibubb Gah."),
        },
    };

    // 1494 "Trust, but Verify" (Ixal, accepted as a crafter): at sequence 2 the beans are used on a marked territory
    // (the item rows are on the objects), an opo-opo appears and is slain for its claw.
    private static readonly (string, uint)[] TrustArgs =
    {
        ("ACTOR0", 1009211), ("ITEM0", 2001373), ("ACTOR1", 1000656), ("ITEM1", 2001374), ("EOBJECT0", 2004452), ("ENEMY0", 4890723), ("ITEM2", 2001375),
        ("EOBJECT1", 2004453), ("ENEMY1", 4890724), ("EOBJECT2", 2004454), ("ENEMY2", 4890725), ("EOBJECT3", 2004455), ("ENEMY3", 4890726), ("RITEM0", 8052),
        ("STATUS0", 547), ("RITEM1", 8091),
    };
    private static readonly ListenerRow[] TrustListeners =
    {
        Row(1000656, 0, 0, 1, 1), Row(1009211, 0, 0, 1, 1), Row(2004452, 0, 0, 1, 2), Row(2004452, 0, 0, 8, 2), Row(3239, 3, 4890723, 9, 2), Row(1000656, 0, 0, 1, 2),
        Row(1009211, 0, 0, 1, 2), Row(2004453, 0, 0, 1, 2), Row(2004453, 0, 0, 8, 2), Row(3239, 3, 4890724, 9, 2), Row(2004454, 0, 0, 1, 2), Row(2004454, 0, 0, 8, 2),
        Row(3239, 3, 4890725, 9, 2), Row(2004455, 0, 0, 1, 2), Row(2004455, 0, 0, 8, 2), Row(3239, 3, 4890726, 9, 2), Row(1000656, 0, 0, 1, 3), Row(1009211, 0, 0, 1, 3),
        Row(1009211, 0, 0, 1, 255), Row(1000656, 0, 0, 1, 255),
    };
    private static readonly Func<uint, ScriptSpot?> TrustLevels = Levels(
        (4890723, Shroud, 325.1f, -13.9f, 180.2f, 3239), (4890724, Shroud, 303.3f, -18.9f, 190.2f, 3239), (4890725, Shroud, 319.6f, -16.1f, 206.5f, 3239),
        (4890726, Shroud, 323.6f, -14.1f, 196.5f, 3239));

    private static QuestScript Trust() => new()
    {
        Quest = 65536 + 1494, Name = "Trust, but Verify", Giver = 1009211, TargetEnd = 1009211,
        Enemies = EnemyRules.Read(TrustArgs, TrustListeners, TrustLevels),
        ItemObjects = EnemyRules.ItemObjects(TrustListeners),
    };

    // ------------------------------------------------------------------ reading the data

    [Fact]
    public void AFieldMobDailyNamesItsMobsByKindAndName()
    {
        var enemies = Elite().Enemies;
        Assert.Equal(2, enemies.Count);
        Assert.Contains(new QuestEnemy(739, 1837, 1, true, EnemyTask.Defeat), enemies);
        Assert.Contains(new QuestEnemy(741, 1838, 1, true, EnemyTask.Defeat), enemies);
        Assert.True(EnemyRules.Fights(Elite(), 1));
        Assert.False(EnemyRules.Fights(Elite(), 255));
    }

    [Fact]
    public void ASurveyAndSpawnDailyHasItsEnemiesAtTheirSpawnPoints()
    {
        var enemies = Shaft().Enemies;
        Assert.Equal(2, enemies.Count);
        Assert.All(enemies, e =>
        {
            Assert.Equal(5550u, e.Base);
            Assert.False(e.Field);
            Assert.Equal(EnemyTask.DefeatForItem, e.Task);
            Assert.Equal(1, e.Sequence);
            Assert.Equal(Forelands, e.Spawn!.Territory);
        });
        Assert.Equal(new uint[] { 6211294, 6211295 }, enemies.Select(e => e.Spawn!.Level));
        Assert.Equal(new Vector3(295.2f, -70.6f, 173.7f), enemies[0].Spawn!.Position);
        // The pliant melia branch is picked up, not used: no object takes a key item.
        Assert.Empty(Shaft().ItemObjects);
    }

    [Fact]
    public void AnItemOnSubduedDailyHasTheItemOnItsEnemiesOnly()
    {
        var q = Tempered();
        // Seven spawn points over three kinds, and the item on each kind.
        Assert.Equal(7, q.Enemies.Count(e => e.Task == EnemyTask.Defeat && e.Spawn != null));
        Assert.Equal(new uint[] { 79, 80, 81 }, q.Enemies.Where(e => e.Task == EnemyTask.UseItem).Select(e => e.Base).OrderBy(b => b));
        Assert.All(q.Enemies, e => Assert.False(e.Field));
        Assert.All(q.Enemies, e => Assert.Equal(1, e.Sequence));
        // The tempering grounds are used (kind 1), the ash isn't for them.
        Assert.Empty(q.ItemObjects);
    }

    [Fact]
    public void AnIxalFightIsAtItsOwnSequenceAndItsItemGoesOnObjects()
    {
        var q = Trust();
        Assert.Equal(4, q.Enemies.Count);
        Assert.All(q.Enemies, e =>
        {
            Assert.Equal(3239u, e.Base);
            Assert.Equal(2, e.Sequence);
            Assert.Equal(EnemyTask.DefeatForItem, e.Task);
            Assert.Equal(Shroud, e.Spawn!.Territory);
        });
        Assert.False(EnemyRules.Fights(q, 1));
        Assert.True(EnemyRules.Fights(q, 2));
        Assert.False(EnemyRules.Fights(q, 3));
        Assert.False(EnemyRules.Fights(q, 255));
        // The beans go on the four marked territories, at sequence 2.
        Assert.Equal(new uint[] { 2004452, 2004453, 2004454, 2004455 }, q.ItemObjects.Select(o => o.Object).OrderBy(o => o));
        Assert.All(q.ItemObjects, o => Assert.Equal(2, o.Sequence));
    }

    [Fact]
    public void ADefeatWithNoConditionCountsAnyMobOfTheKind()
    {
        // 1276 "Fungal Foulness": greater banestools (2464), no name, no spawn point.
        var enemies = EnemyRules.Read(new[] { ("ENEMY0", 2464u) }, new[] { Row(2464, 0, 0, 5, 1) }, _ => null);
        Assert.Equal(new[] { new QuestEnemy(2464, 0, 1, true, EnemyTask.Defeat) }, enemies);
    }

    [Fact]
    public void ADefeatWithNoConditionForAKindTheQuestPlacesIsNotAFieldMob()
    {
        // 1339 "Operation Riffraff": kind 2840 has a plain defeat row and seven spawn points at sequence 4.
        var levels = Levels((4627502, 180, 10, 0, 10, 2840), (4627503, 180, 20, 0, 20, 2840));
        var enemies = EnemyRules.Read(new[] { ("ENEMY0", 2840u), ("ENEMY1", 4627502u), ("ENEMY2", 4627503u) },
            new[] { Row(2840, 0, 0, 5, 4), Row(2840, 3, 4627502, 5, 4), Row(2840, 3, 4627503, 5, 4) }, levels);
        Assert.Equal(2, enemies.Count);
        Assert.All(enemies, e => Assert.False(e.Field));
    }

    [Fact]
    public void AnItemOnAFieldMobIsAFieldLineWithTheItemTask()
    {
        // 1516 "Hot Cross Buns" (Ixal): the shearing knife on a feral croc (395, name 784).
        var enemies = EnemyRules.Read(new[] { ("ENEMY0", 395u) }, new[] { Row(395, 1, 784, 8, 1) }, _ => null);
        Assert.Equal(new[] { new QuestEnemy(395, 784, 1, true, EnemyTask.UseItem) }, enemies);
    }

    [Fact]
    public void ScriptArgumentsFillInWhatNoListenerNames()
    {
        var levels = Levels((4000001, 146, 5, 0, 5, 77));
        var enemies = EnemyRules.Read(new[] { ("ENEMY0", 55u), ("ENEMY1", 4000001u), ("ENEMY_ID", 9u), ("ACTOR0", 1000001u) }, Array.Empty<ListenerRow>(), levels);
        Assert.Equal(2, enemies.Count);
        // A kind only the arguments name is known, but not a field mob: it's fought when the quest marks it or it attacks.
        Assert.Contains(new QuestEnemy(55, 0, 0, false, EnemyTask.Defeat), enemies);
        Assert.Contains(enemies, e => e.Base == 77 && e.Spawn?.Level == 4000001 && e.Sequence == 0);
        // Without a sequence in the data: active at any but the report.
        Assert.True(EnemyRules.ActiveIn(enemies[0], 1));
        Assert.False(EnemyRules.ActiveIn(enemies[0], 255));
    }

    [Fact]
    public void OtherListenersAreNotEnemies()
    {
        // NPCs, objects, areas (5,000,000) and kinds the engine doesn't know.
        var enemies = EnemyRules.Read(Array.Empty<(string, uint)>(),
            new[] { Row(1005552, 0, 0, 1, 1), Row(2003166, 0, 0, 8, 1), Row(5000000, 3, 7109002, 10, 1), Row(739, 1, 1837, 1, 1), Row(0, 0, 0, 5, 1) }, _ => null);
        Assert.Empty(enemies);
    }

    // ------------------------------------------------------------------ the filter

    private static StepEnemy Mob(ulong id, uint baseId, uint name, float x, float z, bool hostile = true, EnemyTarget target = EnemyTarget.None, bool combat = false,
        uint icon = 0, uint drawn = 0, bool held = false, uint fate = 0, bool weakened = false, bool targetable = true)
        => new(id, baseId, name, $"mob {baseId}", new Vector3(x, 0, z))
        {
            Hostile = hostile, Target = target, InCombat = combat || target != EnemyTarget.None, Icon = icon, Drawn = drawn, Held = held, Fate = fate, Weakened = weakened,
            Targetable = targetable,
        };

    private static StepSnapshot Snap(QuestScript q, byte seq, uint zone, Vector3 me, IEnumerable<StepEnemy> mobs, IEnumerable<StepMarker>? markers = null,
        Dictionary<uint, int>? items = null)
        => new()
        {
            Quest = q.Quest, Accepted = true, Sequence = seq, AcceptJob = 19, Job = 19, Fighter = true, Territory = zone, Player = me,
            Hostiles = mobs.ToList(), Markers = markers?.ToList() ?? new List<StepMarker>(), Items = items ?? new Dictionary<uint, int>(),
        };

    private static readonly StepMarker EliteMarker = new(Thanalan, new Vector3(128.9f, -0.3f, -9.6f), 125, 60493, 4505574, 0);
    private static readonly Vector3 InZaharak = new(120, 0, 0);

    private static EnemyVerdict Verdict(QuestScript q, StepSnapshot s, StepEnemy e) => EnemyRules.Judge(q, s, e).Verdict;

    [Fact]
    public void WhateverAttacksIsAlwaysFought()
    {
        var q = Elite();
        var stranger = Mob(1, 9999, 1, 125, 0, target: EnemyTarget.You);
        var atChocobo = Mob(2, 9999, 1, 125, 0, target: EnemyTarget.YourChocobo);
        var fateMob = Mob(3, 9999, 1, 125, 0, target: EnemyTarget.You, fate: 120);
        var s = Snap(q, 1, Thanalan, InZaharak, new[] { stranger, atChocobo, fateMob });
        Assert.Equal(EnemyVerdict.Attacker, Verdict(q, s, stranger));
        Assert.Equal(EnemyVerdict.Attacker, Verdict(q, s, atChocobo));
        Assert.Equal(EnemyVerdict.Attacker, Verdict(q, s, fateMob));
        // Even at the report, where the quest has no enemies of its own.
        Assert.Equal(EnemyVerdict.Attacker, Verdict(q, Snap(q, 255, Thanalan, InZaharak, new[] { stranger }), stranger));
    }

    [Fact]
    public void AnEnemyTheRunIsFightingStaysInTheFightWhileItFightsNobodyElse()
    {
        // A quest spawn nothing marks, fought because it attacked: between two attacks it has no target.
        var q = Shaft();
        var melia = Mob(5, 5550, 4100, 296, 174, combat: true);
        var s = Snap(q, 1, Forelands, new Vector3(300, -70, 170), new[] { melia });
        var m = new StepMemory();
        Assert.Equal(EnemyVerdict.No, EnemyRules.Judge(q, s, melia, m).Verdict);
        m.Fought.Add(5);
        Assert.Equal(EnemyVerdict.Attacker, EnemyRules.Judge(q, s, melia, m).Verdict);
        // It turned on someone else, or went back to where it stood (out of combat): no longer the run's.
        var turned = melia with { Target = EnemyTarget.OtherPlayer };
        Assert.Equal(EnemyVerdict.No, EnemyRules.Judge(q, Snap(q, 1, Forelands, new Vector3(300, -70, 170), new[] { turned }), turned, m).Verdict);
        var reset = melia with { InCombat = false };
        Assert.Equal(EnemyVerdict.No, EnemyRules.Judge(q, Snap(q, 1, Forelands, new Vector3(300, -70, 170), new[] { reset }), reset, m).Verdict);
    }

    [Fact]
    public void AStrangerThatDoesntAttackIsLeftAlone()
    {
        var q = Elite();
        var stranger = Mob(1, 9999, 1, 125, 0);
        var elsewhere = Mob(2, 9999, 1, 125, 0, target: EnemyTarget.OtherPlayer);
        var s = Snap(q, 1, Thanalan, InZaharak, new[] { stranger, elsewhere }, new[] { EliteMarker });
        Assert.Equal(EnemyVerdict.No, Verdict(q, s, stranger));
        Assert.Equal(EnemyVerdict.No, Verdict(q, s, elsewhere));
    }

    [Fact]
    public void AFieldMobCountsByKindAndNameInsideTheMarkedArea()
    {
        var q = Elite();
        var lancer = Mob(1, 739, 1837, 130, 10);
        var archer = Mob(2, 741, 1838, 140, -20);
        var s = Snap(q, 1, Thanalan, InZaharak, new[] { lancer, archer }, new[] { EliteMarker });
        Assert.Equal(EnemyVerdict.FieldMob, Verdict(q, s, lancer));
        Assert.Equal(EnemyVerdict.FieldMob, Verdict(q, s, archer));
    }

    [Fact]
    public void TheSameKindUnderAnotherNameIsNotTheQuests()
    {
        // An Amalj'aa lancer shares the Zahar'ak lancer's kind (739); so does a Zanr'ak lancer with another quest's icon.
        var q = Elite();
        var amaljaa = Mob(1, 739, 1859, 130, 10);
        var othersQuest = Mob(2, 739, 1860, 131, 10, drawn: 71244);
        var s = Snap(q, 1, Thanalan, InZaharak, new[] { amaljaa, othersQuest }, new[] { EliteMarker });
        Assert.Equal(EnemyVerdict.No, Verdict(q, s, amaljaa));
        Assert.Equal(EnemyVerdict.No, Verdict(q, s, othersQuest));
        Assert.Contains("name", EnemyRules.Judge(q, s, amaljaa).Why);
    }

    [Fact]
    public void AFieldMobSomeoneElseFightsIsTheirs()
    {
        var q = Elite();
        var taken = Mob(1, 739, 1837, 130, 10, target: EnemyTarget.OtherPlayer);
        var byNpc = Mob(2, 739, 1837, 130, 12, target: EnemyTarget.Other);
        var s = Snap(q, 1, Thanalan, InZaharak, new[] { taken, byNpc }, new[] { EliteMarker });
        Assert.Equal(EnemyVerdict.No, Verdict(q, s, taken));
        Assert.Equal(EnemyVerdict.No, Verdict(q, s, byNpc));
    }

    [Fact]
    public void AFateMobOfTheQuestsKindIsLeftAlone()
    {
        var q = Elite();
        var fate = Mob(1, 739, 1837, 130, 10, fate: 404, drawn: 60093);
        Assert.Equal(EnemyVerdict.No, Verdict(q, Snap(q, 1, Thanalan, InZaharak, new[] { fate }, new[] { EliteMarker }), fate));
    }

    [Fact]
    public void AFieldMobOutsideTheMarkedAreaIsLeftAlone()
    {
        var q = Elite();
        var far = Mob(1, 739, 1837, 128.9f + 125 + 80, -9.6f);
        var edge = Mob(2, 739, 1837, 128.9f + 125 + 40, -9.6f);
        var s = Snap(q, 1, Thanalan, new Vector3(250, 0, 0), new[] { far, edge }, new[] { EliteMarker });
        Assert.Equal(EnemyVerdict.No, Verdict(q, s, far));
        // A little past the edge still counts (the circle is drawn by hand around where they roam).
        Assert.Equal(EnemyVerdict.FieldMob, Verdict(q, s, edge));
    }

    [Fact]
    public void WithoutALiveMarkerTheToDoLinesAreaIsGoneBy()
    {
        var q = Elite();
        var near = Mob(1, 739, 1837, 200, 50);
        var far = Mob(2, 739, 1837, 128.9f, -9.6f - 220);
        var s = Snap(q, 1, Thanalan, new Vector3(128, 0, -100), new[] { near, far });
        Assert.Equal(EnemyVerdict.FieldMob, Verdict(q, s, near));
        Assert.Equal(EnemyVerdict.No, Verdict(q, s, far));
    }

    [Fact]
    public void AtAnotherSequenceTheQuestsMobsAreLeftAlone()
    {
        var q = Elite();
        var lancer = Mob(1, 739, 1837, 130, 10);
        var s = Snap(q, 255, Thanalan, InZaharak, new[] { lancer }, new[] { EliteMarker });
        Assert.Equal(EnemyVerdict.No, Verdict(q, s, lancer));
        Assert.Contains("not at this step", EnemyRules.Judge(q, s, lancer).Why);
    }

    [Fact]
    public void CloseByAFieldMobWithoutTheIconAmongOthersThatHaveItIsNotWanted()
    {
        // The lancers' line is done: the archers still carry the quest's icon, the lancers don't.
        var q = Elite();
        var lancer = Mob(1, 739, 1837, 130, 10);
        var archer = Mob(2, 741, 1838, 135, 5, drawn: 71224);
        var farLancer = Mob(3, 739, 1837, 190, 10);
        var s = Snap(q, 1, Thanalan, InZaharak, new[] { lancer, archer, farLancer }, new[] { EliteMarker });
        Assert.Equal(EnemyVerdict.Marked, Verdict(q, s, archer));
        Assert.Equal(EnemyVerdict.No, Verdict(q, s, lancer));
        // Further off than icons are drawn, the kind and name are all there is to go by.
        Assert.Equal(EnemyVerdict.FieldMob, Verdict(q, s, farLancer));
    }

    [Fact]
    public void AKindWhoseDefeatsStoppedCountingIsLeftForTheQuestsOtherKinds()
    {
        // The lancers' line is done and nothing marks field mobs: two lancers fell without the quest moving on.
        var q = Elite();
        var lancer = Mob(1, 739, 1837, 180, 0);
        var archer = Mob(2, 741, 1838, 190, 0);
        var s = Snap(q, 1, Thanalan, InZaharak, new[] { lancer, archer }, new[] { EliteMarker });
        var m = new StepMemory { DefeatsCount = true };
        m.IdleDefeats[739] = EnemyRules.IdleDefeats - 1;
        Assert.Equal(1ul, EnemyRules.Next(q, s, m)!.Id);
        m.IdleDefeats[739] = EnemyRules.IdleDefeats;
        // Not before a defeat was seen to count at all (the quest's state may not show defeats).
        Assert.Equal(EnemyVerdict.FieldMob, EnemyRules.Judge(q, s, lancer, new StepMemory { IdleDefeats = { [739] = 5 } }).Verdict);
        Assert.Equal(EnemyVerdict.No, EnemyRules.Judge(q, s, lancer, m).Verdict);
        Assert.Equal(2ul, EnemyRules.Next(q, s, m)!.Id);
        // One that attacks is fought all the same.
        var biting = lancer with { Target = EnemyTarget.You, InCombat = true };
        Assert.Equal(EnemyVerdict.Attacker, EnemyRules.Judge(q, s, biting, m).Verdict);
    }

    [Fact]
    public void AQuestsOnlyKindIsNeverLeftAloneForDefeatsThatDidntCount()
    {
        // 1276 "Fungal Foulness": greater banestools are all there is; the idle count is the stepper's to ask about.
        var q = new QuestScript { Quest = 65536 + 1276, Enemies = EnemyRules.Read(new[] { ("ENEMY0", 2464u) }, new[] { Row(2464, 0, 0, 5, 1) }, _ => null) };
        var banestool = Mob(1, 2464, 3000, 10, 60);
        var m = new StepMemory { DefeatsCount = true };
        m.IdleDefeats[2464] = 5;
        Assert.Equal(EnemyVerdict.FieldMob, EnemyRules.Judge(q, Snap(q, 1, 152, Vector3.Zero, new[] { banestool }), banestool, m).Verdict);
    }

    [Fact]
    public void AnUntargetableMobIsNeverFought()
    {
        var q = Elite();
        var gone = Mob(1, 739, 1837, 130, 10, target: EnemyTarget.You, targetable: false);
        Assert.Equal(EnemyVerdict.No, Verdict(q, Snap(q, 1, Thanalan, InZaharak, new[] { gone }, new[] { EliteMarker }), gone));
    }

    private static readonly Vector3 AtSurvey = new(300, -70, 170);

    [Fact]
    public void AQuestSpawnIsFoughtWhenTheQuestMarksIt()
    {
        var q = Shaft();
        var ownIcon = Mob(1, 5550, 4100, 296, 174, icon: 71224);
        var drawn = Mob(2, 5550, 4100, 297, 175, drawn: 71224);
        var held = Mob(3, 5550, 4100, 298, 176, held: true);
        var s = Snap(q, 1, Forelands, AtSurvey, new[] { ownIcon, drawn, held });
        Assert.Equal(EnemyVerdict.Marked, Verdict(q, s, ownIcon));
        Assert.Equal(EnemyVerdict.Marked, Verdict(q, s, drawn));
        Assert.Equal(EnemyVerdict.Marked, Verdict(q, s, held));
    }

    [Fact]
    public void AMobOfTheQuestsKindThatNothingMarksIsNeverAttacked()
    {
        // A young melia of the open world shares the quest spawn's kind: no icon, not held, not attacking.
        var q = Shaft();
        var wild = Mob(1, 5550, 4100, 296, 174);
        var s = Snap(q, 1, Forelands, AtSurvey, new[] { wild });
        Assert.Equal(EnemyVerdict.No, Verdict(q, s, wild));
        Assert.Contains("doesn't mark", EnemyRules.Judge(q, s, wild).Why);
    }

    [Fact]
    public void HeldAloneCountsOnlyWhereTheQuestPutsItsMobs()
    {
        // Should the handler hold every loaded mob of a kind it listens to, "held" far from any spawn point says nothing.
        var q = Shaft();
        var far = Mob(1, 5550, 4100, 500, 500, held: true);
        Assert.Equal(EnemyVerdict.No, Verdict(q, Snap(q, 1, Forelands, new Vector3(490, 0, 490), new[] { far }), far));
    }

    [Fact]
    public void AMarkedMobSomeoneElseFightsIsLeftAlone()
    {
        var q = Shaft();
        var theirs = Mob(1, 5550, 4100, 296, 174, drawn: 71224, target: EnemyTarget.OtherPlayer);
        Assert.Equal(EnemyVerdict.No, Verdict(q, Snap(q, 1, Forelands, AtSurvey, new[] { theirs }), theirs));
    }

    [Fact]
    public void FateIconsAreNotAQuestsMark()
    {
        Assert.False(EnemyRules.QuestIcon(0));
        Assert.False(EnemyRules.QuestIcon(60093));
        Assert.False(EnemyRules.QuestIcon(60732));
        Assert.True(EnemyRules.QuestIcon(71224));
    }

    // ------------------------------------------------------------------ the quest's item

    private static readonly Vector3 AtGround = new(-78, -3, -104);
    private static readonly Dictionary<uint, int> Ash = new() { [2001191] = 3 };

    [Fact]
    public void TheItemGoesOnASubduedEnemyNotOnOneStillFighting()
    {
        var q = Tempered();
        var fighting = Mob(1, 79, 2000, -77, -105, target: EnemyTarget.You);
        var subdued = fighting with { Weakened = true };
        var m = new StepMemory();
        Assert.Null(EnemyRules.ItemNow(q, Snap(q, 1, Thanalan, AtGround, new[] { fighting }, items: Ash), fighting, m));
        Assert.Equal(SacredAsh, EnemyRules.ItemNow(q, Snap(q, 1, Thanalan, AtGround, new[] { subdued }, items: Ash), subdued, m));
        // Not without the item in the bags.
        Assert.Null(EnemyRules.ItemNow(q, Snap(q, 1, Thanalan, AtGround, new[] { subdued }), subdued, m));
    }

    [Fact]
    public void TheItemIsTriedThreeTimesPerEnemyAtMost()
    {
        var q = Tempered();
        var subdued = Mob(1, 79, 2000, -77, -105, target: EnemyTarget.You, weakened: true);
        var s = Snap(q, 1, Thanalan, AtGround, new[] { subdued }, items: Ash);
        var m = new StepMemory();
        m.ItemUses[1] = EnemyRules.ItemTries - 1;
        Assert.NotNull(EnemyRules.ItemNow(q, s, subdued, m));
        m.ItemUses[1] = EnemyRules.ItemTries;
        Assert.Null(EnemyRules.ItemNow(q, s, subdued, m));
    }

    [Fact]
    public void AFightThatStalledTriesTheItem()
    {
        var q = Tempered();
        var fighting = Mob(1, 79, 2000, -77, -105, target: EnemyTarget.You);
        var s = Snap(q, 1, Thanalan, AtGround, new[] { fighting }, items: Ash);
        Assert.NotNull(EnemyRules.ItemNow(q, s, fighting, new StepMemory(), stalled: true));
        // Never on a kind the quest names no item for.
        var stranger = Mob(2, 9999, 1, -77, -105, target: EnemyTarget.You);
        Assert.Null(EnemyRules.ItemNow(q, s, stranger, new StepMemory(), stalled: true));
    }

    [Fact]
    public void AnItemThrownFromAfarGoesFirstOnce()
    {
        // 3056 "Keeping an Eye off Things" (Ananta): the blood red eye drops (their action reaches 15 yalms) on the
        // formidable foper (8590), then it's slain.
        var drops = new KeyItemFact(2002438, "Blood Red Eye Drops", 306, false, false, 15, 1, new[] { "Blood Red Eye Drops" });
        var q = new QuestScript
        {
            Quest = 65536 + 3056, KeyItems = new[] { drops },
            Enemies = EnemyRules.Read(new[] { ("ENEMY0", 7230983u) }, new[] { Row(8590, 3, 7230983, 5, 1), Row(8590, 0, 0, 8, 1) }, Levels((7230983, 612, -56, 38, -265, 8590))),
        };
        var foper = Mob(1, 8590, 7000, -55, -266, drawn: 71224);
        var s = Snap(q, 1, 612, new Vector3(-50, 38, -260), new[] { foper }, items: new() { [2002438] = 1 });
        var m = new StepMemory();
        Assert.Equal(drops, EnemyRules.ItemNow(q, s, foper, m));
        m.ItemUses[1] = 1;
        Assert.Null(EnemyRules.ItemNow(q, s, foper, m));
    }

    [Fact]
    public void AnEnemyOnlyTheItemIsWantedOfNeedsTheItemInTheBags()
    {
        // 1516: the shearing knife on a feral croc; nothing says to slay it.
        var knife = new KeyItemFact(2001403, "Shearing Knife", 1, false, false, 3, 3, new[] { "Shearing Knife" });
        var q = new QuestScript { Quest = 65536 + 1516, KeyItems = new[] { knife }, Enemies = EnemyRules.Read(new[] { ("ENEMY0", 395u) }, new[] { Row(395, 1, 784, 8, 1) }, _ => null) };
        var croc = Mob(1, 395, 784, 10, 10);
        var without = Snap(q, 1, 155, Vector3.Zero, new[] { croc });
        var with = Snap(q, 1, 155, Vector3.Zero, new[] { croc }, items: new() { [2001403] = 1 });
        Assert.Equal(EnemyVerdict.No, Verdict(q, without, croc));
        Assert.Equal(EnemyVerdict.FieldMob, Verdict(q, with, croc));
        // The knife first: the quest wants nothing else of the croc.
        Assert.Equal(knife, EnemyRules.ItemNow(q, with, croc, new StepMemory()));
    }

    [Fact]
    public void ASubduedEnemyThatFightsNoMoreStillGetsTheItem()
    {
        // Subdued, it stops attacking (and may stop counting as hostile): the one the run fought stays the run's.
        var q = Tempered();
        var subdued = Mob(1, 79, 2000, -77, -105, hostile: false, weakened: true);
        var stillHostile = Mob(3, 79, 2000, -78, -105, weakened: true);
        var passive = Mob(2, 79, 2000, -77, -106, hostile: false);
        var s = Snap(q, 1, Thanalan, AtGround, new[] { subdued, stillHostile, passive }, items: Ash);
        var fought = new StepMemory();
        fought.Fought.Add(1);
        fought.Fought.Add(3);
        Assert.Equal(EnemyVerdict.Subdued, EnemyRules.Judge(q, s, subdued, fought).Verdict);
        Assert.Equal(EnemyVerdict.Subdued, EnemyRules.Judge(q, s, stillHostile, fought).Verdict);
        Assert.Equal(1ul, EnemyRules.Next(q, s, fought, keep: 1)!.Id);
        Assert.Equal(EnemyVerdict.No, Verdict(q, s, passive));
        // With no ash left there's nothing to do with it.
        Assert.Equal(EnemyVerdict.No, EnemyRules.Judge(q, Snap(q, 1, Thanalan, AtGround, new[] { subdued }), subdued, fought).Verdict);
    }

    [Fact]
    public void SomeoneElsesSubduedEnemyIsLeftAlone()
    {
        // Another player's tempered, subdued at the same spawn point: the run didn't fight it and the quest doesn't mark it.
        var q = Tempered();
        var theirs = Mob(1, 79, 2000, -77, -105, weakened: true);
        var s = Snap(q, 1, Thanalan, AtGround, new[] { theirs }, items: Ash);
        Assert.Equal(EnemyVerdict.No, EnemyRules.Judge(q, s, theirs, new StepMemory()).Verdict);
        Assert.Contains("not by the run", EnemyRules.Judge(q, s, theirs, new StepMemory()).Why);
        // One the quest marks is the run's even when it was found subdued (after a knock-out, say).
        var marked = theirs with { Drawn = 71224 };
        Assert.Equal(EnemyVerdict.Subdued, EnemyRules.Judge(q, Snap(q, 1, Thanalan, AtGround, new[] { marked }, items: Ash), marked, new StepMemory()).Verdict);
    }

    // ------------------------------------------------------------------ which one next

    [Fact]
    public void WhatAttacksComesBeforeTheQuestsMarkedOnesAndThoseBeforeFieldMobs()
    {
        var q = Elite();
        var field = Mob(1, 739, 1837, 121, 0);
        var marked = Mob(2, 741, 1838, 190, 0, drawn: 71224);
        var attacker = Mob(3, 9999, 1, 160, 0, target: EnemyTarget.You);
        var m = new StepMemory();
        Assert.Equal(3ul, EnemyRules.Next(q, Snap(q, 1, Thanalan, InZaharak, new[] { field, marked, attacker }, new[] { EliteMarker }), m)!.Id);
        // (The field mob is further off than icons are drawn, so it still counts.)
        var farField = Mob(1, 739, 1837, 180, 0);
        Assert.Equal(2ul, EnemyRules.Next(q, Snap(q, 1, Thanalan, InZaharak, new[] { farField, marked }, new[] { EliteMarker }), m)!.Id);
        Assert.Equal(1ul, EnemyRules.Next(q, Snap(q, 1, Thanalan, InZaharak, new[] { farField }, new[] { EliteMarker }), m)!.Id);
        Assert.Null(EnemyRules.Next(q, Snap(q, 1, Thanalan, InZaharak, Array.Empty<StepEnemy>(), new[] { EliteMarker }), m));
    }

    [Fact]
    public void TheNearestOfAKindComesFirst()
    {
        var q = Elite();
        var far = Mob(1, 739, 1837, 190, 0);
        var near = Mob(2, 739, 1837, 170, 0);
        Assert.Equal(2ul, EnemyRules.Next(q, Snap(q, 1, Thanalan, InZaharak, new[] { far, near }, new[] { EliteMarker }), new StepMemory())!.Id);
    }

    [Fact]
    public void TheEnemyBeingFoughtStaysTheTargetUnlessSomethingElseAttacks()
    {
        var q = Elite();
        var fought = Mob(1, 739, 1837, 190, 0);
        var nearer = Mob(2, 739, 1837, 170, 0);
        var m = new StepMemory();
        var s = Snap(q, 1, Thanalan, InZaharak, new[] { fought, nearer }, new[] { EliteMarker });
        Assert.Equal(1ul, EnemyRules.Next(q, s, m, keep: 1)!.Id);
        // It doesn't attack, another does: that one first.
        var attacker = Mob(3, 9999, 1, 125, 0, target: EnemyTarget.You);
        Assert.Equal(3ul, EnemyRules.Next(q, Snap(q, 1, Thanalan, InZaharak, new[] { fought, nearer, attacker }, new[] { EliteMarker }), m, keep: 1)!.Id);
        // Two attack: the one being fought is finished first.
        var foughtAttacker = Mob(1, 739, 1837, 190, 0, target: EnemyTarget.You);
        Assert.Equal(1ul, EnemyRules.Next(q, Snap(q, 1, Thanalan, InZaharak, new[] { foughtAttacker, attacker }, new[] { EliteMarker }), m, keep: 1)!.Id);
    }

    [Fact]
    public void EnemiesGivenUpOnAreLeftOut()
    {
        var q = Elite();
        var attacker = Mob(3, 9999, 1, 125, 0, target: EnemyTarget.You);
        var m = new StepMemory();
        m.GivenUp.Add(3);
        Assert.Null(EnemyRules.Next(q, Snap(q, 1, Thanalan, InZaharak, new[] { attacker }, new[] { EliteMarker }), m));
    }

    // ------------------------------------------------------------------ where to look

    [Fact]
    public void WithNoneInViewTheSpawnPointsAreGoneToNearestFirstEachOnce()
    {
        var q = Shaft();
        var s = Snap(q, 1, Forelands, new Vector3(30, -66, 120), Array.Empty<StepEnemy>());
        var m = new StepMemory();
        var first = EnemyRules.Search(q, s, m)!;
        Assert.Equal(StepKind.Go, first.Kind);
        Assert.Equal(6211295u, first.Level);
        Assert.Equal(EnemyRules.SpawnWithin, first.Within);
        m.Looked.Add(first.Level);
        Assert.Equal(6211294u, EnemyRules.Search(q, s, m)!.Level);
        m.Looked.Add(6211294);
        Assert.Null(EnemyRules.Search(q, s, m));
    }

    [Fact]
    public void FieldMobsAreLookedForAroundTheMarkedAreaTwiceRound()
    {
        var q = Elite();
        var s = Snap(q, 1, Thanalan, InZaharak, Array.Empty<StepEnemy>(), new[] { EliteMarker });
        var m = new StepMemory();
        var seen = new List<Vector3>();
        for (var i = 0; i < EnemyRules.RingPoints * EnemyRules.RingRounds; i++)
        {
            var go = EnemyRules.Search(q, s, m);
            Assert.NotNull(go);
            Assert.Equal(StepKind.Go, go!.Kind);
            Assert.InRange(Vector3.Distance(go.Spot!.Value, EliteMarker.Position), 125 * EnemyRules.RingAt - 1, 125 * EnemyRules.RingAt + 1);
            seen.Add(go.Spot.Value);
            Assert.True(m.Looked.Add(go.Level));
        }
        Assert.Equal(EnemyRules.RingPoints, seen.Distinct().Count());
        Assert.Null(EnemyRules.Search(q, s, m));
    }

    [Fact]
    public void NothingIsLookedForWhereTheQuestHasNoEnemies()
    {
        var q = Elite();
        Assert.Null(EnemyRules.Search(q, Snap(q, 255, Thanalan, InZaharak, Array.Empty<StepEnemy>(), new[] { EliteMarker }), new StepMemory()));
    }
}
