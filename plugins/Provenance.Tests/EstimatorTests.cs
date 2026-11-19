using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Estimating;
using Provenance.Core.Fishing;
using Provenance.Core.Orders;
using Provenance.Core.Planning;
using Xunit;
using static Provenance.Tests.FakeCatalog;

namespace Provenance.Tests;

public class EstimatorTests
{
    private static readonly Timings Flat = new()
    {
        SecondsPerAction = 3, SecondsBetweenCrafts = 0, SecondsPerJobSwitch = 0, GatheredPerMinute = 10, FishPerMinute = 1,
    };

    private static Plan PlanOf(params (uint Id, int Qty)[] lines)
    {
        var o = new Order();
        foreach (var (id, qty) in lines) o.Add(id, qty);
        return Planner.Build(o, new FakeCatalog(), new Dictionary<uint, int>(), new PlannerOptions { PreferVendor = false });
    }

    [Fact]
    public void Ready_times_add_up_in_run_order()
    {
        // 1 lumber: 3 logs + 1 wind shard gathered (4 items, 10/min = 24 s), then 1 craft of 10 steps (30 s).
        var plan = PlanOf((Lumber, 1));
        var e = Estimator.Estimate(plan, Flat, (_, _) => 10, _ => true);
        Assert.Equal(24, e.Gathering.TotalSeconds, 3);
        var craft = Assert.Single(e.Crafts);
        Assert.Equal(30, craft.Duration.TotalSeconds, 3);
        Assert.Equal(54, e.ReadyAfter[Lumber].TotalSeconds, 3);
        Assert.Equal(54, e.Total.TotalSeconds, 3);
        Assert.False(e.Approximate);
    }

    [Fact]
    public void Each_ordered_item_is_ready_after_everything_before_it()
    {
        var plan = PlanOf((Bow, 1), (Shield, 1));
        var e = Estimator.Estimate(plan, Flat, (_, _) => 5, _ => true);
        var order = e.Crafts.Select(c => c.Recipe.ItemId).ToList();
        Assert.True(e.ReadyAfter[Bow] < e.ReadyAfter[Shield] == order.IndexOf(Bow) < order.IndexOf(Shield));
        Assert.Equal(e.Total, e.ReadyAfter.Values.Max());
        // every step finishes after the one before
        for (var i = 1; i < e.Crafts.Count; i++) Assert.True(e.Crafts[i].FinishedAfter > e.Crafts[i - 1].FinishedAfter);
    }

    [Fact]
    public void Unknown_rotations_use_typical_lengths_and_say_so()
    {
        var plan = PlanOf((Lumber, 1));
        var hq = Estimator.Estimate(plan, Flat, (_, _) => null, _ => true);
        var fast = Estimator.Estimate(plan, Flat, (_, _) => null, _ => false);
        Assert.True(hq.Approximate);
        Assert.Equal(Estimator.TypicalStepsHq * 3, hq.Crafts.Single().Duration.TotalSeconds, 3);
        Assert.Equal(Estimator.TypicalStepsFast * 3, fast.Crafts.Single().Duration.TotalSeconds, 3);
    }

    [Fact]
    public void Intermediates_never_ask_for_hq()
    {
        var plan = PlanOf((Bow, 1));
        var asked = new List<(uint, bool)>();
        Estimator.Estimate(plan, Flat, (r, hq) => { asked.Add((r.ItemId, hq)); return 5; }, _ => true);
        Assert.Contains((Bow, true), asked);
        Assert.Contains((Lumber, false), asked);
    }

    [Fact]
    public void Gathered_order_items_are_ready_when_gathering_ends()
    {
        var plan = PlanOf((Log, 20));
        var e = Estimator.Estimate(plan, Flat, (_, _) => 5, _ => true);
        Assert.Equal(TimeSpan.FromMinutes(2), e.ReadyAfter[Log]);
    }

