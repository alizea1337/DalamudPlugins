using System.Collections.Generic;
using Provenance.Core.Scrips;
using Xunit;

namespace Provenance.Tests;

/// <summary>
/// Scrips shared by everything bought at exchanges: books (one each) and materials (several of one item, bought in
/// part when the scrips at hand only stretch that far).
/// </summary>
public class ScripBudgetTests
{
    private const uint Orange = 41784, Cap = 4000;
    private const uint FlintCorn = 49229, Book = 900;

    private static ScripBudget Allocate(int have, params ScripCost[] costs)
        => ScripBudget.Allocate(costs, c => c == Orange ? have : 0, _ => (int)Cap);

    [Fact]
    public void Materials_are_bought_in_part_and_the_rest_earned()
    {
        // Ten Flint Corn at 15: 100 at hand buys six (90); the other four need 60, 10 of it already there.
        var b = Allocate(100, new ScripCost(FlintCorn, Orange, 15, 10));
        Assert.Equal(6, b.BuyNow[FlintCorn]);
        Assert.Equal(50, b.ToEarn[Orange]);
        Assert.Equal(1, b.Later[Orange]);
    }

    [Fact]
    public void The_cheaper_unit_goes_first_across_books_and_materials()
    {
        // 500 at hand: all ten Flint Corn (150), then the 450 book doesn't fit in the 350 left.
        var b = Allocate(500, new ScripCost(Book, Orange, 450, 1), new ScripCost(FlintCorn, Orange, 15, 10));
        Assert.Equal(10, b.BuyNow[FlintCorn]);
        Assert.False(b.BuyNow.ContainsKey(Book));
        Assert.Equal(100, b.ToEarn[Orange]);
    }

    [Fact]
    public void More_than_the_cap_takes_rounds()
    {
        // 300 at 15 is 4,500: more than a character can hold. Earn up to the cap, buy, earn again.
        var b = Allocate(0, new ScripCost(FlintCorn, Orange, 15, 300));
        Assert.False(b.BuyNow.ContainsKey(FlintCorn));
        Assert.Equal(4000, b.ToEarn[Orange]);

        b = Allocate(4000, new ScripCost(FlintCorn, Orange, 15, 300));
        Assert.Equal(266, b.BuyNow[FlintCorn]);
        Assert.Equal(500, b.ToEarn[Orange]); // 34 left to buy at 15 = 510, 10 of it at hand
    }

    [Fact]
    public void Everything_affordable_earns_nothing()
    {
        var b = Allocate(200, new ScripCost(FlintCorn, Orange, 15, 10));
        Assert.Equal(10, b.BuyNow[FlintCorn]);
        Assert.Empty(b.ToEarn);
        Assert.Empty(b.Later);
    }
}
