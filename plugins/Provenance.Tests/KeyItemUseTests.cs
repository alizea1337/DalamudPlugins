using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Provenance.Core.Tribes;
using Xunit;

namespace Provenance.Tests;

/// <summary>
/// When a key item is used, and when it's collected or handed over by talking: the quest's listeners decide (kind 8 is
/// "use a key item on it", at the sequence it names). The quests' data is the game's (7.58: Quest sheet listeners and
/// to-do lines, EventItem, the quests' own text); the scripts agree (their <c>IsEventItemUsable</c> names the same
/// sequences and targets, and 1247 hands its idols over in a trade).
/// </summary>
public class KeyItemUseTests
{
    private static readonly DateTime T0 = new(2026, 11, 28, 15, 0, 0, DateTimeKind.Utc);
    private const uint Blm = 25;

    private static ListenerRow Row(uint listener, byte kind, byte seq, byte type = 0, uint value = 0) => new(listener, type, value, kind, seq);
    private static ScriptSpot Spot(uint level, uint zone, float x, float y, float z, uint obj) => new(level, zone, new Vector3(x, y, z), obj, obj == 0);
    private static KeyItemFact Item(uint id, string name, string singular, string plural, uint action = 1, bool ground = false, float range = 3)
        => new(id, name, action, ground, false, range, 3, new[] { name, singular, plural });

    private static QuestScript Script(uint quest, string name, uint giver, ListenerRow[] listeners, KeyItemFact[] items, ScriptTodo[] todos,
        (string, uint)[]? args = null, uint carry = 0, string[]? prompts = null)
        => new()
        {
            MenuPrompts = prompts ?? Array.Empty<string>(),
            Quest = 65536 + quest, Name = name, Repeatable = true, Giver = giver, TargetEnd = giver, Todos = todos, KeyItems = items,
            Enemies = EnemyRules.Read(args ?? Array.Empty<(string, uint)>(), listeners, _ => null),
            ItemObjects = EnemyRules.ItemObjects(listeners),
            ItemSequences = EnemyRules.ItemSequences(listeners),
            CarryStatus = carry,
        };

    private static StepObject Npc(ulong id, uint baseId, Vector3 at, string name = "someone") => new(id, baseId, StepObjectKind.Npc, name, at, 71223, true, true);
    private static StepObject Thing(ulong id, uint baseId, Vector3 at, string name = "something") => new(id, baseId, StepObjectKind.Object, name, at, 0, true, true);

    private static StepSnapshot Snap(uint zone, byte seq, Vector3 me, StepObject[] objects, Dictionary<uint, int> items, HashSet<uint>? statuses = null)
        => new()
        {
            Quest = 1, Accepted = true, Sequence = seq, AcceptJob = Blm, Job = Blm, Fighter = true, Territory = zone, Player = me,
            Objects = objects, Items = items, Statuses = statuses ?? new HashSet<uint>(),
        };

    private static StepPlan Decide(QuestScript q, StepSnapshot s, StepMemory? m = null)
    {
        m ??= new StepMemory();
        m.Observe(s.Signature(), T0);
        return StepRules.Decide(q, s, m, T0);
    }

    // ------------------------------------------------------------------ 1260 "Feed Me" (Sylphs, zone 152)

    private const uint Shroud = 152, Tonaxia = 1005561, SugarySap = 2001178;