    [Theory]
    [InlineData(30, "under a minute")]
    [InlineData(600, "about 10 min")]
    [InlineData(3900, "about 1 h 05 min")]
    public void Describes_durations(int seconds, string text)
    {
        Assert.Equal(text, Estimator.Describe(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Blending_moves_toward_measurements()
    {
        Assert.Equal(2.9, Timings.Blend(2.6, 3.6), 3);
    }

    // ------------------------------------------------------------------ fishing

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static OrderEstimate Fished(Plan plan, Func<uint, FishTime?> fish)
        => Estimator.Estimate(plan, Flat, (_, _) => 5, _ => true, null, new FishInputs(fish, Now));

    private static (DateTimeOffset, DateTimeOffset)[] Window(double fromMinutes, double toMinutes)
        => new[] { (Now.AddMinutes(fromMinutes), Now.AddMinutes(toMinutes)) };

    [Fact]
    public void Without_fish_inputs_fish_go_at_the_learned_rate()
    {
        var e = Estimator.Estimate(PlanOf((Trout, 3)), Flat, (_, _) => 5, _ => true);
        Assert.Equal(180, e.Gathering.TotalSeconds, 3); // 1 per minute
        Assert.Equal(e.Gathering, e.Fishing);
    }

    [Fact]
    public void Fish_go_by_their_catch_time_plus_a_trip()
    {
        var e = Fished(PlanOf((Trout, 10)), id => id == Trout ? new FishTime(60) : null);
        Assert.Equal(Flat.SecondsPerFishingTrip + 600, e.Gathering.TotalSeconds, 3);
        Assert.Equal(e.Gathering, e.Fishing);
        Assert.Equal(TimeSpan.Zero, e.FishWait);
        Assert.Null(e.FishBeyondForecast);
        Assert.Equal(e.Gathering, e.ReadyAfter[Trout]);
    }

    [Fact]
    public void Fish_already_owned_take_no_time()
    {
        var order = new Order();
        order.Add(TroutStew, 1);
        var plan = Planner.Build(order, new FakeCatalog(), new Dictionary<uint, int> { [Trout] = 2, [FireShard] = 1 }, new PlannerOptions { PreferVendor = false });
        var e = Estimator.Estimate(plan, Flat, (_, _) => 5, _ => true, null, new FishInputs(_ => new FishTime(600), Now));
        Assert.Equal(TimeSpan.Zero, e.Fishing);
        Assert.Equal(TimeSpan.Zero, e.Gathering);
    }

    [Fact]
    public void Fish_the_data_does_not_know_go_at_the_learned_rate()
    {
        var e = Fished(PlanOf((Trout, 3)), _ => null);
        Assert.Equal(180, e.Gathering.TotalSeconds, 3);
    }

    [Fact]
    public void Waiting_for_a_window_counts_when_nothing_else_fills_it()
    {
        var e = Fished(PlanOf((OldLord, 1)), _ => new FishTime(300, Window(10, 30)));
        Assert.Equal(600 + Flat.SecondsPerFishingTrip + 300, e.Gathering.TotalSeconds, 3);
        Assert.Equal(TimeSpan.FromMinutes(10), e.FishWait);
        Assert.Equal(e.Gathering, e.Fishing);
    }

    [Fact]
    public void Other_work_fills_the_wait_and_an_open_window_goes_first()
    {
        // Trout take 690 s; the Old Lord's 390 s start when its window opens at 300 s. Nobody waits.
        var e = Fished(PlanOf((Trout, 10), (OldLord, 1)), id => id == Trout ? new FishTime(60) : new FishTime(300, Window(5, 30)));
        Assert.Equal(690 + 390, e.Gathering.TotalSeconds, 3);
        Assert.Equal(TimeSpan.Zero, e.FishWait);
    }

    [Fact]
    public void Gathering_fills_the_wait_too()
    {
        // 20 logs at 10 a minute: 2 minutes of gathering before the window opens at 5 minutes.
        var e = Fished(PlanOf((Log, 20), (OldLord, 1)), _ => new FishTime(300, Window(5, 30)));
        Assert.Equal(TimeSpan.FromMinutes(3), e.FishWait);
        Assert.Equal(300 + Flat.SecondsPerFishingTrip + 300, e.Gathering.TotalSeconds, 3);
        Assert.Equal(e.Gathering - TimeSpan.FromMinutes(2), e.Fishing);
    }

    [Fact]
    public void Fish_without_a_window_in_the_forecast_are_named_and_still_counted()
    {
        var e = Fished(PlanOf((OldLord, 2)), _ => new FishTime(300, Array.Empty<(DateTimeOffset, DateTimeOffset)>()));
        Assert.Equal(new[] { OldLord }, e.FishBeyondForecast);
        Assert.Equal(Flat.SecondsPerFishingTrip + 600, e.Gathering.TotalSeconds, 3);
        Assert.Equal(TimeSpan.Zero, e.FishWait);
    }

    [Fact]
    public void Fish_sharing_a_window_take_turns_then_wait_for_the_next()
    {
        var a = new WindowedWork(1, 8 * 60, new[] { (Now, Now.AddMinutes(10)), (Now.AddMinutes(70), Now.AddMinutes(80)) });
        var b = new WindowedWork(2, 8 * 60, new[] { (Now, Now.AddMinutes(12)), (Now.AddMinutes(70), Now.AddMinutes(82)) });
        var r = FishSchedule.Run(0, new[] { a, b }, Now);
        // The window closing first goes first (0–8 min); the other fishes 8–12, waits for 70 and finishes at 74.
        Assert.Equal(TimeSpan.FromMinutes(74), r.Total);
        Assert.Equal(TimeSpan.FromMinutes(58), r.Waiting);
        Assert.Empty(r.Unscheduled);
    }

    [Fact]
    public void Fishing_that_runs_out_of_windows_is_unscheduled()
    {
        var r = FishSchedule.Run(60, new[] { new WindowedWork(7, 900, Window(1, 6)) }, Now);
        // 60 s of other work, a 5-minute window from 1 min: 300 of the 900 s fit, the other 600 are counted on top.
        Assert.Equal(new uint[] { 7 }, r.Unscheduled);
        Assert.Equal(TimeSpan.FromSeconds(360 + 600), r.Total);
        Assert.Equal(TimeSpan.Zero, r.Waiting);
    }

    [Fact]
    public void Windows_follow_the_fish_and_end_at_the_horizon()
    {
        const long hour = 175_000; // one Eorzea hour in real milliseconds
        var fish = new FishInfo(1, false, 2585, Array.Empty<uint>(), Array.Empty<(uint, int)>(), 0,
            new Uptime(6 * hour, 18 * hour, 9 * hour), Array.Empty<uint>(), Array.Empty<uint>(),
            HookSet.Precise, Tug.Weak, false, "None", SpearSize.Unknown,
            SpearSpeed.Unknown, 0, false, false, Array.Empty<uint>(), (3f, 20f));
        var table = new (uint, int)[] { (1, 100) };
        var from = DateTimeOffset.FromUnixTimeMilliseconds(8 * hour);
        var windows = FishSchedule.Windows(fish, new[] { table }, from, TimeSpan.FromMilliseconds(30 * hour));
        Assert.Equal(new[]
        {
            (DateTimeOffset.FromUnixTimeMilliseconds(9 * hour), DateTimeOffset.FromUnixTimeMilliseconds(15 * hour)),
            (DateTimeOffset.FromUnixTimeMilliseconds(33 * hour), DateTimeOffset.FromUnixTimeMilliseconds(38 * hour)), // cut at the horizon
        }, windows);
        // A fish without a window bites the whole time.
        var always = fish with { Window = null };
        Assert.Equal(new[] { (from, from + TimeSpan.FromHours(2)) }, FishSchedule.Windows(always, new[] { table }, from, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void Windows_of_several_spots_merge()
    {
        var t0 = Now;
        var merged = FishSchedule.Merge(new[]
        {
            (t0.AddMinutes(30), t0.AddMinutes(40)), (t0, t0.AddMinutes(10)), (t0.AddMinutes(5), t0.AddMinutes(20)), (t0.AddMinutes(40), t0.AddMinutes(50)),
        });
        Assert.Equal(new[] { (t0, t0.AddMinutes(20)), (t0.AddMinutes(30), t0.AddMinutes(50)) }, merged);
    }
}
