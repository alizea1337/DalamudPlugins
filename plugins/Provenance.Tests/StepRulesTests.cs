using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Provenance.Core.Tribes;
using Xunit;

namespace Provenance.Tests;

public class StepRulesTests
{
    private static readonly DateTime T0 = new(2026, 11, 23, 15, 0, 0, DateTimeKind.Utc);
    private const uint Zone = 813, Giver = 1033712, Crp = 8, Min = 16;

    private static ScriptSpot Npc(uint id, float x, uint level = 0) => new(level == 0 ? id + 7 : level, Zone, new Vector3(x, 0, 0), id, false);
    private static ScriptSpot Area(uint level, float x, uint zone = Zone) => new(level, zone, new Vector3(x, 0, 0), 0, true);
    private static ScriptTodo Line(int index, byte seq, string text, params ScriptSpot[] spots) => new(index, seq, 1, spots, text);

    private static QuestScript Quest(params ScriptTodo[] todos) => new()
    {
        Quest = 65536 + 3902, Name = "Lakeland Neighbors", Giver = Giver, TargetEnd = Giver,
        GiverSpot = Npc(Giver, 500, 8368176), Todos = todos,
    };

    private static StepObject Obj(ulong id, uint baseId, float x, uint icon = 71223, bool active = true, StepObjectKind kind = StepObjectKind.Npc, string name = "someone")
        => new(id, baseId, kind, name, new Vector3(x, 0, 0), icon, active, true);

    private static StepSnapshot Snap(byte seq, IEnumerable<StepObject>? objects = null, IEnumerable<TodoProgress>? todos = null,
        Dictionary<uint, int>? items = null, uint job = Crp, uint accept = Crp, IEnumerable<StepMarker>? markers = null,
        HashSet<uint>? statuses = null, bool mounted = false, uint mount = 0, bool accepted = true, bool completed = false, bool busy = false)
        => new()
        {
            Quest = 65536 + 3902, Accepted = accepted, Completed = completed, Sequence = seq, AcceptJob = accept, Job = job,
            Territory = Zone, Player = Vector3.Zero, Objects = objects?.ToList() ?? new List<StepObject>(),
            Todos = todos?.ToList() ?? new List<TodoProgress>(), Items = items ?? new Dictionary<uint, int>(),
            Markers = markers?.ToList() ?? new List<StepMarker>(), Statuses = statuses ?? new HashSet<uint>(),
            Mounted = mounted, Mount = mount, Busy = busy,
        };

    private static StepPlan Decide(QuestScript q, StepSnapshot s, StepMemory? m = null, DateTime? now = null, bool leaveOutSay = false)
    {
        m ??= new StepMemory();
        m.Observe(s.Signature(q), now ?? T0);
        return StepRules.Decide(q, s, m, now ?? T0, leaveOutSay);
    }

    // ------------------------------------------------------------------ end states

    [Fact]
    public void CompletedQuestIsDone()
        => Assert.Equal(StepKind.Done, Decide(Quest(), Snap(255, accepted: false, completed: true)).Kind);

    [Fact]
    public void AbandonedQuestFails()
        => Assert.Equal(StepKind.Failed, Decide(Quest(), Snap(1, accepted: false)).Kind);

    [Fact]
    public void BusyWaits()
        => Assert.Equal(StepKind.Wait, Decide(Quest(), Snap(1, new[] { Obj(1, 5, 10) }, busy: true)).Kind);

    [Fact]
    public void NoProgressForLongHandsTheStepToThePlayer()
    {
        var q = Quest(Line(0, 1, "Speak with someone."));
        var s = Snap(1, new[] { Obj(1, 5, 10) });
        var m = new StepMemory();
        m.Observe(s.Signature(q), T0);
        Assert.Equal(StepKind.Talk, StepRules.Decide(q, s, m, T0.AddMinutes(4)).Kind);
        Assert.Equal(StepKind.AskUser, StepRules.Decide(q, s, m, T0.AddMinutes(6)).Kind);
        m.Touch(T0.AddMinutes(6));
        Assert.Equal(StepKind.Talk, StepRules.Decide(q, s, m, T0.AddMinutes(7)).Kind);
    }

    // ------------------------------------------------------------------ crafting

