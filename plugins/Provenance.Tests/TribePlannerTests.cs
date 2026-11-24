using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Provenance.Core.Tribes;
using Xunit;

namespace Provenance.Tests;

public class TribePlannerTests
{
    // ------------------------------------------------------------------ fixtures

    private const uint Here = 9999;

    private static QuestCraft CraftOf(uint product, uint equip = 0)
        => new(product, product + 1,
            Enumerable.Range(8, 8).Select(j => new QuestRecipe(36000u + (uint)j, (uint)j, 90, new[] { (10u, 8), (9u, 8) }, equip)).ToList(), false);

    private static TribeQuest Daily(uint tribe, ushort number, uint giver, int reputation = 60, TribeKind classes = TribeKind.Crafter, int level = 90,
        bool craft = true, StepKinds steps = StepKinds.None, uint equip = 0)
        => new(0x10000u + number, tribe, $"Quest {number}", TribeQuestKind.Daily, 1, 1, giver, giver, classes, level, reputation,
            steps | (craft ? StepKinds.Craft : 0), craft ? CraftOf(40000u + number, equip) : null, Array.Empty<QuestGather>(),
            0, Array.Empty<uint>(), (0, 0), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>());

    private static Tribe Society(uint id, int maxRank = 8, uint territory = 100, TribeKind kind = TribeKind.Crafter, params TribeGiver[] givers)
        => new(id, $"Society {id}", 5, kind, maxRank, 1, 0, territory, "Place", 0, 90,
            givers.Length > 0 ? givers : new[] { Giver(id * 100) },
            Array.Empty<TribeUnlockStep>(),
            new[] { new TribeRankQuest(0x10000u + 9000 + id, $"Rank quest {id}", 3, 1, "Npc", territory, "Place", Vector2.Zero, Array.Empty<(uint, string)>()) },
            0, "", Array.Empty<TribeShopItem>(), Array.Empty<TribeAchievement>(), Array.Empty<TribeQuest>());

    private static TribeGiver Giver(uint npc, int minRank = 1, int maxRank = 8) => new(npc, $"Giver {npc}", 100, "Place", Vector3.Zero, minRank, maxRank);

    private static TribeToday Day(Tribe t, int rank = 3, int value = 0, int needed = 1000, bool rankedUp = false, int taken = 0,
        params TribeOffer[] offers)
        => new(t, rank, rankedUp, value, needed, false, new[] { new GiverToday(t.Givers[0], offers, taken) });

    private static TribeOffer Open(TribeQuest q) => new(q, OfferState.Open);

    private static List<ClassInfo> Crafters(uint current = 8, int level = 100)
        => Enumerable.Range(8, 8).Select(j => new ClassInfo((uint)j, j == 9 ? level - 5 : level, true, j == 10 ? 720 : 700)).ToList();

    private static TribePlan Plan(IReadOnlyList<TribeToday> days, int allowances = 12, TribeGoal? goal = null, IReadOnlyList<AcceptedTribeQuest>? accepted = null,
        List<ClassInfo>? classes = null, uint current = 8, bool leaveOutSay = false, Dictionary<uint, string>? excluded = null, Func<uint, bool>? owns = null,
        Func<TribeQuest, int?>? craftsLeft = null, HashSet<uint>? leftOut = null)
        => TribePlanner.Plan(new TribePlanInput
        {
            Days = days, Accepted = accepted ?? Array.Empty<AcceptedTribeQuest>(), Allowances = allowances, Goal = goal,
            LeftOut = leftOut ?? new HashSet<uint>(), LeaveOutSay = leaveOutSay, Classes = classes ?? Crafters(current), CurrentJob = current,
            Territory = Here, Owns = owns ?? (_ => true), ItemName = id => $"Item {id}", ItemNamePlural = id => $"Items {id}", ClassName = id => $"Class {id}",
            CraftsLeft = craftsLeft ?? (_ => null), Excluded = excluded ?? new Dictionary<uint, string>(),
        });

