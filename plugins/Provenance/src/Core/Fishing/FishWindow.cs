using System;
using System.Collections.Generic;
using Provenance.Core.Timing;

namespace Provenance.Core.Fishing;

/// <summary>
/// When a fish bites at a spot: its Eorzea-time window (<see cref="FishInfo.Window"/>) and the zone's
/// weather (<see cref="FishInfo.Weather"/>, and the weather of the period before for
/// <see cref="FishInfo.PreviousWeather"/>). Weather belongs to the spot's zone, so a fish that lives in
/// several zones is checked per spot. Pure: the zone's weather table comes in as data.
/// </summary>
public static class FishWindow
{
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(Weather.PeriodSeconds);

    /// <summary>The fish bites at this moment (always true for fish without a window or weather).</summary>
    public static bool IsOpen(FishInfo fish, IReadOnlyList<(uint Weather, int Rate)> table, DateTimeOffset at)
    {
        if (fish.Window is { } w && !w.IsUp(at)) return false;
        return WeatherOk(fish, table, Weather.PeriodStart(at));
    }

    /// <summary>
    /// The first moment at or after <paramref name="from"/> when the fish bites: <paramref name="from"/>
    /// itself when it bites now, null when it doesn't within <paramref name="horizon"/>.
    /// </summary>
    public static DateTimeOffset? NextOpen(FishInfo fish, IReadOnlyList<(uint Weather, int Rate)> table, DateTimeOffset from, TimeSpan horizon)
    {
        if (!fish.Restricted) return from;
        if (fish.Window is { On: <= 0 }) return null;
        var end = from + horizon;
        for (var start = Weather.PeriodStart(from); start <= end; start += Period)
        {
            if (!WeatherOk(fish, table, start)) continue;
            var lo = start < from ? from : start;
            if (fish.Window is not { } w) return lo <= end ? lo : null;
            var open = w.IsUp(lo) ? lo : lo + w.UntilOpen(lo);
            if (open < start + Period) return open <= end ? open : null;
        }
        return null;
    }

    /// <summary>Real time until the fish bites (zero when it bites now), null when not within <paramref name="horizon"/>.</summary>
    public static TimeSpan? UntilOpen(FishInfo fish, IReadOnlyList<(uint Weather, int Rate)> table, DateTimeOffset at, TimeSpan horizon)
        => NextOpen(fish, table, at, horizon) is { } open ? open - at : null;

    /// <summary>
    /// How long the fish keeps biting from <paramref name="at"/>: zero when it doesn't bite now,
    /// <see cref="TimeSpan.MaxValue"/> for fish without a window or weather. A window that runs past
    /// <paramref name="horizon"/> counts as lasting exactly that long.
    /// </summary>
    public static TimeSpan Left(FishInfo fish, IReadOnlyList<(uint Weather, int Rate)> table, DateTimeOffset at, TimeSpan horizon)
    {
        if (!fish.Restricted) return TimeSpan.MaxValue;
        if (!IsOpen(fish, table, at)) return TimeSpan.Zero;
        var limit = at + horizon;
        var t = at;
        while (true)
        {
            var periodEnd = Weather.PeriodStart(t) + Period;
            if (fish.Window is { } w && w.Off > 0)
            {
                var windowEnd = t + w.Left(t);
                if (windowEnd < periodEnd) return Cap(windowEnd);
            }
            // Open to the end of this weather period: the next period decides.
            if (periodEnd >= limit) return limit - at;
            if (!IsOpen(fish, table, periodEnd)) return periodEnd - at;
            t = periodEnd;
        }

        TimeSpan Cap(DateTimeOffset end) => (end < limit ? end : limit) - at;
    }

    private static bool WeatherOk(FishInfo fish, IReadOnlyList<(uint Weather, int Rate)> table, DateTimeOffset periodStart)
    {
        if (fish.Weather.Count > 0 && !Contains(fish.Weather, Weather.At(table, periodStart))) return false;
        if (fish.PreviousWeather.Count > 0 && !Contains(fish.PreviousWeather, Weather.At(table, periodStart - Period))) return false;
        return true;
    }

    private static bool Contains(IReadOnlyList<uint> list, uint value)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i] == value) return true;
        return false;
    }
}
