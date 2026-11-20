using System;
using System.Collections.Generic;
using System.Linq;

namespace Provenance.Core.Scrips;

/// <summary>A book a run can buy at an exchange: its price in one currency.</summary>
public readonly record struct BookCost(uint ItemId, uint Currency, int Cost);

/// <summary>
/// How a run gets a batch of books with scrips: which it buys now with what the character has, and how many
/// scrips to earn in the next round for the rest. Scrips stop at a cap (4,000), so a big batch takes turns:
/// buy what's affordable, earn up to the cap, buy again.
/// </summary>
/// <param name="BuyNow">Books the scrips at hand pay for (the cheapest first).</param>
/// <param name="ToEarn">Scrips to earn in the next round, per currency (never past the cap).</param>
/// <param name="Later">Books that wait for a later round, per currency.</param>
public sealed record BookBudget(IReadOnlySet<uint> BuyNow, IReadOnlyDictionary<uint, int> ToEarn, IReadOnlyDictionary<uint, int> Later)
{
    public static readonly BookBudget None = new(new HashSet<uint>(), new Dictionary<uint, int>(), new Dictionary<uint, int>());

    /// <param name="have">Scrips the character has, by currency item.</param>
    /// <param name="cap">Most of a currency the character can hold (int.MaxValue when unlimited).</param>
    public static BookBudget Allocate(IEnumerable<BookCost> books, Func<uint, int> have, Func<uint, int> cap)
    {
        var buyNow = new HashSet<uint>();
        var toEarn = new Dictionary<uint, int>();
        var later = new Dictionary<uint, int>();
        foreach (var group in books.Where(b => b.Cost > 0).DistinctBy(b => b.ItemId).GroupBy(b => b.Currency))
        {
            var left = Math.Max(0, have(group.Key));
            var rest = 0L;
            var waiting = 0;
            // Cheapest first: the most books for the scrips at hand, and once one doesn't fit, no dearer one does.
            foreach (var book in group.OrderBy(b => b.Cost).ThenBy(b => b.ItemId))
            {
                if (rest == 0 && book.Cost <= left) { buyNow.Add(book.ItemId); left -= book.Cost; }
                else { rest += book.Cost; waiting++; }
            }
            if (waiting == 0) continue;
            later[group.Key] = waiting;
            var room = (long)Math.Max(0, cap(group.Key)) - left;
            var earn = Math.Min(rest - left, room);
            if (earn > 0) toEarn[group.Key] = (int)earn;
        }
        return new BookBudget(buyNow, toEarn, later);
    }
}
