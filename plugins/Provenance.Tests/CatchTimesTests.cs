using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Estimating;
using Provenance.Core.Fishing;
using Xunit;
using static Provenance.Tests.FishCases;

namespace Provenance.Tests;

/// <summary>
/// When the plan says a fish can be caught (<see cref="FishSchedule.Times"/>): its own window narrowed by its mooch
/// chain's, and the fish caught first for Fisher's Intuition at the same spot. Real fish from fish.json at their zones'
/// weather, and made-up ones for the edges.
/// </summary>
public class CatchTimesTests
{
    private const long Hour = 175_000;     // one Eorzea hour in real milliseconds
    private const long Day = 24 * Hour;
    private const long SomeDay = 400_000;  // an Eorzea day in 2023, as good as any

    /// <summary>A moment on <see cref="SomeDay"/> (or days later) at an Eorzea hour.</summary>
    private static DateTimeOffset Et(double hour, long days = 0) => DateTimeOffset.FromUnixTimeMilliseconds((SomeDay + days) * Day + (long)(hour * Hour));

    /// <summary>A shipped fish at one of its fishing holes, over the forecast from <paramref name="from"/>.</summary>
    private static CatchTimes Shipped(uint fish, uint spot, DateTimeOffset from, IReadOnlyList<(uint, int)>? table = null)
        => FishSchedule.Times(ShippedFish.Get(fish), ShippedFish.Data, new[] { table ?? ShippedFish.WeatherOf(spot) }, from, FishWindow.Forecast);

    // ------------------------------------------------------------------ windows from a mooch chain

    [Theory]
    [InlineData(4906u, 63u, 4898u, 18, 12)]   // Nautilus: bites any time, but only from a Fullmoon Sardine (18:00-06:00 ET)
    [InlineData(4918u, 63u, 4898u, 18, 12)]   // Coelacanth, the same
    [InlineData(23055u, 168u, 22397u, 4, 4)]
    [InlineData(23056u, 168u, 22397u, 4, 4)]
    [InlineData(24203u, 168u, 21177u, 0, 4)]
    public void A_fish_mooched_from_a_timed_fish_is_only_caught_in_that_fish_s_hours(uint id, uint spot, uint link, int opens, int hours)
    {
        var fish = ShippedFish.Get(id);
        var from = Et(opens + hours + 1); // an hour after its link's hours end
        Assert.False(fish.Restricted);
        Assert.Equal(new[] { (from, from + FishWindow.Forecast) }, FishSchedule.Windows(fish, new[] { ShippedFish.WeatherOf(spot) }, from, FishWindow.Forecast));

        var times = Shipped(id, spot, from);
        Assert.True(times.Restricted);
        Assert.Equal(new[] { link }, times.Links.Select(l => l.ItemId));
        // The forecast holds 144 Eorzea days: the link's hours on each of the next 144.
        var expected = Enumerable.Range(1, 144).Select(d => (Et(opens, d), Et(opens + hours, d))).ToList();
        Assert.Equal(expected, times.Windows!);
        Assert.Equal(TimeSpan.FromMilliseconds((24 - hours - 1) * Hour), FishSchedule.OpensIn(times.Windows, from));
        Assert.Equal(TimeSpan.FromMilliseconds((hours - 1) * Hour), FishSchedule.OpenFor(times.Windows, Et(opens + 1, 1)));
        Assert.Empty(times.Predators);
        Assert.Null(times.PredatorBeyond);
    }

    [Fact]
    public void Shonisaurus_is_only_caught_in_the_weather_its_mahar_needs()
    {
        // 8772 bites any time, mooched from Mahar (8771: Blizzards after Clear Skies or Fair Skies).
        var from = Et(0);
        var times = Shipped(8772, 27, from);
        Assert.Equal(new[] { 8771u }, times.Links.Select(l => l.ItemId));
        var mahar = FishSchedule.Windows(ShippedFish.Get(8771), new[] { ShippedFish.WeatherOf(27) }, from, FishWindow.Forecast);
        Assert.NotEmpty(mahar);
        Assert.Equal(mahar, times.Windows!);
    }