    private static QuestScript CraftQuest(uint status = 0) => new()
    {
        Quest = 65536 + 3902, Name = "Lakeland Neighbors", Giver = Giver, TargetEnd = Giver, Product = 31147, Materials = 31146, CraftStatus = status,
        Todos = new[]
        {
            Line(0, 1, "Speak with the dwarven tinkerer.", Npc(1034103, 20)),
            Line(1, 2, "Synthesize .", Npc(1034104, 21)),
            Line(2, 255, "Report to Regitt.", Npc(Giver, 500)),
        },
    };

    [Fact]
    public void CraftLineWithMaterialsNeedsCrafting()
    {
        var s = Snap(2, new[] { Obj(1, 1034104, 21) }, new[] { new TodoProgress(1, false, 1, 3, 31147) }, new() { [31147] = 1, [31146] = 2 });
        var plan = Decide(CraftQuest(), s);
        Assert.Equal(StepKind.Craft, plan.Kind);
        Assert.Equal(31147u, plan.Id);
        Assert.Equal(31146u, plan.Other);
        Assert.Equal(2, plan.Count);
    }

    [Fact]
    public void OlderCraftQuestsCountTheMaterials()
    {
        // No item from the to-do line: one product per materials item.
        var s = Snap(2, new[] { Obj(1, 1034104, 21) }, new[] { new TodoProgress(1, false, 0, 0, 0) }, new() { [31146] = 3 });
        var plan = Decide(CraftQuest(), s);
        Assert.Equal(StepKind.Craft, plan.Kind);
        Assert.Equal(3, plan.Count);
    }

    [Fact]
    public void CraftedItemsGoToTheMarkedNpc()
    {
        var s = Snap(2, new[] { Obj(1, 1034104, 21) }, new[] { new TodoProgress(1, false, 3, 3, 31147) }, new() { [31147] = 3 });
        var plan = Decide(CraftQuest(), s);
        Assert.Equal(StepKind.Talk, plan.Kind);
        Assert.Equal(1034104u, plan.Target!.BaseId);
    }

    [Fact]
    public void ShortOfMaterialsTalksToWhoeverIsMarked()
    {
        var s = Snap(2, new[] { Obj(1, 1025770, 21, name: "cumbersome crate") }, new[] { new TodoProgress(1, false, 0, 2, 31147) });
        Assert.Equal(StepKind.Talk, Decide(CraftQuest(), s).Kind);
    }

    [Fact]
    public void ShortOfMaterialsWithNobodyLeftAsksThePlayer()
    {
        var q = CraftQuest();
        var s = Snap(2, new[] { Obj(1, 1034104, 21) }, new[] { new TodoProgress(1, false, 0, 2, 31147) });
        var m = new StepMemory();
        m.Observe(s.Signature(q), T0);
        m.Tried.Add(1);
        m.Looked.Add(1034104 + 7);
        var plan = StepRules.Decide(q, s, m, T0);
        Assert.Equal(StepKind.AskUser, plan.Kind);
        Assert.Contains("materials", plan.What);
    }

    [Fact]
    public void IxalCraftingFetchesTheFacilityStatusFirst()
    {
        var q = new QuestScript
        {
            Quest = 65536 + 1508, Giver = 1009212, TargetEnd = 1009212, Product = 8099, Materials = 8049, CraftStatus = 549,
            Todos = new[] { new ScriptTodo(2, 255, 1, new[] { Npc(1009212, 500), Npc(1009218, 30) }, "Synthesize crafting tool components.") },
        };
        var s = Snap(255, new[] { Obj(1, 1009218, 30, icon: 0), Obj(2, 1009212, 500) }, new[] { new TodoProgress(2, false, 0, 2, 8099) }, new() { [8049] = 2 });
        var plan = Decide(q, s);
        Assert.Equal(StepKind.Talk, plan.Kind);
        Assert.Equal(1009218u, plan.Target!.BaseId);

        var withStatus = Snap(255, new[] { Obj(1, 1009218, 30, icon: 0) }, new[] { new TodoProgress(2, false, 0, 2, 8099) }, new() { [8049] = 2 }, statuses: new() { 549 });
        Assert.Equal(StepKind.Craft, Decide(q, withStatus).Kind);
    }

    // ------------------------------------------------------------------ gathering and the class

