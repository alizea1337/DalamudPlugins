using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using Provenance.Core.Fishing;
using Provenance.Integrations;

namespace Provenance.Data;

/// <summary>
/// Where and whether this character can fish something: the spots a run can go to (open world, level,
/// reachable, with places to stand or known nodes), and the Fisher actions the fish needs. Shared by the
/// plan's readiness check, the run's start check and the fisher itself.
/// </summary>
internal static unsafe class FishAccess
{
    public const uint Fisher = 18;
    public const uint VersatileLure = 29717;

    public const uint Cast = 289, Quit = 299, Hook = 296, Rest = 37047;
    public const uint Mooch = 297, MoochII = 268, Snagging = 4100, Collect = 4101, Gig = 7632;
    public const uint AmbitiousLure = 37594, ModestLure = 37595;

    /// <summary>The character has the action: level high enough and its unlock quest done.</summary>
    public static bool Learned(uint actionId, int level)
    {
        if (Services.Data.GetExcelSheet<Action>().GetRowOrDefault(actionId) is not { } row || row.ClassJobLevel > level) return false;
        return row.UnlockLink.RowId == 0 || UIState.Instance()->IsUnlockLinkUnlockedOrQuestCompleted(row.UnlockLink.RowId);
    }

    public static string ActionName(uint actionId) => Services.Data.GetExcelSheet<Action>().GetRowOrDefault(actionId)?.Name.ExtractText() ?? $"action {actionId}";

    /// <summary>Actions catching this fish can't do without, by id.</summary>
    public static IEnumerable<uint> NeededActions(FishInfo fish)
    {
        if (fish.Spear) { yield return Gig; yield break; }
        if (fish.Mooched) yield return Mooch;
        if (fish.Snag) yield return Snagging;
        if (fish.Lure == "Ambitious") yield return AmbitiousLure;
        else if (fish.Lure == "Modest") yield return ModestLure;
    }

    /// <summary>Names of the needed actions this character hasn't learned yet (empty when none is missing).</summary>
    public static List<string> MissingActions(FishInfo fish, int fisherLevel)
        => NeededActions(fish).Where(a => !Learned(a, fisherLevel)).Select(ActionName).ToList();

    /// <summary>Open world zones (towns, fields, housing districts), as the catalog counts them.</summary>
    public static bool OpenWorld(uint territoryId)
        => Services.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryId)?.TerritoryIntendedUse.RowId is 0 or 1 or 13;

    /// <summary>Somewhere to stand: recorded stands for a fishing hole, a known node position for a spearfishing spot.</summary>
    public static bool HasPlaces(GatherData data, FishingSpotInfo spot)
        => spot.Spear
            ? data.PointsOfBase(spot.PointBase).Any(p => data.Positions(p).Count > 0)
            : data.StandsFor(spot.Id).Count > 0;

    /// <summary>Why a run can't fish at this spot, or null when it can.</summary>
    public static string? SpotBlocker(GatherData data, FishingSpotInfo spot, int fisherLevel)
    {
        if (!OpenWorld(spot.TerritoryId)) return "not in the open world";
        if (spot.Level > fisherLevel) return $"needs Fisher level {spot.Level}";
        if (!Teleporter.CanReach(spot.TerritoryId)) return Zone(spot.TerritoryId) is var z && z.Length > 0 ? $"{z} can't be reached (no aetheryte or house there)" : "can't be reached";
        if (!HasPlaces(data, spot)) return spot.Spear ? "no known node position" : "no known place to stand";
        return null;
    }

    /// <summary>The spots where the fish lives that a run can use now, lowest level first.</summary>
    public static List<FishingSpotInfo> UsableSpots(GatherData data, FishInfo fish, int fisherLevel)
        => fish.Spots.Select(data.Spot).OfType<FishingSpotInfo>()
            .Where(s => s.Spear == fish.Spear && SpotBlocker(data, s, fisherLevel) == null)
            .OrderBy(s => s.Level).ThenBy(s => s.Id).ToList();

    /// <summary>
    /// Why the fish can't be fished by this character (level, reach, actions, bait), or null when it can.
    /// Buying the bait is the plan's business; this only checks that the fish has a known way to bite.
    /// </summary>
    public static string? Blocker(GatherData data, FishInfo fish, int fisherLevel)
    {
        if (fisherLevel <= 0) return "Fisher isn't unlocked";
        if (!fish.Spear && fish.Bait == 0) return "no known bait for it";
        if (fish.Ocean) return "only caught on ocean fishing voyages";
        var spots = fish.Spots.Select(data.Spot).OfType<FishingSpotInfo>().Where(s => s.Spear == fish.Spear).ToList();
        if (spots.Count == 0) return "no known fishing spot";
        if (!spots.Any(s => SpotBlocker(data, s, fisherLevel) == null))
        {
            // The easiest spot's reason says the most ("needs Fisher level 50" over "can't be reached").
            var best = spots.OrderBy(s => s.Level).First();
            var why = SpotBlocker(data, best, fisherLevel);
            var name = best.Name.Length > 0 ? best.Name : Zone(best.TerritoryId);
            return name.Length > 0 ? $"{name}: {why}" : why;
        }
        var missing = MissingActions(fish, fisherLevel);
        if (missing.Count > 0) return "needs " + string.Join(" and ", missing);
        return null;
    }

    public static string Zone(uint territoryId)
        => Services.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryId)?.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
}
