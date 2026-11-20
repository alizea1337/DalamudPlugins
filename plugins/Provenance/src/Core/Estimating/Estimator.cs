using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Catalog;
using Provenance.Core.Gathering;
using Provenance.Core.Planning;

namespace Provenance.Core.Estimating;

/// <summary>Timing figures. Start from sensible defaults, then follow what runs actually measure.</summary>
[Serializable]
public sealed class Timings
{
    /// <summary>One crafting action including its animation.</summary>
    public double SecondsPerAction { get; set; } = 2.6;
    /// <summary>Between two crafts: filling ingredients, starting the synthesis, result window.</summary>
    public double SecondsBetweenCrafts { get; set; } = 4.0;
    /// <summary>Changing job, leaving the crafting log, eating.</summary>
    public double SecondsPerJobSwitch { get; set; } = 8.0;
    /// <summary>Gathered items per minute, travel between nodes included.</summary>
    public double GatheredPerMinute { get; set; } = 12;
    /// <summary>
    /// Fish per minute. The plan's estimate uses it for fish the fish data doesn't know; the others go by their
    /// bite times, the casts a catch takes and their windows (<see cref="FishInputs"/>).
    /// </summary>
    public double FishPerMinute { get; set; } = 1.5;
    /// <summary>Getting to a fishing spot and ready to cast, once per kind of fish: the teleport, the walk to a stand, gearset and bait.</summary>
    public double SecondsPerFishingTrip { get; set; } = 90;
    /// <summary>Collectables a collectable node gives (a few per node, with Solid Reason more).</summary>
    public double CollectablesPerVisit { get; set; } = 2;
    /// <summary>GP a collectable node visit spends (Scrutiny, sometimes Solid Reason).</summary>
    public double GpPerCollectableVisit { get; set; } = 350;
    /// <summary>One collectable node: getting there and working it, waiting for GP not included.</summary>
    public double SecondsPerCollectableVisit { get; set; } = 45;
    /// <summary>The part of a collectable visit spent inside the node, where GP doesn't come back.</summary>
    public double SecondsInCollectableNode { get; set; } = 20;

    /// <summary>Moves a figure toward a new measurement (exponential average, 30% weight).</summary>
    public static double Blend(double current, double measured) => current * 0.7 + measured * 0.3;
}

public sealed record CraftEstimate(RecipeInfo Recipe, int Crafts, bool Hq, int? StepsPerCraft, TimeSpan Duration, TimeSpan FinishedAfter);

/// <summary>What the estimate needs about collectables; without it they count like any gathered item.</summary>
/// <param name="GatheredAsCollectable">Items nodes hand out as collectables (a few per node).</param>
/// <param name="MaxGp">The gatherer's maximum GP for an item (0 when unknown).</param>
/// <param name="Cordials">Cordials a run may drink, one entry per potion.</param>
/// <param name="WaitForGp">The run waits for GP before collectable nodes.</param>
public sealed record CollectableInputs(Func<uint, bool> GatheredAsCollectable, Func<uint, int> MaxGp, IReadOnlyList<Cordial> Cordials, bool WaitForGp);

public sealed record OrderEstimate(
    TimeSpan Gathering,
    bool HasTimedNodes,
    IReadOnlyList<CraftEstimate> Crafts,
    /// <summary>When each ordered item is ready, counted from the start of the run.</summary>
    IReadOnlyDictionary<uint, TimeSpan> ReadyAfter,
    /// <summary>Some recipes have no rotation yet, so a typical step count was assumed.</summary>
    bool Approximate,
    /// <summary>Part of <see cref="Gathering"/>: waiting for GP before collectable nodes, and the cordials drunk instead.</summary>
    GpWaitEstimate GpWait = default,
    /// <summary>Part of <see cref="Gathering"/>: fishing, with the waits for fish windows.</summary>
    TimeSpan Fishing = default,
    /// <summary>Part of <see cref="Fishing"/>: waiting for a fish to start biting with nothing else left to do.</summary>
    TimeSpan FishWait = default,
    /// <summary>
    /// Fish with no window at all within the weather forecast (their own, their mooch chain's, or that of a fish
    /// caught first for Fisher's Intuition), so a run can't catch them yet: their fishing is counted, no wait for them.
    /// </summary>
    IReadOnlyList<uint>? FishBeyondForecast = null,
    /// <summary>
    /// Fish whose windows within the forecast don't hold all their fishing: a run keeps fishing them window by
    /// window. All their fishing is counted, the wait for the later windows isn't.
    /// </summary>
    IReadOnlyList<uint>? FishShortOfWindows = null)
{
    public TimeSpan Crafting => Crafts.Aggregate(TimeSpan.Zero, (t, c) => t + c.Duration);
    public TimeSpan Total => Gathering + Crafting;

    public static readonly OrderEstimate Empty = new(TimeSpan.Zero, false, Array.Empty<CraftEstimate>(), new Dictionary<uint, TimeSpan>(), false);
}

