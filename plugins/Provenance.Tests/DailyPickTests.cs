using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Tribes;
using Xunit;

namespace Provenance.Tests;

/// <summary>
/// The daily pick on small hand-made givers. The expected values were worked out by hand from the algorithm (and with a
/// separate throwaway script); whether the algorithm is the game's is checked in game by the giver-list match check
/// (<c>[tribes] match</c> / <c>MISMATCH</c> in the debug log).
/// </summary>
public class DailyPickTests
{
    private static List<DailyCandidate> Normal(int rank, params uint[] ids) => ids.Select(id => new DailyCandidate(id, rank, false)).ToList();

    private static uint[] Ids(IEnumerable<DailyCandidate> picks) => picks.Select(p => p.QuestId).ToArray();

    [Fact]
    public void TheGeneratorFollowsTheXorshiftSteps()
    {
        // Seed 1: t = 1 ^ (1 << 11) = 2049, s1' = 2049 ^ (2049 >> 8) = 2057. The next three draws start from the zero
        // words, so they repeat it until the seed's word comes round again.
        var g = new DailyPick.Generator(1);
        Assert.Equal(new uint[] { 2057, 2057, 2057, 2057, 4196416, 2049 }, Enumerable.Range(0, 6).Select(_ => g.NextRaw()).ToArray());

        g = new DailyPick.Generator(255);
        Assert.Equal(new uint[] { 524039, 524039, 524039, 524039, 1070057415, 522495, 1070055487, 524039 },
            Enumerable.Range(0, 8).Select(_ => g.NextRaw()).ToArray());
    }

    [Fact]
    public void SeedZeroDrawsZero()
    {
        var g = new DailyPick.Generator(0);
        for (var i = 0; i < 8; i++) Assert.Equal(0u, g.NextRaw());
    }

    [Fact]
    public void ADrawIsTheRawValueModuloTheRange()
    {
        var a = new DailyPick.Generator(200);
        var b = new DailyPick.Generator(200);
        for (var i = 0; i < 8; i++) Assert.Equal((int)(a.NextRaw() % 7), b.Next(7));
    }

    [Fact]
    public void ATakenPickStepsToTheNextFreeOne()
    {
        // Seed 1 draws 2057 three times: 2057 % 5 = 2, so 103, then 104 and 105 by stepping.
        Assert.Equal(new uint[] { 103, 104, 105 }, Ids(DailyPick.Offers(Normal(1, 101, 102, 103, 104, 105), 1, 1, false)));
        // Seed 7: 7 ^ (7 << 11) = 14343, ^ (14343 >> 8) = 14399; 14399 % 5 = 4.
        Assert.Equal(new uint[] { 105, 101, 102 }, Ids(DailyPick.Offers(Normal(1, 101, 102, 103, 104, 105), 7, 1, false)));
    }

    [Fact]
    public void SteppingWrapsRoundToTheStart()
    {
        // Seed 3 draws 6171 three times: 6171 % 4 = 3, so the last quest, then the first two.
        Assert.Equal(new uint[] { 504, 501, 502 }, Ids(DailyPick.Offers(Normal(2, 501, 502, 503, 504), 3, 2, false)));
    }

    [Fact]
    public void TheGiversOrderIsByQuestIdWhateverTheInputOrder()
    {
        var shuffled = Normal(1, 105, 103, 101, 104, 102);
        Assert.Equal(new uint[] { 103, 104, 105 }, Ids(DailyPick.Offers(shuffled, 1, 1, false)));
    }

    [Fact]
    public void FewerThanThreeEligibleAreAllOffered()
    {
        var quests = Normal(1, 11, 12).Concat(Normal(4, 13, 14, 15)).ToList();
        Assert.Equal(new uint[] { 11, 12 }, Ids(DailyPick.Offers(quests, 0, 2, false)).OrderBy(i => i).ToArray());
    }

    [Fact]
    public void ALockedSocietyOffersNothing()
    {
        Assert.Empty(DailyPick.Offers(Normal(1, 1, 2, 3), 9, 0, false));
    }

