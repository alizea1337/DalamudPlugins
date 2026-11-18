using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Planning;
using Provenance.Core.Timing;

namespace Provenance.Core.Run;

public enum GatherMode
{
    /// <summary>Something can be gathered now (or soon enough that GatherBuddy Reborn should get going).</summary>
    Gather,
    /// <summary>Only timed nodes are left, none is up for a while, and there is something to craft.</summary>
    CraftMeanwhile,
    /// <summary>Only timed nodes are left, none is up yet, and nothing can be crafted: wait.</summary>
    Wait,
}

/// <summary>A gathering target as the scheduler sees it.</summary>
/// <param name="Mask">Uptime mask for timed items (see <see cref="Eorzea"/>); 0 for items always up.</param>
public readonly record struct ScheduledTarget(uint ItemId, Supply Supply, int Left, uint Mask);

public sealed record GatherDecision(GatherMode Mode, TimeSpan? NextWindow);

/// <summary>Decides whether to keep gathering, craft while waiting for timed nodes, or just wait.</summary>
public static class Interleave
{
    public static GatherDecision Decide(IEnumerable<ScheduledTarget> targets, DateTimeOffset now, TimeSpan lead, bool canCraft)
    {
        var open = targets.Where(t => t.Left > 0).ToList();
        if (open.Count == 0) return new GatherDecision(GatherMode.Gather, null);

        // Anything that isn't tied to a window keeps GatherBuddy Reborn busy.
        if (open.Any(t => t.Supply != Supply.GatherTimed || t.Mask == 0)) return new GatherDecision(GatherMode.Gather, null);

        var waits = open.Select(t => Eorzea.UntilOpen(t.Mask, now)).Where(w => w != null).Select(w => w!.Value).ToList();
        if (waits.Count == 0) return new GatherDecision(GatherMode.Gather, null);
        var next = waits.Min();
        if (next <= lead) return new GatherDecision(GatherMode.Gather, next);
        return new GatherDecision(canCraft ? GatherMode.CraftMeanwhile : GatherMode.Wait, next);
    }
}