    private static QuestScript GatherQuest() => new()
    {
        Quest = 65536 + 4607, Giver = 1043417, TargetEnd = 1043417, GatherItems = new Dictionary<uint, uint> { [Min] = 38281, [17] = 38305 },
        Todos = new[]
        {
            new ScriptTodo(0, 1, 1, new[] { Npc(1044042, 50), Area(9406560, 11, 1073) }, "Mine  in the Elysian fields."),
            Line(1, 255, "Report to Stigma-4.", Npc(1043417, 400)),
        },
    };

    [Fact]
    public void GatherLineNeedsGatheringInTheMarkedArea()
    {
        var markers = new[] { new StepMarker(1073, new Vector3(11, 482, 17), 40, 71023, 9406560, 0) };
        var s = Snap(1, todos: new[] { new TodoProgress(0, false, 1, 3, 38281) }, items: new() { [38281] = 1 }, job: Min, accept: Min, markers: markers);
        var plan = Decide(GatherQuest(), s);
        Assert.Equal(StepKind.Gather, plan.Kind);
        Assert.Equal(2, plan.Count);
        Assert.Equal(1073u, plan.Territory);
    }

    [Fact]
    public void AQuestThatCraftsOrGathersGathersWhenAcceptedAsAGatherer()
    {
        var q = new QuestScript
        {
            Quest = 65536 + 3103, Giver = 1025602, Product = 22725, Materials = 22699,
            GatherItems = new Dictionary<uint, uint> { [Min] = 22621, [17] = 22647 },
            Todos = new[] { Line(1, 2, "Mine .") },
        };
        var s = Snap(2, todos: new[] { new TodoProgress(1, false, 0, 3, 22621) }, items: new() { [22699] = 1 }, job: Min, accept: Min);
        var plan = Decide(q, s);
        Assert.Equal(StepKind.Gather, plan.Kind);
        Assert.Equal(22621u, plan.Id);
    }

    [Fact]
    public void GatheredItemsGoBackToTheGiver()
    {
        var s = Snap(255, new[] { Obj(1, 1043417, 400) }, new[] { new TodoProgress(1, false, 0, 0, 0) }, new() { [38281] = 3 }, job: Min, accept: Min);
        Assert.Equal(StepKind.Talk, Decide(GatherQuest(), s).Kind);
    }

    [Fact]
    public void WrongClassSwitchesBack()
    {
        var plan = Decide(Quest(Line(0, 1, "Speak.")), Snap(1, new[] { Obj(1, 5, 10) }, job: 9, accept: Crp));
        Assert.Equal(StepKind.SwitchJob, plan.Kind);
        Assert.Equal(Crp, plan.Id);
    }

    // ------------------------------------------------------------------ /say and emotes

    private static QuestScript SayQuest() => new()
    {
        Quest = 65536 + 4608, Giver = 1043417, TargetEnd = 1043417,
        Todos = new[] { new ScriptTodo(0, 1, 2, new[] { Npc(1044043, 10), Npc(1044044, 20) }, "Enter any phrase containing “coffee break”.") },
        Say = new Dictionary<int, string> { [0] = "coffee break" },
    };

    [Fact]
    public void SayIsSentOnceThenRetriedOnlyAfterFifteenQuietSeconds()
    {
        var q = SayQuest();
        var s = Snap(1, new[] { Obj(1, 1044043, 10) }, new[] { new TodoProgress(0, false, 0, 2, 0) });
        var m = new StepMemory();
        m.Observe(s.Signature(q), T0);

        var plan = StepRules.Decide(q, s, m, T0);
        Assert.Equal(StepKind.Say, plan.Kind);
        Assert.Equal("coffee break", plan.Text);
        Assert.True(plan.Public);
        m.Note(plan.Key, T0, isPublic: true);

        Assert.Equal(StepKind.Wait, StepRules.Decide(q, s, m, T0.AddSeconds(10)).Kind);
        var retry = StepRules.Decide(q, s, m, T0.AddSeconds(16));
        Assert.Equal(StepKind.Say, retry.Kind);
        m.Note(retry.Key, T0.AddSeconds(16), isPublic: true);

        var after = StepRules.Decide(q, s, m, T0.AddSeconds(60));
        Assert.Equal(StepKind.AskUser, after.Kind);
        Assert.Contains("coffee break", after.What);
    }

