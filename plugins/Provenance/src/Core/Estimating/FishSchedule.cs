using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Fishing;

namespace Provenance.Core.Estimating;

/// <summary>One kind of fish for the estimate: fishing time per catch, and when it bites.</summary>
/// <param name="SecondsPerCatch">
/// Fishing time for one catch: its casts and bite time, a mooch chain, the fish caught first for Fisher's
/// Intuition (<see cref="BaitNeeds.SecondsPerCatch"/>). Travel to the spot is <see cref="Timings.SecondsPerFishingTrip"/>.
/// </param>
/// <param name="Windows">
/// When it can be caught from now on, earliest first (over every spot the run can use, see <see cref="FishSchedule.Times"/>);
/// null when it bites any time, empty when it can't be caught within the forecast.
/// </param>
public sealed record FishTime(double SecondsPerCatch, IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)>? Windows = null);

/// <summary>What the estimate needs about fish; without it every fish goes at <see cref="Timings.FishPerMinute"/>.</summary>
/// <param name="Fish">Per fish item; null for fish the data doesn't know (those go at <see cref="Timings.FishPerMinute"/>).</param>
/// <param name="Now">The moment the run would start: windows are counted from here.</param>
public sealed record FishInputs(Func<uint, FishTime?> Fish, DateTimeOffset Now);

/// <summary>Fishing that can only happen inside windows: the item, how much fishing time it takes, and when it bites.</summary>
public readonly record struct WindowedWork(uint ItemId, double Seconds, IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> Windows);

/// <summary>How long the gathering stage takes once fish windows are in it.</summary>
/// <param name="Total">From the start to the last catch, waits included.</param>
/// <param name="Waiting">Part of the total spent waiting for a window with nothing else to do.</param>
/// <param name="Unscheduled">
/// Fish whose windows ran out before their fishing was done: no window within the forecast at all, or windows
/// too short (or taken by another fish) for all of it. Their fishing time is in the total, waiting for later
/// windows isn't.
/// </param>
public sealed record ScheduleResult(TimeSpan Total, TimeSpan Waiting, IReadOnlyList<uint> Unscheduled);

/// <summary>
/// A fish caught first for Fisher's Intuition that only bites at some hours or in some weather (its own, or a link's
/// of its mooch chain), at the spots the fish it is caught for is fished at.
/// </summary>
/// <param name="Count">Catches Fisher's Intuition needs.</param>
/// <param name="Links">The links of its mooch chain that only bite at some hours or in some weather (<see cref="CatchWindow.Links"/>).</param>
/// <param name="Windows">
/// When it bites from the start on, earliest first, at the spots where its fish can be caught (where its fish bites,
/// when there are none; all of them, when its fish doesn't bite either); empty when not within the horizon.
/// </param>
/// <param name="Slows">
/// Waiting for it makes catches slow: for at least <see cref="FishSchedule.SlowShare"/> of the time the fish bites, the
/// predator doesn't, nor did it within the intuition's length before, so no catch of it could bring the intuition then.
/// </param>
public sealed record PredatorTimes(FishInfo Fish, int Count, IReadOnlyList<FishInfo> Links,
    IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> Windows, bool Slows);

/// <summary>When a fish can be caught at the spots a run can use (one weather table per spot zone).</summary>
/// <param name="Windows">
/// When it can be caught from the start on, earliest first: its own window narrowed by its mooch chain's, at the spots
/// where the fish caught first for Fisher's Intuition bite within the horizon too. Null when it bites any time; empty
/// when it can't be caught within the horizon (its window doesn't come, or a predator's doesn't).
/// </param>
/// <param name="Restricted">It only bites at some hours or in some weather: its own, or a link's of its mooch chain.</param>
/// <param name="Links">The links of its mooch chain that only bite at some hours or in some weather (<see cref="CatchWindow.Links"/>).</param>
/// <param name="Predators">The fish caught first for Fisher's Intuition that only bite at some hours or in some weather.</param>
/// <param name="PredatorBeyond">
/// One of <see cref="Predators"/> that doesn't bite within the horizon at any spot where the fish does: why
/// <see cref="Windows"/> is empty although the fish bites. Null otherwise.
/// </param>
public sealed record CatchTimes(IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)>? Windows, bool Restricted,
    IReadOnlyList<FishInfo> Links, IReadOnlyList<PredatorTimes> Predators, PredatorTimes? PredatorBeyond);

