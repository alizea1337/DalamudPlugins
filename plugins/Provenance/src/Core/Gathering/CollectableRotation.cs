// Follows GatherBuddy Reborn's AutoGather/AutoGather.Collectables.cs CollectableRotation (Apache-2.0,
// https://github.com/FFXIV-CombatReborn/GatherBuddyReborn), in its "choose best actions automatically"
// mode. Changed: pure, with the window values and player state passed in.
namespace Provenance.Core.Gathering;

/// <summary>The collectable window (GatheringMasterpiece) and the player, as the rotation needs them.</summary>
public readonly record struct CollectableState(
    int Collectability,
    int Integrity,
    int MaxIntegrity,
    int ScourGain,
    int MeticulousGain,
    int BrazenGainMax,
    int LowThreshold,
    int MidThreshold,
    int HighThreshold,
    int Gp,
    int Level,
    bool SolidAgeActive,
    bool ScrutinyActive,
    bool HighStandard,
    bool Standard,
    int ItemsLeft);

/// <summary>Levels and GP costs of the collectable actions (from the Action sheet).</summary>
public readonly record struct CollectableActions(
    int ScourLevel, int ScourCost,
    int BrazenLevel, int BrazenCost,
    int MeticulousLevel, int MeticulousCost,
    int ScrutinyLevel, int ScrutinyCost,
    int SolidAgeLevel, int SolidAgeCost,
    int WiseLevel);

/// <summary>Picks the next action in the collectable window: raise collectability to the best reward tier the window shows, then collect.</summary>
public static class CollectableRotation
{
    /// <summary>GP needed to bother with Scrutiny and Solid Reason at all.</summary>
    public const int FullRotationGp = 600;

    public static GatherAction Next(CollectableState s, CollectableActions a, bool fullRotation)
    {
        // Wise to the World: free integrity back after Solid Reason's proc.
        if (s.Integrity < s.MaxIntegrity && s.Level >= a.WiseLevel && s.SolidAgeActive) return GatherAction.Wise;

        var (target, min) = Scores(s);
        if (s.Collectability >= target)
        {
            if (fullRotation && UseSolidAge(s, a)) return GatherAction.SolidAge;
            return GatherAction.Collect;
        }
        if (s.Integrity == 1 && s.Collectability >= min) return GatherAction.Collect;

        bool Meticulous() => s.Level >= a.MeticulousLevel && s.Gp >= a.MeticulousCost;
        bool Scour() => s.Level >= a.ScourLevel && s.Gp >= a.ScourCost;
        bool Brazen() => s.Level >= a.BrazenLevel && s.Gp >= a.BrazenCost;
        bool Scrutiny() => s.Level >= a.ScrutinyLevel && s.Gp >= a.ScrutinyCost && !s.ScrutinyActive;

        // Only steps the rotation would take count. Brazen's best roll is luck, except under Collector's
        // High Standard, which makes it sure (and Brazen is picked then).
        var oneStepReaches = (s.ScourGain + s.Collectability >= target && Scour())
                             || (s.MeticulousGain + s.Collectability >= target && Meticulous())
                             || (s.HighStandard && s.BrazenGainMax + s.Collectability >= target && Brazen());
        if (fullRotation && !oneStepReaches && Scrutiny()) return GatherAction.Scrutiny;
        if (s.MeticulousGain + s.Collectability >= target && Meticulous()) return GatherAction.Meticulous;
        if (s.HighStandard && Brazen()) return GatherAction.Brazen;
        if (s.ScourGain + s.Collectability >= target && Scour()) return GatherAction.Scour;
        if (Meticulous()) return GatherAction.Meticulous;
        if (s.Standard && Brazen()) return GatherAction.Brazen;
        if (Scour()) return GatherAction.Scour;
        if (Brazen()) return GatherAction.Brazen;
        return GatherAction.Collect;
    }

    /// <summary>Target: the highest tier the window shows; minimum: the lowest non-zero tier.</summary>
    public static (int Target, int Min) Scores(CollectableState s)
    {
        var target = s.HighThreshold > 0 ? s.HighThreshold : s.MidThreshold > 0 ? s.MidThreshold : s.LowThreshold;
        var min = 0;
        foreach (var t in new[] { s.LowThreshold, s.MidThreshold, s.HighThreshold })
            if (t > 0 && (min == 0 || t < min)) min = t;
        return (target, min == 0 ? 1 : min);
    }

    /// <summary>Solid Reason for one more collectable when integrity was spent and more items are wanted.</summary>
    private static bool UseSolidAge(CollectableState s, CollectableActions a)
        => s.Level >= a.SolidAgeLevel && s.Gp >= a.SolidAgeCost && !s.SolidAgeActive
           && s.Integrity < s.MaxIntegrity && s.ItemsLeft > s.Integrity;
}
