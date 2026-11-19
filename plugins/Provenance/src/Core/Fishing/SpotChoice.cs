using System;
using System.Collections.Generic;
using System.Linq;

namespace Provenance.Core.Fishing;

/// <summary>A fish at one of its spots, as the fisher weighs where to go next.</summary>
/// <param name="ItemId">The fish.</param>
/// <param name="SpotId">The fishing hole or spearfishing spot.</param>
/// <param name="Restricted">
/// The fish only bites there in a window: at some hours or in some weather, its own or its mooch chain's, or while
/// a fish caught first for Fisher's Intuition (or gigged first for its swimming shadows) bites.
/// </param>
/// <param name="Wait">Real time until it bites there (zero when it bites now); null when not within the forecast.</param>
/// <param name="Bites">How long it keeps biting once open; <see cref="TimeSpan.MaxValue"/> for a fish without a window.</param>
/// <param name="Here">The spot is in the zone the player is in.</param>
/// <param name="TeleportCost">Gil the teleport to the spot's zone costs (0 when here).</param>
/// <param name="Resting">Skipped for a while: the fish there sensed something amiss, or no place to stand worked.</param>
public readonly record struct SpotCandidate(uint ItemId, uint SpotId, bool Restricted, TimeSpan? Wait, TimeSpan Bites, bool Here, int TeleportCost, bool Resting = false)
{
    /// <summary>When the window closes, from now; <see cref="TimeSpan.MaxValue"/> without a window or when it never opens.</summary>
    public TimeSpan Closes => Wait is not { } w || Bites == TimeSpan.MaxValue || TimeSpan.MaxValue - w < Bites ? TimeSpan.MaxValue : w + Bites;

    /// <summary>Bound to a window that ends: worked on before fish that always bite (<see cref="SpotChoice.Rank"/>).</summary>
    public bool Timed => Restricted && Bites != TimeSpan.MaxValue;
}