    [Fact]
    public void A_fish_with_a_window_of_its_own_keeps_it()
    {
        var from = Et(0);
        var fish = Fish(930, window: new Uptime(6 * Hour, 18 * Hour, 9 * Hour));
        var times = FishSchedule.Times(fish, Data, new[] { new (uint, int)[] { (1, 100) } }, from, TimeSpan.FromMilliseconds(Day));
        Assert.True(times.Restricted);
        Assert.Empty(times.Links);
        Assert.Equal(new[] { (Et(9), Et(15)) }, times.Windows!);

        // Without one, and nothing caught first: any time.
        var always = FishSchedule.Times(Straight, Data, new[] { new (uint, int)[] { (1, 100) } }, from, TimeSpan.FromMilliseconds(Day));
        Assert.False(always.Restricted);
        Assert.Null(always.Windows);
        Assert.Equal(TimeSpan.Zero, FishSchedule.OpensIn(always.Windows, from));
        Assert.Equal(TimeSpan.MaxValue, FishSchedule.OpenFor(always.Windows, from));
    }

    // ------------------------------------------------------------------ fish caught first for Fisher's Intuition

    [Fact]
    public void A_fish_whose_predator_does_not_bite_within_the_forecast_can_not_be_caught()
    {
        // Namitaro bites any time under Fisher's Intuition from Giant Takitaro, which only bites in Thunder or Thunderstorms.
        var times = Shipped(8775, 17, Et(0));
        Assert.False(times.Restricted);
        Assert.Null(times.Windows);
        Assert.Equal(8774u, Assert.Single(times.Predators).Fish.ItemId);
        Assert.NotEmpty(times.Predators[0].Windows);
        Assert.Null(times.PredatorBeyond);

        // Where that weather never comes, no catch of it can bring the intuition: nothing to wait for, and why.
        var never = Shipped(8775, 17, Et(0), new (uint, int)[] { (2, 100) });
        Assert.Empty(never.Windows!);
        Assert.Null(FishSchedule.OpensIn(never.Windows, Et(0)));
        var beyond = never.PredatorBeyond!;
        Assert.Equal(8774u, beyond.Fish.ItemId);
        Assert.Empty(beyond.Windows);
    }

    [Fact]
    public void Only_spots_where_the_predators_bite_as_well_count()
    {
        // The fish bites in weather 1, its predator in weather 5; both are caught at the same spot.
        var predator = Fish(941, weather: new uint[] { 5 });
        var fish = Fish(940, weather: new uint[] { 1 }, predators: new (uint, int)[] { (941, 1) }, intuition: 120);
        FishInfo? Known(uint id) => id == 941 ? predator : id == 940 ? fish : null;
        var onlyFish = new (uint, int)[] { (1, 100) };
        var both = new (uint, int)[] { (1, 50), (5, 50) };
        var from = Et(0);
        var horizon = FishWindow.Forecast;

        var there = FishSchedule.Times(fish, Known, new[] { onlyFish }, from, horizon);
        Assert.Empty(there.Windows!);
        Assert.Equal(941u, there.PredatorBeyond!.Fish.ItemId);

        var twoZones = FishSchedule.Times(fish, Known, new[] { onlyFish, both }, from, horizon);
        Assert.Null(twoZones.PredatorBeyond);
        Assert.NotEmpty(twoZones.Windows!);
        Assert.Equal(FishSchedule.Windows(fish, new[] { both }, from, horizon), twoZones.Windows!);
        Assert.Equal(FishSchedule.Windows(predator, new[] { both }, from, horizon), Assert.Single(twoZones.Predators).Windows);

        // The fish itself never bites: that's the reason, and its predator still shows when it bites.
        var onlyPredator = new (uint, int)[] { (5, 100) };
        var fishNever = FishSchedule.Times(fish, Known, new[] { onlyPredator }, from, horizon);
        Assert.Empty(fishNever.Windows!);
        Assert.Null(fishNever.PredatorBeyond);
        Assert.Equal(new[] { (from, from + horizon) }, Assert.Single(fishNever.Predators).Windows);
    }

