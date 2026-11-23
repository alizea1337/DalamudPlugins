using System.Collections.Generic;
using System.Linq;

namespace Provenance.Core.Tribes;

/// <summary>One of the rewards a quest's reward window asks you to choose from.</summary>
/// <param name="Slot">Its place in the window (0 is the first listed).</param>
/// <param name="ItemId">The item, without the high-quality mark (1 is gil).</param>
/// <param name="Hq">High quality.</param>
/// <param name="Icon">The icon the window shows for it (<see cref="RewardChoice.GilIcon"/>: a sack of gil).</param>
/// <param name="Amount">How many (for gil, how much).</param>
/// <param name="Name">The name the window shows.</param>
/// <param name="PriceLow">What a vendor pays for one (the item's <c>PriceLow</c>; 0 for gil).</param>
public sealed record RewardOption(int Slot, uint ItemId, bool Hq, uint Icon, uint Amount, string Name, uint PriceLow)
{
    public bool IsGil => ItemId == RewardChoice.GilItem || Icon == RewardChoice.GilIcon;

    /// <summary>What a vendor pays for all of it (an amount of 0 counts as one).</summary>
    public ulong VendorValue => (ulong)PriceLow * (Amount == 0 ? 1u : Amount);

    public override string ToString()
        => IsGil ? $"{Slot + 1}: {Amount} gil" : $"{Slot + 1}: {Name}{(Hq ? " (HQ)" : "")} x{Amount} (item {ItemId}, sells for {VendorValue} gil)";
}

/// <summary>
/// Which reward to take when the quest's reward window makes you choose: gil first (the larger sum), then the item a
/// vendor pays the most for (its price times the amount), then the first listed. The player asked for that order.
/// </summary>
public static class RewardChoice
{
    /// <summary>The icon the reward window shows for a sack of gil.</summary>
    public const uint GilIcon = 26001;
    /// <summary>The item id of gil.</summary>
    public const uint GilItem = 1;

    /// <summary>The reward to take; null when there's nothing to choose from.</summary>
    public static RewardOption? Pick(IReadOnlyList<RewardOption> options)
    {
        if (options.Count == 0) return null;
        // OrderBy keeps the listed order among equals: a tie goes to the first listed.
        var listed = options.OrderBy(o => o.Slot).ToList();
        if (listed.Any(o => o.IsGil)) return listed.Where(o => o.IsGil).OrderByDescending(o => o.Amount).First();
        return listed.OrderByDescending(o => o.VendorValue).First();
    }

    /// <summary>Why the pick is the one taken (for the log).</summary>
    public static string Why(RewardOption pick, IReadOnlyList<RewardOption> options)
    {
        if (pick.IsGil) return "gil comes first";
        var best = options.Count(o => o.VendorValue == pick.VendorValue);
        return best > 1 ? $"{best} sell for the most ({pick.VendorValue} gil); the first listed" : $"sells for the most ({pick.VendorValue} gil)";
    }
}
