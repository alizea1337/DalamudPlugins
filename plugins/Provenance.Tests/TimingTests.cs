using System;
using Provenance.Core.Planning;
using Provenance.Core.Run;
using Provenance.Core.Timing;
using Xunit;

namespace Provenance.Tests;

public class TimingTests
{
    /// <summary>A real moment at Eorzea hour h (plus a fraction).</summary>
    private static DateTimeOffset AtEorzeaHour(double h)
    {
        // Unix second 0 is Eorzea 00:00 of a day; pick a later day with the same phase.
        var day = 24 * Eorzea.RealSecondsPerHour;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)((10_000 * day + h * Eorzea.RealSecondsPerHour) * 1000));
    }

    [Fact]
    public void Eorzea_hour_runs_175_seconds()
    {
        Assert.Equal(9.0, Eorzea.Hour(AtEorzeaHour(9)), 3);
        Assert.Equal(9.5, Eorzea.Hour(AtEorzeaHour(9.5)), 3);
    }

    [Fact]
    public void Windows_from_game_data()
    {
        Assert.Equal(0b111u << 9, Eorzea.Window(900, 300));          // 9:00 for 3 hours
        Assert.Equal(0b11u << 9, Eorzea.Window(900, 160));           // 1h60 is two hours
        Assert.Equal((1u << 23) | 1u, Eorzea.Window(2300, 200));     // wraps midnight
        Assert.Equal(0xFu << 4, Eorzea.Between(400, 800));           // ephemeral 4-8
        Assert.Equal(0xFu << 20, Eorzea.Between(2000, 0));           // 20-24
        Assert.Equal(Eorzea.AllHours, Eorzea.Between(0, 0));
    }

    /// <summary>
    /// Every ephemeral node points at row 0 of the rare-pop table, an empty placeholder (start 65535, duration 0).
    /// It means "no table": the node's own start and end count, never "never up" (which left every ephemeral
    /// node closed for good, and the run waiting for Levin Quartz, Goldbranch and the like forever).
    /// </summary>
    [Fact]
    public void Ephemeral_nodes_keep_their_own_hours()
    {
        var placeholder = new[] { (65535, 0), (65535, 0), (65535, 0) };
        Assert.Equal(0xFu, Eorzea.NodeUptime(0, placeholder, 0, 400));            // Levin Quartz 0-4
        Assert.Equal(0xFu << 20, Eorzea.NodeUptime(0, placeholder, 2000, 0));     // Electrocoal 20-24
        Assert.Equal(0xFu << 4, Eorzea.NodeUptime(0, Array.Empty<(int, int)>(), 400, 800));
        // Unspoiled and legendary nodes go by their table.
        Assert.Equal((0b11u << 8) | (0b11u << 20), Eorzea.NodeUptime(12, new[] { (800, 200), (2000, 200), (65535, 0) }, 0, 0));
    }

    [Fact]
    public void Until_open_and_up_for()
    {
        var mask = Eorzea.Window(900, 300);
        Assert.Equal(TimeSpan.Zero, Eorzea.UntilOpen(mask, AtEorzeaHour(10)));
        Assert.Equal(TimeSpan.FromSeconds(2 * 175), Eorzea.UntilOpen(mask, AtEorzeaHour(7)));
        Assert.Equal(TimeSpan.FromSeconds(1.5 * 175), Eorzea.UpFor(mask, AtEorzeaHour(10.5)));
        Assert.Equal(TimeSpan.FromSeconds(21 * 175), Eorzea.UntilOpen(mask, AtEorzeaHour(12)));  // next day
        Assert.Null(Eorzea.UntilOpen(0, AtEorzeaHour(1)));
    }

    private static readonly uint NineToNoon = Eorzea.Window(900, 300);

    [Fact]
    public void Normal_targets_keep_gathering()
    {
        var d = Interleave.Decide(new[]
        {
            new ScheduledTarget(1, Supply.Gather, 5, 0),
            new ScheduledTarget(2, Supply.GatherTimed, 3, NineToNoon),
        }, AtEorzeaHour(1), TimeSpan.FromMinutes(2), canCraft: true);
        Assert.Equal(GatherMode.Gather, d.Mode);
    }

    [Fact]
    public void Only_closed_timed_nodes_left_means_craft_meanwhile_or_wait()
    {
        var targets = new[] { new ScheduledTarget(1, Supply.Gather, 0, 0), new ScheduledTarget(2, Supply.GatherTimed, 3, NineToNoon) };
        var craft = Interleave.Decide(targets, AtEorzeaHour(1), TimeSpan.FromMinutes(2), canCraft: true);
        Assert.Equal(GatherMode.CraftMeanwhile, craft.Mode);
        Assert.Equal(TimeSpan.FromSeconds(8 * 175), craft.NextWindow);
        Assert.Equal(GatherMode.Wait, Interleave.Decide(targets, AtEorzeaHour(1), TimeSpan.FromMinutes(2), canCraft: false).Mode);
    }

    [Fact]
    public void Hand_back_before_the_window_opens()
    {
        var targets = new[] { new ScheduledTarget(2, Supply.GatherTimed, 3, NineToNoon) };
        // 8:30: the window opens in half an Eorzea hour (87.5 s), inside a 2 minute lead.
        Assert.Equal(GatherMode.Gather, Interleave.Decide(targets, AtEorzeaHour(8.5), TimeSpan.FromMinutes(2), true).Mode);
        // 10:00: up now.
        Assert.Equal(GatherMode.Gather, Interleave.Decide(targets, AtEorzeaHour(10), TimeSpan.FromMinutes(2), true).Mode);
    }

    [Fact]
    public void Fish_windows_count_like_timed_nodes()
    {
        var now = AtEorzeaHour(1);
        // A fish that bites in 20 minutes: craft meanwhile, and the next window is the fish's.
        var fish = new ScheduledTarget(3, Supply.Fish, 2, 0, TimeSpan.FromMinutes(20));
        var d = Interleave.Decide(new[] { fish }, now, TimeSpan.FromMinutes(2), canCraft: true);
        Assert.Equal(GatherMode.CraftMeanwhile, d.Mode);
        Assert.Equal(TimeSpan.FromMinutes(20), d.NextWindow);

        // The earlier of a timed node and a fish wins.
        d = Interleave.Decide(new[] { fish with { Wait = TimeSpan.FromMinutes(90) }, new ScheduledTarget(2, Supply.GatherTimed, 3, NineToNoon) },
            now, TimeSpan.FromMinutes(2), canCraft: false);
        Assert.Equal(GatherMode.Wait, d.Mode);
        Assert.True(d.NextWindow < TimeSpan.FromMinutes(90));

        // Biting now, or a fish without a window: keep going.
        Assert.Equal(GatherMode.Gather, Interleave.Decide(new[] { fish with { Wait = TimeSpan.Zero } }, now, TimeSpan.FromMinutes(2), true).Mode);
        Assert.Equal(GatherMode.Gather, Interleave.Decide(new[] { fish with { Wait = null } }, now, TimeSpan.FromMinutes(2), true).Mode);

        // Not within the forecast: no window to wait for.
        Assert.Null(Interleave.Decide(new[] { fish with { Wait = TimeSpan.MaxValue } }, now, TimeSpan.FromMinutes(2), true).NextWindow);
    }
}
