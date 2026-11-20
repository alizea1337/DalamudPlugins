using System;
using System.Collections.Generic;

namespace Provenance.Core.Timing;

/// <summary>
/// Eorzea time and timed-node windows. One Eorzea hour lasts 175 real seconds; a node's uptime is a
/// 24-bit mask, bit n set when the node is up during Eorzea hour n.
/// </summary>
public static class Eorzea
{
    public const double RealSecondsPerHour = 175;
    public const uint AllHours = 0x00FF_FFFF;

    /// <summary>Current Eorzea hour as a fraction (0 to 24).</summary>
    public static double Hour(DateTimeOffset now) => now.ToUnixTimeMilliseconds() / 1000.0 / RealSecondsPerHour % 24;

    /// <summary>Hours covered by a window given as game data writes it: start HHMM and length HHMM (or end HHMM).</summary>
    public static uint Window(int startHhmm, int lengthHhmm)
    {
        if (startHhmm is < 0 or > 2400 || lengthHhmm <= 0) return 0;
        var minutes = lengthHhmm / 100 * 60 + lengthHhmm % 100;
        var hours = Math.Max(1, (int)Math.Ceiling(minutes / 60.0));
        var start = startHhmm / 100 % 24;
        uint mask = 0;
        for (var i = 0; i < Math.Min(hours, 24); i++) mask |= 1u << ((start + i) % 24);
        return mask;
    }

    /// <summary>
    /// A timed node's uptime as the game data writes it: unspoiled and legendary nodes by their rare-pop table (start
    /// HHMM and duration HHMM per slot, 65535 for an unused slot), ephemeral nodes by their own start and end (HHMM).
    /// Ephemeral nodes point at table row 0, an empty placeholder: that means no table, not "never up".
    /// </summary>
    public static uint NodeUptime(uint tableRow, IReadOnlyList<(int Start, int Duration)> table, int ephemeralStart, int ephemeralEnd)
    {
        if (tableRow == 0) return Between(ephemeralStart, ephemeralEnd);
        uint mask = 0;
        foreach (var (start, duration) in table)
            if (start != 65535 && duration != 0) mask |= Window(start, duration);
        return mask;
    }

    /// <summary>Ephemeral nodes: from start HHMM to end HHMM, wrapping past midnight.</summary>
    public static uint Between(int startHhmm, int endHhmm)
    {
        if (startHhmm == endHhmm || startHhmm > 2400 || endHhmm > 2400) return AllHours;
        var start = startHhmm / 100 % 24;
        var end = endHhmm / 100 % 24;
        if (end <= start) end += 24;
        uint mask = 0;
        for (var h = start; h < end; h++) mask |= 1u << (h % 24);
        return mask;
    }

    public static bool IsUp(uint mask, DateTimeOffset now) => (mask & (1u << (int)Hour(now))) != 0;

    /// <summary>Real time until the window next opens; zero when it's up now, null when it never opens.</summary>
    public static TimeSpan? UntilOpen(uint mask, DateTimeOffset now)
    {
        if ((mask & AllHours) == 0) return null;
        var hour = Hour(now);
        var whole = (int)hour;
        if ((mask & (1u << whole)) != 0) return TimeSpan.Zero;
        for (var k = 1; k <= 24; k++)
            if ((mask & (1u << ((whole + k) % 24))) != 0)
                return TimeSpan.FromSeconds((whole + k - hour) * RealSecondsPerHour);
        return null;
    }

    /// <summary>Real time the window stays up from now (zero when it isn't up).</summary>
    public static TimeSpan UpFor(uint mask, DateTimeOffset now)
    {
        var hour = Hour(now);
        var whole = (int)hour;
        if ((mask & (1u << whole)) == 0) return TimeSpan.Zero;
        var k = 1;
        while (k < 24 && (mask & (1u << ((whole + k) % 24))) != 0) k++;
        return TimeSpan.FromSeconds((whole + k - hour) * RealSecondsPerHour);
    }
}
