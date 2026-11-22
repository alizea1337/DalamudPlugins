using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Cosmic;
using Provenance.Core.Crafting;
using Provenance.Integrations;

namespace Provenance.Data;

/// <summary>The food and medicine the plan suggests: with them a class reliably golds a better mission.</summary>
/// <param name="MinutesSaved">How much sooner the goal is reached; null when the goal has no end (then <paramref name="ExtraPerHour"/> says it).</param>
internal sealed record ConsumableHint(uint ClassJob, uint Food, bool FoodHq, int FoodCount, uint Medicine, bool MedicineHq, int MedicineCount,
    MissionChoice Without, MissionChoice With, double? MinutesSaved, double ExtraPerHour);

/// <param name="Problem">Why the class can't take missions at all ("no gearset saved"), or null.</param>
/// <param name="Pending">Rotations are still being worked out, or more missions are about to be judged: the ranking may still change.</param>
/// <param name="Judged">Missions whose rotation is worked out, of <paramref name="Total"/> to judge.</param>
internal sealed record CosmicClassPlan(uint ClassJob, int Level, MissionRanking Ranking, string? Problem, ConsumableHint? Hint, bool Pending = false,
    int Judged = 0, int Total = 0);

/// <param name="ToGoal">Time left to the goal at the best class's pace; null when the goal has no end or nothing can be done.</param>
/// <param name="Settled">The rotations are worked out (or the time for it is up): the ranking holds until something changes.</param>
/// <param name="SecondsLeft">While working out: how long until the best found so far is taken.</param>
internal sealed record CosmicPlan(Planet? Planet, IReadOnlyList<CosmicClassPlan> Classes, double CreditsPerHour, TimeSpan? ToGoal,
    int CreditsNeeded, string? Problem, bool RanksKnown, bool Settled = true, double SecondsLeft = 0)
{
    public int Judged => Classes.Sum(c => c.Judged);
    public int Total => Classes.Sum(c => c.Total);

    public CosmicClassPlan? Best => Classes.Where(c => c.Ranking.Best != null).MaxBy(c => c.Ranking.Best!.CreditsPerHour);
}

/// <summary>
/// Works out the Cosmic Exploration plan: per ticked crafter, the missions it can open on the planet, judged with
/// Raphael on its gearset's stats (no lucky procs), ranked by Cosmocredits per hour. Rotations are cached, so asking
/// again is cheap; the plan is rebuilt at most once a second.
/// </summary>
internal sealed class CosmicPlanner
{
    /// <summary>Missions judged per class at first (the best-paying ones); more when none of them reaches silver, up to <see cref="JudgeMost"/>.</summary>
    private const int JudgeFirst = 8;
    private const int JudgeMost = 24;

    /// <summary>The longest the plan waits for rotations; then the best found so far is taken and kept.</summary>
    public const int ThinkSeconds = 30;

    private readonly Plugin plugin;
    private CosmicPlan? last;
    /// <summary>What the kept ranking was worked out for (planet, crafters, their levels and stats, food), when it started, and the ranking itself.</summary>
    private string? frozenKey;
    private DateTime planningSince;
    private List<CosmicClassPlan>? frozen;

    /// <summary>Works the plan out again (the "Work it out again" link).</summary>
    public void Replan()
    {
        frozenKey = null;
        judgeLimit.Clear();
        last = null;
    }
    private DateTime builtAt;
    private readonly Dictionary<uint, int> judgeLimit = new();

    public CosmicPlanner(Plugin plugin) => this.plugin = plugin;

    /// <summary>The planet the loop works on: the one picked, else the one you're on, else the newest unlocked.</summary>
    public static Planet? TargetPlanet(CosmicSettings s)
    {
        if (s.Planet is >= 0 and <= 3) return (Planet)s.Planet;
        return Cosmic.CurrentPlanet ?? Cosmic.Newest;
    }

    private bool lastWithHints;

    /// <param name="fresh">Rebuild now rather than within the second.</param>
    /// <param name="hints">Also work out the food and medicine suggestions (the Plan page; they cost rotations of their own).</param>
    public CosmicPlan Plan(bool fresh = false, bool hints = false)
    {
        if (!fresh && last != null && DateTime.UtcNow - builtAt < TimeSpan.FromSeconds(1) && (lastWithHints || !hints)) return last;
        builtAt = DateTime.UtcNow;
        lastWithHints = hints;
        last = Build(hints);
        return last;
    }

