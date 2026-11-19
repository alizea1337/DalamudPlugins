using System;
using System.Collections.Generic;
using Provenance.Core.Estimating;
using Provenance.Core.Gathering;
using Provenance.Core.Orders;
using Provenance.Core.Planning;
using Xunit;

namespace Provenance.Tests;

public class GpBudgetTests
{
    private static readonly Cordial Hi = new(12669, false, 400, 180);
    private static readonly Cordial Plain = new(6141, false, 300, 240);
    private static readonly Cordial PlainHq = new(6141, true, 350, 240);
    private static readonly Cordial Watered = new(16911, false, 150, 140);

    [Fact]
    public void Picks_the_smallest_cordial_that_closes_the_gap()
    {
        var all = new[] { Hi, Plain, PlainHq, Watered };
        Assert.Equal(Watered, GpBudget.Pick(all, 500, 600, 884));  // 100 short
        Assert.Equal(PlainHq, GpBudget.Pick(all, 260, 600, 884));  // 340 short: the 350 one
        Assert.Equal(Hi, GpBudget.Pick(all, 0, 600, 884));         // nothing closes 600: the biggest
        Assert.Null(GpBudget.Pick(all, 600, 600, 884));
        Assert.Null(GpBudget.Pick(Array.Empty<Cordial>(), 0, 600, 884));
    }

    [Fact]
    public void A_cordial_mostly_lost_to_the_gp_cap_is_not_worth_it()
    {
        // 680 max, 40 short: a Hi-Cordial would lose 280 to the cap, so wait (or take the small one).
        Assert.Null(GpBudget.Pick(new[] { Hi }, 560, 600, 680));
        Assert.Equal(Watered, GpBudget.Pick(new[] { Hi, Watered }, 560, 600, 680));
    }

    [Fact]
    public void Below_the_full_rotations_gp_nothing_is_waited_for()
    {
        Assert.Equal(600, GpBudget.Need(884));
        Assert.Equal(0, GpBudget.Need(560));   // the full rotation never runs: waiting buys nothing
        Assert.Equal(GpWaitEstimate.None, GpBudget.Simulate(3, 0, 560, new[] { Hi }, 300, 45));
    }

    [Fact]
    public void Gp_comes_back_only_outside_the_node()
    {
        // 45 s visits, 25 of them in the node: 484 + 20 s of regen (517): 83 short, about 50 s.
        var e = GpBudget.Simulate(2, 884, 884, Array.Empty<Cordial>(), 400, 45, nodeSecondsPerVisit: 25);
        Assert.InRange(e.Wait.TotalSeconds, 49, 50.5);
    }

    [Fact]
    public void A_cordial_still_cooling_down_is_waited_for()
    {
        Assert.Equal(new GpWaitEstimate(TimeSpan.Zero, 1), GpBudget.Simulate(1, 500, 884, new[] { Watered }, 400, 45));
        var later = GpBudget.Simulate(1, 500, 884, new[] { Watered }, 400, 45, cordialReadyIn: 30);
        Assert.Equal(1, later.Cordials);
        Assert.InRange(later.Wait.TotalSeconds, 29.9, 30.1);
    }

    [Fact]
    public void Starting_full_the_first_visit_waits_nothing()
        => Assert.Equal(GpWaitEstimate.None, GpBudget.Simulate(1, 884, 884, Array.Empty<Cordial>(), 400, 45));

    [Fact]
    public void Later_visits_wait_for_the_gp_they_spent()
    {
        // 884, a visit spends 400 (484), 75 come back on the way (559): 41 short, about 24.6 s.
        var e = GpBudget.Simulate(2, 884, 884, Array.Empty<Cordial>(), 400, 45);
        Assert.Equal(0, e.Cordials);
        Assert.InRange(e.Wait.TotalSeconds, 24, 25);
    }

    [Fact]
    public void A_cordial_replaces_the_wait()
    {
        var e = GpBudget.Simulate(2, 884, 884, new[] { Watered }, 400, 45);
        Assert.Equal(1, e.Cordials);
        Assert.Equal(TimeSpan.Zero, e.Wait);
    }

    [Fact]
    public void Cordials_wait_for_their_cooldown()
    {
        // From 0 GP with every visit spending it all: the second potion is ready only after 140 s.
        var none = GpBudget.Simulate(3, 0, 600, Array.Empty<Cordial>(), 600, 10);
        var two = GpBudget.Simulate(3, 0, 600, new[] { Watered, Watered }, 600, 10);
        Assert.InRange(none.Wait.TotalSeconds, 1059, 1061);  // 360 + 350 + 350
        Assert.Equal(2, two.Cordials);
        Assert.InRange(two.Wait.TotalSeconds, 879, 881);     // each potion saves 150 GP, 90 s
    }

    [Fact]
    public void Collectables_count_by_node_visits_with_the_wait_for_gp()
    {
        var order = new Order();
        order.Add(FakeCatalog.Ore, 4);
        var plan = Planner.Build(order, new FakeCatalog(), new Dictionary<uint, int>(), new PlannerOptions());
        var t = new Timings { CollectablesPerVisit = 2, SecondsPerCollectableVisit = 45, SecondsInCollectableNode = 0, GpPerCollectableVisit = 400, GatheredPerMinute = 12 };

        var plain = Estimator.Estimate(plan, t, (_, _) => null, _ => false);
        Assert.Equal(TimeSpan.FromMinutes(4 / 12.0), plain.Gathering);

        // Two visits of 45 s, and the second one waits for the GP the first spent.
        var inputs = new CollectableInputs(id => id == FakeCatalog.Ore, _ => 884, Array.Empty<Cordial>(), true);
        var e = Estimator.Estimate(plan, t, (_, _) => null, _ => false, inputs);
        Assert.InRange(e.GpWait.Wait.TotalSeconds, 24, 25);
        Assert.InRange(e.Gathering.TotalSeconds, 90 + 24, 90 + 25);

        var noWait = Estimator.Estimate(plan, t, (_, _) => null, _ => false, inputs with { WaitForGp = false });
        Assert.Equal(TimeSpan.FromSeconds(90), noWait.Gathering);
        Assert.Equal(GpWaitEstimate.None, noWait.GpWait);
    }
}
