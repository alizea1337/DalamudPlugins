using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Fishing;
using Xunit;

namespace Provenance.Tests;

public class BaitNeedsTests
{
    private const uint Worm = 2585, Leech = 2587, Fly = 2599;
    private const long Hour = 175_000; // one Eorzea hour in real milliseconds

    private static FishInfo Fish(uint id, uint bait = Worm, uint[]? mooch = null, (uint, int)[]? predators = null, int intuition = 0,
        Uptime? window = null, uint[]? weather = null, bool big = false, Tug tug = Tug.Weak, (float, float)? bite = null, bool spear = false)
        => new(id, spear, bait, mooch ?? Array.Empty<uint>(), predators ?? Array.Empty<(uint, int)>(), intuition, window,
            weather ?? Array.Empty<uint>(), Array.Empty<uint>(), HookSet.Precise, tug, false, "None", SpearSize.Unknown, SpearSpeed.Unknown,
            0, false, big, Array.Empty<uint>(), bite ?? (5f, 15f));

    private static Func<uint, FishInfo?> Lookup(params FishInfo[] fish)
    {
        var map = fish.ToDictionary(f => f.ItemId);
        return id => map.GetValueOrDefault(id);
    }

    private static double Bait(IReadOnlyList<(uint Bait, double PerCatch)> list, uint bait) => list.Single(b => b.Bait == bait).PerCatch;

    [Fact]
    public void A_common_fish_uses_a_little_more_than_one_bait_a_catch()
    {
        var f = Fish(1);
        var baits = BaitNeeds.PerCatch(f, Lookup(f));
        var only = Assert.Single(baits);
        Assert.Equal(Worm, only.Bait);
        Assert.Equal(BaitNeeds.CommonHooks, only.PerCatch, 6);
    }

    [Fact]
    public void Restricted_big_and_rare_bites_take_more()
    {
        var common = BaitNeeds.HooksPerCatch(Fish(1));
        var timed = BaitNeeds.HooksPerCatch(Fish(2, window: new Uptime(6 * Hour, 18 * Hour, 9 * Hour)));
        var weather = BaitNeeds.HooksPerCatch(Fish(3, weather: new uint[] { 4 }));
        var big = BaitNeeds.HooksPerCatch(Fish(4, big: true, tug: Tug.Legendary));
        var rare = BaitNeeds.HooksPerCatch(Fish(5, tug: Tug.Legendary));
        var slow = BaitNeeds.HooksPerCatch(Fish(6, bite: (30f, 40f)));
        Assert.Equal(BaitNeeds.RestrictedHooks, timed, 6);
        Assert.Equal(BaitNeeds.RestrictedHooks, weather, 6);
        Assert.Equal(BaitNeeds.BigHooks, big, 6);                                // big fish don't count the rare bite twice
        Assert.Equal(BaitNeeds.CommonHooks * BaitNeeds.RareBiteHooks, rare, 6);
        Assert.Equal(rare, slow, 6);
        Assert.True(common < timed && timed < big);

        Assert.True(BaitNeeds.CastsPerCatch(Fish(1)) < BaitNeeds.CastsPerCatch(Fish(2, weather: new uint[] { 4 })));
        Assert.True(BaitNeeds.CastsPerCatch(Fish(2, weather: new uint[] { 4 })) < BaitNeeds.CastsPerCatch(Fish(3, big: true)));
    }

    [Fact]
    public void A_mooched_fish_needs_the_chain_caught_again_for_every_try()
    {
        var goby = Fish(10);
        var wahoo = Fish(11, mooch: new uint[] { 10 });
        var goose = Fish(12, mooch: new uint[] { 10, 11 }, weather: new uint[] { 3 });
        var lookup = Lookup(goby, wahoo, goose);

        // Goby on the worm, mooch it for the Wahoo: one Goby per try, two tries per Wahoo.
        Assert.Equal(BaitNeeds.CommonHooks * BaitNeeds.CommonMoochTries, Bait(BaitNeeds.PerCatch(wahoo, lookup), Worm), 6);
        // Goby → Wahoo (common) → Goosefish (restricted): the tries multiply.
        Assert.Equal(BaitNeeds.CommonMoochTries * BaitNeeds.RestrictedMoochTries, BaitNeeds.ChainTries(goose, lookup), 6);
        Assert.Equal(BaitNeeds.CommonHooks * BaitNeeds.CommonMoochTries * BaitNeeds.RestrictedMoochTries,
            Bait(BaitNeeds.PerCatch(goose, lookup), Worm), 6);
    }

