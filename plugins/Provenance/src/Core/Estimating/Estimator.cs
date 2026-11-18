using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Catalog;
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
    public double FishPerMinute { get; set; } = 1.5;

    /// <summary>Moves a figure toward a new measurement (exponential average, 30% weight).</summary>
    public static double Blend(double current, double measured) => current * 0.7 + measured * 0.3;
}

public sealed record CraftEstimate(RecipeInfo Recipe, int Crafts, bool Hq, int? StepsPerCraft, TimeSpan Duration, TimeSpan FinishedAfter);

public sealed record OrderEstimate(
    TimeSpan Gathering,
    bool HasTimedNodes,
    IReadOnlyList<CraftEstimate> Crafts,
    /// <summary>When each ordered item is ready, counted from the start of the run.</summary>
    IReadOnlyDictionary<uint, TimeSpan> ReadyAfter,
    /// <summary>Some recipes have no rotation yet, so a typical step count was assumed.</summary>
    bool Approximate)
{
    public TimeSpan Crafting => Crafts.Aggregate(TimeSpan.Zero, (t, c) => t + c.Duration);
    public TimeSpan Total => Gathering + Crafting;

    public static readonly OrderEstimate Empty = new(TimeSpan.Zero, false, Array.Empty<CraftEstimate>(), new Dictionary<uint, TimeSpan>(), false);
}

/// <summary>
/// Estimates how long an order takes, in the order the run works: all gathering first, then the
/// recipes, ingredients before the items that use them.
/// </summary>
public static class Estimator
{
    /// <summary>Typical rotation lengths when Raphael hasn't answered yet.</summary>
    public const int TypicalStepsHq = 14;
    public const int TypicalStepsFast = 6;

    /// <param name="steps">Rotation length for a recipe (and whether HQ is wanted), or null when unknown.</param>
    /// <param name="hqWanted">Whether an ordered item should be crafted in high quality.</param>
    public static OrderEstimate Estimate(Plan plan, Timings t, Func<RecipeInfo, bool, int?> steps, Func<uint, bool> hqWanted)
    {
        if (plan.Lines.Count == 0) return OrderEstimate.Empty;

        var gathered = plan.Lines.Where(l => l.Supply is Supply.Gather or Supply.GatherTimed).Sum(l => Math.Max(0, l.Remaining));
        var fished = plan.Lines.Where(l => l.Supply == Supply.Fish).Sum(l => Math.Max(0, l.Remaining));
        var gathering = TimeSpan.FromMinutes(gathered / Math.Max(0.1, t.GatheredPerMinute) + fished / Math.Max(0.05, t.FishPerMinute));
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

        return new OrderEstimate(gathering, timed, crafts, ready, approximate);
    }

    /// <summary>"under a minute", "about 12 min", "about 1 h 05 min".</summary>
    public static string Describe(TimeSpan span)
    {
        if (span.TotalSeconds < 60) return "under a minute";
        var minutes = (int)Math.Round(span.TotalMinutes);
        return minutes < 60 ? $"about {minutes} min" : $"about {minutes / 60} h {minutes % 60:00} min";
    }
}