    /// <summary>Missions of a rank a class has done (or golded), counted once per plan.</summary>
    private readonly Dictionary<(uint, MissionRank, bool), int> counts = new();

    private CosmicPlan Build(bool withHints)
    {
        counts.Clear();
        var s = plugin.Config.Cosmic;
        var catalog = plugin.Plans.Catalog;
        var planet = TargetPlanet(s);
        var ranksKnown = Cosmic.Loaded;
        if (catalog == null) return new CosmicPlan(planet, Array.Empty<CosmicClassPlan>(), 0, null, 0, "Reading the game data…", ranksKnown);
        if (planet == null)
            return new CosmicPlan(null, Array.Empty<CosmicClassPlan>(), 0, null, 0,
                $"Cosmic Exploration isn't open yet: it starts with the quest \"{Cosmic.UnlockQuestName(Planet.SinusArdorum)}\" in Old Sharlayan.", ranksKnown);
        if (Raphael.ExecutablePath() == null)
            return new CosmicPlan(planet, Array.Empty<CosmicClassPlan>(), 0, null, 0, "Provenance's crafting solver (raphael-cli.exe) is missing next to it, so missions can't be worked out. Reinstalling Provenance brings it back.", ranksKnown);

        // The ranking is worked out once (up to ThinkSeconds) and then kept until what it depends on changes, so it doesn't
        // jump around while late rotations come in.
        var key = Key(s, planet.Value);
        if (key != frozenKey)
        {
            frozenKey = key;
            frozen = null;
            planningSince = DateTime.UtcNow;
        }
        List<CosmicClassPlan> classes;
        var settled = true;
        if (frozen != null)
        {
            classes = frozen;
            // A crafter frozen without a mission, still being worked out (or just widened): keep at it.
            for (var i = 0; i < classes.Count; i++)
                if (classes[i] is { Pending: true, Ranking.Best: null } stuck)
                    classes[i] = stuck.ClassJob >= 16
                        ? PlanTwoClass(catalog.Cosmic, planet.Value, stuck.ClassJob, ranksKnown)
                        : PlanClass(catalog.Cosmic, planet.Value, stuck.ClassJob, ranksKnown);
        }
        else
        {
            classes = s.Classes.Where(c => c is >= 8 and <= 18).Distinct()
                .Select(c => c >= 16 ? PlanTwoClass(catalog.Cosmic, planet.Value, c, ranksKnown) : PlanClass(catalog.Cosmic, planet.Value, c, ranksKnown)).ToList();
            var elapsed = (DateTime.UtcNow - planningSince).TotalSeconds;
            settled = !classes.Any(c => c.Pending) || elapsed >= ThinkSeconds;
            if (settled) frozen = classes;
        }
        var secondsLeft = settled ? 0 : Math.Max(0, ThinkSeconds - (DateTime.UtcNow - planningSince).TotalSeconds);
        var best = classes.Where(c => c.Ranking.Best != null).Select(c => c.Ranking.Best!.CreditsPerHour).DefaultIfEmpty(0).Max();
        var needed = CreditsNeeded(s, catalog.Cosmic);
        TimeSpan? toGoal = null;
        if (best > 0)
        {
            if (needed > 0) toGoal = TimeSpan.FromHours(needed / best);
            else if (s.Goal == CosmicGoal.UntilStopped && s.TimeLimitMinutes > 0) toGoal = TimeSpan.FromMinutes(s.TimeLimitMinutes);
            if (toGoal is { } t && s.TimeLimitMinutes > 0 && t.TotalMinutes > s.TimeLimitMinutes) toGoal = TimeSpan.FromMinutes(s.TimeLimitMinutes);
        }
        if (withHints) foreach (var c in classes) WithHint(c, needed, s);
        else hints.Clear();
        return new CosmicPlan(planet, classes.OrderByDescending(c => c.Ranking.Best?.CreditsPerHour ?? -1).ToList(), best, toGoal, needed, null, ranksKnown,
            settled, secondsLeft);
    }

