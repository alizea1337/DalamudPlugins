using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Provenance.Core.Tribes;
using Xunit;

namespace Provenance.Tests;

public class TribeClassesTests
{
    private static readonly QuestCraft Craft = new(46208, 46207,
        Enumerable.Range(8, 8).Select(j => new QuestRecipe(36000u + (uint)j, (uint)j, 90, new[] { (10u, 8), (9u, 8) })).ToList(), false);

    private static TribeQuest Quest(TribeKind classes, int level, QuestCraft? craft = null, StepKinds steps = StepKinds.None)
        => new(0x10000u + 5336, 20, "Snow on the Mountain", TribeQuestKind.Daily, 3, 2, 1, 1, classes, level, 60, steps | (craft != null ? StepKinds.Craft : 0),
            craft, new[] { new QuestGather(16, 500, 1, 1, level, 1, Vector2.Zero, 5), new QuestGather(17, 501, 2, 2, level, 1, Vector2.Zero, 5) },
            0, Array.Empty<uint>(), (0, 0), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>());

    private static List<ClassInfo> Classes(params (uint Job, int Level, bool Gearset, int ItemLevel)[] list)
        => list.Select(c => new ClassInfo(c.Job, c.Level, c.Gearset, c.ItemLevel)).ToList();

    private static QuestReadiness Ready(TribeQuest q, List<ClassInfo> classes, uint current, int crystals = 999, bool owns = true, bool levelling = false)
        => TribeClasses.Readiness(q, classes, current, levelling, _ => crystals, _ => owns, id => "Item " + id, id => "Items " + id);

    [Fact]
    public void TheCurrentClassTakesItWhenItQualifies()
    {
        var classes = Classes((8, 100, true, 700), (9, 95, true, 710));
        Assert.Equal(8u, TribeClasses.Choose(Quest(TribeKind.Crafter, 90), classes, 8));
    }

    [Fact]
    public void OtherwiseTheBestGearedOneDoes()
    {
        var classes = Classes((8, 100, true, 700), (9, 95, true, 710), (16, 100, true, 750));
        // On a gatherer: the crafters qualify, Blacksmith's gear is better.
        Assert.Equal(9u, TribeClasses.Choose(Quest(TribeKind.Crafter, 90), classes, 16));
    }

    [Fact]
    public void LevellingTakesTheLowestThatQualifies()
    {
        var classes = Classes((8, 100, true, 700), (9, 92, true, 710), (10, 80, true, 600));
        Assert.Equal(9u, TribeClasses.Choose(Quest(TribeKind.Crafter, 90), classes, 8, levelling: true));
    }

    [Fact]
    public void AClassWithoutAGearsetOnlyCountsWhileItIsOn()
    {
        var classes = Classes((8, 100, false, 0), (9, 100, false, 0));
        Assert.Equal(8u, TribeClasses.Choose(Quest(TribeKind.Crafter, 90), classes, 8));
        Assert.Equal(0u, TribeClasses.Choose(Quest(TribeKind.Crafter, 90), classes, 16));
    }

    [Fact]
    public void GathererDailiesAreForMinerAndBotanistOnly()
    {
        Assert.Equal(new uint[] { 16, 17 }, TribeClasses.Candidates(TribeKind.Gatherer));
        var classes = Classes((18, 100, true, 700));
        var r = Ready(Quest(TribeKind.Gatherer, 90), classes, 18);
        Assert.Equal(NeedKind.ClassLevel, r.Need);
        Assert.Equal("Needs a Miner or Botanist at level 90", r.Text);
    }

    [Fact]
    public void TooLowSaysWhoAndWhichLevel()
    {
        var r = Ready(Quest(TribeKind.Crafter, 90), Classes((8, 89, true, 600)), 8);
        Assert.Equal(NeedKind.ClassLevel, r.Need);
        Assert.Equal("Needs a crafter at level 90", r.Text);
    }

    [Fact]
    public void AtTheLevelWithoutAGearsetAsksForOne()
    {
        var r = Ready(Quest(TribeKind.Crafter, 90), Classes((8, 95, false, 0)), 16);
        Assert.Equal(NeedKind.Gearset, r.Need);
        Assert.Contains("gearset", r.Text);
    }

    [Fact]
    public void CrystalsAreCheckedForTheMostCrafts()
    {
        // 8 + 8 a craft, up to 4 crafts: 32 of each.
        var classes = Classes((8, 100, true, 700));
        Assert.True(Ready(Quest(TribeKind.Crafter, 90, Craft), classes, 8, crystals: 32).Ready);
        var r = Ready(Quest(TribeKind.Crafter, 90, Craft), classes, 8, crystals: 31);
        Assert.Equal(NeedKind.Crystals, r.Need);
        Assert.Equal(8u, r.ClassJob);
        Assert.StartsWith("Needs up to 32 Items 10 and 32 Items 9", r.Text);
        Assert.Contains("you have 31 and 31", r.Text);
    }

    [Fact]
    public void AGathererOnANamazuDailyNeedsNoCrystals()
    {
        var classes = Classes((16, 100, true, 700));
        var r = Ready(Quest(TribeKind.Either, 60, Craft), classes, 16, crystals: 0);
        Assert.True(r.Ready);
        Assert.Equal(16u, r.ClassJob);
    }

