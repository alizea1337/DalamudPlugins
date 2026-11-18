using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Planning;

namespace Provenance.Core.Run;

/// <summary>One entry of the gathering list handed to GatherBuddy Reborn.</summary>
/// <param name="Target">
/// How many GatherBuddy Reborn should keep gathering until the main bags hold: what the bags hold now
/// plus what the order still needs. GatherBuddy Reborn counts only the main bags, so anything taken
/// from the saddlebag is already part of <paramref name="Remaining"/>.
/// </param>
public readonly record struct GatherTarget(uint ItemId, Supply Supply, int Remaining, uint Target);

public static class GatherTargets
{
    public static bool IsGatherSupply(Supply s) => s is Supply.Gather or Supply.GatherTimed or Supply.Fish;

    /// <param name="bagCount">Count of the item in the main bags as GatherBuddy Reborn counts it.</param>
    public static List<GatherTarget> Build(Plan plan, Func<uint, int> bagCount)
        => plan.Lines
            .Where(l => IsGatherSupply(l.Supply) && l.Remaining > 0)
            .OrderBy(l => l.Supply == Supply.GatherTimed ? 1 : 0)
            .ThenBy(l => l.ItemId)
            .Select(l => new GatherTarget(l.ItemId, l.Supply, l.Remaining, (uint)Math.Max(0, bagCount(l.ItemId) + l.Remaining)))
            .ToList();

    /// <summary>Still to gather for one target: GatherBuddy Reborn's own rule, the bags must hold the target.</summary>
    public static int Left(GatherTarget t, Func<uint, int> bagCount) => Math.Max(0, (int)t.Target - bagCount(t.ItemId));

    /// <summary>The gathering stage is done when the bags hold every target, exactly when GatherBuddy Reborn stops.</summary>
    public static bool AllDone(IEnumerable<GatherTarget> targets, Func<uint, int> bagCount)
        => targets.All(t => Left(t, bagCount) == 0);
}