    private static QuestScript FeedMe()
    {
        var rows = new List<ListenerRow>();
        for (uint o = 2003221; o <= 2003228; o++) rows.Add(Row(o, 1, 1));
        rows.Add(Row(Tonaxia, 1, 1));
        rows.Add(Row(Tonaxia, 1, 2));
        for (uint i = 0; i < 7; i++)
        {
            rows.Add(Row(2003229 + i, 1, 2));
            rows.Add(Row(2003229 + i, 8, 2));
            rows.Add(Row(1008363 + i, 1, 2));
        }
        rows.Add(Row(Tonaxia, 1, 255));
        for (uint o = 2003229; o <= 2003235; o++) rows.Add(Row(o, 1, 255));
        return Script(1260, "Feed Me", Tonaxia, rows.ToArray(),
            new[] { Item(SugarySap, "Sugary Sap", "phial of sugary sap", "phials of sugary sap") },
            new[]
            {
                new ScriptTodo(0, 1, 0, new[] { Spot(4569678, Shroud, 283.967f, -9.41947f, 31.5533f, 0) }, "Obtain sugary sap from deepwood saplings in Larkscall."),
                new ScriptTodo(1, 2, 0, new[] { Spot(4569682, Shroud, 221.79f, -9.94284f, -29.8427f, 0) }, "Use the sugary sap to free the trapped sylphs from the gapemaw plants."),
                new ScriptTodo(2, 255, 0, new[] { Spot(4494851, Shroud, 47.0332f, 6.0578f, 254.034f, Tonaxia) }, "Report to Tonaxia."),
            });
    }

    [Fact]
    public void TheSapIsCollectedFromTheSaplingsNotUsedOnThem()
    {
        // After the first phial: the next sapling is interacted with (the to-do line names the sap, but nothing at
        // sequence 1 takes a key item).
        var me = new Vector3(280, -9, 30);
        var s = Snap(Shroud, 1, me, new[] { Thing(1, 2003224, me + new Vector3(4, 0, 0), "deepwood sapling") }, new() { [SugarySap] = 1 });
        var plan = Decide(FeedMe(), s);
        Assert.Equal(StepKind.Talk, plan.Kind);
        Assert.Equal(2003224u, plan.Target!.BaseId);
    }

    [Fact]
    public void TheSapIsUsedOnTheGapemawPlantsNotTheSylphs()
    {
        var me = new Vector3(220, -10, -30);
        var s = Snap(Shroud, 2, me, new[]
        {
            Npc(1, 1008363, me + new Vector3(1, 0, 0), "trapped sylph"),
            Thing(2, 2003229, me + new Vector3(3, 0, 0), "gapemaw plant"),
        }, new() { [SugarySap] = 3 });
        var plan = Decide(FeedMe(), s);
        Assert.Equal(StepKind.UseItem, plan.Kind);
        Assert.Equal(SugarySap, plan.Id);
        Assert.Equal(2003229u, plan.Target!.BaseId);
    }

    // ------------------------------------------------------------------ 1334 "Bo Zu's Blown Cover" (Kobolds, zone 180)

    private const uint Outer = 180, BoZu = 1005928, BaGo = 1005932, BombCore = 2001284, Furnace = 2003950, Transporting = 404;

    private static QuestScript BlownCover() => Script(1334, "Bo Zu's Blown Cover", BoZu,
        new[]
        {
            Row(BaGo, 1, 1), Row(BoZu, 1, 1), Row(Furnace, 1, 2), Row(2871, 5, 2, 3, 4635165), Row(BoZu, 1, 2), Row(BaGo, 1, 2),
            Row(2003953, 1, 3), Row(17, 9, 3, 3, 4635147), Row(2003954, 1, 3), Row(17, 9, 3, 3, 4635148), Row(2003955, 1, 3),
            Row(17, 9, 3, 3, 4635149), Row(2003956, 1, 3), Row(17, 9, 3, 3, 4635150), Row(2003957, 1, 3), Row(17, 9, 3, 3, 4635151),
            Row(BoZu, 1, 3), Row(BaGo, 1, 3), Row(2003952, 1, 3), Row(BaGo, 1, 4), Row(BoZu, 1, 4), Row(2003952, 1, 4),
            Row(BoZu, 1, 255), Row(2003952, 1, 255),
        },
        new[] { Item(BombCore, "Limpet Bomb Core", "limpet bomb core", "limpet bomb cores") },
        new[]
        {
            new ScriptTodo(0, 1, 1, new[] { Spot(4618733, Outer, -1.20885f, 33.3884f, -244.181f, BaGo) }, "Speak with 789th Order Acolyte Ba Go."),
            new ScriptTodo(1, 2, 1, new[] { Spot(4635134, Outer, 50.0001f, 49.3459f, -342.665f, Furnace), Spot(4618733, Outer, -1.20885f, 33.3884f, -244.181f, BaGo) },
                "Set a limpet bomb at the 411th Order furnace near Iron Lake."),
            new ScriptTodo(2, 3, 1, new[] { Spot(4696112, Outer, 51.3018f, 40.851f, -343.44f, 0) }, "Obtain a limpet bomb core from the embers."),
            new ScriptTodo(3, 4, 1, new[] { Spot(4618733, Outer, -1.20885f, 33.3884f, -244.181f, BaGo) }, "Deliver the limpet bomb core to 789th Order Acolyte Ba Go."),
            new ScriptTodo(4, 255, 1, new[] { Spot(4618734, Outer, 7.10241f, 16.1677f, -188.669f, BoZu) }, "Report to 789th Order Dustman Bo Zu."),
        },
        new[] { ("ACTOR0", BoZu), ("ACTOR1", BaGo), ("ENEMY0", 4635165u), ("ENEMY1", 4635147u), ("ITEM0", BombCore) },
        Transporting);

