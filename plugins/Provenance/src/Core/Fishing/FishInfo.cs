using System;
using System.Collections.Generic;

namespace Provenance.Core.Fishing;

/// <summary>How hard the fish pulls on the line ("!", "!!", "!!!").</summary>
public enum Tug { Unknown, Weak, Strong, Legendary }

/// <summary>Which hook lands the fish.</summary>
public enum HookSet { Unknown, Hook, Precise, Powerful, DoubleHook, TripleHook, Stellar }

public enum SpearSize { Unknown, Small, Average, Large }

public enum SpearSpeed { Unknown, SuperSlow, ExtremelySlow, VerySlow, Slow, Average, Fast, VeryFast, ExtremelyFast, SuperFast, HyperFast, LynFast }

/// <summary>
/// Everything known about catching one fish: bait or mooch chain, the window it bites in (Eorzea time
/// and weather), the fish that must be caught first for Fisher's Intuition, the hook and tug, the bite
/// time range, and where it lives. Data from GatherBuddy Reborn (Apache-2.0) with bite times from
/// AutoHook / Teamcraft, built by tools/GbrExport.
/// </summary>
public sealed record FishInfo(
    uint ItemId,
    bool Spear,
    uint Bait,
    IReadOnlyList<uint> Mooch,
    IReadOnlyList<(uint ItemId, int Count)> Predators,
    int IntuitionSeconds,
    Uptime? Window,
    IReadOnlyList<uint> Weather,
    IReadOnlyList<uint> PreviousWeather,
    HookSet HookSet,
    Tug Tug,
    bool Snag,
    string Lure,
    SpearSize Size,
    SpearSpeed Speed,
    uint SurfaceSlap,
    bool Ocean,
    bool Big,
    IReadOnlyList<uint> Spots,
    (float Min, float Max)? BiteSeconds)
{
    /// <summary>Caught by mooching another fish rather than with bait.</summary>
    public bool Mooched => Mooch.Count > 0;

    /// <summary>Needs Fisher's Intuition from catching other fish first.</summary>
    public bool NeedsIntuition => Predators.Count > 0;

    /// <summary>Only bites in some weather or at some Eorzea hours.</summary>
    public bool Restricted => Window != null || Weather.Count > 0 || PreviousWeather.Count > 0;
}

/// <summary>A fishing hole or spearfishing spot: its zone, the map circle, and recorded places to stand.</summary>
/// <param name="Stands">World X, Y, Z and the facing to cast in (radians), from recorded catches.</param>
/// <param name="PointBase">Spearfishing: the GatheringPointBase whose points are the spot's nodes.</param>
public sealed record FishingSpotInfo(uint Id, uint TerritoryId, bool Spear, int MapX, int MapY, int Radius,
    IReadOnlyList<(float X, float Y, float Z, float Facing)> Stands, uint PointBase);

/// <summary>
/// A repeating real-time window: up for <see cref="On"/> milliseconds, then down for <see cref="Off"/>,
/// aligned so a window starts at <see cref="Shift"/> (milliseconds since the Unix epoch, modulo the period).
/// </summary>
public readonly record struct Uptime(long On, long Off, long Shift)
{
    public long Period => On + Off;

    public bool IsUp(DateTimeOffset at)
    {
        if (Off <= 0) return On > 0;
        if (On <= 0) return false;
        return Mod(at.ToUnixTimeMilliseconds() - Shift, Period) < On;
    }

    /// <summary>Time until the window opens (zero when it is open now).</summary>
    public TimeSpan UntilOpen(DateTimeOffset at)
    {
        if (IsUp(at)) return TimeSpan.Zero;
        var into = Mod(at.ToUnixTimeMilliseconds() - Shift, Period);
        return TimeSpan.FromMilliseconds(Period - into);
    }

    /// <summary>Time left in the current window (zero when it is closed).</summary>
    public TimeSpan Left(DateTimeOffset at)
    {
        if (!IsUp(at)) return TimeSpan.Zero;
        if (Off <= 0) return TimeSpan.MaxValue;
        var into = Mod(at.ToUnixTimeMilliseconds() - Shift, Period);
        return TimeSpan.FromMilliseconds(On - into);
    }

    private static long Mod(long a, long m) => ((a % m) + m) % m;
}
