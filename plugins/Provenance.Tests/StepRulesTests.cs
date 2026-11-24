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
        m.Observe(s.Signature(), now ?? T0);
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
    public void AnUnreadScriptWaits()
    {
        var s = new StepSnapshot { Quest = 65536 + 3902, Accepted = true, Sequence = 1, AcceptJob = Crp, Job = Crp, Territory = Zone, ScriptRead = false };
        Assert.Equal(StepKind.Wait, Decide(Quest(Line(0, 1, "Speak.")), s).Kind);
    }

    [Fact]
    public void AGameThatStaysBusyDoesntStopTheNoProgressClock()
    {
        var q = Quest(Line(0, 1, "Speak with someone."));
        var s = Snap(1, new[] { Obj(1, 5, 10) }, busy: true);
        var m = new StepMemory();
        m.Observe(s.Signature(), T0);
        Assert.Equal(StepKind.Wait, StepRules.Decide(q, s, m, T0.AddMinutes(4)).Kind);
        Assert.Equal(StepKind.AskUser, StepRules.Decide(q, s, m, T0.AddMinutes(6)).Kind);
    }

    [Fact]
    public void NoProgressForLongHandsTheStepToThePlayer()
    {
        var q = Quest(Line(0, 1, "Speak with someone."));
        var s = Snap(1, new[] { Obj(1, 5, 10) });
        var m = new StepMemory();
        m.Observe(s.Signature(), T0);
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
        m.Observe(s.Signature(), T0);
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

    private static QuestScript IxalCraft() => new()
    {
        Quest = 65536 + 1508, Giver = 1009212, TargetEnd = 1009212, Product = 8099, Materials = 8049, CraftStatus = 549,
        Todos = new[] { new ScriptTodo(2, 255, 1, new[] { Npc(1009212, 500), Npc(1009218, 30) }, "Synthesize crafting tool components.") },
    };

    [Fact]
    public void AFacilityAccessAboutToRunOutIsRenewedBeforeCrafting()
    {
        var s = new StepSnapshot
        {
            Quest = 65536 + 1508, Accepted = true, Sequence = 255, AcceptJob = Crp, Job = Crp, Territory = Zone,
            Objects = new[] { Obj(1, 1009218, 30, icon: 0) }, Todos = new[] { new TodoProgress(2, false, 0, 2, 8099) },
            Items = new Dictionary<uint, int> { [8049] = 2 }, Statuses = new HashSet<uint> { 549 }, Expiring = new HashSet<uint> { 549 },
        };
        var plan = Decide(IxalCraft(), s);
        Assert.Equal(StepKind.Talk, plan.Kind);
        Assert.Equal(1009218u, plan.Target!.BaseId);
    }

    [Fact]
    public void TheClassComesBeforeTheFacilityAccess()
    {
        var s = Snap(255, new[] { Obj(1, 1009218, 30, icon: 0) }, new[] { new TodoProgress(2, false, 0, 2, 8099) }, new() { [8049] = 2 }, job: 9, accept: Crp);
        var plan = Decide(IxalCraft(), s);
        Assert.Equal(StepKind.SwitchJob, plan.Kind);
        Assert.Equal(Crp, plan.Id);
    }

    // ------------------------------------------------------------------ the report after crafting or gathering

    // Quest 1523 "A High Wind in Ishgard" (Ixal): the giver in North Shroud, the materials and the facility access from
    // Norbettaux in Coerthas Central Highlands; the craft line is in the report sequence (255).
    private const uint IxalGiver = 1009216, Norbettaux = 1009221, NorthShroud = 154, Coerthas = 155, Facility = 549;
    private static readonly ScriptSpot IxalGiverSpot = new(4889082, NorthShroud, new Vector3(164, -23, 115), IxalGiver, false);
    private static readonly ScriptSpot NorbettauxSpot = new(4889200, Coerthas, new Vector3(202, 302, -254), Norbettaux, false);

    private static QuestScript HighWind() => new()
    {
        Quest = 65536 + 1523, Name = "A High Wind in Ishgard", Giver = IxalGiver, TargetEnd = IxalGiver, GiverSpot = IxalGiverSpot,
        Product = 8123, Materials = 8081, CraftStatus = Facility,
        // The crafting line as the game data has it: first whoever the work goes to, then where the materials come from.
        Todos = new[] { new ScriptTodo(3, 255, 1, new[] { IxalGiverSpot, NorbettauxSpot }, "Synthesize isolator components and deliver them to Jezul Ahuatan.") },
    };

    private static StepSnapshot HighWindSnap(uint made, int products, int materials, IEnumerable<StepObject>? objects = null, IEnumerable<StepMarker>? markers = null, bool access = true)
        => new()
        {
            Quest = 65536 + 1523, Accepted = true, Sequence = 255, AcceptJob = Crp, Job = Crp, Territory = Coerthas, Player = new Vector3(200, 302, -252),
            Objects = objects?.ToList() ?? new List<StepObject>(), Markers = markers?.ToList() ?? new List<StepMarker>(),
            Todos = new[] { new TodoProgress(3, false, made, 3, 8123) },
            Items = new Dictionary<uint, int> { [8123] = products, [8081] = materials },
            Statuses = access ? new HashSet<uint> { Facility } : new HashSet<uint>(),
        };

    private static StepObject Norbettaux_(ulong id = 1) => new(id, Norbettaux, StepObjectKind.Npc, "Norbettaux", new Vector3(202, 302, -254), 71223, true, true);

    [Fact]
    public void AfterCraftingTheReportGoesToTheGiverNotTheMaterialsNpc()
    {
        // The live log: 3 of 3 crafted, Norbettaux still marked two yalms away (he hands out spares), a marker over him.
        var marker = new StepMarker(Coerthas, new Vector3(202, 302, -254), 1, 71223, 4889200, Norbettaux & 0xFFFF);
        var plan = Decide(HighWind(), HighWindSnap(3, 3, 0, new[] { Norbettaux_() }, new[] { marker }));
        Assert.Equal(StepKind.Go, plan.Kind);
        Assert.Null(plan.Target);
        Assert.Equal(NorthShroud, plan.Territory);
        Assert.Equal(IxalGiverSpot.Position, plan.Spot);
        Assert.Equal(IxalGiverSpot.Level, plan.Level);
    }

    [Fact]
    public void AfterCraftingTheGiverInViewIsTalkedToEvenWithTheMaterialsNpcCloser()
    {
        var giver = new StepObject(2, IxalGiver, StepObjectKind.Npc, "Jezul Ahuatan", new Vector3(260, 302, -200), 71225, true, true);
        var plan = Decide(HighWind(), HighWindSnap(3, 3, 0, new[] { Norbettaux_(), giver }));
        Assert.Equal(StepKind.Talk, plan.Kind);
        Assert.Equal(IxalGiver, plan.Target!.BaseId);
    }

    [Fact]
    public void TheFacilityAccessTalkBeforeCraftingStays()
    {
        var plan = Decide(HighWind(), HighWindSnap(0, 0, 3, new[] { Norbettaux_() }, access: false));
        Assert.Equal(StepKind.Talk, plan.Kind);
        Assert.Equal(Norbettaux, plan.Target!.BaseId);
        Assert.Contains("facility access", plan.What);

        Assert.Equal(StepKind.Craft, Decide(HighWind(), HighWindSnap(0, 0, 3, new[] { Norbettaux_() })).Kind);
    }

    [Fact]
    public void ShortOfMaterialsInTheReportStillTalksToTheMaterialsNpc()
    {
        // One made, none left to make it with: the NPC who hands out more is still wanted.
        var plan = Decide(HighWind(), HighWindSnap(1, 1, 0, new[] { Norbettaux_() }));
        Assert.Equal(StepKind.Talk, plan.Kind);
        Assert.Equal(Norbettaux, plan.Target!.BaseId);
        Assert.False(StepRules.Supplied(HighWind(), HighWindSnap(1, 1, 0)));
    }

    [Fact]
    public void TheReportAsksThePlayerOnceTheGiverWasTriedAndHisSpotLookedAt()
    {
        var q = HighWind();
        var giver = new StepObject(2, IxalGiver, StepObjectKind.Npc, "Jezul Ahuatan", new Vector3(260, 302, -200), 71225, true, true);
        var s = HighWindSnap(3, 3, 0, new[] { Norbettaux_(), giver });
        var m = new StepMemory();
        m.Observe(s.Signature(), T0);
        m.Tried.Add(giver.Key);
        Assert.Equal(StepKind.Go, StepRules.Decide(q, s, m, T0).Kind);
        m.Looked.Add(IxalGiverSpot.Level);
        var plan = StepRules.Decide(q, s, m, T0);
        Assert.Equal(StepKind.AskUser, plan.Kind);
        Assert.Contains("report", plan.What);
    }

    [Fact]
    public void OlderCraftQuestsReportOnceTheProductsAreInTheBags()
    {
        // No line counts the product: the products in the bags (and no materials left) mean it's made.
        var q = HighWind();
        var s = new StepSnapshot
        {
            Quest = q.Quest, Accepted = true, Sequence = 255, AcceptJob = Crp, Job = Crp, Territory = Coerthas,
            Objects = new[] { Norbettaux_() }, Todos = new[] { new TodoProgress(3, false, 0, 0, 0) },
            Items = new Dictionary<uint, int> { [8123] = 3 }, Statuses = new HashSet<uint> { Facility },
        };
        Assert.True(StepRules.Supplied(q, s));
        Assert.Equal(StepKind.Go, Decide(q, s).Kind);
    }

    // ------------------------------------------------------------------ the crafting line before the report

    // Quest 2290 "With a Little Help" (Moogles): Mogwon hands the materials out in sequence 1; the crafting line
    // (sequence 2) places Moguppo, whom the work goes to, first and Mogwon second, as the game data has it.
    private const uint Mogwon = 1017351, Moguppo = 1017352;

    private static QuestScript LittleHelp() => new()
    {
        Quest = 65536 + 2290, Name = "With a Little Help", Giver = 1017171, TargetEnd = 1017171, GiverSpot = Npc(1017171, 500, 6254017),
        Product = 15725, Materials = 15751,
        Todos = new[]
        {
            Line(0, 1, "Speak with Mogwon in Asah.", Npc(Mogwon, 2, 6289633)),
            Line(1, 2, "Synthesize training supplies.", Npc(Moguppo, 20, 6289641), Npc(Mogwon, 2, 6289633)),
            new ScriptTodo(2, 3, 3, new[] { Npc(1017353, 40, 6289657), Npc(1017354, 110, 6289663), Npc(1017355, 160, 6289664) }, "Deliver the training supplies to the mentee moogles."),
            Line(3, 255, "Report to Mogek the Marvelous.", Npc(1017171, 500, 6254017)),
        },
    };

    [Fact]
    public void AfterCraftingBeforeTheReportTheWorkGoesToTheLinesFirstPlaceNotTheMaterialsNpc()
    {
        // The live log: 3 of 3 made next to Mogwon (2 yalms away, still marked: he was talked to three times); Moguppo,
        // whom they go to, stands 18 yalms off.
        var s = Snap(2, new[] { Obj(1, Mogwon, 2), Obj(2, Moguppo, 20) }, new[] { new TodoProgress(1, false, 3, 3, 0) }, new() { [15725] = 3 });
        var plan = Decide(LittleHelp(), s);
        Assert.Equal(StepKind.Talk, plan.Kind);
        Assert.Equal(Moguppo, plan.Target!.BaseId);
    }

    [Fact]
    public void AfterCraftingTheWayGoesToWhereTheWorkGoesWhenTheyArentInView()
    {
        var s = Snap(2, new[] { Obj(1, Mogwon, 2) }, new[] { new TodoProgress(1, false, 3, 3, 0) }, new() { [15725] = 3 });
        var plan = Decide(LittleHelp(), s);
        Assert.Equal(StepKind.Go, plan.Kind);
        Assert.Equal(6289641u, plan.Level);
    }

    [Fact]
    public void WithNothingToCraftFromTheMaterialsComeFirst()
    {
        // At the crafting line with neither materials nor anything made: whoever hands them out, though the other is nearer.
        var s = Snap(2, new[] { Obj(1, Mogwon, 30), Obj(2, Moguppo, 3) }, new[] { new TodoProgress(1, false, 0, 3, 0) });
        var plan = Decide(LittleHelp(), s);
        Assert.Equal(StepKind.Talk, plan.Kind);
        Assert.Equal(Mogwon, plan.Target!.BaseId);
        Assert.Contains("materials", plan.What);
    }

    [Fact]
    public void ShortOfMaterialsMidwayGoesBackToWhoeverHandsThemOut()
    {
        // A newer quest counts the product on its line: one made of three, nothing left to make more from.
        var s = Snap(2, new[] { Obj(1, Mogwon, 30), Obj(2, Moguppo, 3) }, new[] { new TodoProgress(1, false, 1, 3, 15725) }, new() { [15725] = 1 });
        var plan = Decide(LittleHelp(), s);
        Assert.Equal(StepKind.Talk, plan.Kind);
        Assert.Equal(Mogwon, plan.Target!.BaseId);
    }

    [Fact]
    public void TheGeneralRulesTakeOverOnceTheLinesFirstPlaceWasTriedWithoutEffect()
    {
        var q = LittleHelp();
        var moguppo = Obj(2, Moguppo, 20);
        var s = Snap(2, new[] { Obj(1, Mogwon, 2), moguppo }, new[] { new TodoProgress(1, false, 3, 3, 0) }, new() { [15725] = 3 });
        var m = new StepMemory();
        m.Observe(s.Signature(), T0);
        m.Tried.Add(moguppo.Key);
        m.Looked.Add(6289641);
        Assert.Equal(Mogwon, StepRules.Decide(q, s, m, T0).Target!.BaseId);
    }

    [Fact]
    public void AcceptedAsAGathererTheGatheredItemsGoToTheLinesFirstPlaceToo()
    {
        // The Namazu quests take either class: the crate holds the crafter's materials and is nobody to a gatherer.
        var q = new QuestScript
        {
            Quest = 65536 + 3103, Name = "The Importance of Being Yes, Yes", Giver = 1025602, TargetEnd = 1025602, GiverSpot = Npc(1025602, 500, 7380346),
            Product = 22725, Materials = 22699, GatherItems = new Dictionary<uint, uint> { [16] = 22621, [17] = 22647 },
            Todos = new[]
            {
                Line(0, 1, "Speak with the earnest Namazu.", Npc(1025800, 6, 700)),
                Line(1, 2, "Synthesize .", Npc(1025800, 6, 700), Npc(1025770, 1, 701)),
                Line(2, 255, "Report to Seigetsu the Enlightened.", Npc(1025602, 500, 7380346)),
            },
        };
        var s = Snap(2, new[] { Obj(1, 1025770, 1, name: "cumbersome crate"), Obj(2, 1025800, 6) }, new[] { new TodoProgress(1, false, 3, 3, 22621) },
            new() { [22621] = 3 }, job: Min, accept: Min);
        var plan = Decide(q, s);
        Assert.Equal(StepKind.Talk, plan.Kind);
        Assert.Equal(1025800u, plan.Target!.BaseId);
    }

    [Fact]
    public void OnlyALineWithTwoPlacesIsTheCraftingLine()
    {
        var q = LittleHelp();
        Assert.Null(StepRules.CraftLine(q, Snap(1)));
        Assert.NotNull(StepRules.CraftLine(q, Snap(2)));
        Assert.Null(StepRules.CraftLine(q, Snap(3)));
        // A quest that crafts nothing has none.
        Assert.Null(StepRules.CraftLine(Quest(Line(0, 1, "Deliver.", Npc(5, 1), Npc(6, 2))), Snap(1)));
    }

    // ------------------------------------------------------------------ items that only count high quality

    // Quest 3911 "The Heart of Tankiness" (Dwarves, the live log): the engine component only counts high quality (the
    // to-do line's item carries the mark, and the quest's script counts with the "high quality only" filter). The
    // crafting line is in the report sequence: Regitt, whom it goes to, first; the Fetchomatic Mechatoad with the
    // materials second.
    private const uint Mechatoad = 1033749, Engine = 31161, EngineMaterials = 31160;

    private static QuestScript Tankiness(bool scriptSaysHq = true) => new()
    {
        Quest = 65536 + 3911, Name = "The Heart of Tankiness", Giver = Giver, TargetEnd = Giver, GiverSpot = Npc(Giver, 2, 8368176),
        Product = Engine, Materials = EngineMaterials, ProductHq = scriptSaysHq,
        Todos = new[]
        {
            Line(0, 1, "Speak with Shigitt.", Npc(1033767, 13, 8373453)),
            Line(1, 255, "Synthesize .", Npc(Giver, 2, 8368176), Npc(Mechatoad, 13, 8371199)),
        },
    };

    private static StepObject[] RegittAndTheMechatoad() => new[]
    {
        Obj(1, Giver, 2, icon: 71235, name: "Regitt"),
        Obj(2, Mechatoad, 13, icon: 0, name: "Fetchomatic Mechatoad"),
    };

    [Fact]
    public void AnItemThatOnlyCountsHighQualityIsCraftedForFullQuality()
    {
        var s = Snap(255, RegittAndTheMechatoad(), new[] { new TodoProgress(1, false, 0, 1, 1_000_000 + Engine) }, new() { [EngineMaterials] = 1 });
        var plan = Decide(Tankiness(), s);
        Assert.Equal(StepKind.Craft, plan.Kind);
        Assert.True(plan.Hq);
        Assert.Equal(1, plan.Count);
        // The to-do line's mark alone says it too (a quest whose script wasn't read).
        Assert.True(Decide(Tankiness(scriptSaysHq: false), s).Hq);
    }

    [Fact]
    public void ANormalQualityCopyDoesntCountAndTheMaterialsAreFetchedAgain()
    {
        // The live log: one made, normal quality; the game still counts 0 of 1. The run talked to Regitt over and over.
        var s = Snap(255, RegittAndTheMechatoad(), new[] { new TodoProgress(1, false, 0, 1, 1_000_000 + Engine) }, new() { [Engine] = 1 });
        Assert.False(StepRules.Supplied(Tankiness(), s));
        var plan = Decide(Tankiness(), s);
        Assert.Equal(StepKind.Talk, plan.Kind);
        Assert.Equal(Mechatoad, plan.Target!.BaseId);

        // With new materials: crafted again, for full quality.
        var again = Snap(255, RegittAndTheMechatoad(), new[] { new TodoProgress(1, false, 0, 1, 1_000_000 + Engine) }, new() { [Engine] = 1, [EngineMaterials] = 1 });
        var craft = Decide(Tankiness(), again);
        Assert.Equal(StepKind.Craft, craft.Kind);
        Assert.True(craft.Hq);
        Assert.Equal(1, craft.Count);
    }

    [Fact]
    public void TheHighQualityCopyIsReported()
    {
        // One normal quality left over from before, one high quality: the game counts 1 of 1.
        var s = Snap(255, RegittAndTheMechatoad(), new[] { new TodoProgress(1, false, 1, 1, 1_000_000 + Engine) },
            new() { [Engine] = 2, [1_000_000 + Engine] = 1 });
        Assert.True(StepRules.Supplied(Tankiness(), s));
        var plan = Decide(Tankiness(), s);
        Assert.Equal(StepKind.Talk, plan.Kind);
        Assert.Equal(Giver, plan.Target!.BaseId);
    }

    [Fact]
    public void AfterTwoRoundsWithoutAHighQualityCopyThePlayerIsAsked()
    {
        var q = Tankiness();
        var s = Snap(255, RegittAndTheMechatoad(), new[] { new TodoProgress(1, false, 0, 1, 1_000_000 + Engine) }, new() { [Engine] = 2, [EngineMaterials] = 1 });
        var m = new StepMemory { HqMisses = 1 };
        m.Observe(s.Signature(), T0);
        Assert.Equal(StepKind.Craft, StepRules.Decide(q, s, m, T0).Kind);
        m.HqMisses = StepRules.MaxHqMisses;
        var plan = StepRules.Decide(q, s, m, T0);
        Assert.Equal(StepKind.AskUser, plan.Kind);
        Assert.Contains("high quality", plan.What);
    }

    [Fact]
    public void OlderQuestsThatOnlyCountHighQualityGoByTheGamesCount()
    {
        // Ixal and Moogle lines name no item: the script says high quality only, and the crafting line's own count
        // (0 of 3 here, though three normal-quality ones are in the bags) says they don't count.
        var q = new QuestScript
        {
            Quest = 65536 + 1523, Name = "A High Wind in Ishgard", Giver = IxalGiver, TargetEnd = IxalGiver, GiverSpot = IxalGiverSpot,
            Product = 8123, Materials = 8081, ProductHq = true,
            Todos = new[] { new ScriptTodo(3, 255, 1, new[] { IxalGiverSpot, NorbettauxSpot }, "Synthesize isolator components .") },
        };
        var s = new StepSnapshot
        {
            Quest = q.Quest, Accepted = true, Sequence = 255, AcceptJob = Crp, Job = Crp, Territory = Coerthas, Player = new Vector3(200, 302, -252),
            Objects = new[] { Norbettaux_() }, Todos = new[] { new TodoProgress(3, false, 0, 3, 0) },
            Items = new Dictionary<uint, int> { [8123] = 3 },
        };
        Assert.False(StepRules.Supplied(q, s));
        var plan = Decide(q, s);
        Assert.Equal(StepKind.Talk, plan.Kind);
        Assert.Equal(Norbettaux, plan.Target!.BaseId);
        Assert.Contains("materials", plan.What);

        // Three high-quality ones: the game counts them, and the quest is reported.
        var done = new StepSnapshot
        {
            Quest = q.Quest, Accepted = true, Sequence = 255, AcceptJob = Crp, Job = Crp, Territory = Coerthas, Player = new Vector3(200, 302, -252),
            Objects = new[] { Norbettaux_() }, Todos = new[] { new TodoProgress(3, false, 3, 3, 0) },
            Items = new Dictionary<uint, int> { [8123] = 3, [1_008_123] = 3 },
        };
        Assert.True(StepRules.Supplied(q, done));
        Assert.Equal(StepKind.Go, Decide(q, done).Kind);
    }

    [Fact]
    public void QuestsThatTakeAnyQualityArentCraftedForHighQuality()
    {
        var s = Snap(2, new[] { Obj(1, 1034104, 21) }, new[] { new TodoProgress(1, false, 1, 3, 31147) }, new() { [31147] = 1, [31146] = 2 });
        var plan = Decide(CraftQuest(), s);
        Assert.Equal(StepKind.Craft, plan.Kind);
        Assert.False(plan.Hq);
        Assert.False(StepRules.WantsHq(CraftQuest(), s));
    }

    [Theory]
    [InlineData(1_031_161u, true)]
    [InlineData(31_161u, false)]
    [InlineData(2_001_982u, false)]
    [InlineData(0u, false)]
    public void ALinesItemCarriesTheHighQualityMark(uint item, bool wantsHq)
        => Assert.Equal(wantsHq, new TodoProgress(1, false, 0, 1, item).WantsHq);

    [Fact]
    public void KeyItemsKeepTheirIdWholeAndHighQualityLosesItsMark()
    {
        // 2001982 is the key item "Training Supplies" the mentee moogles ask for: the hand-over compared it as 1982.
        Assert.Equal(2001982u, StepRules.BaseItem(2001982));
        Assert.Equal(15725u, StepRules.BaseItem(1015725));
        Assert.Equal(15725u, StepRules.BaseItem(15725));
    }

    [Fact]
    public void AGatheringLineInTheReportSequenceReportsOnceCounted()
    {
        var q = new QuestScript
        {
            Quest = 65536 + 4607, Giver = 1043417, TargetEnd = 1043417, GiverSpot = Npc(1043417, 400, 9308276),
            GatherItems = new Dictionary<uint, uint> { [Min] = 38281 },
            Todos = new[] { new ScriptTodo(0, 255, 1, new[] { Npc(1044042, 50), Npc(1043417, 400, 9308276) }, "Mine  and deliver it to Stigma-4.") },
        };
        var s = Snap(255, new[] { Obj(1, 1044042, 50) }, new[] { new TodoProgress(0, false, 3, 3, 38281) }, new() { [38281] = 3 }, job: Min, accept: Min);
        var plan = Decide(q, s);
        Assert.Equal(StepKind.Go, plan.Kind);
        Assert.Equal(9308276u, plan.Level);
    }

    [Fact]
    public void TheReportGoesToTheTargetEndWhenItIsntTheGiver()
    {
        var end = new ScriptSpot(5000001, Zone, new Vector3(80, 0, 0), 1050000, false);
        var q = new QuestScript
        {
            Quest = 65536 + 3902, Giver = Giver, TargetEnd = 1050000, GiverSpot = Npc(Giver, 500, 8368176), Product = 31147, Materials = 31146,
            Todos = new[] { new ScriptTodo(1, 255, 1, new[] { Npc(1034104, 21), end }, "Synthesize  and deliver it.") },
        };
        Assert.Equal(end, StepRules.TurnInSpot(q));
        var s = Snap(255, new[] { Obj(1, 1034104, 21), Obj(2, Giver, 30) }, new[] { new TodoProgress(1, false, 3, 3, 31147) }, new() { [31147] = 3 });
        var plan = Decide(q, s);
        Assert.Equal(StepKind.Go, plan.Kind);
        Assert.Equal(end.Level, plan.Level);
    }

    [Fact]
    public void QuestsThatNeitherCraftNorGatherReportAsBefore()
    {
        // An errand at its report: the marked NPC still counts (the rule is only for crafting and gathering).
        var q = Quest(Line(2, 255, "Report to Regitt."));
        Assert.False(StepRules.Supplied(q, Snap(255)));
        Assert.Equal(1034103u, Decide(q, Snap(255, new[] { Obj(1, 1034103, 5) })).Target!.BaseId);
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
    public void GatheringWithoutAnAreaGoesWhereTheScriptSends()
    {
        var q = new QuestScript
        {
            Quest = 65536 + 4611, Giver = 1043417, GiverSpot = new ScriptSpot(9308276, 960, new Vector3(315, 482, 148), 1043417, false),
            GatherItems = new Dictionary<uint, uint> { [Min] = 38282 }, Territories = new uint[] { 1073 },
            Todos = new[] { new ScriptTodo(0, 1, 1, new[] { new ScriptSpot(9357477, 960, new Vector3(195, 568, 251), 1043936, false) }, "Mine  from the Elysian fields.") },
        };
        var s = Snap(1, todos: new[] { new TodoProgress(0, false, 0, 3, 38282) }, job: Min, accept: Min);
        var plan = Decide(q, s);
        Assert.Equal(StepKind.Gather, plan.Kind);
        Assert.Equal(1073u, plan.Territory);
        Assert.Null(plan.Spot);
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
    public void SayIsSentOnceThenTheStepIsThePlayers()
    {
        var q = SayQuest();
        var s = Snap(1, new[] { Obj(1, 1044043, 10) }, new[] { new TodoProgress(0, false, 0, 2, 0) });
        var m = new StepMemory();
        m.Observe(s.Signature(), T0);

        var plan = StepRules.Decide(q, s, m, T0);
        Assert.Equal(StepKind.Say, plan.Kind);
        Assert.Equal("coffee break", plan.Text);
        Assert.True(plan.Public);
        m.Public.Note(plan.Key, T0);

        // A moment for the game to count it, then the player's: never said twice.
        Assert.Equal(StepKind.Wait, StepRules.Decide(q, s, m, T0.AddSeconds(10)).Kind);
        var after = StepRules.Decide(q, s, m, T0.AddSeconds(16));
        Assert.Equal(StepKind.AskUser, after.Kind);
        Assert.Contains("coffee break", after.What);
        Assert.Contains("do this step yourself", after.What);
    }

    [Fact]
    public void SayIsntSentAgainNearSomeoneElse()
    {
        var q = SayQuest();
        var m = new StepMemory();
        var first = Snap(1, new[] { Obj(1, 1044043, 10) }, new[] { new TodoProgress(0, false, 0, 2, 0) });
        m.Observe(first.Signature(), T0);
        m.Public.Note(StepRules.Decide(q, first, m, T0).Key, T0);
        // The other person of the line comes into view; nothing moved: still the same step.
        var other = Snap(1, new[] { Obj(2, 1044044, 5) }, new[] { new TodoProgress(0, false, 0, 2, 0) });
        Assert.Equal(StepKind.AskUser, StepRules.Decide(q, other, m, T0.AddSeconds(20)).Kind);
    }

    [Fact]
    public void ASecondPersonOfTheLineGetsThePhraseOnceTheFirstCounted()
    {
        var q = SayQuest();
        var m = new StepMemory();
        var first = Snap(1, new[] { Obj(1, 1044043, 10), Obj(2, 1044044, 20) }, new[] { new TodoProgress(0, false, 0, 2, 0) });
        m.Observe(first.Signature(), T0);
        var plan = StepRules.Decide(q, first, m, T0);
        m.Public.Note(plan.Key, T0);
        // It counted: the quest's variables moved, the first person is done.
        var counted = new StepSnapshot
        {
            Quest = first.Quest, Accepted = true, Sequence = 1, AcceptJob = Crp, Job = Crp, Territory = Zone,
            Variables = new byte[] { 0x10, 0, 0, 0, 0, 0 },
            Objects = new[] { Obj(1, 1044043, 10, icon: 0, active: false), Obj(2, 1044044, 20) },
            Todos = new[] { new TodoProgress(0, false, 1, 2, 0) },
        };
        m.Observe(counted.Signature(), T0.AddSeconds(3));
        var next = StepRules.Decide(q, counted, m, T0.AddSeconds(20));
        Assert.Equal(StepKind.Say, next.Kind);
        Assert.Equal(1044044u, next.Target!.BaseId);
        Assert.NotEqual(plan.Key, next.Key);
    }

    [Fact]
    public void SayGoesOnlyToWhomTheLineNames()
    {
        // A marked bystander (another quest's person, the giver) isn't the line's: the phrase isn't said near them.
        var q = SayQuest();
        var plan = Decide(q, Snap(1, new[] { Obj(1, 1043417, 3), Obj(2, 1050000, 4) }, new[] { new TodoProgress(0, false, 0, 2, 0) }));
        Assert.NotEqual(StepKind.Say, plan.Kind);
    }

    [Fact]
    public void APhraseForAPlaceIsSaidThereOnce()
    {
        // Moogles' "free kupo nuts", the Dwarves' "lali-ho": the line points at an area, nobody to say it to.
        var area = new ScriptSpot(9000001, Zone, new Vector3(50, 0, 0), 0, true);
        var q = new QuestScript
        {
            Quest = 65536 + 2295, Giver = 1017171, TargetEnd = 1017171,
            Todos = new[] { new ScriptTodo(0, 1, 1, new[] { area }, "Enter any phrase containing “free kupo nuts”.") },
            Say = new Dictionary<int, string> { [0] = "free kupo nuts" },
        };
        var s = Snap(1, Array.Empty<StepObject>(), new[] { new TodoProgress(0, false, 0, 1, 0) });
        var m = new StepMemory();
        m.Observe(s.Signature(), T0);

        var plan = StepRules.Decide(q, s, m, T0);
        Assert.Equal(StepKind.Say, plan.Kind);
        Assert.Null(plan.Target);
        Assert.Equal(area.Position, plan.Spot);
        m.Public.Note(plan.Key, T0);
        var after = StepRules.Decide(q, s, m, T0.AddSeconds(20));
        Assert.Equal(StepKind.AskUser, after.Kind);
        Assert.Contains("at the marked place", after.What);
    }

    [Fact]
    public void AnEmoteAtPeopleTheQuestDoesntNameIsThePlayers()
    {
        var quest = new TribeQuest(65536 + 2318, 8, "Moogle emote daily", TribeQuestKind.Daily, 3, 2, 1017171, 1017171,
            TribeKind.Crafter, 50, 50, StepKinds.Emote | StepKinds.Craft, null, Array.Empty<QuestGather>(), 0,
            Array.Empty<uint>(), (0, 0), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>());
        var judged = QuestPolicy.Judge(quest, TribeKind.Crafter);
        Assert.False(judged.RunCanDo);
        Assert.Contains("emote", judged.Reason);
    }

    [Fact]
    public void ASayTriedBeforeAStopIsntSentByANewStepper()
    {
        var store = new PublicAttemptStore();
        var q = SayQuest();
        var s = Snap(1, new[] { Obj(1, 1044043, 10) }, new[] { new TodoProgress(0, false, 0, 2, 0) });
        var before = new StepMemory { Public = store };
        before.Observe(s.Signature(), T0);
        store.Note(StepRules.Decide(q, s, before, T0).Key, T0);

        var after = new StepMemory { Public = store };
        after.Observe(s.Signature(), T0.AddMinutes(2));
        Assert.Equal(StepKind.Wait, StepRules.Decide(q, s, after, T0.AddMinutes(2)).Kind);
        Assert.Equal(StepKind.AskUser, StepRules.Decide(q, s, after, T0.AddMinutes(3)).Kind);
    }

    [Fact]
    public void ThePublicStoreStartsOverAtTheDailyReset()
    {
        var store = new PublicAttemptStore();
        var at = new DateTime(2026, 11, 23, 14, 0, 0, DateTimeKind.Utc);
        store.Note("k", at);
        Assert.Equal(1, store.Get("k", at.AddMinutes(59))!.Count);
        Assert.Null(store.Get("k", at.AddMinutes(61)));
        Assert.Equal(PublicAttemptStore.GameDay(at.AddHours(2)), PublicAttemptStore.GameDay(at.AddHours(20)));
    }

    [Fact]
    public void AnEmoteIsTriedOnceMoreAfterFifteenQuietSecondsThenThePlayer()
    {
        var q = EmoteQuest();
        var s = Snap(2, new[] { Obj(2, 1034000, 10) });
        var m = new StepMemory();
        m.Observe(s.Signature(), T0);
        var plan = StepRules.Decide(q, s, m, T0);
        Assert.Equal(StepKind.Emote, plan.Kind);
        m.Public.Note(plan.Key, T0);
        Assert.Equal(StepKind.Wait, StepRules.Decide(q, s, m, T0.AddSeconds(10)).Kind);
        var retry = StepRules.Decide(q, s, m, T0.AddSeconds(16));
        Assert.Equal(StepKind.Emote, retry.Kind);
        m.Public.Note(retry.Key, T0.AddSeconds(16));
        Assert.Equal(StepKind.Wait, StepRules.Decide(q, s, m, T0.AddSeconds(20)).Kind);
        Assert.Equal(StepKind.AskUser, StepRules.Decide(q, s, m, T0.AddSeconds(40)).Kind);
    }

    [Fact]
    public void ProgressElsewhereDelaysTheRetry()
    {
        var tried = new Attempt { Count = 1, Last = T0 };
        Assert.Equal(StepRules.PublicTry.NotYet, StepRules.PublicVerdict(StepKind.Emote, tried, T0.AddSeconds(10), T0.AddSeconds(20)));
        Assert.Equal(StepRules.PublicTry.Yes, StepRules.PublicVerdict(StepKind.Emote, tried, T0.AddSeconds(10), T0.AddSeconds(26)));
        Assert.Equal(StepRules.PublicTry.Yes, StepRules.PublicVerdict(StepKind.Emote, null, T0, T0));
        Assert.Equal(StepRules.PublicTry.GiveUp, StepRules.PublicVerdict(StepKind.Emote, new Attempt { Count = 2, Last = T0 }, T0, T0.AddHours(1)));
        Assert.Equal(StepRules.PublicTry.GiveUp, StepRules.PublicVerdict(StepKind.Say, tried, T0, T0.AddSeconds(26)));
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

    private static QuestScript EmoteQuest() => new()
    {
        Quest = 65536 + 3919, Giver = Giver,
        Todos = new[] { Line(1, 2, "/psych the dwarf.", Npc(1034000, 10)) },
        Emotes = new Dictionary<int, EmoteFact> { [1] = new EmoteFact(30, "/psych") },
    };

    [Fact]
    public void EmoteTargetsTheMarkedPerson()
    {
        var q = EmoteQuest();
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
            Todos = new[] { Line(0, 1, "Place the explosive at the designated location.", new ScriptSpot(8400001, Zone, new Vector3(10, 0, 0), 2011166, false)) },
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
    public void AGroundItemIsntThrownAtWhatTheLineDoesntName()
    {
        var q = new QuestScript
        {
            Quest = 65536 + 3912, Giver = Giver,
            Todos = new[] { Line(0, 1, "Place the explosive at the designated location.", new ScriptSpot(8400001, Zone, new Vector3(10, 0, 0), 2011166, false)) },
            KeyItems = new[] { Item(2003030, "Precision Explosive", ground: true, range: 10) },
        };
        // Another active object of the quest (the other barrel of a random pick) in view, the named one not.
        var s = Snap(1, new[] { Obj(1, 2011167, 10, icon: 0, kind: StepObjectKind.Object) }, items: new() { [2003030] = 1 });
        var plan = Decide(q, s);
        Assert.Equal(StepKind.Go, plan.Kind);
    }

    [Fact]
    public void AnItemTheLineDoesntNameIsntUsedOnObjectsTheLineDoesntName()
    {
        var q = new QuestScript
        {
            Quest = 65536 + 5343, Giver = Giver,
            Todos = new[] { Line(1, 2, "Treat the designated locations.") },
            KeyItems = new[] { Item(2003777, "Melting Salts") },
        };
        var s = Snap(2, new[] { Obj(2, 2014944, 10, icon: 0, kind: StepObjectKind.Object) }, items: new() { [2003777] = 3 });
        Assert.NotEqual(StepKind.UseItem, Decide(q, s).Kind);
    }

    [Fact]
    public void AnItemTheLineDoesntNameIsUsedOnObjectsWhenItsTheOnlyOne()
    {
        var q = new QuestScript
        {
            Quest = 65536 + 5343, Giver = Giver,
            Todos = new[] { Line(1, 2, "Treat the designated locations.", new ScriptSpot(11955068, 1297, new Vector3(10, 0, 0), 2014944, false)) },
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
    public void RidingWithNothingLeftBeforeTheReportAsksThePlayer()
    {
        var q = MountQuest();
        var s = Snap(2, mounted: true, mount: 227);
        var m = new StepMemory();
        m.Observe(s.Signature(), T0);
        m.Looked.Add(1034065 + 7);
        Assert.Equal(StepKind.AskUser, StepRules.Decide(q, s, m, T0).Kind);
    }

    [Fact]
    public void TheQuestMountIsGotBackTwiceAtMost()
    {
        var q = MountQuest();
        var s = Snap(2, new[] { Obj(1, 1033715, 10) });
        var m = new StepMemory { MountGiver = 1033715, Mounts = 2 };
        m.Observe(s.Signature(), T0);
        Assert.Equal(StepKind.Talk, StepRules.Decide(q, s, m, T0).Kind);
        m.Mounts = 3;
        Assert.Equal(StepKind.AskUser, StepRules.Decide(q, s, m, T0).Kind);
    }

    [Fact]
    public void RidingCarryingAndTheFacilityArentProgress()
    {
        var walking = Snap(2, new[] { Obj(1, 1033715, 10) });
        var riding = Snap(2, new[] { Obj(1, 1033715, 10) }, mounted: true, mount: 227, statuses: new() { 404, 549 });
        Assert.Equal(walking.Signature(), riding.Signature());
    }

    [Fact]
    public void RidingWithTargetsOutOfViewFollowsTheMarker()
    {
        var markers = new[] { new StepMarker(Zone, new Vector3(-295, 99, -724), 1, 71223, 8385036, 0) };
        var plan = Decide(MountQuest(), Snap(2, markers: markers, mounted: true, mount: 227));
        Assert.Equal(StepKind.Go, plan.Kind);
        Assert.True(plan.NoFly);
    }

    [Fact]
    public void RidingDoesntGoBackToWhoeverGaveTheMountWhileAnotherPlaceIsMarked()
    {
        // Quest 2297 "A Little Pick-me-up" (the live log): the moofy moogle who gives the cloud mallow stays marked (the
        // lines' second place), nearer than the area to ride to; the run flew 418 yalms back to him to look around.
        var moofy = new ScriptSpot(6288268, Zone, new Vector3(-332, 59, 323), 1017322, false);
        var q = new QuestScript
        {
            Quest = 65536 + 2297, Giver = 1017171, TargetEnd = 1017171, Mount = new MountFact(89, 6294, 0, 6, false, true),
            Todos = new[]
            {
                new ScriptTodo(0, 1, 1, new[] { moofy }, "Speak with the moofy moogle."),
                new ScriptTodo(1, 2, 1, new[] { new ScriptSpot(6323508, Zone, new Vector3(-741, 74, 397), 0, true), moofy }, "Ride the cloud mallow and use its Seed ability at Zenith."),
                new ScriptTodo(2, 3, 1, new[] { new ScriptSpot(6323513, Zone, new Vector3(-72, 75, -273), 0, true), moofy }, "Ride the cloud mallow and use its Seed ability at the Rookery."),
            },
        };
        var markers = new[]
        {
            new StepMarker(Zone, new Vector3(-332, 59, 323), 1, 71023, 6288268, 0),
            new StepMarker(Zone, new Vector3(-72, 75, -273), 50, 60491, 6323513, 0),
        };
        var s = new StepSnapshot
        {
            Quest = q.Quest, Accepted = true, Sequence = 3, AcceptJob = Crp, Job = Crp, Territory = Zone, Player = new Vector3(-737, 84, 426),
            Markers = markers, Mounted = true, Mount = 89,
        };
        var plan = Decide(q, s);
        Assert.Equal(StepKind.Go, plan.Kind);
        Assert.Equal(6323513u, plan.Level);

        // With nowhere else marked, his place is still gone to (better than asking).
        var only = new StepSnapshot
        {
            Quest = q.Quest, Accepted = true, Sequence = 3, AcceptJob = Crp, Job = Crp, Territory = Zone, Player = new Vector3(-737, 84, 426),
            Markers = new[] { markers[0] }, Mounted = true, Mount = 89,
        };
        var m = new StepMemory();
        m.Observe(only.Signature(), T0);
        m.Looked.Add(6323513);
        Assert.Equal(6288268u, StepRules.Decide(q, only, m, T0).Level);
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
        m.Observe(s.Signature(), T0);
        m.Tried.Add(1);
        Assert.Equal(2u, StepRules.Decide(q, s, m, T0).Target!.Id);

        var after = Snap(1, trees, items: new() { [2001991] = 1 });
        Assert.True(m.Observe(after.Signature(), T0.AddSeconds(5)));
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
    public void TheGiverIsntTalkedToBeforeTheReportUnlessTheLineNamesThem()
    {
        // Right after accepting: the giver still shows an icon, but talking to them would open their daily list.
        var q = Quest(Line(0, 1, "Speak with the tinkerer.", Npc(1034103, 400)));
        var plan = Decide(q, Snap(1, new[] { Obj(1, Giver, 1) }));
        Assert.NotEqual(StepKind.Talk, plan.Kind);

        var named = Quest(Line(0, 1, "Speak with Regitt again.", Npc(Giver, 1)));
        Assert.Equal(Giver, Decide(named, Snap(1, new[] { Obj(1, Giver, 1) })).Target!.BaseId);

        var report = Quest(Line(2, 255, "Report to Regitt."));
        Assert.Equal(Giver, Decide(report, Snap(255, new[] { Obj(1, Giver, 1) })).Target!.BaseId);
    }

    [Fact]
    public void OnlyTheQuestsOwnIconMarksAPerson()
    {
        // The game draws "dailies available" over a giver (another handler's icon); this quest gives them none.
        var q = Quest(Line(0, 1, "Speak with someone.", Area(1, 50)));
        var drawnOnly = Obj(1, 1034999, 2, icon: 0) with { Drawn = 71221 };
        Assert.NotEqual(StepKind.Talk, Decide(q, Snap(1, new[] { drawnOnly })).Kind);
    }

    [Fact]
    public void OtherThingsTheQuestHoldsArentTalkedTo()
    {
        var q = Quest(Line(0, 1, "Search the area.", Area(1, 50)));
        var s = Snap(1, new[] { Obj(1, 30001, 2, kind: StepObjectKind.Other) });
        Assert.NotEqual(StepKind.Talk, Decide(q, s).Kind);
    }

    [Fact]
    public void ADecoyStaysTriedOnItsLinesAfterProgress()
    {
        var q = Quest(new ScriptTodo(0, 1, 3, new[] { Area(1, 50) }, "Inspect the artworks."));
        var objects = new[] { Obj(1, 2007249, 10, icon: 0, kind: StepObjectKind.Object), Obj(2, 2007250, 20, icon: 0, kind: StepObjectKind.Object) };
        var s = Snap(1, objects, new[] { new TodoProgress(0, false, 0, 3, 0) });
        var m = new StepMemory();
        m.Observe(s.Signature(), T0);
        m.Decoy(StepRules.LineKey(q, s), objects[0].Key);
        Assert.Equal(2u, StepRules.Decide(q, s, m, T0).Target!.Id);
        // One of the three counted: the decoy stays one (its scene isn't played again).
        var after = Snap(1, objects, new[] { new TodoProgress(0, false, 1, 3, 0) });
        Assert.True(m.Observe(after.Signature(), T0.AddSeconds(5)));
        Assert.Equal(2u, StepRules.Decide(q, after, m, T0.AddSeconds(5)).Target!.Id);
        // On other lines it's forgotten.
        Assert.False(m.IsDecoy(StepRules.LineKey(q, Snap(2, objects)), objects[0].Key));
    }

    [Fact]
    public void AnObjectIsRememberedByItsPlaceInTheLayout()
    {
        var a = Obj(1, 2007249, 10) with { Instance = 77 };
        var reloaded = Obj(9, 2007249, 10) with { Instance = 77 };
        Assert.Equal(a.Key, reloaded.Key);
        Assert.NotEqual(a.Key, (Obj(1, 2007249, 10) with { Instance = 78 }).Key);
        Assert.Equal(5ul, Obj(5, 2007249, 10).Key);
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
        m.Observe(s.Signature(), T0);
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
    [InlineData("Commence “Marks of Friendship”?", "Marks of Friendship")]
    [InlineData("Commence  “A Feast of Indecision”? ", "A Feast of Indecision")]
    [InlineData("Commence?", null)]
    [InlineData("Proceed to Hoper's Hold?", null)]
    public void TheCommenceQuestionNamesItsQuest(string prompt, string? quest)
        => Assert.Equal(quest, StepRules.Filled(prompt, "Commence “”?"));

    [Fact]
    public void AMenuIsLeftByItsOwnNothingEntry()
    {
        var leave = new[] { "Nothing", "Nothing.", "Cancel" };
        Assert.Equal(2, StepRules.LeaveEntry(new[] { "Daily Quests", "Brainstorm", "Nothing." }, leave));
        Assert.Equal(0, StepRules.LeaveEntry(new[] { "Cancel", "Marks of Friendship" }, leave));
        // The last entry by position isn't a way out when it's a quest.
        Assert.Equal(-1, StepRules.LeaveEntry(new[] { "Daily Quests", "Marks of Friendship" }, leave));
    }

    [Fact]
    public void TheBusyWatchAsksAfterItsLimitWithNothingChanging()
    {
        var w = new BlockWatch();
        Assert.False(w.Feed(true, "1|00", T0));
        Assert.False(w.Feed(true, "1|00", T0 + BlockWatch.Limit));
        Assert.True(w.Feed(true, "1|00", T0 + BlockWatch.Limit + TimeSpan.FromSeconds(1)));
        // It starts over after saying so.
        Assert.False(w.Feed(true, "1|00", T0 + BlockWatch.Limit + TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void TheBusyWatchStartsOverOnAChangeOrWhenTheGameIsFree()
    {
        var w = new BlockWatch();
        w.Feed(true, "1|00", T0);
        Assert.False(w.Feed(true, "2|00", T0.AddSeconds(70)));
        Assert.False(w.Feed(true, "2|00", T0.AddSeconds(140)));
        Assert.False(w.Feed(false, "2|00", T0.AddSeconds(141)));
        Assert.False(w.Feed(true, "2|00", T0.AddSeconds(142)));
        Assert.False(w.Feed(true, "2|00", T0.AddSeconds(200)));
        Assert.True(w.Feed(true, "2|00", T0.AddSeconds(220)));
    }

    [Theory]
    [InlineData("Do you really want to trade a high-quality item?", true)]
    [InlineData("Do you  really want to trade a high-quality item? ", true)]
    [InlineData("Commence “Marks of Friendship”?", true)]
    [InlineData("Are you sure you wish to part ways with Dreamingway?", false)]
    [InlineData("Commence?", false)]
    public void KnownQuestionsOnly(string prompt, bool known)
        => Assert.Equal(known, StepRules.KnownQuestion(prompt, new[] { "Do you really want to trade a high-quality item?", "Commence “”?" }));

    /// <summary>The questions a knock-out brings up (Addon 111/196, 117, 112, 3787), split around what the game fills in.</summary>
    private static readonly (string Before, string After, bool Hole)[] KnockOut =
    {
        ("Return to ", "?", true), ("Return to your home point?", "", false), ("Accept Raise?", "", false), ("Accept Raise from ", "?", true),
    };

    [Theory]
    [InlineData("Return to New Gridania?", true)]
    [InlineData("Return to  Camp Dragonhead ?", true)]
    [InlineData("return to your home point?", true)]
    [InlineData("Accept Raise?", true)]
    [InlineData("Accept Raise from Y'shtola Rhul?", true)]
    // Nothing filled in, or only one half of the text: not that question.
    [InlineData("Return to ?", false)]
    [InlineData("Return to", false)]
    [InlineData("Return to New Gridania", false)]
    // A quest's own question, left open when the character went down, is never taken for it.
    [InlineData("Hand over the rigging component materials?", false)]
    [InlineData("Will you return to the fight?", false)]
    [InlineData("", false)]
    public void TheQuestionsAKnockOutBringsUpAreKnownByTheirShape(string prompt, bool known)
        => Assert.Equal(known, StepRules.FitsShape(prompt, KnockOut));

    [Fact]
    public void ATextThatIsAllFilledInSaysNothing()
    {
        // A sheet text with nothing before or after what the game fills in can't tell one question from another.
        Assert.False(StepRules.FitsShape("Anything at all?", new[] { ("", "?", true) }));
        Assert.False(StepRules.FitsShape("Anything at all?", new[] { ("", "", true), ("", "", false) }));
        // A language that puts the place first: the rest of the sentence is enough to go by.
        Assert.True(StepRules.FitsShape("グリダニアへ戻りますか？", new[] { ("", "へ戻りますか？", true) }));
        Assert.False(StepRules.FitsShape("へ戻りますか？", new[] { ("", "へ戻りますか？", true) }));
    }
}