    [Fact]
    public void TheBombCoreIsHandedToBaGoByTalking()
    {
        var me = new Vector3(-3, 33, -242);
        var s = Snap(Outer, 4, me, new[] { Npc(1, BaGo, new Vector3(-1.2f, 33.4f, -244.2f), "789th Order Acolyte Ba Go") }, new() { [BombCore] = 1 });
        var plan = Decide(BlownCover(), s);
        Assert.Equal(StepKind.Talk, plan.Kind);
        Assert.Equal(BaGo, plan.Target!.BaseId);
    }

    [Fact]
    public void CarryingTheBombGoesToTheFurnaceNotBackToBaGo()
    {
        var me = new Vector3(-3, 33, -242);
        var q = BlownCover();
        var s = Snap(Outer, 2, me, new[] { Npc(1, BaGo, new Vector3(-1.2f, 33.4f, -244.2f), "789th Order Acolyte Ba Go") }, new(),
            new HashSet<uint> { Transporting });
        var m = new StepMemory { CarryGiver = BaGo, Pickups = 1 };
        var plan = Decide(q, s, m);
        Assert.Equal(StepKind.Go, plan.Kind);
        Assert.Equal(4635134u, plan.Level);
        Assert.True(plan.OnFoot);
    }

    [Fact]
    public void BaGoIsStillTalkedToForTheBombWhenNothingIsCarried()
    {
        var me = new Vector3(-3, 33, -242);
        var s = Snap(Outer, 2, me, new[] { Npc(1, BaGo, new Vector3(-1.2f, 33.4f, -244.2f), "789th Order Acolyte Ba Go") }, new());
        var plan = Decide(BlownCover(), s, new StepMemory { CarryGiver = BaGo, Pickups = 1 });
        Assert.Equal(StepKind.Talk, plan.Kind);
        Assert.Equal(BaGo, plan.Target!.BaseId);
        // A marked target while the line also points at the furnace: a talk that changes nothing isn't repeated.
        Assert.True(plan.Marked);
        Assert.True(StepRules.PointsElsewhere(BlownCover(), s, BaGo));
        // At the hand-in Ba Go is all the line points at.
        Assert.False(StepRules.PointsElsewhere(BlownCover(), Snap(Outer, 4, me, Array.Empty<StepObject>(), new() { [BombCore] = 1 }), BaGo));
    }

    // ------------------------------------------------------------------ 1384 "A Quick Snip", 1385 "Violence of the Clams" (Sahagin, zone 138)

    private const uint LaNoscea = 138, Fyuu = 1005938, Tamer = 1005946, PugilFlesh = 2001267, CrabShell = 2001268, Clam = 2001269;
    private static readonly Vector3 FyuuAt = new(-220.716f, -40.7197f, 37.2728f);