/// <summary>
/// Which fish to go for and where: a fish's window at each of its spots (per spot zone, since the weather is
/// the zone's), how early to be there (the lead, with time to catch the fish for Fisher's Intuition), the
/// best spot per fish (bites within the lead, this zone, the longest window, the cheapest teleport) and the
/// order between fish (windows closing soonest first, then fish without a window). A fish's window is the one
/// it can be caught in: its own narrowed by its mooch chain's (<see cref="CatchWindow"/>), and for a fish caught
/// under Fisher's Intuition with no window of its own, its predators'. Also how much bait to buy when it runs
/// out mid-run. Follows GatherBuddy Reborn's fish ordering (ActiveItemList, Apache-2.0). Pure.
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

    /// <summary>A window open for longer than this counts as this long: plenty for any order.</summary>
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    /// <summary>Whether a fish is bound to a window doesn't depend on the zone's weather table.</summary>
    private static readonly IReadOnlyList<(uint Weather, int Rate)> AnyWeather = Array.Empty<(uint, int)>();

    /// <summary>
    /// A window at one spot (a fish's own narrowed by its mooch chain's, see <see cref="CatchWindow.For"/>): real time
    /// until it opens (zero when it is open now, null when not within <paramref name="horizon"/>) and how long it then
    /// stays open (<see cref="TimeSpan.MaxValue"/> for a fish without a window).
    /// </summary>
    public static (TimeSpan? Wait, TimeSpan Bites) Window(CatchWindow window, DateTimeOffset now, TimeSpan horizon)
    {
        if (!window.Restricted) return (TimeSpan.Zero, TimeSpan.MaxValue);
        if (window.NextOpen(now, horizon) is not { } open) return (null, TimeSpan.Zero);
        // A window open for longer than a day counts as a day: plenty for any order.
        return (open - now, window.Left(open, Day));
    }

    /// <summary>
    /// When a fish can be caught at a spot (<paramref name="table"/> is the spot zone's weather): its own window
    /// narrowed by its mooch chain's, and for a rod fish caught under Fisher's Intuition, the windows of the
    /// predators that only bite in one (their own or their chain's). Each of those must bite within
    /// <paramref name="horizon"/>, or the fish can't be had there (the catch ends as soon as it finds one that
    /// doesn't). A fish whose own window is always open counts from the soonest predator opening, for as long as
    /// that predator bites: the predators are caught one after another (the others are waited for at the stand),
    /// so their windows aren't intersected. Wait and Bites as <see cref="Window(CatchWindow, DateTimeOffset, TimeSpan)"/>;
    /// Predator is the one whose window counts, or the first that doesn't bite within the horizon, and 0 when the
    /// fish's own window decides.
    /// </summary>
    public static (TimeSpan? Wait, TimeSpan Bites, uint Predator) Window(FishInfo fish, Func<uint, FishInfo?> fishData,
        IReadOnlyList<(uint Weather, int Rate)> table, DateTimeOffset now, TimeSpan horizon)
    {
        var own = CatchWindow.For(fish, fishData, table);
        var (wait, bites) = Window(own, now, horizon);
        // A spearfish's predators are gigged at another spot first (its swimming shadows): the fisher pairs those itself.
        if (wait == null || fish.Spear) return (wait, bites, 0);
        (TimeSpan Wait, TimeSpan Bites, uint Fish)? soonest = null;
        foreach (var predator in PredatorWindows(fish, fishData, table))
        {
            var (opensIn, lasts) = Window(predator, now, horizon);
            if (opensIn is not { } w) return (null, TimeSpan.Zero, predator.Fish.ItemId);
            if (soonest is not { } s || w < s.Wait || w == s.Wait && lasts > s.Bites) soonest = (w, lasts, predator.Fish.ItemId);
        }
        if (own.Restricted || soonest is not { } first) return (wait, bites, 0);
        return (first.Wait, first.Bites, first.Fish);
    }

    /// <summary>
    /// When two windows are open at once, each in its own zone's weather (a fish behind swimming shadows and the
    /// predator gigged first at its own spot, as GBR's IntersectPredatorUptime): real time until both are (zero
    /// when they are now, null when not within <paramref name="horizon"/>) and how long both then stay open
    /// (<see cref="TimeSpan.MaxValue"/> when neither has a window).
    /// </summary>
    public static (TimeSpan? Wait, TimeSpan Bites) Window(CatchWindow fish, CatchWindow other, DateTimeOffset now, TimeSpan horizon)
    {
        if (!other.Restricted) return Window(fish, now, horizon);
        if (!fish.Restricted) return Window(other, now, horizon);
        var end = now + horizon;
        var from = now;
        // Each round starts at the other window's next opening, so it moves on until both are open at once.
        for (var round = 0; round < 1000 && from <= end; round++)
        {
            if (fish.NextOpen(from, end - from) is not { } open) break;
            if (other.NextOpen(open, end - open) is not { } otherOpen) break;
            if (otherOpen == open)
            {
                var a = fish.Left(open, Day);
                var b = other.Left(open, Day);
                return (open - now, a < b ? a : b);
            }
            from = otherOpen;
        }
        return (null, TimeSpan.Zero);
    }

    /// <summary>
    /// How long before a window the fisher wants to be at work: the setting's minutes, plus catching the fish
    /// Fisher's Intuition needs (each catch takes <paramref name="secondsPerCatch"/>, once more per link of a mooch
    /// chain, at most <see cref="MaxPrep"/>). For a rod fish with a window of its own, that is at least as long
    /// as the catch planner starts on them before the window (<see cref="CatchPlanner.PrepLead"/>, from a
    /// predator's own earlier window when it only bites before). A predator with a window of its own is caught
    /// inside it, and adds nothing when that window bounds the fish's: a spearfish's (gigged first for its
    /// swimming shadows), or a rod fish's that has no window of its own.
    /// </summary>
    public static TimeSpan Lead(FishInfo fish, int leadMinutes, Func<uint, FishInfo?> fishData, double secondsPerCatch)
    {
        var own = Bounded(fish, fishData);
        var prep = 0.0;
        foreach (var (id, count) in fish.Predators)
        {
            var predator = fishData(id);
            if ((fish.Spear || !own) && predator != null && Bounded(predator, fishData)) continue;
            prep += Math.Max(1, count) * Math.Max(1, secondsPerCatch) * (1 + (predator?.Mooch.Count ?? 0));
        }
        prep = Math.Min(prep, MaxPrep.TotalSeconds);
        if (own && !fish.Spear) prep = Math.Max(prep, CatchPlanner.PrepLead(fish, fishData).TotalSeconds);
        return TimeSpan.FromMinutes(Math.Max(0, leadMinutes)) + TimeSpan.FromSeconds(prep);
    }

    /// <summary>
    /// The windows at a spot of the fish caught first for Fisher's Intuition that only bite in one (their own or
    /// their mooch chain's), as the catch planner times them.
    /// </summary>
    private static IEnumerable<CatchWindow> PredatorWindows(FishInfo fish, Func<uint, FishInfo?> fishData, IReadOnlyList<(uint Weather, int Rate)> table)
        => fish.Predators.Where(p => p.Count > 0).Select(p => fishData(p.ItemId)).OfType<FishInfo>()
            .Select(p => CatchWindow.For(p, fishData, table)).Where(w => w.Restricted);

    /// <summary>The fish only bites in a window: its own, or its mooch chain's.</summary>
    private static bool Bounded(FishInfo fish, Func<uint, FishInfo?> fishData) => CatchWindow.For(fish, fishData, AnyWeather).Restricted;

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
        var timed = all.Where(c => c.Timed)
            .OrderBy(c => c.Closes).ThenBy(c => c.Wait).ThenByDescending(c => c.Here).ThenBy(c => c.TeleportCost).ThenBy(c => c.ItemId);
        var open = all.Where(c => !c.Timed)
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
