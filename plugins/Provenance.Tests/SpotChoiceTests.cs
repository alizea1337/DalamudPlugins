using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Fishing;
using Xunit;

namespace Provenance.Tests;

public class SpotChoiceTests
{
    private const long Hour = 175_000; // one Eorzea hour in real milliseconds
    private static readonly (uint, int)[] Mixed = { (1, 20), (2, 30), (3, 50) };
    private static readonly TimeSpan Lead = TimeSpan.FromMinutes(3);

    private static FishInfo Fish(uint id = 1, Uptime? window = null, uint[]? weather = null, uint[]? mooch = null,
        (uint, int)[]? predators = null, bool big = false, bool spear = false)
        => new(id, spear, 2585, mooch ?? Array.Empty<uint>(), predators ?? Array.Empty<(uint, int)>(), 0, window,
            weather ?? Array.Empty<uint>(), Array.Empty<uint>(), HookSet.Precise, Tug.Weak, false, "None", SpearSize.Unknown,
            SpearSpeed.Unknown, 0, false, big, new uint[] { 100 }, (3f, 20f));

    private static DateTimeOffset At(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    private static SpotCandidate Timed(uint item, uint spot, double waitMinutes, double bitesMinutes, bool here = true, int cost = 0, bool resting = false)
        => new(item, spot, true, TimeSpan.FromMinutes(waitMinutes), TimeSpan.FromMinutes(bitesMinutes), here, cost, resting);

    private static SpotCandidate Always(uint item, uint spot, bool here = true, int cost = 0)
        => new(item, spot, false, TimeSpan.Zero, TimeSpan.MaxValue, here, cost);

    [Fact]
    public void Fish_without_a_window_always_bite()
    {
        var (wait, bites) = SpotChoice.Window(Fish(), Mixed, At(1_700_000_000_000), SpotChoice.Horizon);
        Assert.Equal(TimeSpan.Zero, wait);
        Assert.Equal(TimeSpan.MaxValue, bites);
    }

    [Fact]
    public void A_time_window_gives_the_wait_and_its_length()
    {
        var f = Fish(window: new Uptime(6 * Hour, 18 * Hour, 9 * Hour)); // 9:00-15:00 ET
        var (wait, bites) = SpotChoice.Window(f, Mixed, At(8 * Hour), SpotChoice.Horizon);
        Assert.Equal(TimeSpan.FromMilliseconds(Hour), wait);
        Assert.Equal(TimeSpan.FromMilliseconds(6 * Hour), bites);

        // Open now: no wait, the rest of the window.
        (wait, bites) = SpotChoice.Window(f, Mixed, At(10 * Hour), SpotChoice.Horizon);
        Assert.Equal(TimeSpan.Zero, wait);
        Assert.Equal(TimeSpan.FromMilliseconds(5 * Hour), bites);
    }

    [Fact]
    public void A_window_past_the_forecast_has_no_wait()
    {
        var only = new (uint, int)[] { (4, 100) };
        var (wait, _) = SpotChoice.Window(Fish(weather: new uint[] { 5 }), only, At(1_700_000_000_000), SpotChoice.Horizon);
        Assert.Null(wait);
    }

    [Fact]
    public void The_lead_adds_time_for_the_intuition_catches()
    {
        var chainFish = Fish(7, mooch: new uint[] { 6 });
        FishInfo? Data(uint id) => id == 7 ? chainFish : Fish(id);
        Assert.Equal(TimeSpan.FromMinutes(3), SpotChoice.Lead(Fish(), 3, Data, 40));
        // 2 plain catches (40 s each) and 3 catches of a fish mooched once (80 s each).
        var target = Fish(predators: new (uint, int)[] { (5, 2), (7, 3) });
        Assert.Equal(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(2 * 40 + 3 * 80), SpotChoice.Lead(target, 3, Data, 40));
        // Never more than the cap.
        var greedy = Fish(predators: new (uint, int)[] { (5, 500) });
        Assert.Equal(TimeSpan.FromMinutes(3) + SpotChoice.MaxPrep, SpotChoice.Lead(greedy, 3, Data, 40));
    }

    [Fact]
    public void A_spearfish_lead_leaves_out_a_predator_with_a_window()
    {
        // The swimming shadows' predator that only bites at some hours is gigged inside its own window (which bounds
        // the fish's); one that always bites can be gigged ahead of the fish's window.
        var timed = Fish(5, window: new Uptime(6 * Hour, 18 * Hour, 9 * Hour), spear: true);
        FishInfo? Data(uint id) => id == 5 ? timed : Fish(id, spear: true);
        Assert.Equal(TimeSpan.FromMinutes(3), SpotChoice.Lead(Fish(spear: true, predators: new (uint, int)[] { (5, 7) }), 3, Data, 40));
        Assert.Equal(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(7 * 40),
            SpotChoice.Lead(Fish(spear: true, predators: new (uint, int)[] { (6, 7) }), 3, Data, 40));
    }

    [Fact]
    public void Two_fish_bite_together_where_their_windows_overlap()
    {
        var fish = Fish(window: new Uptime(6 * Hour, 18 * Hour, 9 * Hour));          // 9:00-15:00 ET
        var predator = Fish(2, window: new Uptime(6 * Hour, 18 * Hour, 12 * Hour));  // 12:00-18:00 ET
        var (wait, bites) = SpotChoice.Window(fish, Mixed, predator, Mixed, At(8 * Hour), SpotChoice.Horizon);
        Assert.Equal(TimeSpan.FromMilliseconds(4 * Hour), wait);
        Assert.Equal(TimeSpan.FromMilliseconds(3 * Hour), bites); // until the fish's own window closes at 15:00

        // Both bite now: the shorter of what's left.
        (wait, bites) = SpotChoice.Window(fish, Mixed, predator, Mixed, At(13 * Hour), SpotChoice.Horizon);
        Assert.Equal(TimeSpan.Zero, wait);
        Assert.Equal(TimeSpan.FromMilliseconds(2 * Hour), bites);

        // Windows that never meet.
        var evening = Fish(3, window: new Uptime(4 * Hour, 20 * Hour, 16 * Hour));   // 16:00-20:00 ET
        Assert.Null(SpotChoice.Window(fish, Mixed, evening, Mixed, At(8 * Hour), SpotChoice.Horizon).Wait);
    }

    [Fact]
    public void A_fish_without_a_window_bites_while_its_predator_does()
    {
        var predator = Fish(2, window: new Uptime(6 * Hour, 18 * Hour, 12 * Hour)); // 12:00-18:00 ET
        var (wait, bites) = SpotChoice.Window(Fish(), Mixed, predator, Mixed, At(8 * Hour), SpotChoice.Horizon);
        Assert.Equal(TimeSpan.FromMilliseconds(4 * Hour), wait);
        Assert.Equal(TimeSpan.FromMilliseconds(6 * Hour), bites);

        // Neither has a window: always.
        (wait, bites) = SpotChoice.Window(Fish(), Mixed, Fish(2), Mixed, At(8 * Hour), SpotChoice.Horizon);
        Assert.Equal(TimeSpan.Zero, wait);
        Assert.Equal(TimeSpan.MaxValue, bites);
    }

    [Fact]
    public void Each_fish_is_judged_by_its_own_zone_weather()
    {
        // The predator only bites in weather 9: its own zone always has it, the fish's zone never does.
        var fish = Fish(window: new Uptime(6 * Hour, 18 * Hour, 9 * Hour)); // 9:00-15:00 ET
        var predator = Fish(2, weather: new uint[] { 9 });
        var always9 = new (uint, int)[] { (9, 100) };
        Assert.Equal(TimeSpan.FromMilliseconds(Hour), SpotChoice.Window(fish, Mixed, predator, always9, At(8 * Hour), SpotChoice.Horizon).Wait);
        Assert.Null(SpotChoice.Window(fish, Mixed, predator, Mixed, At(8 * Hour), SpotChoice.Horizon).Wait);
    }

    [Fact]
    public void Workable_needs_the_window_within_the_lead_and_long_enough()
    {
        Assert.True(SpotChoice.Workable(Always(1, 10), Lead));
        Assert.True(SpotChoice.Workable(Timed(1, 10, 0, 20), Lead));
        Assert.True(SpotChoice.Workable(Timed(1, 10, 2, 20), Lead));
        Assert.False(SpotChoice.Workable(Timed(1, 10, 4, 20), Lead));          // opens after the lead
        Assert.False(SpotChoice.Workable(Timed(1, 10, 0, 20, resting: true), Lead));
        Assert.False(SpotChoice.Workable(new SpotCandidate(1, 10, true, null, TimeSpan.Zero, true, 0), Lead));
        // Closing before the way there is done.
        Assert.False(SpotChoice.Workable(Timed(1, 10, 0, 0.5), Lead));
        Assert.True(SpotChoice.Workable(Timed(1, 10, 0, 2), Lead));
        Assert.False(SpotChoice.Workable(Timed(1, 10, 0, 2, here: false), Lead)); // the teleport eats it
        Assert.True(SpotChoice.Workable(Timed(1, 10, 2.5, 2, here: false), Lead)); // opens after the way: all of it is left
    }

    [Fact]
    public void The_best_spot_is_here_then_the_longest_window_then_the_cheapest()
    {
        var away = Timed(1, 10, 0, 30, here: false, cost: 100);
        var here = Timed(1, 11, 0, 10);
        Assert.Equal(11u, SpotChoice.Best(new[] { away, here }, Lead)!.Value.SpotId);

        var short1 = Timed(1, 12, 0, 10, here: false, cost: 50);
        var long1 = Timed(1, 13, 0, 25, here: false, cost: 300);
        Assert.Equal(13u, SpotChoice.Best(new[] { short1, long1 }, Lead)!.Value.SpotId);

        var cheap = Always(1, 14, here: false, cost: 100);
        var dear = Always(1, 15, here: false, cost: 900);
        Assert.Equal(14u, SpotChoice.Best(new[] { dear, cheap }, Lead)!.Value.SpotId);

        Assert.Null(SpotChoice.Best(new[] { Timed(1, 16, 30, 10) }, Lead));
    }

    [Fact]
    public void The_soonest_spot_skips_resting_ones()
    {
        var resting = Timed(1, 10, 1, 10, resting: true);
        var later = Timed(1, 11, 9, 10);
        var never = new SpotCandidate(1, 12, true, null, TimeSpan.Zero, true, 0);
        Assert.Equal(11u, SpotChoice.Soonest(new[] { resting, later, never })!.Value.SpotId);
        Assert.Null(SpotChoice.Soonest(new[] { never }));
    }

    [Fact]
    public void Windows_closing_soonest_come_first_then_fish_without_one()
    {
        var always = Always(1, 10);
        var closesLate = Timed(2, 11, 0, 40);
        var closesSoon = Timed(3, 12, 0, 8);
        var opensSoon = Timed(4, 13, 2, 4); // closes in 6 minutes
        var ranked = SpotChoice.Rank(new[] { always, closesLate, closesSoon, opensSoon }, 0).Select(c => c.ItemId).ToList();
        Assert.Equal(new uint[] { 4, 3, 2, 1 }, ranked);
    }

    [Fact]
    public void Among_fish_without_a_window_the_current_spot_wins()
    {
        var hereCheap = Always(1, 10);
        var current = Always(2, 11, here: false, cost: 500);
        var ranked = SpotChoice.Rank(new[] { hereCheap, current }, currentSpot: 11).Select(c => c.ItemId).ToList();
        Assert.Equal(new uint[] { 2, 1 }, ranked);
        ranked = SpotChoice.Rank(new[] { current, hereCheap }, currentSpot: 0).Select(c => c.ItemId).ToList();
        Assert.Equal(new uint[] { 1, 2 }, ranked);
    }

    [Fact]
    public void Closing_time_adds_the_wait_and_never_overflows()
    {
        Assert.Equal(TimeSpan.FromMinutes(12), Timed(1, 10, 2, 10).Closes);
        Assert.Equal(TimeSpan.MaxValue, Always(1, 10).Closes);
        Assert.Equal(TimeSpan.MaxValue, new SpotCandidate(1, 10, true, null, TimeSpan.Zero, true, 0).Closes);
    }

    [Fact]
    public void Bait_to_buy_follows_the_catches_and_stays_within_a_stack()
    {
        Assert.Equal(3, SpotChoice.BaitPerCatch(Fish()));
        Assert.Equal(5 * 3, SpotChoice.BaitPerCatch(Fish(window: new Uptime(Hour, 23 * Hour, 0), mooch: new uint[] { 2, 3 })));
        Assert.Equal(12, SpotChoice.BaitPerCatch(Fish(big: true)));
        Assert.Equal(5 * 3 + 20, SpotChoice.BaitToBuy(Fish(), 5, 20));
        Assert.Equal(10, SpotChoice.BaitToBuy(Fish(), 1, 0));    // at least a handful
        Assert.Equal(999, SpotChoice.BaitToBuy(Fish(big: true), 500, 20));
    }
}