    private static QuestScript QuickSnip()
    {
        var rows = new List<ListenerRow> { Row(383, 9, 1, 1, 2533), Row(Tamer, 1, 1), Row(Fyuu, 1, 1) };
        uint[] levels = { 4631982, 4631984, 4631986, 4631987, 4631990 };
        for (uint i = 0; i < 5; i++)
        {
            rows.Add(Row(2003840 + i, 1, 2));
            rows.Add(Row(2003840 + i, 8, 2));
            rows.Add(Row(2842, 9, 2, 3, levels[i]));
        }
        rows.AddRange(new[] { Row(Tamer, 1, 2), Row(Fyuu, 1, 2), Row(Fyuu, 1, 255) });
        var tamer = Spot(4618960, LaNoscea, -239.267f, -42.1074f, 15.2418f, Tamer);
        return Script(1384, "A Quick Snip", Fyuu, rows.ToArray(),
            new[]
            {
                Item(PugilFlesh, "Fresh Pugil Flesh", "slab of fresh pugil flesh", "slabs of fresh pugil flesh"),
                Item(CrabShell, "Rigid Megalocrab Shell", "rigid megalocrab shell", "rigid megalocrab shells"),
            },
            new[]
            {
                new ScriptTodo(0, 1, 2, new[] { Spot(4632148, LaNoscea, -797.886f, -48.0634f, 683.894f, 0), tamer }, "Slay Moonshade pugils and obtain their flesh."),
                new ScriptTodo(1, 2, 2, new[]
                {
                    Spot(4631940, LaNoscea, -770.552f, -38.2076f, 651.426f, 2003840), Spot(4631946, LaNoscea, -765.207f, -38.1108f, 658.113f, 2003841),
                    Spot(4631947, LaNoscea, -758.439f, -38.05f, 662.365f, 2003842), Spot(4631948, LaNoscea, -757.144f, -38.9854f, 676.787f, 2003843),
                    Spot(4631949, LaNoscea, -754.012f, -38.5425f, 684.833f, 2003844), tamer,
                }, "Place fresh pugil flesh near crab burrows, and slay hungry snippers for their shells."),
                new ScriptTodo(2, 255, 0, new[] { Spot(4618738, LaNoscea, FyuuAt.X, FyuuAt.Y, FyuuAt.Z, Fyuu) }, "Deliver the rigid megalocrab shells to Fyuu."),
            },
            new[] { ("ACTOR0", Fyuu), ("ENEMY0", 383u), ("ITEM0", PugilFlesh), ("ACTOR1", Tamer), ("ITEM1", CrabShell) },
            prompts: new[] { "Ride the elbst to Moonshade Isle?" });
    }

    private static QuestScript Clams()
    {
        var rows = new List<ListenerRow>();
        foreach (var o in new uint[] { 2003845, 2003846, 2003847, 2003848, 2003853, 2003854, 2003855 }) rows.Add(Row(o, 1, 1));
        rows.Add(Row(Tamer, 1, 1));
        foreach (var (range, level) in new (uint, uint)[] { (4632323, 4632492), (4632328, 4632493), (4632331, 4632494), (4632334, 4632495), (4632338, 4632496), (4632340, 4632497) })
        {
            rows.Add(Row(5000000, 10, 1, 3, range));
            rows.Add(Row(2874, 5, 1, 3, level));
        }
        foreach (var o in new uint[] { 2003850, 2003851, 2003852 }) rows.Add(Row(o, 1, 1));
        rows.AddRange(new[] { Row(Fyuu, 1, 1), Row(Fyuu, 1, 255), Row(2003850, 1, 255), Row(2003851, 1, 255), Row(2003852, 1, 255) });
        return Script(1385, "Violence of the Clams", Fyuu, rows.ToArray(),
            new[] { Item(Clam, "Whitecap Clam", "whitecap clam", "whitecap clams") },
            new[]
            {
                new ScriptTodo(0, 1, 5, new[] { Spot(4632459, LaNoscea, -794.8f, -47.2876f, 681.288f, 0), Spot(4618960, LaNoscea, -239.267f, -42.1074f, 15.2418f, Tamer) },
                    "Gather whitecap clams on Moonshade Isle."),
                new ScriptTodo(1, 255, 0, new[] { Spot(4618738, LaNoscea, FyuuAt.X, FyuuAt.Y, FyuuAt.Z, Fyuu) }, "Deliver the whitecap clams to Fyuu."),
            });
    }