    [Fact]
    public void Unknown_chain_fish_count_as_common()
    {
        var target = Fish(20, mooch: new uint[] { 999 });
        var baits = BaitNeeds.PerCatch(target, Lookup(target));
        Assert.Equal(BaitNeeds.CommonHooks * BaitNeeds.CommonMoochTries, Bait(baits, Worm), 6);
    }

    [Fact]
    public void Predators_bring_their_own_bait_times_their_count_and_the_intuition_runs()
    {
        var crab = Fish(30, bait: Leech);
        var target = Fish(31, bait: Fly, predators: new[] { (30u, 3) }, intuition: 30, big: true, bite: (19f, 33f));
        var lookup = Lookup(crab, target);
        var baits = BaitNeeds.PerCatch(target, lookup);

        Assert.Equal(Fly, baits[0].Bait); // the fish's own bait comes first
        var runs = BaitNeeds.IntuitionRuns(target, lookup);
        Assert.True(runs > 1); // the fish takes longer than 30 s of intuition
        // Under the intuition even a big fish bites like a restricted one.
        Assert.Equal(BaitNeeds.RestrictedHooks, Bait(baits, Fly), 6);
        Assert.Equal(3 * BaitNeeds.CommonHooks * runs, Bait(baits, Leech), 6);
    }

    [Fact]
    public void Each_mooch_step_is_tried_as_often_as_the_steps_after_it_need()
    {
        var goby = Fish(100, bite: (5f, 15f));                                   // 15 s casts
        var wahoo = Fish(101, mooch: new uint[] { 100 }, bite: (5f, 15f));
        var goose = Fish(102, mooch: new uint[] { 100, 101 }, bite: (5f, 15f));
        var lookup = Lookup(goby, wahoo, goose);
        var firstCatch = BaitNeeds.CommonCasts * 15 + BaitNeeds.LandingSeconds;
        var t = BaitNeeds.CommonMoochTries;
        // Goosefish: its step tried 2 times; the Wahoo step 2 times for each of those (4); 4 Gobies caught.
        var expected = 1 * (t * 15 + BaitNeeds.LandingSeconds) + t * (t * 15 + BaitNeeds.LandingSeconds) + t * t * firstCatch;
        Assert.Equal(expected, BaitNeeds.SecondsPerCatch(goose, lookup), 6);
        Assert.Equal(t * t, BaitNeeds.ChainTries(goose, lookup), 6);
    }

    [Fact]
    public void Intuition_lasts_at_least_one_run_and_a_guess_when_unknown()
    {
        var crab = Fish(30, bait: Leech);
        var quick = Fish(32, predators: new[] { (30u, 1) }, intuition: 600);
        var unknown = Fish(33, predators: new[] { (30u, 1) });
        var lookup = Lookup(crab, quick, unknown);
        Assert.Equal(1, BaitNeeds.IntuitionRuns(quick, lookup), 6);
        Assert.Equal(BaitNeeds.UnknownIntuitionRuns, BaitNeeds.IntuitionRuns(unknown, lookup), 6);
        Assert.Equal(0, BaitNeeds.IntuitionRuns(crab, lookup), 6);
    }

    [Fact]
    public void The_same_bait_is_summed_into_one_entry()
    {
        var crab = Fish(40);
        var target = Fish(41, predators: new[] { (40u, 2) }, intuition: 600);
        var baits = BaitNeeds.PerCatch(target, Lookup(crab, target));
        var only = Assert.Single(baits);
        Assert.Equal(BaitNeeds.RestrictedHooks + 2 * BaitNeeds.CommonHooks, only.PerCatch, 6);
    }

    [Fact]
    public void Spearfishing_and_fish_without_bait_need_none()
    {
        var spear = Fish(50, bait: 0, spear: true);
        Assert.Empty(BaitNeeds.PerCatch(spear, Lookup(spear)));
        var nothing = Fish(51, bait: 0);
        Assert.Empty(BaitNeeds.PerCatch(nothing, Lookup(nothing)));
        // Unknown predators are skipped, not guessed.
        var lonely = Fish(52, predators: new[] { (777u, 5) }, intuition: 100);
        Assert.Single(BaitNeeds.PerCatch(lonely, Lookup(lonely)));
    }