    [Fact]
    public void ProgressElsewhereDelaysTheRetry()
    {
        var tried = new Attempt { Count = 1, Last = T0 };
        Assert.Equal(StepRules.PublicTry.NotYet, StepRules.PublicVerdict(tried, T0.AddSeconds(10), T0.AddSeconds(20)));
        Assert.Equal(StepRules.PublicTry.Yes, StepRules.PublicVerdict(tried, T0.AddSeconds(10), T0.AddSeconds(26)));
        Assert.Equal(StepRules.PublicTry.Yes, StepRules.PublicVerdict(null, T0, T0));
        Assert.Equal(StepRules.PublicTry.GiveUp, StepRules.PublicVerdict(new Attempt { Count = 2, Last = T0 }, T0, T0.AddHours(1)));
    }

    [Fact]
    public void SayIsLeftToThePlayerWhenTurnedOff()
    {
        var plan = Decide(SayQuest(), Snap(1, new[] { Obj(1, 1044043, 10) }), leaveOutSay: true);
        Assert.Equal(StepKind.AskUser, plan.Kind);
    }

    [Fact]
    public void SayGoesToTheMarkerWhenNobodyIsInView()
    {
        var markers = new[] { new StepMarker(1073, new Vector3(-7, 499, -5), 1, 71223, 9397177, 0) };
        var plan = Decide(SayQuest(), Snap(1, markers: markers));
        Assert.Equal(StepKind.Go, plan.Kind);
        Assert.Equal(1073u, plan.Territory);
    }

    [Fact]
    public void EmoteTargetsTheMarkedPerson()
    {
        var q = new QuestScript
        {
            Quest = 65536 + 3919, Giver = Giver,
            Todos = new[] { Line(1, 2, "/psych the dwarf.", Npc(1034000, 10)) },
            Emotes = new Dictionary<int, EmoteFact> { [1] = new EmoteFact(30, "/psych") },
        };
        var s = Snap(2, new[] { Obj(1, 2011000, 5, kind: StepObjectKind.Object), Obj(2, 1034000, 10) });
        var plan = Decide(q, s);
        Assert.Equal(StepKind.Emote, plan.Kind);
        Assert.Equal(30u, plan.Id);
        Assert.Equal(1034000u, plan.Target!.BaseId);
    }

    // ------------------------------------------------------------------ key items

    private static KeyItemFact Item(uint id, string name, bool ground = false, bool self = false, float range = 3, params string[] words)
        => new(id, name, ground ? 1276u : 1u, ground, self, range, 3, words.Length > 0 ? words : new[] { name });

    [Fact]
    public void AKeyItemTheLineNamesIsUsedOnTheTarget()
    {
        var q = new QuestScript
        {
            Quest = 65536 + 3829, Giver = Giver,
            Todos = new[] { Line(1, 2, "Use the large sack to capture the clucking chicken.", Area(8267931, 10)) },
            KeyItems = new[] { Item(2002980, "Large Sack") },
        };
        var s = Snap(2, new[] { Obj(1, 1032831, 10, name: "clucking chicken") }, items: new() { [2002980] = 1 });
        var plan = Decide(q, s);
        Assert.Equal(StepKind.UseItem, plan.Kind);
        Assert.Equal(ItemUse.Target, plan.Use);
        Assert.Equal(2f, plan.Within);
    }

    [Fact]
    public void AGroundItemIsThrownAtTheTarget()
    {
        var q = new QuestScript
        {
            Quest = 65536 + 3912, Giver = Giver,
            Todos = new[] { Line(0, 1, "Place the explosive at the designated location.") },
            KeyItems = new[] { Item(2003030, "Precision Explosive", ground: true, range: 10) },
        };
        var s = Snap(1, new[] { Obj(1, 2011166, 10, icon: 0, kind: StepObjectKind.Object) }, items: new() { [2003030] = 1 });
        var plan = Decide(q, s);
        Assert.Equal(StepKind.UseItem, plan.Kind);
        Assert.Equal(ItemUse.Ground, plan.Use);
        Assert.Equal(9f, plan.Within);
    }

    [Fact]
    public void AnUnnamedItemIsUsedOnMarkedObjectsOnly()
    {
        var q = new QuestScript
        {
            Quest = 65536 + 5343, Giver = Giver,
            Todos = new[] { Line(1, 2, "Salt the designated locations.", new ScriptSpot(11955068, 1297, new Vector3(10, 0, 0), 2014944, false)) },
            KeyItems = new[] { Item(2003777, "Melting Salts", words: "Melting Salts") },
        };
        var s = Snap(2, new[] { Obj(1, 1055786, 3), Obj(2, 2014944, 10, icon: 0, kind: StepObjectKind.Object) }, items: new() { [2003777] = 3 });
        var plan = Decide(q, s);
        Assert.Equal(StepKind.UseItem, plan.Kind);
        Assert.Equal(2014944u, plan.Target!.BaseId);
    }

