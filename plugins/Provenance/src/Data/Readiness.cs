using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Catalog;
using Provenance.Core.Character;
using Provenance.Core.Planning;

namespace Provenance.Data;

/// <summary>Something the character can't do for this order yet (Blocking), or should know about.</summary>
internal sealed record ReadinessIssue(uint ItemId, uint ClassJobId, string Text, bool Blocking);

/// <summary>
/// Can this character actually do the order? Compares every craft and every gathered item with the
/// job levels, the stats of the best gearset from the last character check (plus the food and
/// medicine picked on the Character page), and whether Raphael finds a rotation that finishes each
/// craft. Blocking issues stop the run from starting, like missing items do.
/// </summary>
internal static class Readiness
{
    /// <param name="rotationProblem">Raphael's answer for a recipe: null when it found a rotation (or hasn't answered yet).</param>
    public static List<ReadinessIssue> Check(Plan plan, GameCatalog catalog, CharacterProfile? profile, Configuration cfg,
        Func<RecipeInfo, bool, string?> rotationProblem)
    {
        var issues = new List<ReadinessIssue>();
        if (plan.Lines.Count == 0) return issues;
        var hqItems = cfg.Order.Lines.Where(o => o.Hq).Select(o => o.ItemId).ToHashSet();

        // Crafting: the game's minimum stats, specialist recipes, and whether any rotation finishes the craft.
        foreach (var line in plan.CraftSteps.Where(l => l.Recipe != null && l.Crafts > 0))
        {
            var recipe = line.Recipe!;
            var classJob = recipe.Job.ClassJobId();
            var needs = catalog.NeedsFor(recipe);
            var job = profile?.Job(classJob);
            var abbr = ClassJobs.Abbreviation(classJob);
            if (job == null || job.Source == StatSource.None)
            {
                if (profile != null)
                    issues.Add(new ReadinessIssue(line.ItemId, classJob, $"No gearset saved for {abbr}; save one so the run can put it on.", true));
                continue;
            }
            var stats = WithConsumables(job.Stats, Discipline.Hand, catalog, cfg);
            // Gear-only numbers leave out base stats, so they read low: warn, don't block.
            var sure = job.Source != StatSource.GearOnly;
            var unsure = sure ? "" : " (gear only; switch to it once and check your character again)";
            if (needs.Craftsmanship > stats[0])
                issues.Add(new ReadinessIssue(line.ItemId, classJob, $"Needs {needs.Craftsmanship:N0} Craftsmanship to start; your {abbr} gearset has {stats[0]:N0}{unsure}.", sure));
            if (needs.Control > stats[1])
                issues.Add(new ReadinessIssue(line.ItemId, classJob, $"Needs {needs.Control:N0} Control to start; your {abbr} gearset has {stats[1]:N0}{unsure}.", sure));
            if (needs.Specialist && !job.Specialist)
                issues.Add(new ReadinessIssue(line.ItemId, classJob, $"Only a {abbr} specialist can craft this; equip a Soul of the {recipe.Job} in the gearset.", true));
            if (rotationProblem(recipe, line.IsTarget && recipe.CanHq && hqItems.Contains(line.ItemId)) is { } why)
                issues.Add(new ReadinessIssue(line.ItemId, classJob, $"{why} With your {abbr} stats no rotation finishes this craft{unsure}.", sure));
        }

        // Gathering and fishing: job level and the Perception the item asks for.
        int Level(uint job) => PlanService.JobLevel(job);
        foreach (var line in plan.Lines.Where(l => l.Supply is Supply.Gather or Supply.GatherTimed or Supply.Fish && l.Remaining > 0))
        {
            if (catalog.GatherNeedsFor(line.ItemId, Level) is not { } needs) continue;
            var abbr = ClassJobs.Abbreviation(needs.ClassJobId);
            var level = Level(needs.ClassJobId);
            if (level == 0)
            {
                issues.Add(new ReadinessIssue(line.ItemId, needs.ClassJobId, $"Needs {abbr}, which this character hasn't unlocked.", true));
                continue;
            }
            if (level < needs.Level)
            {
                issues.Add(new ReadinessIssue(line.ItemId, needs.ClassJobId, $"Needs {abbr} level {needs.Level}; yours is {level}.", true));
                continue;
            }
            if (needs.Perception <= 0 || profile?.Job(needs.ClassJobId) is not { Source: not StatSource.None } job) continue;
            var stats = WithConsumables(job.Stats, Discipline.Land, catalog, cfg);
            if (stats[1] < needs.Perception)
                issues.Add(job.Source == StatSource.GearOnly
                    ? new ReadinessIssue(line.ItemId, needs.ClassJobId, $"Needs {needs.Perception:N0} Perception; your {abbr} gear alone has {stats[1]:N0} (switch to it once and check your character again).", false)
                    : new ReadinessIssue(line.ItemId, needs.ClassJobId, $"Needs {needs.Perception:N0} Perception; your {abbr} gearset has {stats[1]:N0}. GatherBuddy Reborn won't start without it.", true));
        }

        if (profile == null)
            issues.Add(new ReadinessIssue(0, 0, "Check your character on the Character page so your gearsets' stats can be compared with what the order asks for.", false));
        return issues;
    }

    /// <summary>Gearset stats plus the food and medicine picked for that discipline.</summary>
    private static int[] WithConsumables(int[] stats, Discipline discipline, GameCatalog catalog, Configuration cfg)
    {
        var result = (int[])stats.Clone();
        var (food, medicine) = cfg.Consumables.For(discipline);
        foreach (var choice in new[] { food, medicine })
        {
            if (catalog.Consumable(choice.ItemId) is not { } info) continue;
            var bonus = info.Bonus(stats, choice.Hq);
            for (var i = 0; i < 3 && i < result.Length; i++) result[i] += bonus[i];
        }
        return result;
    }
}
