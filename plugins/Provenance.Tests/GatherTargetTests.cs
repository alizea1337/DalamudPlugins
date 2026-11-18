using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Orders;
using Provenance.Core.Planning;
using Provenance.Core.Run;
using Xunit;
using static Provenance.Tests.FakeCatalog;

namespace Provenance.Tests;

public class GatherTargetTests
{
    private static Plan PlanFor(uint item, int qty, Dictionary<uint, int>? owned = null)
    {
        var o = new Order();
        o.Add(item, qty);
        return Planner.Build(o, new FakeCatalog(), owned ?? new Dictionary<uint, int>(), new PlannerOptions());
    }

    [Fact]
    public void Targets_are_bag_count_plus_what_is_still_needed()
    {
        // 2 bows: 12 logs, of which 5 are owned (say 3 in the bags, 2 in the saddlebag) -> 7 still to gather.
        var plan = PlanFor(Bow, 2, new Dictionary<uint, int> { [Log] = 5 });
        var targets = GatherTargets.Build(plan, id => id == Log ? 3 : 0);
        var log = targets.Single(t => t.ItemId == Log);
        Assert.Equal(7, log.Remaining);
        Assert.Equal(10u, log.Target);
    }

    [Fact]
    public void Only_gathering_and_fishing_lines_with_something_left_are_included()
    {
        var plan = PlanFor(Bow, 1, new Dictionary<uint, int> { [Ore] = 99 });
        var ids = GatherTargets.Build(plan, _ => 0).Select(t => t.ItemId).ToList();
        Assert.Contains(Log, ids);
        Assert.DoesNotContain(Ore, ids);       // owned
        Assert.DoesNotContain(Lumber, ids);    // crafted
    }

    [Fact]
    public void Timed_items_come_last()
    {
        var o = new Order();
        o.Add(RareLumber, 1);
        o.Add(Lumber, 1);
        var plan = Planner.Build(o, new FakeCatalog(), new Dictionary<uint, int>(), new PlannerOptions());
        var targets = GatherTargets.Build(plan, _ => 0);
        Assert.Equal(Supply.GatherTimed, targets.Last().Supply);
    }

    [Fact]
    public void Done_once_the_bags_hold_every_target()
    {
        var plan = PlanFor(Lumber, 1);
        var targets = GatherTargets.Build(plan, _ => 0);       // 3 logs, 1 wind shard
        Assert.False(GatherTargets.AllDone(targets, id => id == Log ? 3 : 0));
        Assert.Equal(2, GatherTargets.Left(targets.Single(t => t.ItemId == Log), _ => 1));
        Assert.True(GatherTargets.AllDone(targets, id => id == Log ? 5 : 1));
    }

    [Fact]
    public void Gathered_order_items_count_what_is_owned()
    {
        // Ordering 2 logs with 5 owned: nothing to do. Crafted order items still ignore stock by default.
        var plan = PlanFor(Log, 2, new Dictionary<uint, int> { [Log] = 5 });
        Assert.Equal(0, plan[Log]!.Remaining);
        Assert.Empty(GatherTargets.Build(plan, _ => 5));
        var crafted = PlanFor(Lumber, 1, new Dictionary<uint, int> { [Lumber] = 1 });
        Assert.Equal(1, crafted[Lumber]!.Crafts);
    }
}
