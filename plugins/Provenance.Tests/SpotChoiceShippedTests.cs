using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Fishing;
using Xunit;

namespace Provenance.Tests;

/// <summary>
/// The fisher's windows and leads for real fish from the plugin's fish.json, at their real fishing holes and with
/// their zones' weather: windows that come from a mooch chain or from the fish caught first for Fisher's Intuition,
/// judged the way the angler judges them, and how early to set out for them.
/// </summary>
public class SpotChoiceShippedTests
{
    private const long Hour = 175_000;     // one Eorzea hour in real milliseconds
    private const long Day = 24 * Hour;
    private const long SomeDay = 400_000;  // an Eorzea day in 2023, as good as any

    /// <summary>A moment on <see cref="SomeDay"/> (or days later) at an Eorzea hour.</summary>
    private static DateTimeOffset Et(double hour, long days = 0) => DateTimeOffset.FromUnixTimeMilliseconds((SomeDay + days) * Day + (long)(hour * Hour));

    /// <summary>Moments every 37 real minutes over about four real days, from <see cref="SomeDay"/>.</summary>
    private static IEnumerable<DateTimeOffset> Moments => Enumerable.Range(0, 160).Select(i => Et(0) + TimeSpan.FromMinutes(37 * i));

    private static (TimeSpan? Wait, TimeSpan Bites, uint Predator) WindowAt(uint fish, uint spot, DateTimeOffset now, IReadOnlyList<(uint, int)>? table = null)
        => SpotChoice.Window(ShippedFish.Get(fish), ShippedFish.Data, table ?? ShippedFish.WeatherOf(spot), now, SpotChoice.Horizon);

    private static readonly TimeSpan OneDay = TimeSpan.FromDays(1);

    // ------------------------------------------------------------------ windows from a mooch chain

    [Theory]
    [InlineData(4906u, 63u, 18, 6)]    // Nautilus, only mooched from Fullmoon Sardine (18:00-06:00 ET)
    [InlineData(4918u, 63u, 18, 6)]    // Coelacanth, the same
    [InlineData(23055u, 168u, 4, 8)]   // mooched from 22397 (04:00-08:00 ET)
    [InlineData(23056u, 168u, 4, 8)]
    [InlineData(24203u, 168u, 0, 4)]   // mooched from 21177 (00:00-04:00 ET)
    public void A_fish_mooched_from_a_timed_fish_waits_for_its_hours(uint fish, uint spot, int opens, int closes)
    {
        Assert.False(ShippedFish.Get(fish).Restricted);
        var hours = (closes - opens + 24) % 24;

        // Between the link's hours: the wait until they start, then all of them.
        var before = Et((opens + 20) % 24);
        var (wait, bites, predator) = WindowAt(fish, spot, before);
        Assert.Equal(TimeSpan.FromMilliseconds(4 * Hour), wait);
        Assert.Equal(TimeSpan.FromMilliseconds(hours * Hour), bites);
        Assert.Equal(0u, predator);

        // In them: now, for the rest of them.
        (wait, bites, _) = WindowAt(fish, spot, Et(opens + 1));
        Assert.Equal(TimeSpan.Zero, wait);
        Assert.Equal(TimeSpan.FromMilliseconds((hours - 1) * Hour), bites);
    }

    [Theory]
    [InlineData(4906u, 63u)]
    [InlineData(4918u, 63u)]
    [InlineData(8772u, 27u)]    // Shonisaurus: any hour, but only from a Mahar mooched in Blizzards after Clear or Fair Skies
    [InlineData(23055u, 168u)]
    [InlineData(23056u, 168u)]
    [InlineData(24203u, 168u)]
    public void The_fisher_waits_for_the_window_the_angler_fishes_in(uint fish, uint spot)
    {
        var plan = ShippedFish.Plan(fish, spot);
        Assert.True(plan.Window.Restricted);
        var closed = 0;
        foreach (var at in Moments)
        {
            var (wait, bites, _) = WindowAt(fish, spot, at);
            var state = plan.Window.At(at);
            Assert.Equal(state.OpensIn, wait);
            if (state.Open) Assert.Equal(state.Left < OneDay ? state.Left : OneDay, bites);
            else closed++;
        }
        // Never "bites now" at every hour, as the fish's own data alone would say.
        Assert.True(closed > 0);
    }

    // ------------------------------------------------------------------ windows of the fish caught for Fisher's Intuition