/// <summary>
/// Fish windows in the plan and the estimate: the moments a fish can be caught (from <see cref="CatchWindow"/>, per
/// spot, with the fish caught first for Fisher's Intuition), and how a run fits fishing them around its other work.
/// The run fishes a fish whose window is open first (the one that closes soonest), does its other gathering and
/// fishing while every window is closed, and only waits when nothing else is left. Pure.
/// </summary>
public static class FishSchedule
{
    /// <summary>
    /// A predator slows catches when, for at least this share of the time the fish bites, no catch of it can bring
    /// Fisher's Intuition (<see cref="PredatorTimes.Slows"/>).
    /// </summary>
    public const double SlowShare = 0.25;

    /// <summary>
    /// The moments a fish bites from <paramref name="from"/> on, up to <paramref name="horizon"/> ahead, merged
    /// over several weather tables (one per spot the run can use). A window still open at the horizon ends there.
    /// </summary>
    public static List<(DateTimeOffset Start, DateTimeOffset End)> Windows(FishInfo fish, IEnumerable<IReadOnlyList<(uint Weather, int Rate)>> tables,
        DateTimeOffset from, TimeSpan horizon)
    {
        if (!fish.Restricted) return new List<(DateTimeOffset, DateTimeOffset)> { (from, from + horizon) };
        return Windows(tables.Select(table => new CatchWindow(fish, table)), from, horizon);
    }