    private static TribeToday Three(uint id, int rank, int value = 0, int needed = 1000, bool rankedUp = false, int taken = 0, int maxRank = 8)
    {
        var t = Society(id, maxRank);
        var npc = t.Givers[0].NpcId;
        return Day(t, rank, value, needed, rankedUp, taken,
            Open(Daily(id, (ushort)(id * 10 + 1), npc)), Open(Daily(id, (ushort)(id * 10 + 2), npc)), Open(Daily(id, (ushort)(id * 10 + 3), npc)));
    }

    // ------------------------------------------------------------------ goals and order

    [Fact]
    public void RankUpTakesSocietiesBelowTheTopFirstTheLowestFirst()
    {
        var top = Three(1, 8, needed: 0);
        var high = Three(2, 6);
        var low = Three(3, 2);
        var plan = Plan(new[] { top, high, low }, goal: TribeGoal.RankUp);
        Assert.Equal(new uint[] { 3, 2, 1 }, plan.Visits.Select(v => v.Tribe.Id).ToArray());
    }

    [Fact]
    public void TheGoalIsRankUpWhileASocietyIsBelowTheTopThenCurrency()
    {
        Assert.Equal(TribeGoal.RankUp, Plan(new[] { Three(1, 8, needed: 0), Three(2, 5) }).Goal);
        Assert.Equal(TribeGoal.Currency, Plan(new[] { Three(1, 8, needed: 0), Three(2, 8, needed: 0) }).Goal);
        // A society left out doesn't count.
        Assert.Equal(TribeGoal.Currency, Plan(new[] { Three(1, 8, needed: 0), Three(2, 5) }, leftOut: new HashSet<uint> { 2 }).Goal);
    }

    [Fact]
    public void AFullBarIsSkippedUnderRankUpWithANoticeAndItsAllowancesGoElsewhere()
    {
        var full = Three(1, 3, value: 1000, needed: 1000);
        var other = Three(2, 5);
        var plan = Plan(new[] { full, other }, allowances: 3, goal: TribeGoal.RankUp);
        Assert.Equal(new uint[] { 2 }, plan.Visits.Select(v => v.Tribe.Id).ToArray());
        Assert.Equal(3, plan.ToAccept);
        var notice = Assert.Single(plan.Notices);
        Assert.Equal(1u, notice.Tribe.Id);
        Assert.StartsWith("Rank quest ready: do it yourself for now", notice.Text);
    }

    [Fact]
    public void CurrencyAndLevellingKeepAFullBar()
    {
        var full = Three(1, 3, value: 1000, needed: 1000);
        Assert.Equal(3, Plan(new[] { full }, goal: TribeGoal.Currency).ToAccept);
        Assert.Equal(3, Plan(new[] { full }, goal: TribeGoal.Levelling).ToAccept);
        Assert.Single(Plan(new[] { full }, goal: TribeGoal.Currency).Notices);
    }

    [Fact]
    public void RankUpGivesTheAllowancesToTheDailiesThatFillABarFirst()
    {
        // Society 1: 60 reputation each, 50 to go, so one fills it. Society 2 needs all three.
        var near = Three(1, 2, value: 950, needed: 1000);
        var far = Three(2, 3);
        var plan = Plan(new[] { near, far }, allowances: 4, goal: TribeGoal.RankUp);
        Assert.Equal(1, plan.Visits.Where(v => v.Tribe.Id == 1).Sum(v => v.ToAccept.Count()));
        Assert.Equal(3, plan.Visits.Where(v => v.Tribe.Id == 2).Sum(v => v.ToAccept.Count()));
        Assert.Equal(2, plan.LeftOut.Count(l => l.Tribe.Id == 1 && l.Reason.StartsWith("Not needed for the next rank")));
    }