    /// <summary>Everything the ranking depends on: planet, crafters, their levels and stats, the food in use.</summary>
    private string Key(CosmicSettings s, Planet planet)
    {
        var profile = plugin.Plans.Profile;
        var jobs = string.Join(";", s.Classes.OrderBy(c => c).Select(c => profile?.Job(c) is { } j ? $"{c}:{j.Level}:{string.Join(",", j.Stats)}" : c.ToString()));
        return $"{planet}|{jobs}|{InUse(plugin)}|{s.Extras}|{Cosmic.Loaded}|{s.Records.Values.Sum(r => r.Total)}";
    }

    /// <summary>Cosmocredits still to earn for the goal (0 when it has no amount, or it's reached).</summary>
    public static int CreditsNeeded(CosmicSettings s, CosmicData data)
    {
        var have = Cosmic.Credits;
        return s.Goal switch
        {
            CosmicGoal.Cosmocredits => Math.Max(0, Math.Min(s.CreditsTarget, Planets.CosmocreditCap) - have),
            CosmicGoal.ShoppingList => Math.Max(0, ShoppingCost(s, data) - have),
            _ => 0,
        };
    }

    /// <summary>What the shopping list costs in Cosmocredits: each item as many times as it's on the list (Drone Modules cost dronebits).</summary>
    public static int ShoppingCost(CosmicSettings s, CosmicData data)
        => s.Shopping.Sum(w => data.Shop.FirstOrDefault(x => x.ItemId == w.ItemId) is { Dronebits: false } item ? w.Count * item.Cost : 0);

    /// <summary>What the shopping list costs in a planet's dronebits.</summary>
    public static int DronebitCost(CosmicSettings s, CosmicData data, Planet planet)
        => s.Shopping.Sum(w => data.Shop.FirstOrDefault(x => x.ItemId == w.ItemId) is { Dronebits: true } item && item.Planet == planet ? w.Count * item.Cost : 0);

    private CosmicClassPlan PlanClass(CosmicData data, Planet planet, uint classJob, bool ranksKnown)
    {
        var name = JobName(classJob);
        var job = plugin.Plans.Profile?.Job(classJob);
        var empty = new MissionRanking(Array.Empty<MissionChoice>(), Array.Empty<SkippedMission>(), false);
        if (GameState.BestGearset(classJob) < 0) return new CosmicClassPlan(classJob, 0, empty, $"No gearset saved for {name}. Save one, then check your character on the Character page.", null);
        if (job is not { Level: > 0 } || job.Stats.Length < 3 || job.Stats[0] <= 0)
            return new CosmicClassPlan(classJob, 0, empty, $"{name}'s stats aren't known yet. Check your character on the Character page.", null);
        if (job.Level < MissionRank.D.Level()) return new CosmicClassPlan(classJob, job.Level, empty, $"{name} is level {job.Level}; stellar missions start at level 10.", null);

        var candidates = Candidates(data, planet, classJob, job.Level, ranksKnown, out var closed);
        var limit = judgeLimit.GetValueOrDefault(classJob, JudgeFirst);
        var judged = candidates.Take(limit).ToList();
        var consumables = InUse(plugin);
        var ranking = CosmicPicker.Rank(judged, m => Judge(m, job.Level, job.Stats, job.Manipulation, consumables), Record);
        var solving = ranking.Skipped.Any(x => x.Reason == StillWorking);
        // Nothing worth taking among the best-paying ones, and every one of them settled: look further down (to a point).
        var widened = false;
        if (ranking.Best == null && !solving && candidates.Count > limit && limit < JudgeMost)
        {
            judgeLimit[classJob] = limit + JudgeFirst;
            widened = true;
        }
        var skipped = ranking.Skipped.Concat(closed).ToList();
        var stillWorking = ranking.Skipped.Count(x => x.Reason == StillWorking);
        return new CosmicClassPlan(classJob, job.Level, ranking with { Skipped = skipped },
            ranking.Best == null && candidates.Count == 0 ? $"{name} has no crafting missions open on {planet.Name()}." : null, null, solving || widened,
            judged.Count - stillWorking, judged.Count);
    }

    /// <summary>Rounds of gathering and crafting a two-class mission is reckoned to take for gold, until runs of it say otherwise.</summary>
    public const int TwoClassRounds = 3;