    [Fact]
    public void OnTheRankUpDayOnlyTheNewRanksDailiesAreOffered()
    {
        var quests = new List<DailyCandidate>
        {
            new(401, 3, false), new(402, 3, false), new(403, 4, false), new(404, 4, false), new(405, 4, false), new(406, 5, false),
        };
        // A normal day at rank 4: 401-405 are eligible (17 % 5 = 4 → 405, then 401, 402).
        Assert.Equal(new uint[] { 405, 401, 402 }, Ids(DailyPick.Offers(quests, 17, 4, false)));
        // Ranked up to 4 today: only 403-405.
        Assert.Equal(new uint[] { 404, 405, 403 }, Ids(DailyPick.Offers(quests, 17, 4, true)));
        // Ranked up to 5 today: only the one rank-5 daily.
        Assert.Equal(new uint[] { 406 }, Ids(DailyPick.Offers(quests, 17, 5, true)));
    }

    [Fact]
    public void ARankWithNoDailiesOfItsOwnOffersNothingOnItsRankUpDay()
    {
        Assert.Empty(DailyPick.Offers(Normal(3, 1, 2, 3, 4), 50, 7, true));
    }

    /// <summary>An ARR-style giver at its own rank: four normal dailies, three exclusive ones.</summary>
    private static List<DailyCandidate> ArrGiver() => new()
    {
        new(205, 1, true), new(201, 1, false), new(206, 1, true), new(202, 1, false), new(203, 1, false), new(207, 1, true), new(204, 1, false),
    };

    [Fact]
    public void AtTheGiversRankOneOfferComesFromTheExclusivePool()
    {
        // Seed 3 draws 6171 (= 3 × 2057). Exclusive: 6171 % 3 = 0 → 205. Then the normal part up to three in all:
        // 6171 % 4 = 3 → 204, again 3 → taken → 201.
        var picks = DailyPick.Offers(ArrGiver(), 3, 1, false);
        Assert.Equal(new uint[] { 205, 204, 201 }, Ids(picks));
        Assert.Single(picks, p => p.Exclusive);
    }

    [Fact]
    public void AboveTheGiversRankThePoolsAreOneList()
    {
        // Rank 2 is above every daily's rank 1: seven in one list, 6171 % 7 = 4 → 205, then 206 and 207 by stepping.
        Assert.Equal(new uint[] { 205, 206, 207 }, Ids(DailyPick.Offers(ArrGiver(), 3, 2, false)));
    }

    [Fact]
    public void ExclusiveOnlyCountsWhenThePlayerDoesNotOutrankEveryDaily()
    {
        var giver = ArrGiver();
        Assert.False(DailyPick.OutranksAll(giver, 1));
        Assert.True(DailyPick.OutranksAll(giver, 2));
        // One daily at a higher rank (not even eligible) keeps the exclusive rule in force.
        giver.Add(new DailyCandidate(208, 5, false));
        Assert.False(DailyPick.OutranksAll(giver, 2));
    }

    [Fact]
    public void WithTwoNormalDailiesTheTotalIsTwo()
    {
        // The exclusive pick first, then normal ones up to min(normal count, 3) offers in all.
        var giver = new List<DailyCandidate> { new(301, 1, false), new(302, 1, false), new(303, 1, true) };
        Assert.Equal(new uint[] { 303, 302 }, Ids(DailyPick.Offers(giver, 5, 1, false)));
    }

    [Fact]
    public void WithOnlyExclusiveDailiesEligibleOneIsOffered()
    {
        var giver = new List<DailyCandidate> { new(601, 1, true), new(602, 1, true) };
        Assert.Equal(new uint[] { 602 }, Ids(DailyPick.Offers(giver, 9, 1, false)));
    }

    [Fact]
    public void ThreeDistinctOffersForEverySeed()
    {
        var quests = Normal(3, Enumerable.Range(1, 7).Select(i => (uint)i).ToArray());
        for (var seed = 0; seed < 256; seed++)
        {
            var picks = DailyPick.Offers(quests, (byte)seed, 3, false);
            Assert.Equal(3, picks.Count);
            Assert.Equal(3, picks.Distinct().Count());
        }
    }
}