    [Fact]
    public void TheShellsAreReportedToFyuuByTalking()
    {
        var me = FyuuAt + new Vector3(2, 0, 0);
        var s = Snap(LaNoscea, 255, me, new[] { Npc(1, Fyuu, FyuuAt, "Fyuu") }, new() { [CrabShell] = 2 });
        var plan = Decide(QuickSnip(), s);
        Assert.Equal(StepKind.Talk, plan.Kind);
        Assert.Equal(Fyuu, plan.Target!.BaseId);
    }

    [Fact]
    public void TheClamsAreReportedToFyuuByTalking()
    {
        var me = FyuuAt + new Vector3(2, 0, 0);
        var s = Snap(LaNoscea, 255, me, new[] { Npc(1, Fyuu, FyuuAt, "Fyuu") }, new() { [Clam] = 5 });
        var plan = Decide(Clams(), s);
        Assert.Equal(StepKind.Talk, plan.Kind);
        Assert.Equal(Fyuu, plan.Target!.BaseId);
    }

    [Fact]
    public void ThePugilFleshGoesOnTheCrabBurrows()
    {
        var me = new Vector3(-768, -38, 650);
        var s = Snap(LaNoscea, 2, me, new[] { Thing(1, 2003840, new Vector3(-770.552f, -38.2076f, 651.426f), "crab burrow") }, new() { [PugilFlesh] = 3 });
        var plan = Decide(QuickSnip(), s);
        Assert.Equal(StepKind.UseItem, plan.Kind);
        Assert.Equal(PugilFlesh, plan.Id);
        Assert.Equal(2003840u, plan.Target!.BaseId);
    }

    [Fact]
    public void TheRideToTheIsleIsAnsweredOnlyWhenTheStepTalksToWhoeverTheLineNames()
    {
        var q = QuickSnip();
        var tamer = Npc(1, Tamer, new Vector3(-239.3f, -42.1f, 15.2f), "elbst tamer");
        var talk = new StepPlan(StepKind.Talk, "the step's marked target") { Target = tamer };
        Assert.True(StepRules.OwnQuestion(q, 1, talk, "Ride the elbst to Moonshade Isle?"));
        Assert.True(StepRules.OwnQuestion(q, 2, talk, " Ride the elbst to  Moonshade Isle? "));
        // At the report no line names the tamer; another quest's ride; not talking; a question the quest doesn't have.
        Assert.False(StepRules.OwnQuestion(q, 255, talk, "Ride the elbst to Moonshade Isle?"));
        Assert.False(StepRules.OwnQuestion(q, 1, talk, "Ride the elbst to the merchantman?"));
        Assert.False(StepRules.OwnQuestion(q, 1, new StepPlan(StepKind.Go, "the marked spot") { Spot = tamer.Position }, "Ride the elbst to Moonshade Isle?"));
        Assert.False(StepRules.OwnQuestion(q, 1, talk, "Yes"));
    }

    // ------------------------------------------------------------------ genuine uses that stay so