    [Theory]
    [InlineData(8775u, 17u, new uint[] { 8774 })]                  // Namitaro: Giant Takitaro in Thunder or Thunderstorms
    [InlineData(8763u, 94u, new uint[] { 8762 })]                  // Kuno the Killer: Canavan 08:00-18:00 ET in Gloom
    [InlineData(24994u, 168u, new uint[] { 23056, 24203, 24204 })] // Warden of the Seven Hues: two mooch-timed predators, one in its own window
    public void Predators_that_bite_less_often_than_the_fish_slow_its_catches(uint id, uint spot, uint[] slow)
    {
        var times = Shipped(id, spot, Et(0));
        Assert.Null(times.PredatorBeyond);
        Assert.Equal(slow, times.Predators.Where(p => p.Slows).Select(p => p.Fish.ItemId).OrderBy(x => x));
    }

    [Theory]
    [InlineData(33244u, 232u)] // 33325 only bites in the two hours before its window; the 700 s intuition reaches into it
    [InlineData(41408u, 269u)] // 36454 bites in Fair Skies, which the fish itself needs
    public void Predators_the_intuition_reaches_from_or_that_bite_with_the_fish_do_not_slow_it(uint id, uint spot)
    {
        var times = Shipped(id, spot, Et(0));
        Assert.NotEmpty(times.Windows!);
        Assert.NotEmpty(times.Predators);
        Assert.All(times.Predators, p => Assert.False(p.Slows));
    }

    [Fact]
    public void A_predator_counts_until_the_intuition_it_brings_runs_out()
    {
        // The fish bites 00:00-08:00 ET, its predator 00:00-04:00 ET. A 600 s intuition (over 3 Eorzea hours) from a catch
        // at 04:00 leaves only the window's last half hour out of reach; a 60 s one leaves almost half of it.
        var predator = Fish(951, window: new Uptime(4 * Hour, 20 * Hour, 0));
        FishInfo Target(int intuition) => Fish(950, window: new Uptime(8 * Hour, 16 * Hour, 0), predators: new (uint, int)[] { (951, 1) }, intuition: intuition);
        PredatorTimes For(FishInfo target)
            => Assert.Single(FishSchedule.Times(target, id => id == 951 ? predator : null, new[] { new (uint, int)[] { (1, 100) } }, Et(0), FishWindow.Forecast).Predators);
        Assert.False(For(Target(600)).Slows);
        Assert.True(For(Target(60)).Slows);
    }

    [Fact]
    public void Swimming_shadows_leave_their_predators_to_the_fisher()
    {
        // A spearfish's predators are gigged at a spot of their own, not at the fish's.
        var predator = Fish(961, weather: new uint[] { 5 }) with { Spear = true };
        var fish = Fish(960, predators: new (uint, int)[] { (961, 3) }) with { Spear = true };
        var times = FishSchedule.Times(fish, id => id == 961 ? predator : null, new[] { new (uint, int)[] { (1, 100) } }, Et(0), FishWindow.Forecast);
        Assert.Null(times.Windows);
        Assert.Empty(times.Predators);
    }

    // ------------------------------------------------------------------ reading windows

    [Fact]
    public void Opens_in_and_open_for_read_the_windows()
    {
        var now = Et(0);
        var windows = new[] { (now.AddMinutes(-20), now.AddMinutes(-10)), (now.AddMinutes(-5), now.AddMinutes(5)), (now.AddMinutes(30), now.AddMinutes(40)) };
        Assert.Equal(TimeSpan.Zero, FishSchedule.OpensIn(windows, now));
        Assert.Equal(TimeSpan.FromMinutes(5), FishSchedule.OpenFor(windows, now));
        Assert.Equal(TimeSpan.FromMinutes(20), FishSchedule.OpensIn(windows, now.AddMinutes(10)));
        Assert.Equal(TimeSpan.Zero, FishSchedule.OpenFor(windows, now.AddMinutes(10)));
        Assert.Null(FishSchedule.OpensIn(windows, now.AddMinutes(40)));
        Assert.Equal(TimeSpan.Zero, FishSchedule.OpenFor(windows, now.AddMinutes(40)));
    }
}