    [Fact]
    public void AnItemToEquipComesBeforeCrystals()
    {
        var ixal = new QuestCraft(8092, 8053, new[] { new QuestRecipe(30512, 8, 1, new[] { (4u, 1), (3u, 1) }, 8568, 547) }, false);
        var r = Ready(Quest(TribeKind.Crafter, 1, ixal), Classes((8, 50, true, 300)), 8, crystals: 0, owns: false);
        Assert.Equal(NeedKind.EquipItem, r.Need);
        Assert.Contains("Item 8568", r.Text);
    }
}

public class TribeDayTests
{
    private static TribeQuest Daily(uint number, uint giver, int rank)
        => new(0x10000u + number, 20, "Q" + number, TribeQuestKind.Daily, rank, 2, giver, giver, TribeKind.Crafter, 90, 60, StepKinds.Craft,
            null, Array.Empty<QuestGather>(), 0, Array.Empty<uint>(), (0, 0), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>());

    private static Tribe Society(IReadOnlyList<TribeGiver> givers, IReadOnlyList<TribeQuest> dailies, int maxRank = 8)
        => new(20, "Yok Huy", 5, TribeKind.Crafter, maxRank, 3, 46178, 1187, "Urqopacha", 201, 90, givers,
            Array.Empty<TribeUnlockStep>(), new[] { new TribeRankQuest(0x10000u + 5332, "Buried Memories", 4, 1, "Fahrafahr", 1187, "Urqopacha", Vector2.Zero, Array.Empty<(uint, string)>()) },
            0, "", Array.Empty<TribePortal>(), Array.Empty<TribeShopItem>(), Array.Empty<TribeAchievement>(), dailies);

    private static TribeGiver Giver(uint id, int rank) => new(id, "Giver " + id, 1187, "Urqopacha", Vector3.Zero, rank, rank);

    [Fact]
    public void LeftTodayIsTheOpenOffersWithinTheCapAndTheAllowances()
    {
        var g = Giver(1, 3);
        var offers = new[] { new TribeOffer(Daily(1, 1, 3), OfferState.Done), new TribeOffer(Daily(2, 1, 3), OfferState.Open), new TribeOffer(Daily(3, 1, 3), OfferState.Open) };
        var day = new TribeToday(Society(new[] { g }, offers.Select(o => o.Quest).ToList()), 4, false, 100, 720, false, new[] { new GiverToday(g, offers, 1) });
        Assert.Equal(2, day.LeftToday(12));
        Assert.Equal(1, day.LeftToday(1));
        Assert.Equal(new TribeStanding(4, 100, 1, 2), day.Standing(12));
    }

    [Fact]
    public void ARankUpDayAllowsSixInAll()
    {
        var g = Giver(1, 3);
        var offers = new[] { new TribeOffer(Daily(4, 1, 4), OfferState.Open), new TribeOffer(Daily(5, 1, 4), OfferState.Open), new TribeOffer(Daily(6, 1, 4), OfferState.Open) };
        // Three taken before the rank quest, then the new rank's three are offered.
        var day = new TribeToday(Society(new[] { g }, offers.Select(o => o.Quest).ToList()), 4, true, 0, 720, false, new[] { new GiverToday(g, offers, 3) });
        Assert.Equal(6, day.CapPerGiver);
        Assert.Equal(3, day.LeftToday(12));
    }

    [Fact]
    public void AFullBarNamesTheRankQuest()
    {
        var g = Giver(1, 3);
        var day = new TribeToday(Society(new[] { g }, Array.Empty<TribeQuest>()), 4, false, 720, 720, false, new[] { new GiverToday(g, Array.Empty<TribeOffer>(), 0) });
        Assert.True(day.BarFull);
        Assert.Equal("Buried Memories", day.RankQuestReady!.Name);
    }

    [Fact]
    public void ADailyWithoutTodaysSlotIsLeftOver()
    {
        Assert.True(new AcceptedTribeQuest(Daily(1, 1, 3), 1, 8, false).Leftover);
        Assert.False(new AcceptedTribeQuest(Daily(1, 1, 3), 1, 8, true).Leftover);
    }

    [Fact]
    public void TheAutomaticGoalRanksUpUntilEverySocietyIsAtTheTop()
    {
        var g = Giver(1, 3);
        TribeToday At(int rank) => new(Society(new[] { g }, Array.Empty<TribeQuest>()), rank, false, 0, 0, false, Array.Empty<GiverToday>());
        Assert.Equal(TribeGoal.RankUp, TribeGoals.Effective(null, new[] { At(8), At(5) }));
        Assert.Equal(TribeGoal.Currency, TribeGoals.Effective(null, new[] { At(8), At(8) }));
        Assert.Equal(TribeGoal.Levelling, TribeGoals.Effective(TribeGoal.Levelling, new[] { At(5) }));
        // A locked society doesn't count.
        Assert.Equal(TribeGoal.Currency, TribeGoals.Effective(null, new[] { At(0) }));
    }

    [Fact]
    public void ForSeveralGiversTheStandingCountsTheGiverOfYourRank()
    {
        var low = Giver(1, 1);
        var mine = Giver(2, 2);
        var lowOffers = new[] { new TribeOffer(Daily(1, 1, 1), OfferState.Open) };
        var myOffers = new[] { new TribeOffer(Daily(2, 2, 2), OfferState.Done), new TribeOffer(Daily(3, 2, 2), OfferState.Open) };
        var day = new TribeToday(Society(new[] { low, mine }, Array.Empty<TribeQuest>(), 7), 2, false, 10, 360, false,
            new[] { new GiverToday(low, lowOffers, 0), new GiverToday(mine, myOffers, 1) });
        Assert.Equal(new TribeStanding(2, 10, 1, 1), day.Standing(12));
        Assert.Equal(2, day.LeftToday(12));
    }
}