    [Fact]
    public void TheManacleKeyFreesTheCaptivesThoughTheLineDoesntNameIt()
    {
        // 1383 "Spawning Ground Salvation" (Sahagin): the listeners put the key on the three captives (NPCs) at sequence 2.
        var rows = new List<ListenerRow>();
        for (uint i = 0; i < 7; i++)
        {
            rows.Add(Row(1008816 + i, 1, 1));
            rows.Add(Row(767, 9, 1, 3, 4629559 + i));
        }
        rows.AddRange(new[] { Row(Fyuu, 1, 1), Row(1008823, 1, 1), Row(1008824, 1, 1), Row(1008825, 1, 1) });
        foreach (var o in new uint[] { 1008823, 1008824, 1008825 }) { rows.Add(Row(o, 1, 2)); rows.Add(Row(o, 8, 2)); }
        rows.AddRange(new[] { Row(Fyuu, 1, 2), Row(Fyuu, 1, 255) });
        var q = Script(1383, "Spawning Ground Salvation", Fyuu, rows.ToArray(),
            new[] { Item(2001254, "Manacle Key", "manacle key", "manacle keys") },
            new[]
            {
                new ScriptTodo(0, 1, 3, new[] { Spot(4629566, LaNoscea, -182.561f, -50.5566f, -87.781f, 0) }, "Obtain manacle keys from Sahagin sentries."),
                new ScriptTodo(1, 2, 3, new[]
                {
                    Spot(4629545, LaNoscea, -213.573f, -40.0362f, -103.576f, 1008823), Spot(4629546, LaNoscea, -212.743f, -40.0028f, -104.605f, 1008824),
                    Spot(4629547, LaNoscea, -212.699f, -40.0369f, -102.779f, 1008825),
                }, "Free captive storm recruits."),
                new ScriptTodo(2, 255, 1, new[] { Spot(4618738, LaNoscea, FyuuAt.X, FyuuAt.Y, FyuuAt.Z, Fyuu) }, "Report to Fyuu."),
            });
        var me = new Vector3(-211, -40, -103);
        var s = Snap(LaNoscea, 2, me, new[] { Npc(1, 1008823, new Vector3(-213.573f, -40.0362f, -103.576f), "captive storm recruit") }, new() { [2001254] = 3 });
        var plan = Decide(q, s);
        Assert.Equal(StepKind.UseItem, plan.Kind);
        Assert.Equal(1008823u, plan.Target!.BaseId);
    }

    [Fact]
    public void TheExplosiveIsStillThrownAtTheBarrel()
    {
        // 3912 "Boom and Dust" (Dwarves): the explosive (ground-targeted) on either barrel at sequence 1.
        const uint Lakeland = 813;
        var q = Script(3912, "Boom and Dust", 1033712,
            new[]
            {
                Row(2011166, 1, 1), Row(2011166, 8, 1), Row(2011167, 1, 1), Row(2011167, 8, 1), Row(1034059, 1, 1), Row(1034059, 1, 2),
                Row(1033712, 1, 255), Row(1034059, 1, 255),
            },
            new[] { Item(2003030, "Precision Explosive", "precision explosive", "precision explosives", 1276, ground: true, range: 10) },
            new[]
            {
                new ScriptTodo(0, 1, 1, new[] { Spot(8384682, Lakeland, -721.688f, 68.8711f, -1.50029f, 2011166), Spot(8384683, Lakeland, -724.746f, 68.8479f, 1.06011f, 2011167) },
                    "Place the explosive at the designated location."),
                new ScriptTodo(1, 2, 1, new[] { Spot(8384680, Lakeland, -725.685f, 68.8527f, 2.22951f, 1034059) }, "Speak with the dwarven foreman."),
                new ScriptTodo(2, 255, 1, new[] { Spot(8368176, Lakeland, -617.112f, 65.601f, -425.231f, 1033712) }, "Report to Regitt."),
            });
        var me = new Vector3(-715, 68.8f, -1);
        var s = Snap(Lakeland, 1, me, new[] { Thing(1, 2011167, new Vector3(-724.746f, 68.8479f, 1.06011f), "barrel") }, new() { [2003030] = 1 });
        var plan = Decide(q, s);
        Assert.Equal(StepKind.UseItem, plan.Kind);
        Assert.Equal(ItemUse.Ground, plan.Use);
        Assert.Equal(2011167u, plan.Target!.BaseId);
    }

