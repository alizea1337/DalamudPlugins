using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Catalog;
using Provenance.Core.Character;
using Provenance.Core.Estimating;
using Provenance.Core.Fishing;
using Provenance.Core.Planning;
using Provenance.Integrations;

namespace Provenance.Data;

/// <summary>Something the character can't do for this order yet (Blocking), or should know about.</summary>
internal sealed record ReadinessIssue(uint ItemId, uint ClassJobId, string Text, bool Blocking);

/// <summary>
/// Can this character actually do the order? Compares every craft and every gathered item with the
/// job levels, the stats of the best gearset from the last character check (plus the food and
/// medicine picked on the Character page), and whether Raphael finds a rotation that finishes each
/// craft; for fish, whether a spot can be reached and fished (level, actions, known bait) and a Fisher
/// gearset exists, with what to expect (big fish, weather and short windows, Fisher's Intuition).
/// Blocking issues stop the run from starting, like missing items do.
/// </summary>
internal static class Readiness
{
    /// <param name="rotationProblem">Raphael's answer for a recipe: null when it found a rotation (or hasn't answered yet).</param>
    /// <param name="fishOutlook">Where and when a fish can be fished (<see cref="PlanService.Outlook"/>); null skips the fish checks.</param>
    /// <param name="qualityNote">For a craft made in high quality: how often it comes out HQ when its best rotation falls short; null when it doesn't (or isn't known yet).</param>
    public static List<ReadinessIssue> Check(Plan plan, GameCatalog catalog, CharacterProfile? profile, Configuration cfg,
        Func<RecipeInfo, bool, string?> rotationProblem, Func<uint, FishOutlook?>? fishOutlook = null, Func<RecipeInfo, string?>? qualityNote = null)
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
            var hq = line.IsTarget ? recipe.CanHq && hqItems.Contains(line.ItemId) : line.Hq;
            if (rotationProblem(recipe, hq) is { } why)
                issues.Add(new ReadinessIssue(line.ItemId, classJob, $"{why} With your {abbr} stats no rotation finishes this craft{unsure}.", sure));
            else if (hq && qualityNote?.Invoke(recipe) is { } note)
                issues.Add(new ReadinessIssue(line.ItemId, classJob, note, false));
        }

        // Gathering and fishing: job level and the Perception the item asks for; fish get their own checks.
        int Level(uint job) => PlanService.JobLevel(job);

        // Items only from aetherial reduction: reduction unlocked, and one of the collectables within reach.
        foreach (var line in plan.Lines.Where(l => l.Supply == Supply.Reduce && l.Remaining > 0))
        {
            if (Services.ClientState.IsLoggedIn && !Run.Gathering.Upkeep.ReductionUnlocked)
            {
                issues.Add(new ReadinessIssue(line.ItemId, 0, "Comes from aetherial reduction, which this character hasn't unlocked yet: it comes with the quest \"No Longer a Collectable\" in Mor Dhona (a gatherer at level 56).", true));
                continue;
            }
            var sources = Reduction.Sources(catalog, line.ItemId, cfg.Fishing.FishForReduction);
            var known = sources.Select(id => (Id: id, Needs: catalog.GatherNeedsFor(id, Level)))
                .Where(x => x.Needs.HasValue).Select(x => (x.Id, Needs: x.Needs!.Value)).ToList();
            if (known.Count > 0 && known.All(x => Level(x.Needs.ClassJobId) < x.Needs.Level))
            {
                var (id, needs) = known.OrderBy(x => x.Needs.Level).First();
                issues.Add(new ReadinessIssue(line.ItemId, needs.ClassJobId,
                    $"Comes from reducing {catalog.ItemName(id)}, which needs {ClassJobs.Abbreviation(needs.ClassJobId)} level {needs.Level}; yours is {Level(needs.ClassJobId)}.", true));
            }
        }

        var fishing = false;
        foreach (var line in plan.Lines.Where(l => l.Supply is Supply.Gather or Supply.GatherTimed or Supply.Fish && l.Remaining > 0))
        {
            // Fish known only from their fishing log entry have no level here; their own checks cover it.
            if (catalog.GatherNeedsFor(line.ItemId, Level) is not { } needs)
            {
                if (line.Supply == Supply.Fish) CheckFishLine(line);
                continue;
            }
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
            if (line.Supply == Supply.Fish)
            {
                CheckFishLine(line);
                continue;
            }
            if (needs.Perception <= 0 || profile?.Job(needs.ClassJobId) is not { Source: not StatSource.None } job) continue;
            var stats = WithConsumables(job.Stats, Discipline.Land, catalog, cfg);
            if (stats[1] < needs.Perception)
                issues.Add(job.Source == StatSource.GearOnly
                    ? new ReadinessIssue(line.ItemId, needs.ClassJobId, $"Needs {needs.Perception:N0} Perception; your {abbr} gear alone has {stats[1]:N0} (switch to it once and check your character again).", false)
                    : new ReadinessIssue(line.ItemId, needs.ClassJobId, $"Needs {needs.Perception:N0} Perception; your {abbr} gearset has {stats[1]:N0}. The item can't be gathered without it.", true));
        }

        // The run changes into the Fisher gearset before it fishes.
        if (fishing && Services.ClientState.IsLoggedIn && GameState.BestGearset(FishAccess.Fisher) < 0)
            issues.Add(new ReadinessIssue(0, FishAccess.Fisher, "No gearset saved for FSH; save one so the run can change into it to fish.", true));

        if (profile == null)
            issues.Add(new ReadinessIssue(0, 0, "Check your character on the Character page so your gearsets' stats can be compared with what the order asks for.", false));
        return issues;

        void CheckFishLine(PlanLine line)
        {
            if (fishOutlook == null) return;
            fishing = true;
            if (fishOutlook(line.ItemId) is { } outlook) CheckFish(issues, plan, line, outlook, catalog, cfg);
            else
                issues.Add(new ReadinessIssue(line.ItemId, FishAccess.Fisher,
                    "Provenance has no fishing data for this fish (where and on what it bites), so a run can't catch it.", true));
        }
    }

    /// <summary>
    /// One fish: blocking when this character can't fish it (spot level, reach, actions, no bait known), when it
    /// can't be caught within the weather forecast at any spot it can use (its own window, its mooch chain's, or
    /// that of a fish caught first for Fisher's Intuition), or, with buying bait off, when the bags hold none of its
    /// bait; otherwise one note on what to expect, when there's anything worth knowing.
    /// </summary>
    private static void CheckFish(List<ReadinessIssue> issues, Plan plan, PlanLine line, FishOutlook outlook, GameCatalog catalog, Configuration cfg)
    {
        var fish = outlook.Fish;
        if (outlook.Blocker is { } why)
        {
            issues.Add(new ReadinessIssue(line.ItemId, FishAccess.Fisher, $"Can't fish this yet ({why}).", true));
            return;
        }

        var notes = new List<string>(4);
        if (!fish.Spear && !cfg.Fishing.BuyBait) CheckOwnBait(issues, notes, line, fish, catalog, cfg);

        // A run waits for a window within the forecast; with none, the fisher gives the fish up and the run stops
        // before crafting. This clears by itself once a window comes into the forecast.
        var now = DateTimeOffset.UtcNow;
        if (outlook.OpensIn(now) == null)
        {
            issues.Add(new ReadinessIssue(line.ItemId, FishAccess.Fisher, NoWindow(outlook, catalog), true));
            return;
        }
        if (fish.Big) notes.Add("A big fish: it can take many casts, sometimes more than one window.");
        var rule = PlanService.Rule(fish, outlook.Times.Links);
        if (rule.Weather)
            notes.Add($"Only bites {rule.Text}{MoochedFrom(rule, catalog)}; that weather can take hours to come round, and the run gathers or crafts in the meantime when it can.");
        else if (outlook.Windows is { Count: > 0 } windows)
        {
            // A short window may not fit the whole catch: say so when the fishing takes longer than one window.
            var fishing = TimeSpan.FromSeconds(line.Remaining * BaitNeeds.SecondsPerCatch(fish, catalog.Gather.Fish));
            var open = windows.Max(w => w.End - w.Start);
            if (fishing > open)
                notes.Add($"Only bites {rule.Text}{MoochedFrom(rule, catalog)}, {Short(open)} at a time; catching {line.Remaining} likely takes several windows.");
        }
        if (fish.NeedsIntuition)
        {
            var first = string.Join(" and ", fish.Predators.Select(p => $"{p.Count}× {catalog.ItemName(p.ItemId)}"));
            notes.Add(fish.Spear
                ? $"Shows up in swimming shadows after gigging {first} (the run does that first)."
                : $"Needs Fisher's Intuition first: the run catches {first}" + (fish.IntuitionSeconds > 0 ? $", then it bites for {Short(TimeSpan.FromSeconds(fish.IntuitionSeconds))}." : "."));
            // Predators that bite less often than the fish: the run sits waiting for them between tries.
            var slow = outlook.Times.Predators.Where(p => p.Slows).ToList();
            if (slow.Count > 0)
                notes.Add(And(slow.Select(p => $"{catalog.ItemName(p.Fish.ItemId)} only bites {PlanService.Rule(p.Fish, p.Links).Text}").ToList())
                          + $": waiting for {(slow.Count == 1 ? "it" : "them")} before each try can make catches slow.");
        }
        if (cfg.Fishing.BuyBait && StandInFor(plan, line.ItemId, fish, catalog, cfg) is { Length: > 0 } replaced)
            notes.Add($"{replaced} can't be bought for gil, so the run casts Versatile Lure instead once your bags run out; bites may come slower.");
        if (notes.Count > 0) issues.Add(new ReadinessIssue(line.ItemId, FishAccess.Fisher, string.Join(" ", notes), false));
    }

    /// <summary>
    /// Why a fish that can't be caught within the forecast (at the spots this character can use) blocks the start:
    /// "Only bites in Fog after Rain; not in the next 7 days of the weather forecast, so a run can't catch it yet. …",
    /// or the same about a fish caught first for Fisher's Intuition that doesn't bite there.
    /// </summary>
    private static string NoWindow(FishOutlook outlook, GameCatalog catalog)
    {
        var days = PlanService.FishHorizon.TotalDays;
        const string later = "Get it yourself, or start the run once that weather is in the forecast.";
        if (outlook.Times.PredatorBeyond is { } p)
        {
            var name = catalog.ItemName(p.Fish.ItemId);
            var theirs = PlanService.Rule(p.Fish, p.Links);
            return theirs.Weather
                ? $"{name}, caught first for Fisher's Intuition, only bites {theirs.Text}; not in the next {days:0} days of the weather forecast, so a run can't catch this fish yet. {later}"
                : $"{name}, caught first for Fisher's Intuition, doesn't bite in the next {days:0} days at any spot you can use, so a run can't catch this fish. Get it yourself.";
        }
        var rule = PlanService.Rule(outlook.Fish, outlook.Times.Links);
        return rule.Weather
            ? $"Only bites {rule.Text}{MoochedFrom(rule, catalog)}; not in the next {days:0} days of the weather forecast, so a run can't catch it yet. {later}"
            : $"Doesn't bite in the next {days:0} days at any spot you can use, so a run can't catch it. Get it yourself.";
    }

    /// <summary>" (mooched from Fullmoon Sardine, which only bites then)" when a rule's hours or weather are a mooch link's; "" otherwise.</summary>
    private static string MoochedFrom(BiteRuleText rule, GameCatalog catalog)
        => rule.From.Count == 0 ? ""
            : $" (mooched from {And(rule.From.Select(l => catalog.ItemName(l.ItemId)).ToList())}, which only {(rule.From.Count == 1 ? "bites" : "bite")} then)";

    /// <summary>"A", "A and B", "A, B and C".</summary>
    private static string And(IReadOnlyList<string> parts)
        => parts.Count <= 1 ? string.Concat(parts) : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];

    /// <summary>With buying bait off: blocking when the bags hold none of a bait (and no stand-in), a note when they hold less than the estimate.</summary>
    private static void CheckOwnBait(List<ReadinessIssue> issues, List<string> notes, PlanLine line, FishInfo fish, GameCatalog catalog, Configuration cfg)
    {
        if (!Services.ClientState.IsLoggedIn) return;
        var lure = cfg.Fishing.VersatileLureFallback ? GameState.BagCount(FishAccess.VersatileLure) : 0;
        foreach (var (bait, perCatch) in BaitNeeds.PerCatch(fish, catalog.Gather.Fish))
        {
            var have = GameState.BagCount(bait);
            var need = (int)Math.Ceiling(line.Remaining * perCatch - 1e-9);
            var name = catalog.ItemName(bait);
            if (have == 0 && lure == 0)
                issues.Add(new ReadinessIssue(line.ItemId, FishAccess.Fisher,
                    $"No {name} in your bags, and buying bait is off in Settings: bring about {need} (bait in the saddlebag can't be cast).", true));
            else if (have == 0)
                notes.Add($"No {name} in your bags; the run casts your Versatile Lure instead (buying bait is off).");
            else if (have < need)
                notes.Add($"About {need} {name} needed, your bags hold {have}; buying bait is off in Settings.");
        }
    }

    /// <summary>
    /// The fish's bait that Versatile Lure stands in for (no vendor sells it under the cap and the bags don't hold
    /// enough), named ("Brute Leech"); "" when the fish casts its own bait.
    /// </summary>
    private static string StandInFor(Plan plan, uint fishId, FishInfo fish, GameCatalog catalog, Configuration cfg)
    {
        if (plan[FishAccess.VersatileLure] is not { } lure || !lure.NeededFor.Contains(fishId)) return "";
        var replaced = BaitNeeds.PerCatch(fish, catalog.Gather.Fish)
            .Where(b => b.Bait != FishAccess.VersatileLure && !Planner.BuysBait(plan, catalog, b.Bait, cfg.Buying.VendorMaxPrice))
            .Select(b => catalog.ItemName(b.Bait)).ToList();
        return replaced.Count == 0 ? "" : string.Join(" and ", replaced);
    }

    /// <summary>"12 min", "1 h 05 min", "under a minute".</summary>
    private static string Short(TimeSpan span) => Estimator.Describe(span).Replace("about ", "");

    /// <summary>Gearset stats plus the food and medicine picked for that discipline.</summary>
    public static int[] WithConsumables(int[] stats, Discipline discipline, GameCatalog catalog, Configuration cfg)
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