    [Fact]
    public void RankUpGivesTheSpareAllowancesToTheDailiesTheBarDoesntNeed()
    {
        var near = Three(1, 2, value: 950, needed: 1000);
        var far = Three(2, 3);
        var plan = Plan(new[] { near, far }, allowances: 12, goal: TribeGoal.RankUp);
        Assert.Equal(6, plan.ToAccept);
        Assert.Empty(plan.LeftOut);
        // The spare ones join the society's visit (same giver, same class), after the one that fills the bar.
        Assert.Equal(new uint[] { 1, 2 }, plan.Visits.Select(v => v.Tribe.Id).ToArray());
        Assert.Equal(new ushort[] { 11, 12, 13 }, plan.Visits[0].Quests.Select(q => q.Quest.Number).ToArray());
        // Currency takes all three too.
        Assert.Equal(3, Plan(new[] { near }, goal: TribeGoal.Currency).ToAccept);
    }

    [Fact]
    public void RankUpCountsTheDailiesLeftOverFromBeforeTheResetTowardsTheBar()
    {
        // 900 of 1000, and a 60-reputation daily from before the reset in the journal: one new daily fills the bar.
        var near = Three(1, 2, value: 900, needed: 1000);
        var far = Three(2, 3);
        var leftover = Daily(1, 19, near.Tribe.Givers[0].NpcId);
        var plan = Plan(new[] { near, far }, allowances: 4, goal: TribeGoal.RankUp,
            accepted: new[] { new AcceptedTribeQuest(leftover, 1, 8, false) });
        Assert.Null(plan.Blocked);
        Assert.True(plan.Visits[0].Leftover);
        Assert.Equal(1, plan.Visits.Where(v => v.Tribe.Id == 1).Sum(v => v.ToAccept.Count()));
        Assert.Equal(3, plan.Visits.Where(v => v.Tribe.Id == 2).Sum(v => v.ToAccept.Count()));
    }

    [Fact]
    public void UnderCurrencyTheIxalGiversThatPayNoOaknotsComeLastButKeepTheirPlace()
    {
        // The game data says which givers' dailies pay the society's currency: of the Ixal's, the Respected and Honored ones.
        var low = Giver(501, 1, 1) with { PaysCurrency = false };
        var respected = Giver(505, 5, 5);
        var ixal = Society(QuestPolicy.IxalId, 7, 100, TribeKind.Crafter, low, respected) with { CurrencyItem = 21073 };
        var ixalDay = new TribeToday(ixal, 6, false, 0, 1000, false, new[]
        {
            new GiverToday(low, new[] { Open(Daily(5, 1501, 501)), Open(Daily(5, 1502, 501)), Open(Daily(5, 1503, 501)) }, 0),
            new GiverToday(respected, new[] { Open(Daily(5, 1551, 505)), Open(Daily(5, 1552, 505)), Open(Daily(5, 1553, 505)) }, 0),
        });
        var other = Three(1, 3);

        // Six allowances: the Respected giver's three and the other society's three; the low giver's wait.
        var plan = Plan(new[] { ixalDay, other }, allowances: 6, goal: TribeGoal.Currency);
        Assert.Equal(6, plan.ToAccept);
        Assert.DoesNotContain(plan.Quests, q => q.Quest.Giver == 501);
        // The reason names the currency (the game's plural: "Ixali Oaknots").
        Assert.Equal(3, plan.LeftOut.Count(l => l.Quest.Giver == 501
            && l.Reason == "No allowances left for it today: this giver's dailies pay no Items 21073, so they come last under Currency"));

        // With allowances for all, the low giver's dailies are taken where the Ixal are on the tour.
        plan = Plan(new[] { ixalDay, other }, allowances: 12, goal: TribeGoal.Currency);
        Assert.Equal(9, plan.ToAccept);
        Assert.Equal(new uint[] { 505, 501, 100 }, plan.Visits.Select(v => v.Giver.NpcId).ToArray());

        // Other goals take them in the usual order (the giver of the highest rank first, then the others).
        Assert.Equal(new uint[] { 505, 501, 100 },
            Plan(new[] { ixalDay, other }, allowances: 12, goal: TribeGoal.Levelling).Visits.Select(v => v.Giver.NpcId).ToArray());
    }

