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
        // A book is one item: cheapest first, and once one doesn't fit, no dearer one does (see ScripBudget).
        var b = ScripBudget.Allocate(books.Select(x => new ScripCost(x.ItemId, x.Currency, x.Cost, 1)), have, cap);
        return new BookBudget(b.BuyNow.Keys.ToHashSet(), b.ToEarn, b.Later);
    }
}