/// <summary>
/// Estimates how long an order takes, in the order the run works: all gathering first (fishing included,
/// fitted around fish windows when <see cref="FishInputs"/> are given), then the recipes, ingredients before
/// the items that use them.
/// </summary>
public static class Estimator
{
    /// <summary>Typical rotation lengths when Raphael hasn't answered yet.</summary>
    public const int TypicalStepsHq = 14;

    /// <summary>Items one collectable gives in aetherial reduction, on average at a good collectability (a guess; it varies).</summary>
    public const double ReduceYield = 1.5;
    public const int TypicalStepsFast = 6;

    /// <param name="steps">Rotation length for a recipe (and whether HQ is wanted), or null when unknown.</param>
    /// <param name="hqWanted">Whether an ordered item should be crafted in high quality.</param>
    /// <param name="collectables">Collectable gathering: by node visits, with the wait for GP.</param>
    /// <param name="fish">Fish by their catch time and windows; without it every fish goes at <see cref="Timings.FishPerMinute"/>.</param>
    public static OrderEstimate Estimate(Plan plan, Timings t, Func<RecipeInfo, bool, int?> steps, Func<uint, bool> hqWanted,
        CollectableInputs? collectables = null, FishInputs? fish = null)
    {
        if (plan.Lines.Count == 0) return OrderEstimate.Empty;

        var collectable = collectables?.GatheredAsCollectable ?? (_ => false);
        var gatherLines = plan.Lines.Where(l => l.Supply is Supply.Gather or Supply.GatherTimed && l.Remaining > 0).ToList();
        var gathered = gatherLines.Where(l => !collectable(l.ItemId)).Sum(l => l.Remaining);
        var fished = plan.Lines.Where(l => l.Supply == Supply.Fish).Sum(l => Math.Max(0, l.Remaining));
        var collectLines = gatherLines.Where(l => collectable(l.ItemId)).ToList();
        var gpWait = CollectableGp(collectLines, t, collectables);
        // Items that only come from aetherial reduction: collectables gathered and reduced (one gives about
        // ReduceYield of them at a good collectability).
        var reduceVisits = plan.Lines.Where(l => l.Supply == Supply.Reduce && l.Remaining > 0)
            .Sum(l => Visits((int)Math.Ceiling(l.Remaining / ReduceYield), t));
        var collectTime = TimeSpan.FromSeconds((collectLines.Sum(l => Visits(l.Remaining, t)) + reduceVisits) * t.SecondsPerCollectableVisit);
        TimeSpan gathering, fishing;
        var fishWait = TimeSpan.Zero;
        IReadOnlyList<uint>? beyond = null, tight = null;
        if (fish == null)
        {
            gathering = TimeSpan.FromMinutes(gathered / Math.Max(0.1, t.GatheredPerMinute) + fished / Math.Max(0.05, t.FishPerMinute))
                        + collectTime + gpWait.Wait;
            fishing = TimeSpan.FromMinutes(fished / Math.Max(0.05, t.FishPerMinute));
        }
        else
        {
            // Fish the data knows take their catch time plus a trip; fish with a window fit around the rest.
            var steady = 0.0;
            var windowed = new List<WindowedWork>();
            foreach (var line in plan.Lines.Where(l => l.Supply == Supply.Fish && l.Remaining > 0))
            {
                if (fish.Fish(line.ItemId) is not { } catchTime)
                {
                    steady += line.Remaining / Math.Max(0.05, t.FishPerMinute) * 60;
                    continue;
                }
                var seconds = Math.Max(0, t.SecondsPerFishingTrip) + line.Remaining * Math.Max(0, catchTime.SecondsPerCatch);
                if (catchTime.Windows == null) steady += seconds;
                else windowed.Add(new WindowedWork(line.ItemId, seconds, catchTime.Windows));
            }
            var land = gathered / Math.Max(0.1, t.GatheredPerMinute) * 60 + collectTime.TotalSeconds + gpWait.Wait.TotalSeconds;
            var schedule = FishSchedule.Run(land + steady, windowed, fish.Now);
            gathering = schedule.Total;
            fishWait = schedule.Waiting;
            fishing = TimeSpan.FromSeconds(steady + windowed.Sum(w => w.Seconds)) + schedule.Waiting;
            // Fishing left over: the fish doesn't bite within the forecast at all, or its windows there are too few.
            var none = schedule.Unscheduled.Where(id => windowed.First(w => w.ItemId == id).Windows.All(x => x.End <= fish.Now)).ToList();
            var rest = schedule.Unscheduled.Except(none).ToList();
            if (none.Count > 0) beyond = none;
            if (rest.Count > 0) tight = rest;
        }
        var timed = plan.Lines.Any(l => l.Supply == Supply.GatherTimed && l.Remaining > 0);

        var crafts = new List<CraftEstimate>();
        var ready = new Dictionary<uint, TimeSpan>();
        var clock = gathering;
        Job? lastJob = null;
        var approximate = false;
        foreach (var line in plan.CraftSteps)
        {
            if (line.Recipe is not { } recipe || line.Crafts <= 0) continue;
            var hq = line.IsTarget && recipe.CanHq && hqWanted(line.ItemId);
            var known = steps(recipe, hq);
            if (known == null) approximate = true;
            var perCraft = (known ?? (hq ? TypicalStepsHq : TypicalStepsFast)) * t.SecondsPerAction + t.SecondsBetweenCrafts;
            var duration = TimeSpan.FromSeconds(perCraft * line.Crafts + (lastJob != recipe.Job ? t.SecondsPerJobSwitch : 0));
            lastJob = recipe.Job;
            clock += duration;
            crafts.Add(new CraftEstimate(recipe, line.Crafts, hq, known, duration, clock));
            if (line.IsTarget) ready[line.ItemId] = clock;
        }

        // Ordered items that aren't crafted are ready once gathering is done.
        foreach (var line in plan.Lines.Where(l => l.IsTarget && l.Supply != Supply.Craft))
            ready.TryAdd(line.ItemId, gathering);

        return new OrderEstimate(gathering, timed, crafts, ready, approximate, gpWait, fishing, fishWait, beyond, tight);
    }