    [Fact]
    public void AmongEqualsTheSocietyOfTheZoneYouAreInComesFirst()
    {
        var a = Three(1, 3);
        var b = Three(2, 3);
        b = b with { Tribe = b.Tribe with { Territory = Here } };
        var plan = Plan(new[] { a, b }, goal: TribeGoal.Currency);
        Assert.Equal(2u, plan.Visits[0].Tribe.Id);
    }

    // ------------------------------------------------------------------ allowances and caps

    [Fact]
    public void AllowancesCapTheNewDailies()
    {
        var plan = Plan(new[] { Three(1, 3), Three(2, 3) }, allowances: 4, goal: TribeGoal.Currency);
        Assert.Equal(4, plan.ToAccept);
        Assert.Equal(2, plan.LeftOut.Count(l => l.Reason == "No allowances left for it today"));
    }

    [Fact]
    public void AGiverGivesThreeADayAndSixOnARankUpDay()
    {
        // Three taken this morning: the day's three are gone.
        var normal = Three(1, 3, taken: 3);
        Assert.Equal(0, Plan(new[] { normal }, goal: TribeGoal.Currency).ToAccept);
        // The day of a rank-up: three more of the new rank.
        var rankUp = Three(1, 4, taken: 3, rankedUp: true);
        Assert.Equal(3, Plan(new[] { rankUp }, goal: TribeGoal.Currency).ToAccept);
    }

    [Fact]
    public void NoAllowancesLeftBlocksNewDailiesButNotTheJournal()
    {
        var day = Three(1, 3);
        var inJournal = Daily(1, 19, day.Tribe.Givers[0].NpcId);
        var plan = Plan(new[] { day }, allowances: 0, accepted: new[] { new AcceptedTribeQuest(inJournal, 1, 8, true) });
        Assert.NotNull(plan.Blocked);
        Assert.Equal(0, plan.ToAccept);
        var only = Assert.Single(plan.Quests);
        Assert.True(only.Accepted);
    }

    // ------------------------------------------------------------------ the journal

    [Fact]
    public void LeftoversComeFirst()
    {
        var a = Three(1, 3);
        var b = Three(2, 3);
        var leftover = Daily(2, 29, b.Tribe.Givers[0].NpcId);
        var plan = Plan(new[] { a, b }, goal: TribeGoal.Currency, accepted: new[] { new AcceptedTribeQuest(leftover, 1, 9, false) });
        Assert.True(plan.Visits[0].Leftover);
        Assert.Equal(leftover.RowId, plan.Visits[0].Quests.Single().Quest.RowId);
        Assert.Null(plan.Blocked);
        // New dailies are planned after it (it's handed in first).
        Assert.Equal(6, plan.ToAccept);
    }

    [Fact]
    public void ALeftoverTheRunCantDoBlocksNewDailies()
    {
        var a = Three(1, 3);
        var snipe = Daily(1, 3124, a.Tribe.Givers[0].NpcId, craft: false, steps: StepKinds.Snipe);
        var plan = Plan(new[] { a }, accepted: new[] { new AcceptedTribeQuest(snipe, 1, 8, false) });
        Assert.Equal(0, plan.ToAccept);
        Assert.Contains("\"Quest 3124\"", plan.Blocked);
        Assert.Contains(plan.LeftOut, l => l.Quest.Number == 3124);
    }

    [Fact]
    public void TodaysAcceptedDailiesGoWithTheirGiverAndTakeNoAllowance()
    {
        var day = Three(1, 3, taken: 1);
        var mine = Daily(1, 19, day.Tribe.Givers[0].NpcId);
        var plan = Plan(new[] { day }, allowances: 2, goal: TribeGoal.Currency, accepted: new[] { new AcceptedTribeQuest(mine, 1, 12, true) });
        var visit = Assert.Single(plan.Visits);
        // In the journal first (as the class it was accepted as), then the new ones, within the giver's three a day.
        Assert.Equal(mine.RowId, visit.Quests[0].Quest.RowId);
        Assert.Equal(12u, visit.Quests[0].ClassJob);
        Assert.Equal(2, plan.ToAccept);
    }

