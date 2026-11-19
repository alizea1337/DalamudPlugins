using System;
using System.Collections.Generic;

namespace Provenance.Core.Timing;

/// <summary>
/// Eorzea weather: it changes every 8 Eorzea hours (1,400 real seconds), and which weather comes is a
/// fixed function of the time and the zone's weather table (a list of weathers with cumulative chances).
/// This is the game's well-known forecast calculation, also used by every weather tracker.
/// </summary>
public static class Weather
{
    /// <summary>Real seconds in one weather period (8 Eorzea hours).</summary>
    public const long PeriodSeconds = 1400;

    /// <summary>The forecast's roll for a moment: 0–99, the same for the whole weather period.</summary>
    public static int Chance(DateTimeOffset at)
    {
        var unix = at.ToUnixTimeSeconds();
        var bell = unix / 175;
        var increment = (uint)((bell + 8 - bell % 8) % 24);
        var totalDays = (uint)(unix / 4200);
        var calcBase = totalDays * 100 + increment;
        var step1 = (calcBase << 11) ^ calcBase;
        var step2 = (step1 >> 8) ^ step1;
        return (int)(step2 % 100);
    }

    /// <summary>Start of the weather period a moment falls in.</summary>
    public static DateTimeOffset PeriodStart(DateTimeOffset at)
        => DateTimeOffset.FromUnixTimeSeconds(at.ToUnixTimeSeconds() / PeriodSeconds * PeriodSeconds);

    /// <summary>The weather a zone has at a moment, from its table of (weather, chance) rows.</summary>
    /// <param name="table">Weathers with their chances in percent, in the table's order (cumulative up to 100).</param>
    public static uint At(IReadOnlyList<(uint Weather, int Rate)> table, DateTimeOffset at)
    {
        var roll = Chance(at);
        var sum = 0;
        foreach (var (weather, rate) in table)
        {
            sum += rate;
            if (roll < sum) return weather;
        }
        return table.Count > 0 ? table[^1].Weather : 0;
    }

    /// <summary>
    /// The next moment (from <paramref name="from"/>, up to <paramref name="horizon"/> ahead) when the
    /// zone has one of the wanted weathers, optionally right after one of the wanted previous weathers.
    /// Null when it doesn't happen within the horizon.
    /// </summary>
    public static DateTimeOffset? Next(IReadOnlyList<(uint Weather, int Rate)> table, IReadOnlyCollection<uint> wanted,
        IReadOnlyCollection<uint> previous, DateTimeOffset from, TimeSpan horizon)
    {
        var start = PeriodStart(from);
        var end = from + horizon;
        for (var t = start; t <= end; t = t.AddSeconds(PeriodSeconds))
        {
            var now = At(table, t);
            if (wanted.Count > 0 && !Contains(wanted, now)) continue;
            if (previous.Count > 0 && !Contains(previous, At(table, t.AddSeconds(-PeriodSeconds)))) continue;
            return t < from ? from : t;
        }
        return null;
    }

    private static bool Contains(IReadOnlyCollection<uint> set, uint value)
    {
        foreach (var v in set) if (v == value) return true;
        return false;
    }
}
