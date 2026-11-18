using System;
using System.Collections.Generic;
using System.Linq;

namespace Provenance.Core.Scrips;

/// <summary>A collectable the appraiser takes for scrips: who makes or gathers it, at what level, and what it pays.</summary>
/// <param name="Level">Job level needed to make or gather it (the higher of the appraiser's minimum and the recipe or node level).</param>
/// <param name="Currency">Scrip item id it pays.</param>
/// <param name="Tab">Job tab of the appraiser window (0-based).</param>
/// <param name="Crafted">Made by a crafter; otherwise gathered.</param>
public sealed record CollectableOption(
    uint ItemId, uint ClassJobId, int Level, uint Currency,
    int LowReward, int MidReward, int HighReward,
    int LowCollectability, int MidCollectability, int HighCollectability,
    int Tab, bool Crafted);

/// <summary>What to make or gather to earn enough of one scrip, and what it should pay.</summary>
public sealed record ScripTask(CollectableOption Option, int Count, int Expected);

/// <summary>Picks the collectable to earn missing scrips with. Pure, so it is tested without the game.</summary>
public static class ScripPlanner
{
    /// <summary>
    /// The quickest way to earn <paramref name="missing"/> of a scrip: the collectable that needs the
    /// fewest items (counted at its middle reward, so a run that misses the top collectability still
    /// earns enough), the lower level first on a tie because its materials and rotation are simpler.
    /// Only collectables the character's job is high enough for, and that <paramref name="usable"/> accepts.
    /// </summary>
    public static ScripTask? Plan(uint currency, int missing, IEnumerable<CollectableOption> options, Func<uint, int> jobLevel,
        Func<CollectableOption, bool>? usable = null)
    {
        if (missing <= 0) return null;
        var pick = options
            .Where(o => o.Currency == currency && o.MidReward > 0 && jobLevel(o.ClassJobId) >= o.Level && (usable?.Invoke(o) ?? true))
            .OrderBy(o => Count(o, missing))
            .ThenBy(o => o.Level)
            .ThenBy(o => o.ItemId)
            .FirstOrDefault();
        if (pick == null) return null;
        var count = Count(pick, missing);
        return new ScripTask(pick, count, count * pick.MidReward);
    }

    private static int Count(CollectableOption o, int missing) => (missing + o.MidReward - 1) / o.MidReward;
}
