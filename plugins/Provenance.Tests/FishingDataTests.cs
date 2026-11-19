using System;
using Provenance.Core.Fishing;
using Provenance.Core.Timing;
using Xunit;

namespace Provenance.Tests;

public class FishingDataTests
{
    // 1 Eorzea hour = 175 real seconds = 175,000 ms; a day = 4,200,000 ms.
    private const long Hour = 175_000;
    private static DateTimeOffset At(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    [Fact]
    public void Uptime_follows_its_window()
    {
        // Up from 9:00 to 15:00 Eorzea time (as GatherBuddy Reborn stores it: on, off, shift).
        var w = new Uptime(6 * Hour, 18 * Hour, 9 * Hour);
        Assert.False(w.IsUp(At(8 * Hour)));
        Assert.True(w.IsUp(At(9 * Hour)));
        Assert.True(w.IsUp(At(14 * Hour + 1)));
        Assert.False(w.IsUp(At(15 * Hour)));
        Assert.Equal(TimeSpan.FromMilliseconds(Hour), w.UntilOpen(At(8 * Hour)));
        Assert.Equal(TimeSpan.FromMilliseconds(2 * Hour), w.Left(At(13 * Hour)));
        // Next day, same hours.
        Assert.True(w.IsUp(At(24 * Hour + 10 * Hour)));
        Assert.Equal(TimeSpan.FromMilliseconds(18 * Hour), w.UntilOpen(At(15 * Hour)));
    }

    [Fact]
    public void Weather_roll_matches_the_game_formula()
    {
        // Unix 0: Eorzea hour 0, so the period starting at 8 and day 0: base 8 → 16392 → 16456 → 56.
        Assert.Equal(56, Weather.Chance(DateTimeOffset.FromUnixTimeSeconds(0)));
        // The roll holds for a whole 8-hour (1,400 s) period and is always 0–99.
        var start = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 / 1400 * 1400);
        Assert.Equal(Weather.Chance(start), Weather.Chance(start.AddSeconds(1399)));
        for (var i = 0; i < 200; i++) Assert.InRange(Weather.Chance(start.AddSeconds(i * 1400)), 0, 99);
    }

    [Fact]
    public void Weather_table_and_next_matching_period()
    {
        var table = new (uint, int)[] { (1, 20), (2, 30), (3, 50) }; // clear 0–19, fair 20–49, clouds 50–99
        var t = DateTimeOffset.FromUnixTimeSeconds(0); // roll 56
        Assert.Equal(3u, Weather.At(table, t));

        var next = Weather.Next(table, new uint[] { 1 }, Array.Empty<uint>(), t, TimeSpan.FromDays(3));
        Assert.NotNull(next);
        Assert.Equal(1u, Weather.At(table, next!.Value));
        Assert.Equal(0, next.Value.ToUnixTimeSeconds() % 1400); // starts on a period boundary

        // "After weather 2" restricts to periods following a period with weather 2.
        var after = Weather.Next(table, new uint[] { 1 }, new uint[] { 2 }, t, TimeSpan.FromDays(10));
        if (after is { } a) Assert.Equal(2u, Weather.At(table, a.AddSeconds(-1400)));
    }
}
