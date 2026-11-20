using System;
using System.Collections.Generic;
using System.Linq;

namespace Provenance.Core.Scrips;

/// <summary>Something a run buys at an exchange: its price per item in one currency, and how many.</summary>
public readonly record struct ScripCost(uint ItemId, uint Currency, int UnitCost, int Amount);

/// <summary>
/// How a run spends scrips on what an order buys at exchanges (master books, folklore tomes, materials): what the
/// scrips at hand buy now, and how many to earn in the next round for the rest. Scrips stop at a cap (4,000), so a
/// big batch takes turns: buy what's affordable, earn up to the cap, buy again.
/// </summary>
/// <param name="BuyNow">How many of each item the scrips at hand pay for now (the cheapest per item first).</param>
/// <param name="ToEarn">Scrips to earn in the next round, per currency (never past the cap).</param>
/// <param name="Later">How many items wait (in whole or in part) for a later round, per currency.</param>
public sealed record ScripBudget(IReadOnlyDictionary<uint, int> BuyNow, IReadOnlyDictionary<uint, int> ToEarn, IReadOnlyDictionary<uint, int> Later)
{
    public static readonly ScripBudget None = new(new Dictionary<uint, int>(), new Dictionary<uint, int>(), new Dictionary<uint, int>());

    /// <param name="have">Scrips the character has, by currency item.</param>
    /// <param name="cap">Most of a currency the character can hold (int.MaxValue when unlimited).</param>
    public static ScripBudget Allocate(IEnumerable<ScripCost> costs, Func<uint, int> have, Func<uint, int> cap)
    {
        var buyNow = new Dictionary<uint, int>();
        var toEarn = new Dictionary<uint, int>();
        var later = new Dictionary<uint, int>();
        foreach (var group in costs.Where(c => c.UnitCost > 0 && c.Amount > 0).DistinctBy(c => c.ItemId).GroupBy(c => c.Currency))
        {
            var left = Math.Max(0, have(group.Key));
            var rest = 0L;
            var waiting = 0;
            // Cheapest per item first: the most for the scrips at hand. Once something doesn't fit in full, everything
            // dearer waits for the next round too (a material is still bought in part, as far as the scrips go).
            foreach (var c in group.OrderBy(c => c.UnitCost).ThenBy(c => c.ItemId))
            {
                var bought = rest == 0 ? Math.Min(c.Amount, left / c.UnitCost) : 0;
                if (bought > 0)
                {
                    buyNow[c.ItemId] = bought;
                    left -= bought * c.UnitCost;
                }
                if (bought == c.Amount) continue;
                rest += (long)(c.Amount - bought) * c.UnitCost;
                waiting++;
            }
            if (waiting == 0) continue;
            later[group.Key] = waiting;
            var room = (long)Math.Max(0, cap(group.Key)) - left;
            var earn = Math.Min(rest - left, room);
            if (earn > 0) toEarn[group.Key] = (int)earn;
        }
        return new ScripBudget(buyNow, toEarn, later);
    }
}
