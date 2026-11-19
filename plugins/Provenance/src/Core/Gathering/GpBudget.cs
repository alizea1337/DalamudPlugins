using System;
using System.Collections.Generic;
using System.Linq;

namespace Provenance.Core.Gathering;

/// <summary>A GP potion (Hi-Cordial, Cordial, Watered Cordial): GP it gives back and its cooldown.</summary>
public readonly record struct Cordial(uint ItemId, bool Hq, int Gp, int CooldownSeconds);

/// <param name="Wait">Time spent standing at nodes until the GP is back.</param>
/// <param name="Cordials">Cordials drunk instead of waiting.</param>
public readonly record struct GpWaitEstimate(TimeSpan Wait, int Cordials)
{
    public static readonly GpWaitEstimate None = new(TimeSpan.Zero, 0);
}

/// <summary>
/// GP before collectable nodes. The full collectable rotation (Scrutiny, Solid Reason) wants
/// <see cref="CollectableRotation.FullRotationGp"/> at the node; a visit spends some of it, and GP comes
/// back at 5 every 3 seconds outside a node. Cordials close the gap instead of waiting, one per cooldown.
/// </summary>
public static class GpBudget
{
    public const double RegenPerSecond = 5.0 / 3.0;

    /// <summary>
    /// GP to have at a collectable node before opening it: the full rotation's. A job with less maximum
    /// GP never runs the full rotation, so there's nothing to wait for (0).
    /// </summary>
    public static int Need(int maxGp) => maxGp >= CollectableRotation.FullRotationGp ? CollectableRotation.FullRotationGp : 0;

    /// <summary>
    /// The cordial to drink for a GP gap: one that closes it, losing the least to the GP cap, the smallest
    /// such (so the big ones last); else the biggest. One that would lose more to the cap than the gap
    /// itself isn't worth it (the wait is short then). Null when there's no gap or nothing worth drinking.
    /// </summary>
    public static Cordial? Pick(IEnumerable<Cordial> available, int gp, int need, int maxGp)
    {
        var gap = need - gp;
        if (gap <= 0) return null;
        Cordial? best = null;
        var bestCloses = false;
        var bestWaste = 0;
        foreach (var c in available)
        {
            if (c.Gp <= 0) continue;
            var waste = Math.Max(0, gp + c.Gp - maxGp);
            if (waste > gap) continue;
            var closes = c.Gp >= gap;
            var better = best is not { } b
                         || (closes != bestCloses ? closes
                             : waste != bestWaste ? waste < bestWaste
                             : c.Gp != b.Gp ? (closes ? c.Gp < b.Gp : c.Gp > b.Gp)
                             : c.CooldownSeconds < b.CooldownSeconds);
            if (!better) continue;
            best = c;
            bestCloses = closes;
            bestWaste = waste;
        }
        return best;
    }

    /// <summary>
    /// Waiting for GP over the collectable node visits still to come, drinking the cordials (one entry
    /// per potion) the way a run does: before a visit that lacks GP, when off cooldown and worth it.
    /// </summary>
    /// <param name="spendPerVisit">GP a visit spends (Scrutiny, sometimes Solid Reason).</param>
    /// <param name="secondsPerVisit">Travel plus the node itself.</param>
    /// <param name="nodeSecondsPerVisit">The part of a visit spent inside the node, where GP doesn't come back.</param>
    /// <param name="cordialReadyIn">Seconds until the cordial cooldown is over (one was drunk just before).</param>
    public static GpWaitEstimate Simulate(int visits, int startGp, int maxGp, IReadOnlyList<Cordial> cordials, int spendPerVisit, double secondsPerVisit,
        double nodeSecondsPerVisit = 0, double cordialReadyIn = 0)
    {
        var need = Need(maxGp);
        if (visits <= 0 || need <= 0) return GpWaitEstimate.None;
        var pool = cordials.Where(c => c.Gp > 0).ToList();
        var travelRegen = Math.Max(0, secondsPerVisit - nodeSecondsPerVisit) * RegenPerSecond;
        double gp = Math.Clamp(startGp, 0, maxGp), clock = 0, wait = 0, readyAt = Math.Max(0, cordialReadyIn);
        var drunk = 0;
        for (var v = 0; v < visits; v++)
        {
            while (gp < need)
            {
                if (pool.Count > 0 && clock >= readyAt && Pick(pool, (int)gp, need, maxGp) is { } c)
                {
                    gp = Math.Min(maxGp, gp + c.Gp);
                    readyAt = clock + c.CooldownSeconds;
                    pool.Remove(c);
                    drunk++;
                    continue;
                }
                // Stand until the GP is back, or until the cordial cooldown is over (then look again).
                var untilFull = (need - gp) / RegenPerSecond;
                var untilReady = pool.Count > 0 && readyAt > clock ? readyAt - clock : double.MaxValue;
                var step = Math.Min(untilFull, untilReady);
                wait += step;
                clock += step;
                gp = step >= untilFull ? need : gp + step * RegenPerSecond;
            }
            gp = Math.Max(0, gp - spendPerVisit);
            clock += secondsPerVisit;
            gp = Math.Min(maxGp, gp + travelRegen);
        }
        return new GpWaitEstimate(TimeSpan.FromSeconds(wait), drunk);
    }
}