    [Fact]
    public void AnItemTheLineDoesntNameIsUsedOnObjectsWhenItsTheOnlyOne()
    {
        var q = new QuestScript
        {
            Quest = 65536 + 5343, Giver = Giver,
            Todos = new[] { Line(1, 2, "Treat the designated locations.") },
            KeyItems = new[] { Item(2003777, "Melting Salts") },
        };
        var s = Snap(2, new[] { Obj(1, 1055786, 3), Obj(2, 2014944, 10, icon: 0, kind: StepObjectKind.Object) }, items: new() { [2003777] = 3 });
        var plan = Decide(q, s);
        Assert.Equal(StepKind.UseItem, plan.Kind);
        Assert.Equal(2014944u, plan.Target!.BaseId);
    }

    [Fact]
    public void AnItemWithNothingMarkedIsNotUsedOnPeople()
    {
        var q = new QuestScript
        {
            Quest = 65536 + 3810, Giver = Giver,
            Todos = new[] { Line(1, 2, "Speak with Ersabel.") },
            KeyItems = new[] { Item(2002977, "Qitari Fertilizer") },
        };
        var s = Snap(2, new[] { Obj(1, 1027741, 3) }, items: new() { [2002977] = 1 });
        Assert.Equal(StepKind.Talk, Decide(q, s).Kind);
    }

    [Fact]
    public void ACommonPluralDoesntNameAnItem()
    {
        // The large sack's plural "large sacks" mustn't make "Deliver the clucking sack" a line about it.
        var q = new QuestScript
        {
            Quest = 65536 + 3829, Giver = Giver,
            Todos = new[] { Line(2, 3, "Deliver the clucking sack to the poultryman.") },
            KeyItems = new[] { new KeyItemFact(2002980, "Large Sack", 1, false, false, 3, 3, new[] { "Large Sack", "large sack", "large sacks" }) },
        };
        var s = Snap(3, new[] { Obj(1, 1032830, 3, name: "poultryman") }, items: new() { [2002980] = 1, [2002981] = 1 });
        Assert.Equal(StepKind.Talk, Decide(q, s).Kind);
    }

    [Theory]
    [InlineData("Use the firecrackers to aid the stuporous Namazu.", "Firecrackers", true)]
    [InlineData("Place the explosive at the designated location.", "Precision Explosive", true)]
    [InlineData("Use the fertilizer at the vegetable plots.", "Qitari Fertilizer", true)]
    [InlineData("Salt the designated locations.", "Melting Salts", true)]
    [InlineData("Speak with Ersabel.", "Qitari Fertilizer", false)]
    [InlineData("Deliver the clucking sack to the poultryman.", "Large Sack", false)]
    public void LinesNameItems(string text, string name, bool named) => Assert.Equal(named, StepRules.Names(text, name));

    // ------------------------------------------------------------------ quest mounts

    private static QuestScript MountQuest(bool line = false) => new()
    {
        Quest = 65536 + 3923, Giver = Giver, TargetEnd = Giver,
        Mount = new MountFact(227, 20121, 0, 20, line, false),
        Todos = new[]
        {
            Line(0, 1, "Speak with Wamutt.", Npc(1033715, 10)),
            Line(1, 2, "Fire the cannon at the target automaton.", Npc(1034065, 300)),
            Line(3, 255, "Report to Regitt.", Npc(Giver, 500)),
        },
    };

    [Fact]
    public void NotRidingTalksToTheMountGiver()
    {
        var s = Snap(2, new[] { Obj(1, 1033715, 10) });
        var plan = Decide(MountQuest(), s);
        Assert.Equal(StepKind.Talk, plan.Kind);
        Assert.Equal(1033715u, plan.Target!.BaseId);
    }

    [Fact]
    public void RidingUsesTheActionOnTargetsNotTheGiver()
    {
        var s = Snap(2, new[] { Obj(1, 1033715, 5), Obj(2, 1034065, 300) }, mounted: true, mount: 227);
        var plan = Decide(MountQuest(line: true), s);
        Assert.Equal(StepKind.MountAction, plan.Kind);
        Assert.Equal(1034065u, plan.Target!.BaseId);
        Assert.True(plan.Face);
        Assert.InRange(plan.Within, 4f, 12f);
    }

