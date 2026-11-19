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
/// When it bites from now on, earliest first (over every spot the run can use); null when it bites any time,
/// empty when it doesn't bite within the forecast.
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
/// Fish windows in the estimate: the moments a fish bites (from <see cref="FishWindow"/>, per spot), and how
/// a run fits fishing them around its other work. The run fishes a fish whose window is open first (the one
/// that closes soonest), does its other gathering and fishing while every window is closed, and only waits
/// when nothing else is left. Pure.
/// </summary>
public static class FishSchedule
{
    /// <summary>
    /// The moments a fish bites from <paramref name="from"/> on, up to <paramref name="horizon"/> ahead, merged
    /// over several weather tables (one per spot the run can use). A window still open at the horizon ends there.
    /// </summary>
    public static List<(DateTimeOffset Start, DateTimeOffset End)> Windows(FishInfo fish, IEnumerable<IReadOnlyList<(uint Weather, int Rate)>> tables,
        DateTimeOffset from, TimeSpan horizon)
    {
        var end = from + horizon;
        if (!fish.Restricted) return new List<(DateTimeOffset, DateTimeOffset)> { (from, end) };
        var all = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var table in tables)
        {
            var t = from;
            for (var guard = 0; guard < 2000 && t < end; guard++)
            {
                if (FishWindow.NextOpen(fish, table, t, end - t) is not { } open || open >= end) break;
                var left = FishWindow.Left(fish, table, open, end - open);
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