    [Theory]
    [InlineData(8775u, 8774u, 17u)]   // Namitaro: Giant Takitaro only bites in Thunder or Thunderstorms
    [InlineData(8763u, 8762u, 94u)]   // Kuno the Killer: Canavan only bites 08:00-18:00 ET in Gloom
    public void A_target_without_a_window_waits_for_its_predator_s(uint target, uint predator, uint spot)
    {
        Assert.False(ShippedFish.Get(target).Restricted);
        var plan = ShippedFish.Plan(target, spot);
        Assert.False(plan.Window.Restricted);
        var theirs = plan.PredatorWindows[predator];
        bool open = false, closed = false;
        foreach (var at in Moments)
        {
            var (wait, bites, by) = WindowAt(target, spot, at);
            var state = theirs.At(at);
            Assert.Equal(predator, by);
            Assert.Equal(state.OpensIn, wait);
            if (state.Open)
            {
                open = true;
                Assert.Equal(state.Left < OneDay ? state.Left : OneDay, bites);
            }
            else closed = true;
        }
        Assert.True(open && closed);

        // A predator whose weather doesn't come in the forecast rules the fish out there (the angler would end at once).
        var never = new (uint, int)[] { (2, 100) };
        Assert.Equal(((TimeSpan?)null, TimeSpan.Zero, predator), WindowAt(target, spot, Et(0), never));
    }

    [Fact]
    public void Warden_counts_from_the_soonest_of_its_predators_windows()
    {
        // 24994 needs 23056 (mooched from 22397, 04:00-08:00 ET), 24203 (from 21177, 00:00-04:00 ET) and 24204 (its own
        // window). They're caught one after another, so the soonest opening counts; the first two never bite together.
        var plan = ShippedFish.Plan(24994, 168);
        var windows = plan.PredatorWindows;
        Assert.Equal(new[] { 23056u, 24203u, 24204u }, windows.Keys.OrderBy(k => k));
        foreach (var at in Moments)
        {
            var (wait, bites, by) = WindowAt(24994, 168, at);
            var soonest = windows.Values.Select(w => w.At(at).OpensIn).Min();
            Assert.Equal(soonest, wait);
            Assert.Equal(soonest, windows[by].At(at).OpensIn);
            Assert.NotEqual(TimeSpan.MaxValue, bites);
        }
        Assert.Null(SpotChoice.Window(windows[23056], windows[24203], Et(0), SpotChoice.Horizon).Wait);

        // At 05:00 ET 23056 bites: now, for at least the rest of 22397's hours.
        var (now, lasts, _) = WindowAt(24994, 168, Et(5));
        Assert.Equal(TimeSpan.Zero, now);
        Assert.True(lasts >= TimeSpan.FromMilliseconds(3 * Hour));
    }

    [Theory]
    [InlineData(33244u, 232u)]   // its own window (00:00-02:00 ET in Clouds after Fair Skies); 33325 bites before it
    [InlineData(41408u, 269u)]   // its own window (14:00-16:00 ET in Fair Skies after Showers); 36454 in Fair Skies
    public void A_target_with_a_window_of_its_own_keeps_it(uint target, uint spot)
    {
        var plan = ShippedFish.Plan(target, spot);
        foreach (var at in Moments)
        {
            var (wait, _, predator) = WindowAt(target, spot, at);
            Assert.Equal(plan.Window.At(at).OpensIn, wait);
            Assert.Equal(0u, predator);
        }
    }

    [Fact]
    public void A_spearfish_keeps_its_own_window()
    {
        // Snailfish's swimming shadows wait for Ichimonji at its own spot: the fisher pairs those two itself.
        Assert.Equal(((TimeSpan?)TimeSpan.Zero, TimeSpan.MaxValue, 0u), WindowAt(21180, 168, Et(0)));
    }

    // ------------------------------------------------------------------ how early to set out

    [Theory]
    [InlineData(33244u, 350)]    // from the start of 33325's 22:00 ET window (2 catches take 80 s)
    [InlineData(41408u, 1332)]   // 12 of 36454, each mooched (960 s by the learned pace)
    public void The_lead_covers_the_catch_planner_s_start_on_the_predators(uint target, int prepSeconds)
    {
        var fish = ShippedFish.Get(target);
        Assert.Equal(TimeSpan.FromSeconds(prepSeconds), CatchPlanner.PrepLead(fish, ShippedFish.Data));
        // 3 minutes to be there, then the predators from the moment the angler starts on them.
        Assert.Equal(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(prepSeconds), SpotChoice.Lead(fish, 3, ShippedFish.Data, 40));
    }

    [Theory]
    [InlineData(8775u)]
    [InlineData(8763u)]
    [InlineData(24994u)]
    [InlineData(21180u)]   // spearfish: Ichimonji for the swimming shadows
    public void Predators_whose_windows_bound_the_fish_add_nothing(uint target)
        => Assert.Equal(TimeSpan.FromMinutes(3), SpotChoice.Lead(ShippedFish.Get(target), 3, ShippedFish.Data, 40));
}
