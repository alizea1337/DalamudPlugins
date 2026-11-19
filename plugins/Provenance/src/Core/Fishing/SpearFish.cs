using System.Collections.Generic;
using System.Linq;

namespace Provenance.Core.Fishing;

/// <summary>
/// Recognising spearfish in the SpearFishing window. The window tells nothing about a fish but its size
/// and its speed, so a lane's fish counts as a wanted one only when both match exactly (AutoHook's AutoGig
/// rule, no wildcards). A fish whose size or speed isn't known is never recognised. Two fish of one pool
/// that share a size and speed (<see cref="LookAlikes"/>) can't be told apart before the catch: gigging
/// that shape may land either one, and only the bags tell which it was.
/// Values as the game shows them (FFXIVClientStructs AddonSpearFishing.FishInfo): size 1 small,
/// 2 average, 3 large (-1 for none); speed in steps of 50 from 100 ("super slow") to 600 ("mega fast").
/// Ported from AutoHook (BSD-3, Puni.sh).
/// </summary>
public static class SpearFish
{
    /// <summary>The window's size value for a size (small 1, average 2, large 3); null when the size isn't known.</summary>
    public static int? GameSize(SpearSize size)
        => size is SpearSize.Small or SpearSize.Average or SpearSize.Large ? (int)size : null;

    /// <summary>
    /// The window's speed value: 50·(n+1) for the n-th speed, from super slow (1) = 100 to Lyn fast (11) = 600,
    /// the "mega fast" of the game; null when the speed isn't known.
    /// </summary>
    public static int? GameSpeed(SpearSpeed speed)
        => speed is > SpearSpeed.Unknown and <= SpearSpeed.LynFast ? 50 * ((int)speed + 1) : null;

    /// <summary>The size a window value stands for (<see cref="SpearSize.Unknown"/> for none or anything odd).</summary>
    public static SpearSize SizeOf(int gameSize)
        => gameSize is >= 1 and <= 3 ? (SpearSize)gameSize : SpearSize.Unknown;

    /// <summary>The speed a window value stands for (<see cref="SpearSpeed.Unknown"/> off the 50-step scale).</summary>
    public static SpearSpeed SpeedOf(int gameSpeed)
        => gameSpeed is >= 100 and <= 600 && gameSpeed % 50 == 0 ? (SpearSpeed)(gameSpeed / 50 - 1) : SpearSpeed.Unknown;

    /// <summary>Both its size and its speed are known, so the window can show it.</summary>
    public static bool Recognisable(FishInfo fish) => GameSize(fish.Size) != null && GameSpeed(fish.Speed) != null;

    /// <summary>A fish of this size and speed (window values) can be this one.</summary>
    public static bool Matches(FishInfo fish, int gameSize, int gameSpeed)
        => GameSize(fish.Size) is { } size && GameSpeed(fish.Speed) is { } speed && size == gameSize && speed == gameSpeed;

    /// <summary>The wanted fish a lane's fish can be (several when they share a size and speed; empty when none).</summary>
    public static List<FishInfo> Candidates(IEnumerable<FishInfo> wanted, int gameSize, int gameSpeed)
        => wanted.Where(f => Matches(f, gameSize, gameSpeed)).ToList();

    /// <summary>Gig a fish of this size and speed: it can be one of the wanted fish, or every fish is wanted (empty list).</summary>
    public static bool Wanted(IReadOnlyList<FishInfo> wanted, int gameSize, int gameSpeed)
    {
        if (wanted.Count == 0) return true;
        for (var i = 0; i < wanted.Count; i++)
            if (Matches(wanted[i], gameSize, gameSpeed)) return true;
        return false;
    }

    /// <summary>
    /// Fish of the pool the run doesn't want that look exactly like a wanted one (same size and speed): gigging
    /// for the wanted fish may land them instead. The shipped data has four such pairs, all in swimming-shadow
    /// pools. Each pair once, in pool order.
    /// </summary>
    public static List<(FishInfo Wanted, FishInfo LookAlike)> LookAlikes(IReadOnlyCollection<FishInfo> wanted, IEnumerable<FishInfo> pool)
    {
        var wantedIds = wanted.Select(f => f.ItemId).ToHashSet();
        var seen = new HashSet<(uint, uint)>();
        var result = new List<(FishInfo, FishInfo)>();
        foreach (var other in pool)
        {
            if (wantedIds.Contains(other.ItemId) || !Recognisable(other)) continue;
            foreach (var w in wanted)
                if (Recognisable(w) && w.Size == other.Size && w.Speed == other.Speed && seen.Add((w.ItemId, other.ItemId)))
                    result.Add((w, other));
        }
        return result;
    }
}