    /// <summary>Collectable node visits for an amount of collectables.</summary>
    public static int Visits(int collectables, Timings t)
        => collectables <= 0 ? 0 : (int)Math.Ceiling(collectables / Math.Max(0.5, t.CollectablesPerVisit));

    /// <summary>
    /// Waiting for GP before the collectable nodes still to visit (regular nodes only: timed ones are
    /// never waited for), starting from <paramref name="startGp"/> (full when null).
    /// </summary>
    public static GpWaitEstimate CollectableGp(IEnumerable<PlanLine> collectLines, Timings t, CollectableInputs? inputs, int? startGp = null)
    {
        if (inputs is not { WaitForGp: true }) return GpWaitEstimate.None;
        var lines = collectLines.ToList();
        var visits = lines.Where(l => l.Supply == Supply.Gather).Sum(l => Visits(l.Remaining, t));
        if (visits == 0) return GpWaitEstimate.None;
        var maxGp = lines.Max(l => inputs.MaxGp(l.ItemId));
        if (maxGp <= 0) maxGp = CollectableRotation.FullRotationGp;
        return GpBudget.Simulate(visits, startGp ?? maxGp, maxGp, inputs.Cordials, (int)t.GpPerCollectableVisit, t.SecondsPerCollectableVisit,
            t.SecondsInCollectableNode);
    }

    /// <summary>"under a minute", "about 12 min", "about 1 h 05 min".</summary>
    public static string Describe(TimeSpan span)
    {
        if (span.TotalSeconds < 60) return "under a minute";
        var minutes = (int)Math.Round(span.TotalMinutes);
        return minutes < 60 ? $"about {minutes} min" : $"about {minutes / 60} h {minutes % 60:00} min";
    }
}