    /// <summary>
    /// A miner's, botanist's or fisher's plan: for now its two-class missions on Sinus Ardorum (gather or fish the
    /// material, the partner crafter makes the item), judged by the partner crafter's rotation.
    /// </summary>
    private CosmicClassPlan PlanTwoClass(CosmicData data, Planet planet, uint classJob, bool ranksKnown)
    {
        var name = JobName(classJob);
        var s = plugin.Config.Cosmic;
        var empty = new MissionRanking(Array.Empty<MissionChoice>(), Array.Empty<SkippedMission>(), false);
        var job = plugin.Plans.Profile?.Job(classJob);
        if (GameState.BestGearset(classJob) < 0) return new CosmicClassPlan(classJob, 0, empty, $"No gearset saved for {name}. Save one, then check your character on the Character page.", null);
        if (!s.Has(CosmicExtra.TwoClassMissions))
            return new CosmicClassPlan(classJob, job?.Level ?? 0, empty, $"{name} takes two-class missions for now: turn on \"Two-class missions\" under Extras. Its own missions come in a later version.", null);
        if (planet != Planet.SinusArdorum)
            return new CosmicClassPlan(classJob, job?.Level ?? 0, empty, $"{name}'s two-class missions are only on Sinus Ardorum; its own missions come in a later version.", null);
        if (job is not { Level: > 0 }) return new CosmicClassPlan(classJob, 0, empty, $"{name}'s level isn't known yet. Check your character on the Character page.", null);
        var gather = plugin.Plans.Catalog?.Gather;
        var all = data.For(planet, classJob).Where(m => m.Kinds == MissionKind.TwoClass && m.ClassJob == classJob && m.IsCraft
            && (classJob == 18
                ? gather?.Fish(m.Steps[0].IngredientId)?.Spots.Any(id => gather.Spot(id) is { Stands.Count: > 0 }) == true
                : m.Marker != null)).ToList();
        var paired = all.Where(m => s.Classes.Contains(m.SecondClassJob)).ToList();
        if (paired.Count == 0)
            return new CosmicClassPlan(classJob, job.Level, empty,
                $"{name}'s two-class missions pair it with {string.Join(", ", all.Select(m => JobName(m.SecondClassJob)).Distinct())}; tick one of those crafters too.", null);
        var closed = new List<SkippedMission>();
        var open = new List<CosmicMission>();
        foreach (var m in paired)
        {
            var isOpen = ranksKnown
                ? CosmicPicker.RankOpen(m.Rank, job.Level, r => Count(data, classJob, r, false), r => Count(data, classJob, r, true))
                : job.Level >= m.Rank.Level();
            if (isOpen) open.Add(m);
            else closed.Add(new SkippedMission(m, CosmicPicker.RankRequirement(m.Rank) + $" (for {name})"));
        }
        var ranking = CosmicPicker.Rank(open, JudgeTwoClass, Record);
        var stillWorking = ranking.Skipped.Count(x => x.Reason == StillWorking);
        return new CosmicClassPlan(classJob, job.Level, ranking with { Skipped = ranking.Skipped.Concat(closed).ToList() }, null, null,
            stillWorking > 0, open.Count - stillWorking, open.Count);
    }

    /// <summary>A two-class mission judged by its crafter's rotation; its length counts the gathering rounds too.</summary>
    public Judgement? JudgeTwoClass(CosmicMission m)
    {
        var partner = plugin.Plans.Profile?.Job(m.SecondClassJob);
        if (partner is not { Level: > 0 } || partner.Stats.Length < 3 || partner.Stats[0] <= 0)
            return new Judgement(Tier.None, Basis.Estimated, $"{JobName(m.SecondClassJob)}'s stats aren't known; check your character on the Character page", 0);
        var j = Judge(m, partner.Level, partner.Stats, partner.Manipulation, InUse(plugin));
        if (j == null || j.Tier == Tier.None) return j;
        var amount = m.Steps.Count > 0 ? m.Steps[0].IngredientAmount : 1;
        var gather = m.ClassJob == 18 ? amount * 60.0 : 90.0;
        return j with
        {
            Reason = $"{JobName(m.SecondClassJob)}'s {j.Reason}",
            Seconds = (j.Seconds - 20) * TwoClassRounds + gather * TwoClassRounds + 40,
        };
    }

