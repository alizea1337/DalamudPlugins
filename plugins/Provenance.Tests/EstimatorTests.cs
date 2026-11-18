using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Estimating;
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
}
