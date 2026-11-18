using System.Collections.Generic;
using Provenance.Core.Scrips;
using Xunit;

namespace Provenance.Tests;

public class ScripTests
{
    private const uint Purple = 33913;
    private const uint Orange = 41784;

    private static CollectableOption Option(uint item, uint job, int level, uint currency, int mid)
        => new(item, job, level, currency, mid - 5, mid, mid + 5, 100, 200, 300, 0, true);

    private static readonly List<CollectableOption> Options = new()
    {
        Option(1, 8, 60, Purple, 49),
        Option(2, 8, 90, Purple, 117),
        Option(3, 9, 90, Purple, 117),
        Option(4, 8, 100, Orange, 140),
        Option(5, 8, 80, Purple, 97),
    };

    [Fact]
    public void Fewest_items_at_the_lowest_reward()
    {
        var task = ScripPlanner.Plan(Purple, 300, Options, _ => 100)!;
        Assert.Equal(2u, task.Option.ItemId);   // 3 × 112 at the lowest reward, item 3 ties but has the higher id
        Assert.Equal(3, task.Count);
        Assert.Equal(336, task.Expected);
    }

    [Fact]
    public void Only_what_the_job_level_allows()
    {
        var task = ScripPlanner.Plan(Purple, 300, Options, job => job == 8 ? 85 : 1)!;
        Assert.Equal(5u, task.Option.ItemId);   // level 80, 4 × 92 at the lowest reward
        Assert.Equal(4, task.Count);
    }

    [Fact]
    public void Lower_level_wins_when_the_count_is_the_same()
    {
        var task = ScripPlanner.Plan(Purple, 40, Options, _ => 100)!;
        Assert.Equal(1u, task.Option.ItemId);   // one of anything covers 40: take the level 60 one
        Assert.Equal(1, task.Count);
    }

    [Fact]
    public void Nothing_to_earn_or_nothing_usable()
    {
        Assert.Null(ScripPlanner.Plan(Purple, 0, Options, _ => 100));
        Assert.Null(ScripPlanner.Plan(Orange, 100, Options, _ => 90));
        Assert.Null(ScripPlanner.Plan(Purple, 100, Options, _ => 100, _ => false));
    }
}