    [Fact]
    public void AJournalQuestOnAClassWithoutAGearsetIsLeftOut()
    {
        var day = Three(1, 3);
        var mine = Daily(1, 19, day.Tribe.Givers[0].NpcId);
        var classes = Crafters().Select(c => c.ClassJob == 12 ? c with { HasGearset = false } : c).ToList();
        var plan = Plan(new[] { day }, classes: classes, accepted: new[] { new AcceptedTribeQuest(mine, 1, 12, true) });
        Assert.Contains(plan.LeftOut, l => l.Quest.RowId == mine.RowId && l.Reason.Contains("no saved gearset"));
    }

    [Fact]
    public void AGiverThatListsNoneOfItsOpenDailiesHoldsThemBackForTheLeftovers()
    {
        // Allowances left, room at the giver, open offers, dailies in the journal, and none listed: held back.
        Assert.True(TribeLeftovers.HeldBack(allowances: 8, listed: 0, open: 3, room: 3, inJournal: 2));
        // Any of these explains an empty list without a leftover.
        Assert.False(TribeLeftovers.HeldBack(allowances: 0, listed: 0, open: 3, room: 3, inJournal: 2));
        Assert.False(TribeLeftovers.HeldBack(allowances: 8, listed: 0, open: 0, room: 3, inJournal: 2));
        Assert.False(TribeLeftovers.HeldBack(allowances: 8, listed: 0, open: 3, room: 0, inJournal: 2));
        Assert.False(TribeLeftovers.HeldBack(allowances: 8, listed: 0, open: 3, room: 3, inJournal: 0));
        // Dailies listed: nothing's held back.
        Assert.False(TribeLeftovers.HeldBack(allowances: 8, listed: 2, open: 3, room: 3, inJournal: 2));
    }

    // ------------------------------------------------------------------ the class

    [Fact]
    public void OneClassPerVisitTheCurrentOneWhenItQualifies()
    {
        var plan = Plan(new[] { Three(1, 3) }, current: 9);
        var visit = Assert.Single(plan.Visits);
        Assert.Equal(9u, visit.ClassJob);
        Assert.All(visit.Quests, q => Assert.Equal(9u, q.ClassJob));
    }

    [Fact]
    public void OnAGathererTheBestGearedCrafterTakesThem()
        => Assert.Equal(10u, Plan(new[] { Three(1, 3) }, current: 16).Visits.Single().ClassJob);

    [Fact]
    public void LevellingTakesTheLowestClassThatQualifies()
        => Assert.Equal(9u, Plan(new[] { Three(1, 3) }, goal: TribeGoal.Levelling).Visits.Single().ClassJob);

    [Fact]
    public void AQuestAboveEveryClassIsLeftOutWithTheLevel()
    {
        var t = Society(1);
        var npc = t.Givers[0].NpcId;
        var day = Day(t, 3, 0, 1000, false, 0, Open(Daily(1, 11, npc, level: 90)), Open(Daily(1, 12, npc, level: 100)));
        var plan = Plan(new[] { day }, classes: Crafters(level: 95), goal: TribeGoal.Currency);
        Assert.Equal(1, plan.ToAccept);
        Assert.Contains(plan.LeftOut, l => l.Quest.Number == 12 && l.Reason == "Needs a crafter at level 100");
    }

    [Fact]
    public void QuestsOnlyAnotherClassTakesAreAVisitOfTheirOwn()
    {
        var t = Society(1, kind: TribeKind.Either);
        var npc = t.Givers[0].NpcId;
        var day = Day(t, 3, 0, 1000, false, 0, Open(Daily(1, 11, npc)), Open(Daily(1, 12, npc, classes: TribeKind.Gatherer, craft: false)));
        var classes = Crafters().Concat(new[] { new ClassInfo(16, 100, true, 700) }).ToList();
        var plan = Plan(new[] { day }, classes: classes, goal: TribeGoal.Currency);
        Assert.Equal(2, plan.Visits.Count);
        Assert.Equal(new uint[] { 8, 16 }, plan.Visits.Select(v => v.ClassJob).ToArray());
    }

    // ------------------------------------------------------------------ what's left out

