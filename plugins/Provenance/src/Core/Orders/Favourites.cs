using System;
using System.Collections.Generic;
using System.Linq;

namespace Provenance.Core.Orders;

/// <summary>An order kept to use again: its name, what's on it, and when it was saved.</summary>
[Serializable]
public sealed class FavouriteOrder
{
    /// <summary>Stays the same through renames and updates (for the window's widgets).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public List<OrderLine> Lines { get; set; } = new();
    public DateTime SavedUtc { get; set; }

    /// <summary>A copy of what's on an order (lines with an amount only).</summary>
    public static FavouriteOrder From(Order order, string name, DateTime nowUtc)
        => new() { Name = name, Lines = Favourites.CopyLines(order.Lines), SavedUtc = nowUtc };

    /// <summary>A fresh order with this favourite's name and lines (a copy, so editing it leaves the favourite alone).</summary>
    public Order ToOrder() => new() { Name = Name, Lines = Favourites.CopyLines(Lines) };
}

public static class Favourites
{
    public static List<OrderLine> CopyLines(IEnumerable<OrderLine> lines)
        => lines.Where(l => l.Quantity > 0).Select(l => new OrderLine(l.ItemId, l.Quantity, l.RecipeId, l.Hq)).ToList();

    /// <summary>
    /// Teamcraft's "Copy as text": one "10x Item Name" per item, the lines of one item summed (quality and recipe
    /// aren't part of the format). Pasted into Teamcraft or into Provenance's own import, it comes back as these items.
    /// </summary>
    public static string ToTeamcraftText(IEnumerable<OrderLine> lines, Func<uint, string> name)
        => string.Join("\n", lines.Where(l => l.Quantity > 0).GroupBy(l => l.ItemId).Select(g => $"{g.Sum(l => l.Quantity)}x {name(g.Key)}"));

    /// <summary>A favourite that is just this one item (in any amount, quality or recipe): what a star in Browse saves.</summary>
    public static bool IsJust(FavouriteOrder favourite, uint itemId)
    {
        var lines = favourite.Lines.Where(l => l.Quantity > 0).ToList();
        return lines.Count > 0 && lines.All(l => l.ItemId == itemId);
    }

    /// <summary>One item as a favourite order of its own (a star in Browse).</summary>
    public static FavouriteOrder OfItem(uint itemId, int quantity, uint recipeId, bool hq, string name, DateTime nowUtc)
        => new() { Name = name, Lines = new List<OrderLine> { new(itemId, Math.Max(1, quantity), recipeId, hq) }, SavedUtc = nowUtc };

    /// <summary>A name no other favourite has: "Popcorn", then "Popcorn (2)", "Popcorn (3)" (ignoring case).</summary>
    public static string UniqueName(string wanted, IEnumerable<string> taken)
    {
        var name = string.IsNullOrWhiteSpace(wanted) ? "Favourite order" : wanted.Trim();
        var names = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(name)) return name;
        for (var i = 2; ; i++)
            if (!names.Contains($"{name} ({i})")) return $"{name} ({i})";
    }

    /// <summary>The same items in the same amounts, quality and recipe, whatever the order of the lines.</summary>
    public static bool SameLines(IEnumerable<OrderLine> a, IEnumerable<OrderLine> b)
    {
        static IEnumerable<(uint, uint, bool, int)> Key(IEnumerable<OrderLine> lines)
            => lines.Where(l => l.Quantity > 0).GroupBy(l => (l.ItemId, l.RecipeId, l.Hq)).Select(g => (g.Key.ItemId, g.Key.RecipeId, g.Key.Hq, g.Sum(l => l.Quantity)))
                .OrderBy(x => x.ItemId).ThenBy(x => x.RecipeId).ThenBy(x => x.Hq);
        return Key(a).SequenceEqual(Key(b));
    }
}