    /// <summary>
    /// The basic crafting missions a class can take on the planet, best-paying first; the ones it can't open yet
    /// come back in <paramref name="closed"/> with the reason (one per rank).
    /// </summary>
    public List<CosmicMission> Candidates(CosmicData data, Planet planet, uint classJob, int level, bool ranksKnown, out List<SkippedMission> closed)
    {
        closed = new List<SkippedMission>();
        var open = new Dictionary<MissionRank, bool>();
        var list = new List<CosmicMission>();
        foreach (var m in data.For(planet, classJob))
        {
            if (!m.IsCraft || m.ClassJob != classJob || m.Kinds != MissionKind.None) continue;
            if (!open.TryGetValue(m.Rank, out var isOpen))
            {
                isOpen = ranksKnown
                    ? CosmicPicker.RankOpen(m.Rank, level, r => Count(data, classJob, r, false), r => Count(data, classJob, r, true))
                    : level >= m.Rank.Level();
                open[m.Rank] = isOpen;
                if (!isOpen) closed.Add(new SkippedMission(m, CosmicPicker.RankRequirement(m.Rank)));
            }
            if (isOpen) list.Add(m);
        }
        return list.OrderByDescending(m => m.Reward.Cosmocredits).ThenByDescending(m => m.Rank).ToList();
    }

    /// <summary>Different missions of a rank a class has done (or golded), on any planet.</summary>
    private int Count(CosmicData data, uint classJob, MissionRank rank, bool gold)
    {
        if (counts.TryGetValue((classJob, rank, gold), out var n)) return n;
        n = data.Missions.Count(m => m.ClassJob == classJob && m.Rank == rank && !m.Is(MissionKind.Critical)
                                     && (gold ? Cosmic.Golded(m.Id) : Cosmic.Completed(m.Id)));
        counts[(classJob, rank, gold)] = n;
        return n;
    }

    public MissionRecord? Record(uint mission) => plugin.Config.Cosmic.Records.GetValueOrDefault(mission);

    /// <summary>What <see cref="CosmicPicker.Rank"/> says of a mission whose rotation isn't worked out yet.</summary>
    public const string StillWorking = "Still working out the best rotation";

    /// <summary>
    /// The crafting food and medicine the loop uses, when the plan's suggestion was taken and some are still in the
    /// bags; null otherwise (and once they run out, so the lower missions are judged again).
    /// </summary>
    public static (uint Food, bool FoodHq, uint Potion, bool PotionHq)? InUse(Plugin plugin)
    {
        if (!plugin.Config.Cosmic.UseConsumables) return null;
        var (food, medicine) = plugin.Config.Consumables.For(Core.Character.Discipline.Hand);
        var f = food.ItemId != 0 && GameState.BagCount(food.ItemId) > 0 ? food.ItemId : 0;
        var p = medicine.ItemId != 0 && GameState.BagCount(medicine.ItemId) > 0 ? medicine.ItemId : 0;
        if (f == 0 && p == 0) return null;
        return (f, food.Hq && GameState.BagCount(food.ItemId, true) > 0, p, medicine.Hq && GameState.BagCount(medicine.ItemId, true) > 0);
    }

    /// <summary>
    /// A craft mission judged with a class's stats: every recipe must finish; the final one's quality decides the tier.
    /// Null while a rotation is still being worked out (it's started here).
    /// </summary>
    public Judgement? Judge(CosmicMission m, int level, int[] stats, bool manipulation, (uint Food, bool FoodHq, uint Potion, bool PotionHq)? consumables)
    {
        var steps = new List<int>(m.Steps.Count);
        RaphaelSolution? final = null;
        foreach (var step in m.Steps)
        {
            var request = Request(m, step, level, stats, manipulation, consumables);
            var task = plugin.Raphael.Solve(request);
            if (!task.IsCompleted) return null;
            if (!task.IsCompletedSuccessfully)
                return new Judgement(Tier.None, Basis.Estimated, "Can't be finished with this gear: " + (task.Exception?.GetBaseException().Message ?? "no rotation"), 0);
            steps.Add(task.Result.Actions.Count);
            final = task.Result;
        }
        if (final == null) return new Judgement(Tier.None, Basis.Estimated, "Not a crafting mission", 0);
        var (tier, basis) = CosmicPicker.PredictCraft(final.FinalQuality, final.MaxQuality, Record(m.Id));
        var reason = final.MaxQuality > 0
            ? $"best rotation reaches {final.FinalQuality:N0} of {final.MaxQuality:N0} quality" + (basis == Basis.Learned ? ", judged from earlier runs" : "")
            : "finishing is all it takes";
        return new Judgement(tier, basis, reason, CosmicPicker.CraftSeconds(m, steps));
    }

