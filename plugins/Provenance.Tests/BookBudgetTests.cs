using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Scrips;
using Xunit;

namespace Provenance.Tests;

public class BookBudgetTests
{
    private const uint Purple = 33913, Orange = 41784, Cap = 4000;

    private static BookBudget Allocate(Dictionary<uint, int> have, params BookCost[] books)
        => BookBudget.Allocate(books, c => have.GetValueOrDefault(c), _ => (int)Cap);

    [Fact]
    public void Scrips_at_hand_buy_the_cheapest_books_first()
    {
        var b = Allocate(new() { [Purple] = 700 },
            new BookCost(1, Purple, 400), new BookCost(2, Purple, 300), new BookCost(3, Purple, 500));
        Assert.Equal(new uint[] { 2, 1 }, b.BuyNow.OrderBy(i => i == 2 ? 0 : 1));
        Assert.Equal(2, b.BuyNow.Count);
        // 700 − 300 − 400 = 0 left; the 500 book needs 500 more.
        Assert.Equal(500, b.ToEarn[Purple]);
        Assert.Equal(1, b.Later[Purple]);
    }

    [Fact]
    public void A_round_never_earns_past_the_cap()
    {
        // Fourteen books at 600: 8,400 in all, 100 at hand. One round can only bring the bags to 4,000.
        var books = Enumerable.Range(1, 14).Select(i => new BookCost((uint)i, Orange, 600)).ToArray();
        var b = Allocate(new() { [Orange] = 100 }, books);
        Assert.Empty(b.BuyNow);
        Assert.Equal(3900, b.ToEarn[Orange]);
        Assert.Equal(14, b.Later[Orange]);

        // At the cap: six books now (3,600), then earn up to the cap again from the 400 left.
        b = Allocate(new() { [Orange] = 4000 }, books);
        Assert.Equal(6, b.BuyNow.Count);
        Assert.Equal(3600, b.ToEarn[Orange]);
        Assert.Equal(8, b.Later[Orange]);
    }

    [Fact]
    public void Enough_at_hand_means_nothing_to_earn()
    {
        var b = Allocate(new() { [Purple] = 1000, [Orange] = 0 }, new BookCost(1, Purple, 300), new BookCost(2, Orange, 200));
        Assert.Contains(1u, b.BuyNow);
        Assert.False(b.ToEarn.ContainsKey(Purple));
        Assert.Equal(200, b.ToEarn[Orange]);
    }

    [Fact]
    public void A_book_listed_twice_is_bought_once()
    {
        var b = Allocate(new() { [Purple] = 300 }, new BookCost(1, Purple, 300), new BookCost(1, Purple, 300));
        Assert.Single(b.BuyNow);
        Assert.Empty(b.ToEarn);
    }
}