    [Fact]
    public void TheIdolsAreHandedToTheAltarNotUsedOnIt()
    {
        // 1247 "Fire-spitting Image" (Amalj'aa): "Cast wildfire idols into the Inferno altar" is a trade with the altar
        // (its script hands them over; no listener takes a key item).
        const uint Thanalan = 146;
        var q = Script(1247, "Fire-spitting Image", 1005552,
            new[] { Row(2457, 9, 1, 1, 2303), Row(1005552, 1, 1), Row(2002918, 1, 2), Row(1005552, 1, 2), Row(1005552, 1, 255), Row(2002918, 1, 255) },
            new[] { Item(2001123, "Wildfire Idol", "wildfire idol", "wildfire idols") },
            new[]
            {
                new ScriptTodo(0, 1, 0, new[] { Spot(4505280, Thanalan, 414.229f, -4.0893f, -90.176f, 0) }, "Slay Zahar'ak fortune-tellers and obtain wildfire idols."),
                new ScriptTodo(1, 2, 0, new[] { Spot(4504684, Thanalan, 483.823f, 11.1841f, -164.2f, 2002918) }, "Cast wildfire idols into the Inferno altar."),
                new ScriptTodo(2, 255, 0, new[] { Spot(4496979, Thanalan, 85.6442f, 15.2945f, -355.697f, 1005552) }, "Report to Yadovv Gah."),
            },
            new[] { ("ACTOR0", 1005552u), ("ENEMY0", 2457u), ("ITEM0", 2001123u), ("EOBJECT0", 2002918u) });
        var me = new Vector3(482, 11, -162);
        var s = Snap(Thanalan, 2, me, new[] { Thing(1, 2002918, new Vector3(483.823f, 11.1841f, -164.2f), "Inferno altar") }, new() { [2001123] = 3 });
        var plan = Decide(q, s);
        Assert.Equal(StepKind.Talk, plan.Kind);
        Assert.Equal(2002918u, plan.Target!.BaseId);
    }

    [Fact]
    public void ListenersSayWhenAKeyItemIsUsed()
    {
        Assert.Equal(new byte[] { 2 }, FeedMe().ItemSequences!.ToArray());
        Assert.Empty(BlownCover().ItemSequences!);
        Assert.Empty(Clams().ItemSequences!);
        Assert.Null(EnemyRules.ItemSequences(Array.Empty<ListenerRow>()));
    }

    // ------------------------------------------------------------------ the game turning an item down

    [Fact]
    public void AnObjectTheGameTurnsTheItemDownForIsTalkedToInstead()
    {
        // No listener data (a quest the data says nothing about): the line names the item, so it's tried on the NPC
        // first; once the game says "Invalid target", the same NPC is talked to, not left out.
        var q = new QuestScript
        {
            Quest = 65536 + 9999, Giver = 1, TargetEnd = 1,
            Todos = new[] { new ScriptTodo(0, 4, 1, new[] { Spot(4618733, Outer, -1.2f, 33.4f, -244.2f, BaGo) }, "Deliver the limpet bomb core to 789th Order Acolyte Ba Go.") },
            KeyItems = new[] { Item(BombCore, "Limpet Bomb Core", "limpet bomb core", "limpet bomb cores") },
        };
        var me = new Vector3(-3, 33, -242);
        var baGo = Npc(1, BaGo, new Vector3(-1.2f, 33.4f, -244.2f), "789th Order Acolyte Ba Go");
        var s = Snap(Outer, 4, me, new[] { baGo }, new() { [BombCore] = 1 });
        var m = new StepMemory();
        Assert.Equal(StepKind.UseItem, Decide(q, s, m).Kind);
        m.RefusedItem(s, BombCore, baGo.Key);
        var plan = StepRules.Decide(q, s, m, T0);
        Assert.Equal(StepKind.Talk, plan.Kind);
        Assert.Equal(BaGo, plan.Target!.BaseId);
        // Once the quest moves on, the refusal is forgotten.
        Assert.True(m.NotForItem(s, BombCore, baGo.Key));
        m.Observe(s.Signature() + "|moved", T0.AddSeconds(5));
        Assert.False(m.NotForItem(s, BombCore, baGo.Key));
    }
}