    [Fact]
    public void QuestsThePolicyKeepsForThePlayerAreLeftOutWithTheReason()
    {
        var t = Society(1);
        var npc = t.Givers[0].NpcId;
        var day = Day(t, 3, 0, 1000, false, 0, Open(Daily(1, 3124, npc, craft: false, steps: StepKinds.Snipe)),
            Open(Daily(1, 12, npc, craft: false, steps: StepKinds.Say)), Open(Daily(1, 13, npc)));
        var plan = Plan(new[] { day }, leaveOutSay: true, goal: TribeGoal.Currency);
        Assert.Equal(new ushort[] { 13 }, plan.Quests.Select(q => q.Quest.Number).ToArray());
        Assert.Contains(plan.LeftOut, l => l.Quest.Number == 3124 && l.Reason.Contains("minigame"));
        Assert.Contains(plan.LeftOut, l => l.Quest.Number == 12 && l.Reason.Contains("/say"));
    }

    [Fact]
    public void QuestsTheRunGaveUpOnAreLeftOut()
    {
        var day = Three(1, 3);
        var row = day.Offers.First().Quest.RowId;
        var plan = Plan(new[] { day }, goal: TribeGoal.Currency, excluded: new Dictionary<uint, string> { [row] = "Couldn't get there" });
        Assert.Equal(2, plan.ToAccept);
        Assert.Contains(plan.LeftOut, l => l.Quest.RowId == row && l.Reason == "Couldn't get there");
    }

    [Fact]
    public void AnItemToWearThatIsntOwnedLeavesTheCraftOut()
    {
        var t = Society(5);
        var npc = t.Givers[0].NpcId;
        var day = Day(t, 3, 0, 1000, false, 0, Open(Daily(5, 1494, npc, equip: 8568)));
        var plan = Plan(new[] { day }, owns: id => id != 8568, goal: TribeGoal.Currency);
        Assert.Equal(0, plan.ToAccept);
        Assert.Contains(plan.LeftOut, l => l.Reason.Contains("Item 8568"));
    }

    [Fact]
    public void ASocietyLeftOutIsntVisited()
    {
        var plan = Plan(new[] { Three(1, 3), Three(2, 3) }, goal: TribeGoal.Currency, leftOut: new HashSet<uint> { 1 });
        Assert.All(plan.Visits, v => Assert.Equal(2u, v.Tribe.Id));
    }

    // ------------------------------------------------------------------ crystals

    [Fact]
    public void CrystalsAreCountedForTheMostCraftsADailyAsks()
    {
        var plan = Plan(new[] { Three(1, 3) }, allowances: 1, goal: TribeGoal.Currency);
        var q = plan.Quests.Single();
        Assert.Equal(new[] { (10u, 8 * QuestCraft.MostCrafts), (9u, 8 * QuestCraft.MostCrafts) }, q.Crystals.ToArray());
        Assert.Equal(8 * QuestCraft.MostCrafts, plan.Crystals()[10]);
    }

    [Fact]
    public void AQuestInTheJournalCountsTheCraftsLeft()
    {
        var day = Three(1, 3);
        var mine = Daily(1, 19, day.Tribe.Givers[0].NpcId);
        var plan = Plan(new[] { day }, allowances: 0, accepted: new[] { new AcceptedTribeQuest(mine, 1, 8, true) }, craftsLeft: _ => 2);
        Assert.Equal(16, plan.Quests.Single().Crystals.First().Amount);
    }

    [Fact]
    public void AGathererOnAnEitherDailyNeedsNoCrystals()
    {
        var t = Society(1, kind: TribeKind.Either);
        var npc = t.Givers[0].NpcId;
        var day = Day(t, 3, 0, 1000, false, 0, Open(Daily(1, 11, npc, classes: TribeKind.Either)));
        var classes = new List<ClassInfo> { new(16, 100, true, 700) };
        var plan = Plan(new[] { day }, classes: classes, current: 16, goal: TribeGoal.Currency);
        Assert.Empty(plan.Quests.Single().Crystals);
    }
}