    /// <summary>
    /// The moments a fish can be caught from <paramref name="from"/> on, up to <paramref name="horizon"/> ahead, merged
    /// over its windows at several spots (one per spot zone): its own window narrowed by its mooch chain's
    /// (<see cref="CatchWindow"/>). A window still open at the horizon ends there.
    /// </summary>
    public static List<(DateTimeOffset Start, DateTimeOffset End)> Windows(IEnumerable<CatchWindow> windows, DateTimeOffset from, TimeSpan horizon)
    {
        var end = from + horizon;
        var all = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var window in windows)
        {
            if (!window.Restricted) return new List<(DateTimeOffset, DateTimeOffset)> { (from, end) };
            var t = from;
            for (var guard = 0; guard < 2000 && t < end; guard++)
            {
                if (window.NextOpen(t, end - t) is not { } open || open >= end) break;
                var left = window.Left(open, end - open);
                if (left <= TimeSpan.Zero)
                {
                    // Shouldn't happen (open means some time left); step on rather than loop.
                    t = open + TimeSpan.FromSeconds(1);
                    continue;
                }
                var close = left == TimeSpan.MaxValue || open + left > end ? end : open + left;
                all.Add((open, close));
                t = close;
            }
        }
        return Merge(all);
    }

    /// <summary>
    /// When a fish can be caught at its spots (<paramref name="tables"/>, one per spot zone) from <paramref name="from"/>
    /// on, up to <paramref name="horizon"/> ahead: its window narrowed by its mooch chain's (<see cref="CatchWindow"/>),
    /// at the spots where the fish caught first for Fisher's Intuition bite as well (they're caught at the same spot).
    /// The predators' windows aren't intersected with each other, nor with the fish's: they're caught one after the
    /// other, and before the fish. Swimming shadows' predators are gigged at a spot of their own, so a spearfish has none here.
    /// </summary>
    public static CatchTimes Times(FishInfo fish, Func<uint, FishInfo?> fishData, IEnumerable<IReadOnlyList<(uint Weather, int Rate)>> tables,
        DateTimeOffset from, TimeSpan horizon)
    {
        // Which links and predators keep hours or weather doesn't depend on the zone.
        var noTable = Array.Empty<(uint, int)>();
        var links = CatchWindow.For(fish, fishData, noTable).Links;
        var restricted = fish.Restricted || links.Count > 0;
        var bound = new List<(FishInfo Fish, int Count, IReadOnlyList<FishInfo> Links)>();
        if (!fish.Spear)
            foreach (var (id, count) in fish.Predators)
                if (count > 0 && fishData(id) is { Spear: false } p && CatchWindow.For(p, fishData, noTable) is { Restricted: true } w)
                    bound.Add((p, count, w.Links));
        if (!restricted && bound.Count == 0) return new CatchTimes(null, false, links, Array.Empty<PredatorTimes>(), null);

        var always = new List<(DateTimeOffset Start, DateTimeOffset End)> { (from, from + horizon) };
        // The last predator catch brings the intuition, which lasts this long: the fish can bite until then.
        var reach = TimeSpan.FromSeconds(fish.IntuitionSeconds > 0 ? fish.IntuitionSeconds : CatchPlanner.DefaultIntuitionSeconds);
        var own = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        // Each predator's windows at the spots where the fish can be caught, at those where it bites, and at all of them.
        var usable = bound.Select(_ => new List<(DateTimeOffset Start, DateTimeOffset End)>()).ToArray();
        var seen = bound.Select(_ => new List<(DateTimeOffset Start, DateTimeOffset End)>()).ToArray();
        var anywhere = bound.Select(_ => new List<(DateTimeOffset Start, DateTimeOffset End)>()).ToArray();
        var uncovered = new TimeSpan[bound.Count];
        var biting = TimeSpan.Zero;
        var bites = false;
        var beyond = -1;
        foreach (var table in tables)
        {
            var mine = restricted ? Windows(new[] { CatchWindow.For(fish, fishData, table) }, from, horizon) : always;
            var theirs = bound.Select(b => Windows(new[] { CatchWindow.For(b.Fish, fishData, table) }, from, horizon)).ToList();
            for (var i = 0; i < bound.Count; i++) anywhere[i].AddRange(theirs[i]);
            if (mine.Count == 0) continue;
            bites = true;
            for (var i = 0; i < bound.Count; i++) seen[i].AddRange(theirs[i]);
            // A predator that doesn't come within the horizon here leaves the fish without Fisher's Intuition here.
            var missing = theirs.FindIndex(w => w.Count == 0);
            if (missing >= 0)
            {
                if (beyond < 0) beyond = missing;
                continue;
            }
            own.AddRange(mine);
            biting += Length(mine);
            for (var i = 0; i < bound.Count; i++)
            {
                usable[i].AddRange(theirs[i]);
                uncovered[i] += Uncovered(mine, Merge(theirs[i].Select(w => (w.Start, w.End + reach))));
            }
        }

        // Nowhere to catch it: no windows, and a predator that doesn't come where the fish bites is named.
        var found = biting > TimeSpan.Zero;
        var predators = bound.Select((b, i) => new PredatorTimes(b.Fish, b.Count, b.Links, Merge(found ? usable[i] : bites ? seen[i] : anywhere[i]),
            found && uncovered[i].Ticks >= biting.Ticks * SlowShare)).ToList();
        var windows = !found ? new List<(DateTimeOffset Start, DateTimeOffset End)>() : restricted ? Merge(own) : null;
        return new CatchTimes(windows, restricted, links, predators, !found && beyond >= 0 ? predators[beyond] : null);
    }

    /// <summary>Time until the first of <paramref name="windows"/> not over yet opens: zero when one is open (or for null: any time), null when none is left.</summary>
    public static TimeSpan? OpensIn(IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)>? windows, DateTimeOffset now)
    {
        if (windows == null) return TimeSpan.Zero;
        foreach (var (start, end) in windows)
        {
            if (end <= now) continue;
            return start <= now ? TimeSpan.Zero : start - now;
        }
        return null;
    }

    /// <summary>Time left in the one of <paramref name="windows"/> open now: zero when none is, <see cref="TimeSpan.MaxValue"/> for null (any time).</summary>
    public static TimeSpan OpenFor(IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)>? windows, DateTimeOffset now)
    {
        if (windows == null) return TimeSpan.MaxValue;
        foreach (var (start, end) in windows)
        {
            if (end <= now) continue;
            return start <= now ? end - now : TimeSpan.Zero;
        }
        return TimeSpan.Zero;
    }

    private static TimeSpan Length(IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> windows)
        => windows.Aggregate(TimeSpan.Zero, (sum, w) => sum + (w.End - w.Start));

    /// <summary>The time inside <paramref name="windows"/> that <paramref name="cover"/> leaves out (both sorted and merged).</summary>
    private static TimeSpan Uncovered(IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> windows, IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> cover)
    {
        var left = TimeSpan.Zero;
        var first = 0;
        foreach (var (start, end) in windows)
        {
            // Cover that ends before this window can't reach any later one either.
            while (first < cover.Count && cover[first].End <= start) first++;
            var t = start;
            for (var k = first; k < cover.Count && cover[k].Start < end && t < end; k++)
            {
                if (cover[k].Start > t) left += cover[k].Start - t;
                if (cover[k].End > t) t = cover[k].End;
            }
            if (t < end) left += end - t;
        }
        return left;
    }

    /// <summary>Sorted, with overlapping and touching windows joined.</summary>
    public static List<(DateTimeOffset Start, DateTimeOffset End)> Merge(IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> windows)
    {
        var result = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var w in windows.Where(w => w.End > w.Start).OrderBy(w => w.Start))
        {
            if (result.Count > 0 && w.Start <= result[^1].End)
            {
                if (w.End > result[^1].End) result[^1] = (result[^1].Start, w.End);
                continue;
            }
            result.Add(w);
        }
        return result;
    }

    /// <summary>
    /// Plays the gathering stage through from <paramref name="now"/>: windowed fishing goes whenever its window
    /// is open (the window that closes first wins), <paramref name="otherSeconds"/> of other work (gathering,
    /// fish without a window) fills the time between windows, and the run waits only when nothing else is left.
    /// </summary>
    public static ScheduleResult Run(double otherSeconds, IReadOnlyList<WindowedWork> windowed, DateTimeOffset now)
    {
        var left = windowed.Select(w => Math.Max(0, w.Seconds)).ToArray();
        var other = Math.Max(0, otherSeconds);
        var t = now;
        var waiting = TimeSpan.Zero;
        for (var guard = 0; guard < 100_000; guard++)
        {
            var pick = -1;
            var pickEnd = DateTimeOffset.MaxValue;
            DateTimeOffset? nextOpen = null;
            for (var i = 0; i < windowed.Count; i++)
            {
                if (left[i] <= 0) continue;
                // Windows are sorted: the first one not over yet is open now or the next to open.
                foreach (var (start, end) in windowed[i].Windows)
                {
                    if (end <= t) continue;
                    if (start <= t)
                    {
                        if (end < pickEnd) { pick = i; pickEnd = end; }
                    }
                    else if (nextOpen == null || start < nextOpen) nextOpen = start;
                    break;
                }
            }

            if (pick >= 0)
            {
                var room = (pickEnd - t).TotalSeconds;
                if (left[pick] >= room)
                {
                    left[pick] -= room;
                    t = pickEnd;
                }
                else
                {
                    t += Ticks(left[pick]);
                    left[pick] = 0;
                }
                continue;
            }
            if (nextOpen is not { } opens) break;

            // Every window is closed: other work first, then wait.
            var gap = (opens - t).TotalSeconds;
            if (other >= gap)
            {
                other -= gap;
                t = opens;
                continue;
            }
            t += Ticks(other);
            other = 0;
            if (opens > t) waiting += opens - t;
            t = opens;
        }

        // Whatever is left: the other work, and fishing that ran out of windows (counted, never waited for).
        var unscheduled = new List<uint>();
        var rest = other;
        for (var i = 0; i < windowed.Count; i++)
        {
            if (left[i] <= 1e-6) continue;
            unscheduled.Add(windowed[i].ItemId);
            rest += left[i];
        }
        t += Ticks(rest);
        return new ScheduleResult(t - now, waiting, unscheduled);
    }

    private static TimeSpan Ticks(double seconds) => TimeSpan.FromTicks((long)Math.Ceiling(Math.Max(0, seconds) * TimeSpan.TicksPerSecond));
}
