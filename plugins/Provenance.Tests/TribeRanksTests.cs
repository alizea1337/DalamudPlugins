using System.Linq;
using Provenance.Core.Tribes;
using Xunit;

namespace Provenance.Tests;

public class TribeRanksTests
{
    /// <summary><c>BeastReputationRank.RequiredReputation</c>, Neutral to Bloodsworn (index = rank).</summary>
    private static readonly int[] Required = { 0, 150, 360, 510, 720, 990, 1320, 1730, 0 };

    /// <summary>A modern society: starts Friendly, 60 a daily, dailies at Friendly to Honored.</summary>
    private static TribeLadder Modern(bool ownDailies = true)
        => TribeLadder.From(8, Required, Enumerable.Range(3, 4).SelectMany(r => Enumerable.Repeat((ownDailies ? r : 3, 60), 7)));

    /// <summary>The Moogles: start Neutral, 50 a daily, dailies at Neutral to Honored.</summary>
    private static TribeLadder Moogles() => TribeLadder.From(8, Required, Enumerable.Range(1, 6).SelectMany(r => Enumerable.Repeat((r, 50), 5)));

    [Fact]
    public void TheLadderTakesTheBestDailyAtOrBelowEachRank()
    {
        // Ixal-like givers: 20, 24, 29 … by rank; the top rank has no bar.
        var ladder = TribeLadder.From(7, Required, new[] { (1, 20), (2, 24), (3, 29), (4, 35), (5, 42), (6, 50) });
        Assert.Equal(new[] { 0, 20, 24, 29, 35, 42, 50, 50 }, ladder.RepPerDaily);
        Assert.Equal(0, ladder.RequiredAt(7));
        Assert.Equal(1320, ladder.RequiredAt(6));
        Assert.False(ladder.OwnDailiesAt(7));
        Assert.True(ladder.OwnDailiesAt(6));
    }

    [Fact]
    public void FriendlyToBloodswornIs89DailiesAnd28DaysWithTheRankUpDays()
    {
        var e = TribeRanks.ToTop(Modern(), new TribeStanding(3, 0, 0, 3));
        Assert.Equal(89, e.Dailies);
        Assert.Equal(28, e.Days);
        Assert.Equal(5, e.RankQuests);
        Assert.True(e.Known);
    }

    [Fact]
    public void WithoutDailiesOfTheNewRankItIsTheWikis31Days()
    {
        // Every daily at Friendly: a rank-up day offers nothing more, so each rank takes whole days of its own.
        var e = TribeRanks.ToTop(Modern(false), new TribeStanding(3, 0, 0, 3));
        Assert.Equal(89, e.Dailies);
        Assert.Equal(31, e.Days);
    }

    [Fact]
    public void TheMooglesNeutralToBloodsworn()
    {
        // 3 + 8 + 11 + 15 + 20 + 27 + 35 dailies; 41 days without the rank-up days, 5 of them save a day each.
        var e = TribeRanks.ToTop(Moogles(), new TribeStanding(1, 0, 0, 3));
        Assert.Equal(119, e.Dailies);
        Assert.Equal(36, e.Days);
        Assert.Equal(7, e.RankQuests);
    }

    [Fact]
    public void TheNextRankCountsWhatIsLeftToday()
    {
        // 480 of 510 with one of today's three left: one daily, today.
        var e = TribeRanks.ToNext(Modern(), new TribeStanding(3, 480, 2, 1));
        Assert.Equal(new RankEstimate(1, 1, 0, 1), e);
    }

    [Fact]
    public void NothingLeftTodayStartsTomorrow()
    {
        var e = TribeRanks.ToNext(Modern(), new TribeStanding(3, 480, 3, 0));
        Assert.Equal(1, e.Dailies);
        Assert.Equal(1, e.LastDay);
    }

    [Fact]
    public void ReputationOverTheBarIsLost()
    {
        // 9 dailies for 510 (540 given, 30 lost); 3 a day.
        var e = TribeRanks.ToNext(Modern(), new TribeStanding(3, 0, 0, 3));
        Assert.Equal(9, e.Dailies);
        Assert.Equal(3, e.Days);
        Assert.Equal(2, e.LastDay);
    }

    [Fact]
    public void AFullBarNeedsOnlyTheRankQuest()
    {
        Assert.True(TribeRanks.BarFull(4, 720, 720, 8));
        var e = TribeRanks.ToNext(Modern(), new TribeStanding(4, 720, 0, 3));
        Assert.Equal(new RankEstimate(0, 0, -1, 1), e);
    }

    [Fact]
    public void AFullBarOnTheRankUpDayGivesTheNewRanksDailies()
    {
        // Bar full at Friendly with three taken today: the rank quest, then Trusted's three the same day (cap 6).
        var e = TribeRanks.Estimate(Modern(), new TribeStanding(3, 510, 3, 0), 5);
        Assert.Equal(12, e.Dailies);
        // 3 today after the rank quest, then 3 a day: days 0, 1, 2, 3.
        Assert.Equal(4, e.Days);
    }

    [Fact]
    public void TheTopRankIsTheEnd()
    {
        Assert.True(TribeRanks.AtTop(8, 8));
        Assert.False(TribeRanks.BarFull(8, 0, 0, 8));
        Assert.Equal(RankEstimate.Nothing, TribeRanks.ToNext(Modern(), new TribeStanding(8, 0, 0, 3)));
        Assert.Equal(RankEstimate.Nothing, TribeRanks.ToTop(Modern(), new TribeStanding(8, 0, 0, 3)));
    }

    [Fact]
    public void ALockedSocietyHasNoEstimate()
    {
        Assert.Equal(RankEstimate.Nothing, TribeRanks.ToTop(Modern(), new TribeStanding(0, 0, 0, 3)));
    }

    [Fact]
    public void ARankWithoutDailiesIsUnknown()
    {
        // A ladder whose dailies start at Trusted can't say how Friendly fills.
        var ladder = TribeLadder.From(8, Required, new[] { (4, 60) });
        var e = TribeRanks.ToNext(ladder, new TribeStanding(3, 0, 0, 3));
        Assert.False(e.Known);
    }

    [Fact]
    public void DailiesToFillRoundsUp()
    {
        Assert.Equal(1, TribeRanks.DailiesToFill(450, 510, 60));
        Assert.Equal(9, TribeRanks.DailiesToFill(0, 510, 60));
        Assert.Equal(0, TribeRanks.DailiesToFill(510, 510, 60));
        Assert.Equal(0, TribeRanks.DailiesToFill(0, 0, 60));
    }
}
