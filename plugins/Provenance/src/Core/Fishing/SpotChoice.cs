using System;
using System.Collections.Generic;
using System.Linq;

namespace Provenance.Core.Fishing;

/// <summary>A fish at one of its spots, as the fisher weighs where to go next.</summary>
/// <param name="ItemId">The fish.</param>
/// <param name="SpotId">The fishing hole or spearfishing spot.</param>
/// <param name="Restricted">The fish only bites at some hours or in some weather.</param>
/// <param name="Wait">Real time until it bites there (zero when it bites now); null when not within the forecast.</param>
/// <param name="Bites">How long it keeps biting once open; <see cref="TimeSpan.MaxValue"/> for a fish without a window.</param>
/// <param name="Here">The spot is in the zone the player is in.</param>
/// <param name="TeleportCost">Gil the teleport to the spot's zone costs (0 when here).</param>
/// <param name="Resting">Skipped for a while: the fish there sensed something amiss, or no place to stand worked.</param>
public readonly record struct SpotCandidate(uint ItemId, uint SpotId, bool Restricted, TimeSpan? Wait, TimeSpan Bites, bool Here, int TeleportCost, bool Resting = false)
{
    /// <summary>When the window closes, from now; <see cref="TimeSpan.MaxValue"/> without a window or when it never opens.</summary>
    public TimeSpan Closes => Wait is not { } w || Bites == TimeSpan.MaxValue || TimeSpan.MaxValue - w < Bites ? TimeSpan.MaxValue : w + Bites;
}

/// <summary>
/// Which fish to go for and where: a fish's window at each of its spots (per spot zone, since the weather is
/// the zone's), how early to be there (the lead, with time to catch the fish for Fisher's Intuition), the
/// best spot per fish (bites within the lead, this zone, the longest window, the cheapest teleport) and the
/// order between fish (windows closing soonest first, then fish without a window). Also how much bait to buy
/// when it runs out mid-run. Follows GatherBuddy Reborn's fish ordering (ActiveItemList, Apache-2.0). Pure.
/// </summary>
public static class SpotChoice
{
    /// <summary>How far ahead the weather forecast is searched for a fish's next window.</summary>
    public static readonly TimeSpan Horizon = FishWindow.Forecast;

    /// <summary>A window must stay open this long to be worth the way: in this zone (walk to a stand) and from another zone (teleport too).</summary>
    public static readonly TimeSpan MinUsefulHere = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MinUsefulAway = TimeSpan.FromSeconds(150);

    /// <summary>Catching the fish for Fisher's Intuition counts for at most this long in the lead.</summary>
    public static readonly TimeSpan MaxPrep = TimeSpan.FromMinutes(30);

    /// <summary>
    /// A fish's window at one spot: real time until it bites (zero when it bites now, null when not within
    /// <paramref name="horizon"/>) and how long it then keeps biting (<see cref="TimeSpan.MaxValue"/> without a window).
    /// </summary>
    public static (TimeSpan? Wait, TimeSpan Bites) Window(FishInfo fish, IReadOnlyList<(uint Weather, int Rate)> table, DateTimeOffset now, TimeSpan horizon)
    {
        if (!fish.Restricted) return (TimeSpan.Zero, TimeSpan.MaxValue);
        if (FishWindow.NextOpen(fish, table, now, horizon) is not { } open) return (null, TimeSpan.Zero);
        // A window open for longer than a day counts as a day: plenty for any order.
        return (open - now, FishWindow.Left(fish, table, open, TimeSpan.FromDays(1)));
    }

    /// <summary>
    /// When a fish and another one it depends on both bite, each in its own zone's weather (a fish behind swimming
    /// shadows and the predator gigged first at its own spot, as GBR's IntersectPredatorUptime): real time until
    /// both do (zero when they do now, null when not within <paramref name="horizon"/>) and how long both then keep
    /// biting (<see cref="TimeSpan.MaxValue"/> when neither has a window).
    /// </summary>
    public static (TimeSpan? Wait, TimeSpan Bites) Window(FishInfo fish, IReadOnlyList<(uint Weather, int Rate)> table,
        FishInfo other, IReadOnlyList<(uint Weather, int Rate)> otherTable, DateTimeOffset now, TimeSpan horizon)
    {
        if (!other.Restricted) return Window(fish, table, now, horizon);
        if (!fish.Restricted) return Window(other, otherTable, now, horizon);
        var end = now + horizon;
        var from = now;
        // Each round starts at the other fish's next opening, so it moves on until both bite at once.
        for (var round = 0; round < 1000 && from <= end; round++)
        {
            if (FishWindow.NextOpen(fish, table, from, end - from) is not { } open) break;
            if (FishWindow.NextOpen(other, otherTable, open, end - open) is not { } otherOpen) break;
            if (otherOpen == open)
            {
                var day = TimeSpan.FromDays(1);
                var a = FishWindow.Left(fish, table, open, day);
                var b = FishWindow.Left(other, otherTable, open, day);
                return (open - now, a < b ? a : b);
            }
            from = otherOpen;
        }
        return (null, TimeSpan.Zero);
    }