    [Fact]
    public void SelfCentredActionsGetWithinTheirReach()
    {
        var q = new QuestScript
        {
            Quest = 65536 + 4621, Giver = 1043417, Mount = new MountFact(307, 29718, 0, 6, false, true),
            Todos = new[] { Line(1, 2, "Use Electric Flux.", Npc(1, 50)) },
        };
        var plan = Decide(q, Snap(2, new[] { Obj(1, 1044100, 50) }, mounted: true, mount: 307));
        Assert.Equal(StepKind.MountAction, plan.Kind);
        Assert.Equal(4f, plan.Within);
        Assert.False(plan.NoFly);
    }

    [Fact]
    public void RidingWithNothingLeftAtTheReportGetsOff()
    {
        var plan = Decide(MountQuest(), Snap(255, new[] { Obj(1, Giver, 500) }, mounted: true, mount: 227));
        Assert.Equal(StepKind.Dismount, plan.Kind);
    }

    [Fact]
    public void RidingWithTargetsOutOfViewFollowsTheMarker()
    {
        var markers = new[] { new StepMarker(Zone, new Vector3(-295, 99, -724), 1, 71223, 8385036, 0) };
        var plan = Decide(MountQuest(), Snap(2, markers: markers, mounted: true, mount: 227));
        Assert.Equal(StepKind.Go, plan.Kind);
        Assert.True(plan.NoFly);
    }

    // ------------------------------------------------------------------ targets and places

    [Fact]
    public void UnmarkedPeopleAreNotTargetsButActiveObjectsAre()
    {
        var q = Quest(Line(0, 1, "Search the area.", Area(1, 50)));
        var s = Snap(1, new[] { Obj(1, 7, 2, icon: 0), Obj(2, 2007249, 30, icon: 0, kind: StepObjectKind.Object) });
        Assert.Equal(2007249u, Decide(q, s).Target!.BaseId);
    }

    [Fact]
    public void TriedObjectsAreSkippedUntilSomethingChanges()
    {
        var q = Quest(Line(0, 1, "Harvest a crystal sample.", Area(1, 50)));
        var trees = new[] { Obj(1, 2007249, 10, icon: 0, kind: StepObjectKind.Object), Obj(2, 2007250, 20, icon: 0, kind: StepObjectKind.Object) };
        var s = Snap(1, trees);
        var m = new StepMemory();
        m.Observe(s.Signature(q), T0);
        m.Tried.Add(1);
        Assert.Equal(2u, StepRules.Decide(q, s, m, T0).Target!.Id);

        var after = Snap(1, trees, items: new() { [2001991] = 1 });
        Assert.True(m.Observe(after.Signature(q), T0.AddSeconds(5)));
        Assert.Empty(m.Tried);
    }

    [Fact]
    public void MarkedTargetsComeFirstAndTheGiverLast()
    {
        var q = Quest(Line(0, 1, "Speak with them."));
        var s = Snap(1, new[] { Obj(1, Giver, 1), Obj(2, 2007000, 2, icon: 0, kind: StepObjectKind.Object), Obj(3, 1034103, 40) });
        Assert.Equal(1034103u, Decide(q, s).Target!.BaseId);
    }

    [Fact]
    public void MarkersInThisZoneComeFirstThenSpots()
    {
        var q = Quest(Line(0, 1, "Go there.", Area(77, 100)));
        var markers = new[]
        {
            new StepMarker(1297, new Vector3(5, 0, 5), 1, 71223, 501, 0),
            new StepMarker(Zone, new Vector3(80, 0, 0), 30, 71023, 502, 0),
        };
        var m = new StepMemory();
        var s = Snap(1, markers: markers);
        m.Observe(s.Signature(q), T0);
        var first = StepRules.Decide(q, s, m, T0);
        Assert.Equal(StepKind.Go, first.Kind);
        Assert.Equal(502u, first.Level);
        Assert.Equal(15f, first.Within);
        m.Looked.Add(502);
        Assert.Equal(501u, StepRules.Decide(q, s, m, T0).Level);
        m.Looked.Add(501);
        Assert.Equal(77u, StepRules.Decide(q, s, m, T0).Level);
        m.Looked.Add(77);
        Assert.Equal(StepKind.AskUser, StepRules.Decide(q, s, m, T0).Kind);
    }