    public static RaphaelRequest Request(CosmicMission m, MissionStep step, int level, int[] stats, bool manipulation,
        (uint Food, bool FoodHq, uint Potion, bool PotionHq)? consumables)
        => new(step.RecipeId, level, stats[0], stats[1], stats[2], manipulation, false, null, null,
            m.Action == StellarAction.SteadyHand ? m.ActionCharges : 0,
            consumables?.Food ?? 0, consumables?.FoodHq ?? false, consumables?.Potion ?? 0, consumables?.PotionHq ?? false);

    /// <summary>
    /// The suggestion for a class: judged again with the crafting food and medicine picked under Settings, Crafting
    /// (those in the bags), does a better mission reliably gold? Only while the suggestion isn't taken yet.
    /// </summary>
    private void WithHint(CosmicClassPlan plan, int needed, CosmicSettings s)
    {
        hints.Remove(plan.ClassJob);
        if (s.UseConsumables || plan.Ranking.Best is not { } without || plan.Problem != null) return;
        var (food, medicine) = plugin.Config.Consumables.For(Core.Character.Discipline.Hand);
        var foodId = food.ItemId != 0 && GameState.BagCount(food.ItemId) > 0 ? food.ItemId : 0;
        var potionId = medicine.ItemId != 0 && GameState.BagCount(medicine.ItemId) > 0 ? medicine.ItemId : 0;
        if (foodId == 0 && potionId == 0) return;
        var foodHq = food.Hq && GameState.BagCount(food.ItemId, true) > 0;
        var potionHq = medicine.Hq && GameState.BagCount(medicine.ItemId, true) > 0;
        var job = plugin.Plans.Profile?.Job(plan.ClassJob);
        if (job == null || plugin.Plans.Catalog is not { } catalog || TargetPlanet(s) is not { } planet) return;
        var candidates = Candidates(catalog.Cosmic, planet, plan.ClassJob, job.Level, Cosmic.Loaded, out _)
            .Take(judgeLimit.GetValueOrDefault(plan.ClassJob, JudgeFirst)).ToList();
        var with = CosmicPicker.Rank(candidates, m => Judge(m, job.Level, job.Stats, job.Manipulation, (foodId, foodHq, potionId, potionHq)), Record).Best;
        if (with == null || with.CreditsPerHour <= without.CreditsPerHour * 1.05) return;

        var minutesWith = needed > 0 ? needed / with.CreditsPerHour * 60 : s.TimeLimitMinutes;
        var minutesWithout = needed > 0 ? needed / without.CreditsPerHour * 60 : (double?)null;
        var lasting = minutesWith > 0 ? minutesWith : 60;
        var hint = new ConsumableHint(plan.ClassJob, foodId, foodHq, foodId == 0 ? 0 : (int)Math.Ceiling(lasting / 30.0),
            potionId, potionHq, potionId == 0 ? 0 : (int)Math.Ceiling(lasting / 15.0), without, with,
            minutesWithout is { } mw ? mw - minutesWith : null, with.CreditsPerHour - without.CreditsPerHour);
        hints[plan.ClassJob] = hint;
    }

    private readonly Dictionary<uint, ConsumableHint> hints = new();

    /// <summary>The food and medicine suggestion for a class, from the last plan.</summary>
    public ConsumableHint? Hint(uint classJob) => plugin.Config.Cosmic.UseConsumables ? null : hints.GetValueOrDefault(classJob);

    public static string JobName(uint classJob)
    {
        var name = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>().GetRowOrDefault(classJob)?.Name.ExtractText() ?? "";
        return name.Length == 0 ? Core.Character.ClassJobs.Abbreviation(classJob)
            : string.Join(' ', name.Split(' ').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
    }
}