    [Fact]
    public void Predator_loops_end()
    {
        var a = Fish(60, bait: Worm, predators: new[] { (61u, 1) }, intuition: 100);
        var b = Fish(61, bait: Leech, predators: new[] { (60u, 1) }, intuition: 100);
        var lookup = Lookup(a, b);
        var baits = BaitNeeds.PerCatch(a, lookup);
        Assert.Equal(new[] { Worm, Leech }, baits.Select(x => x.Bait).ToArray());
        Assert.True(BaitNeeds.SecondsPerCatch(a, lookup) > 0);
    }

    [Theory]
    [InlineData(5f, 15f, 15.0)]   // 5 s overhead, earliest bite 5 s, half the spread
    [InlineData(2f, 94f, 22.0)]   // a wide bite time counts at most 15 s past the earliest bite
    [InlineData(0f, 0f, 20.0)]    // no bite time: 15 s
    public void A_cast_lasts_about_as_long_as_the_typical_bite(float min, float max, double seconds)
    {
        Assert.Equal(seconds, BaitNeeds.SecondsPerCast(Fish(70, bite: (min, max))), 6);
    }

    [Fact]
    public void Harder_fish_take_longer()
    {
        var goby = Fish(80);
        var timed = Fish(81, window: new Uptime(6 * Hour, 18 * Hour, 9 * Hour));
        var big = Fish(82, big: true, tug: Tug.Legendary);
        var mooched = Fish(83, mooch: new uint[] { 80 });
        var behindIntuition = Fish(84, predators: new[] { (80u, 3) }, intuition: 120);
        var lookup = Lookup(goby, timed, big, mooched, behindIntuition);

        var common = BaitNeeds.SecondsPerCatch(goby, lookup);
        Assert.Equal(BaitNeeds.CommonCasts * 15 + BaitNeeds.LandingSeconds, common, 6);
        Assert.True(BaitNeeds.SecondsPerCatch(timed, lookup) > common);
        Assert.True(BaitNeeds.SecondsPerCatch(big, lookup) > BaitNeeds.SecondsPerCatch(timed, lookup));
        Assert.True(BaitNeeds.SecondsPerCatch(mooched, lookup) > common);
        // Behind Fisher's Intuition: it bites like a restricted fish once the 3 predators are in (one run is enough here).
        Assert.Equal(BaitNeeds.RestrictedCasts * 15 + BaitNeeds.LandingSeconds + 3 * common, BaitNeeds.SecondsPerCatch(behindIntuition, lookup), 6);
    }

    [Fact]
    public void Spearfishing_goes_by_node_time()
    {
        var small = Fish(90, bait: 0, spear: true);
        var big = Fish(91, bait: 0, spear: true, big: true);
        var lookup = Lookup(small, big);
        Assert.Equal(BaitNeeds.SpearCommonSeconds, BaitNeeds.SecondsPerCatch(small, lookup), 6);
        Assert.Equal(BaitNeeds.SpearBigSeconds, BaitNeeds.SecondsPerCatch(big, lookup), 6);
    }

    [Fact]
    public void Learned_bait_per_catch_moves_the_plan_toward_what_was_seen()
    {
        // Nothing seen yet: the data's figure.
        Assert.Equal(1.4, BaitUse.Blend(1.4, null), 6);
        Assert.Equal(1.4, BaitUse.Blend(1.4, new BaitUse()), 6);
        // 30 bait for 2 catches: (1.4 × 3 + 30) / 5.
        Assert.Equal((1.4 * 3 + 30) / 5, BaitUse.Blend(1.4, new BaitUse { Bait = 30, Catches = 2 }), 6);
        // Bait gone without a catch raises it too.
        Assert.True(BaitUse.Blend(1.4, new BaitUse { Bait = 12, Catches = 0 }) > 5);
        // Many catches: close to what was seen.
        Assert.Equal(10.0, BaitUse.Blend(1.4, new BaitUse { Bait = 10_000, Catches = 1_000 }), 1);
    }
}
