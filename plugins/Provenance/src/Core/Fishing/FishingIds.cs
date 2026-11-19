namespace Provenance.Core.Fishing;

/// <summary>
/// Fisher action, status and item ids the rod fishing logic works with (Action, Status and Item sheets;
/// the same ids as AutoHook's IDs.cs, BSD-3, Puni.sh). Kept here so the pure catch planner and decider
/// can name them without game access.
/// </summary>
public static class FishingIds
{
    // Actions (ActionType.Action).
    public const uint Cast = 289, Hook = 296, Mooch = 297, MoochII = 268, Quit = 299, Rest = 37047;
    public const uint PrecisionHookset = 4179, PowerfulHookset = 4103;
    public const uint Patience = 4102, PatienceII = 4106, Chum = 4104, Snagging = 4100, Collect = 4101;
    public const uint SurfaceSlap = 4595, IdenticalCast = 4596, PrizeCatch = 26806;
    public const uint ThaliaksFavor = 26804, MakeshiftBait = 26805;
    public const uint AmbitiousLure = 37594, ModestLure = 37595;

    // Statuses.
    public const uint IntuitionStatus = 568, SnaggingStatus = 761, ChumStatus = 763, CollectStatus = 805;
    /// <summary>Angler's Fortune: Patience or Patience II is up (Precision and Powerful Hookset work only then).</summary>
    public const uint AnglersFortune = 850;
    public const uint SurfaceSlapStatus = 1803, IdenticalCastStatus = 1804;
    /// <summary>Angler's Art: stacks (up to 10) that pay for Thaliak's Favor and Makeshift Bait.</summary>
    public const uint AnglersArt = 2778;
    public const uint MakeshiftBaitStatus = 2779, PrizeCatchStatus = 2780;
    /// <summary>Ambitious and Modest Lure: the status's stack count is how many lures went on this cast.</summary>
    public const uint AmbitiousLureStatus = 3972, ModestLureStatus = 3973;

    // GP costs (Action sheet).
    public const int HooksetGp = 50, PatienceGp = 200, PatienceIIGp = 560, ChumGp = 100, MoochIIGp = 100;
    public const int SurfaceSlapGp = 200, IdenticalCastGp = 350, PrizeCatchGp = 200;
    /// <summary>Thaliak's Favor gives back this much GP for 3 Angler's Art.</summary>
    public const int ThaliaksFavorGp = 150, ThaliaksFavorArt = 3, MakeshiftBaitArt = 5;
    /// <summary>A lure costs 10 GP for the first use on a cast, 20 for the second, 30 for the third.</summary>
    public const int MaxLures = 3;

    public static int LureGp(int stacksBefore) => 10 * (stacksBefore + 1);
}
