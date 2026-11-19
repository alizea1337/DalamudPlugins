using System;
using System.Collections.Generic;
using Provenance.Core.Fishing;
using Provenance.Core.Timing;
using Xunit;

namespace Provenance.Tests;

public class FishWindowTests
{
    private const long Hour = 175_000; // one Eorzea hour in real milliseconds
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);
    private static readonly (uint, int)[] Mixed = { (1, 20), (2, 30), (3, 50) };

    private static FishInfo Fish(Uptime? window = null, uint[]? weather = null, uint[]? previous = null)
        => new(1, false, 2585, Array.Empty<uint>(), Array.Empty<(uint, int)>(), 0, window, weather ?? Array.Empty<uint>(),
            previous ?? Array.Empty<uint>(), HookSet.Precise, Tug.Weak, false, "None", SpearSize.Unknown, SpearSpeed.Unknown,
            0, false, false, Array.Empty<uint>(), (3f, 20f));

    private static DateTimeOffset At(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    [Fact]
    public void Unrestricted_fish_always_bite()
    {
        var f = Fish();
        var now = At(1_700_000_000_000);
        Assert.True(FishWindow.IsOpen(f, Mixed, now));
        Assert.Equal(now, FishWindow.NextOpen(f, Mixed, now, Week));
        Assert.Equal(TimeSpan.MaxValue, FishWindow.Left(f, Mixed, now, Week));
    }

    [Fact]
    public void Time_window_alone_follows_the_uptime()
    {
        var f = Fish(new Uptime(6 * Hour, 18 * Hour, 9 * Hour)); // 9:00–15:00 ET
        Assert.False(FishWindow.IsOpen(f, Mixed, At(8 * Hour)));
        Assert.Equal(At(9 * Hour), FishWindow.NextOpen(f, Mixed, At(8 * Hour), Week));
        Assert.True(FishWindow.IsOpen(f, Mixed, At(10 * Hour)));
        Assert.Equal(TimeSpan.FromMilliseconds(5 * Hour), FishWindow.Left(f, Mixed, At(10 * Hour), Week));
        Assert.Equal(TimeSpan.FromMilliseconds(Hour), FishWindow.UntilOpen(f, Mixed, At(8 * Hour), Week));
    }

    [Fact]
    public void Weather_alone_matches_the_forecast()
    {
        var f = Fish(weather: new uint[] { 1 });
        var from = DateTimeOffset.FromUnixTimeSeconds(0); // roll 56: weather 3
        Assert.False(FishWindow.IsOpen(f, Mixed, from));
        var next = FishWindow.NextOpen(f, Mixed, from, Week);
        Assert.Equal(Weather.Next(Mixed, new uint[] { 1 }, Array.Empty<uint>(), from, Week), next);
        Assert.True(FishWindow.IsOpen(f, Mixed, next!.Value));
        // It stays open to the end of the weather period at least.
        var left = FishWindow.Left(f, Mixed, next.Value, Week);
        Assert.True(left >= TimeSpan.FromSeconds(Weather.PeriodSeconds));
        Assert.True(FishWindow.IsOpen(f, Mixed, next.Value + left - TimeSpan.FromSeconds(1)));
        Assert.False(FishWindow.IsOpen(f, Mixed, next.Value + left));
    }

    [Fact]
    public void One_weather_zone_is_always_open_or_never()
    {
        var only = new (uint, int)[] { (4, 100) };
        var now = At(1_700_000_000_000);
        Assert.True(FishWindow.IsOpen(Fish(weather: new uint[] { 4 }), only, now));
        Assert.Null(FishWindow.NextOpen(Fish(weather: new uint[] { 5 }), only, now, Week));
        // Always open within the horizon: the window lasts as long as the horizon.
        Assert.Equal(TimeSpan.FromHours(3), FishWindow.Left(Fish(weather: new uint[] { 4 }), only, now, TimeSpan.FromHours(3)));
    }

    [Theory]
    [InlineData(9, 6, new uint[] { 1 }, new uint[0])]
    [InlineData(17, 1, new uint[] { 2, 3 }, new uint[] { 1 })]
    [InlineData(22, 4, new uint[] { 3 }, new uint[] { 2, 3 })]
    [InlineData(0, 2, new uint[0], new uint[] { 1 })]
    public void Window_and_weather_agree_with_a_brute_force_scan(int startHour, int hours, uint[] weather, uint[] previous)
    {
        var f = Fish(new Uptime(hours * Hour, (24 - hours) * Hour, startHour * Hour), weather, previous);
        var from = At(1_700_000_123_000);
        var next = FishWindow.NextOpen(f, Mixed, from, Week);

        // Brute force in 5-second steps.
        DateTimeOffset? scan = null;
        for (var t = from; t <= from + Week; t = t.AddSeconds(5))
            if (FishWindow.IsOpen(f, Mixed, t)) { scan = t; break; }

        Assert.Equal(scan.HasValue, next.HasValue);
        if (next is not { } n) return;
        Assert.True(FishWindow.IsOpen(f, Mixed, n));
        Assert.True(n <= scan!.Value && scan.Value - n < TimeSpan.FromSeconds(5));
        if (n > from) Assert.False(FishWindow.IsOpen(f, Mixed, n.AddSeconds(-1)));

        var left = FishWindow.Left(f, Mixed, n, Week);
        Assert.True(left > TimeSpan.Zero);
        Assert.True(FishWindow.IsOpen(f, Mixed, n + left - TimeSpan.FromSeconds(1)));
        Assert.False(FishWindow.IsOpen(f, Mixed, n + left));
    }

    [Fact]
    public void Closed_fish_have_no_time_left()
    {
        var f = Fish(new Uptime(6 * Hour, 18 * Hour, 9 * Hour));
        Assert.Equal(TimeSpan.Zero, FishWindow.Left(f, Mixed, At(20 * Hour), Week));
    }

    [Fact]
    public void Nothing_within_the_horizon_is_null()
    {
        var f = Fish(new Uptime(6 * Hour, 18 * Hour, 9 * Hour));
        Assert.Null(FishWindow.NextOpen(f, Mixed, At(16 * Hour), TimeSpan.FromMinutes(30))); // opens in 17 Eorzea hours, about 50 minutes
        Assert.Null(FishWindow.UntilOpen(f, Mixed, At(16 * Hour), TimeSpan.FromMinutes(30)));
    }
}