    /// <summary>
    /// How long before a window the fisher wants to be at work: the setting's minutes, plus catching the
    /// fish Fisher's Intuition needs (each catch takes <paramref name="secondsPerCatch"/>, once more per link
    /// of a mooch chain), at most <see cref="MaxPrep"/>. A spearfish's predator (for its swimming shadows) that
    /// has a window of its own is gigged inside that window, which already bounds the fish's: nothing is added.
    /// </summary>
    public static TimeSpan Lead(FishInfo fish, int leadMinutes, Func<uint, FishInfo?> fishData, double secondsPerCatch)
    {
        var prep = 0.0;
        foreach (var (id, count) in fish.Predators)
        {
            var predator = fishData(id);
            if (fish.Spear && predator is { Restricted: true }) continue;
            prep += Math.Max(1, count) * Math.Max(1, secondsPerCatch) * (1 + (predator?.Mooch.Count ?? 0));
        }
        return TimeSpan.FromMinutes(Math.Max(0, leadMinutes)) + TimeSpan.FromSeconds(Math.Min(prep, MaxPrep.TotalSeconds));
    }

    /// <summary>
    /// Worth working on now: it bites within the lead, its window stays open long enough for the way there,
    /// and the spot isn't resting.
    /// </summary>
    public static bool Workable(SpotCandidate c, TimeSpan lead)
    {
        if (c.Resting || c.Wait is not { } wait || wait > lead) return false;
        if (!c.Restricted || c.Bites == TimeSpan.MaxValue) return true;
        // Time left once there: a window open now loses the way; one opening later keeps all of it.
        var way = c.Here ? MinUsefulHere : MinUsefulAway;
        var usable = wait >= way ? c.Bites : c.Bites - (way - wait);
        return usable >= MinUsefulHere;
    }

    /// <summary>
    /// The best spot for one fish among its candidates, or null when none is workable now: this zone first,
    /// then the window that closes last (more time to catch enough), then the cheaper teleport.
    /// </summary>
    public static SpotCandidate? Best(IEnumerable<SpotCandidate> spots, TimeSpan lead)
        => spots.Where(c => Workable(c, lead))
            .OrderByDescending(c => c.Here)
            .ThenByDescending(c => c.Closes)
            .ThenBy(c => c.TeleportCost)
            .ThenBy(c => c.SpotId)
            .Cast<SpotCandidate?>()
            .FirstOrDefault();

    /// <summary>The spot where a fish bites soonest (resting spots left out), for waiting; null when none opens within the forecast.</summary>
    public static SpotCandidate? Soonest(IEnumerable<SpotCandidate> spots)
        => spots.Where(c => !c.Resting && c.Wait != null)
            .OrderBy(c => c.Wait)
            .ThenByDescending(c => c.Here)
            .ThenBy(c => c.TeleportCost)
            .ThenBy(c => c.SpotId)
            .Cast<SpotCandidate?>()
            .FirstOrDefault();

    /// <summary>
    /// The order to work on fish in, from each fish's best spot: fish with a window first, the one closing
    /// soonest first (it needs the time now); then fish without a window, the spot being fished first (no
    /// needless trips), then this zone, then the cheaper teleport.
    /// </summary>
    public static List<SpotCandidate> Rank(IEnumerable<SpotCandidate> best, uint currentSpot)
    {
        var all = best.ToList();
        var timed = all.Where(c => c.Restricted && c.Bites != TimeSpan.MaxValue)
            .OrderBy(c => c.Closes).ThenBy(c => c.Wait).ThenByDescending(c => c.Here).ThenBy(c => c.TeleportCost).ThenBy(c => c.ItemId);
        var open = all.Where(c => !(c.Restricted && c.Bites != TimeSpan.MaxValue))
            .OrderByDescending(c => currentSpot != 0 && c.SpotId == currentSpot).ThenByDescending(c => c.Here).ThenBy(c => c.TeleportCost).ThenBy(c => c.ItemId);
        return timed.Concat(open).ToList();
    }

    /// <summary>Bait casts one catch of a fish takes, roughly: big and window-bound fish need more, every mooch link starts again from bait.</summary>
    public static int BaitPerCatch(FishInfo fish)
        => (fish.Big ? 12 : fish.Restricted ? 5 : 3) * (1 + fish.Mooch.Count);

    /// <summary>Bait to buy when it runs out mid-run: enough for <paramref name="catches"/> more catches plus the spare, 10 to 999 (one stack).</summary>
    public static int BaitToBuy(FishInfo fish, int catches, int spare)
        => Math.Clamp(Math.Max(0, catches) * BaitPerCatch(fish) + Math.Max(0, spare), 10, 999);
}