    [Fact]
    public void TheReportGoesToTheGiversSpot()
    {
        var q = Quest(Line(2, 255, "Report to Regitt."));
        var plan = Decide(q, Snap(255));
        Assert.Equal(StepKind.Go, plan.Kind);
        Assert.Equal(8368176u, plan.Level);
    }

    [Fact]
    public void CarryingWalks()
    {
        var q = new QuestScript
        {
            Quest = 65536 + 5356, Giver = 1054635, CarryStatus = 404,
            Todos = new[] { Line(1, 2, "Carry the stone box of foodstuffs to Garharlor.", Npc(1054676, 40)) },
        };
        var plan = Decide(q, Snap(2, new[] { Obj(1, 1054676, 40) }, statuses: new() { 404 }));
        Assert.Equal(StepKind.Talk, plan.Kind);
        Assert.True(plan.OnFoot);
    }

    // ------------------------------------------------------------------ menus and questions

    private static QuestScript MenuQuest() => new()
    {
        Quest = 65536 + 4633, Name = "Brainstorm",
        MenuPrompts = new[] { "What will you consume?" },
        MenuAnswers = new[] { "The potentially metaphorical single slice of bread.", "The decidedly unpoisoned apple.", "The low-grade choking hazard of an extra-sticky rice ball." },
    };

    [Fact]
    public void TheQuestsOwnTitleIsPickedInAnNpcsList()
    {
        var entries = new[] { "Marks of Friendship", "Brainstorm", "Nothing." };
        Assert.Equal(1, StepRules.ChooseEntry("", entries, MenuQuest(), new StepMemory(), Array.Empty<string>()));
    }

    [Fact]
    public void TheCutsceneSkipQuestionTakesTheFirstEntry()
        => Assert.Equal(0, StepRules.ChooseEntry("Skip cutscene?", new[] { "Yes.", "No." }, MenuQuest(), new StepMemory(), new[] { "Skip cutscene?" }));

    [Fact]
    public void AnUnknownMenuIsLeftToThePlayer()
        => Assert.Equal(-1, StepRules.ChooseEntry("What will you do?", new[] { "Part ways.", "Nothing." }, MenuQuest(), new StepMemory(), new[] { "Skip cutscene?" }));

    [Fact]
    public void AQuestionThatComesBackUnchangedGetsTheNextAnswerThenThePlayer()
    {
        var q = MenuQuest();
        var entries = q.MenuAnswers.ToList();
        var m = new StepMemory();
        m.Observe("a", T0);
        var first = StepRules.ChooseEntry("What will you consume?", entries, q, m, Array.Empty<string>());
        Assert.Equal(0, first);
        StepRules.Answered("What will you consume?", entries, first, m);
        Assert.Equal(1, StepRules.ChooseEntry("What will you consume?", entries, q, m, Array.Empty<string>()));
        StepRules.Answered("What will you consume?", entries, 1, m);
        StepRules.Answered("What will you consume?", entries, 2, m);
        Assert.Equal(-1, StepRules.ChooseEntry("What will you consume?", entries, q, m, Array.Empty<string>()));
    }

    [Fact]
    public void AQuestionAfterProgressKeepsItsAnswer()
    {
        var q = MenuQuest();
        var entries = q.MenuAnswers.ToList();
        var m = new StepMemory();
        m.Observe("a", T0);
        StepRules.Answered("What will you consume?", entries, 0, m);
        m.Observe("b", T0.AddSeconds(3));
        Assert.Equal(0, StepRules.ChooseEntry("What will you consume?", entries, q, m, Array.Empty<string>()));
    }

    [Theory]
    [InlineData("Do you really want to trade a high-quality item?", true)]
    [InlineData("Do you  really want to trade a high-quality item? ", true)]
    [InlineData("Commence “Marks of Friendship”?", true)]
    [InlineData("Are you sure you wish to part ways with Dreamingway?", false)]
    [InlineData("Commence?", false)]
    public void KnownQuestionsOnly(string prompt, bool known)
        => Assert.Equal(known, StepRules.KnownQuestion(prompt, new[] { "Do you really want to trade a high-quality item?", "Commence “”?" }));
}
