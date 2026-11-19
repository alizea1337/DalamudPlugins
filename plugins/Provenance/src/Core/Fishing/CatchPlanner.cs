using System;
using System.Collections.Generic;
using System.Linq;

namespace Provenance.Core.Fishing;

/// <summary>What the catch planner works from.</summary>
/// <param name="Target">The fish the order wants.</param>
/// <param name="Bait">The tackle the run casts with for the target (its own bait, or Versatile Lure); 0: the fish's own.</param>
/// <param name="SpotFish">Every fish living at the fishing hole (empty when unknown): predators must be among them, and a tug no other fish there has can be hooked at any time.</param>
/// <param name="FishData">The fish database (chain fish, predators, the slap fish).</param>
/// <param name="Learned">Fisher actions the character has learned (level and unlock quest).</param>
/// <param name="MaxGp">The Fisher's maximum GP.</param>
/// <param name="WeatherTable">The spot zone's weather table, for the target's (and predators') windows.</param>
/// <param name="UseBuffs">Chum, Patience, Prize Catch, Makeshift Bait, Surface Slap and Identical Cast when they help.</param>
/// <param name="UseCordials">Cordials between casts for the GP the buffs need.</param>
public sealed record CatchRequest(
    FishInfo Target,
    uint Bait,
    IReadOnlyList<FishInfo> SpotFish,
    Func<uint, FishInfo?> FishData,
    IReadOnlySet<uint> Learned,
    int MaxGp,
    IReadOnlyList<(uint Weather, int Rate)> WeatherTable,
    bool UseBuffs = true,
    bool UseCordials = true);

/// <summary>
/// Builds a <see cref="CatchPlan"/> from our fish data. Ports the intent of AutoHook's FishSolver and
/// SolverPresetBuilder (fish-solver §5–§7) with the defects of §7.9 fixed and §11's recommendations:
/// <list type="bullet">
/// <item>Per cast context only the wanted fish's tug is hooked, inside its bite window [min, max + 1];
/// every other bite is Rested. A fish without bite data is hooked by tug at any time (D4), and a tug no
/// other fish at the hole has is hooked at any time.</item>
/// <item>Every chain link is wired, for the target and for predators reached by mooching (D7, D9, D13);
/// fish sharing a tug in one context keep their own windows (D10).</item>
/// <item>Predators are caught on their own tackle, never on a fish id (D8); Identical Cast goes on the
/// next-to-last catch of a predator caught on tackle.</item>
/// <item>Lure fish get their lure on the cast that hooks them, up to three, Rest when it fails (D19).</item>
/// <item>Surface Slap goes on GatherBuddy Reborn's curated slap fish only (no pool data, D1/D17).</item>
/// <item>Long intuition keeps Chum (D20); Chum's shorter bites are handled by the decider (D5).</item>
/// </list>
/// Ported from AutoHook (BSD-3, Puni.sh). Pure: no game access.
/// </summary>
public static class CatchPlanner
{
    /// <summary>Fisher's Intuition length assumed when the data has none.</summary>
    public const int DefaultIntuitionSeconds = 60;

    /// <summary>
    /// A target that opens later than this beyond the predator lead isn't waited for (the session ends as
    /// the window closed). Generous: the run starts sessions ahead of a window by its own lead time.
    /// </summary>
    public static readonly TimeSpan EarlyWait = TimeSpan.FromMinutes(60);

    /// <summary>The catch plan for a target at a fishing hole; <see cref="CatchPlan.Problem"/> says why it can't work.</summary>
    public static CatchPlan Plan(CatchRequest request)
    {
        var target = request.Target;
        CatchProblem? problem = null;
        void Fail(CatchProblem p) => problem ??= p;

        if (target.Spear) Fail(new CatchProblem(CatchProblemKind.Spear, target.ItemId));
        if (target.Ocean) Fail(new CatchProblem(CatchProblemKind.Ocean, target.ItemId));

        var targetRoute = BuildRoute(0, target, request.Bait != 0 ? request.Bait : target.Bait, 0, request, Fail);
        if (targetRoute.Bait == 0) Fail(new CatchProblem(CatchProblemKind.NoBait, target.ItemId));

        var predators = new List<CatchRoute>();
        foreach (var (id, count) in target.Predators)
        {
            if (count <= 0) continue;
            if (request.FishData(id) is not { } fish)
            {
                Fail(new CatchProblem(CatchProblemKind.UnknownFish, id));
                continue;
            }
            if (fish.NeedsIntuition) Fail(new CatchProblem(CatchProblemKind.NestedIntuition, id));
            predators.Add(BuildRoute(predators.Count + 1, fish, fish.Bait, count, request, Fail));
        }
        var routes = new List<CatchRoute> { targetRoute };
        routes.AddRange(predators);
        var allFish = routes.SelectMany(r => r.Chain.Append(r.Fish)).ToList();

        // Actions the plan can't do without.
        bool Has(uint action) => request.Learned.Contains(action);
        if (routes.Any(r => r.Mooches) && !Has(FishingIds.Mooch)) Fail(new CatchProblem(CatchProblemKind.MissingAction, target.ItemId, FishingIds.Mooch));
        if (routes.Any(r => r.Snag) && !Has(FishingIds.Snagging)) Fail(new CatchProblem(CatchProblemKind.MissingAction, target.ItemId, FishingIds.Snagging));
        foreach (var f in allFish)
        {
            var lure = LureOf(f);
            if (lure == LureKind.Ambitious && !Has(FishingIds.AmbitiousLure)) Fail(new CatchProblem(CatchProblemKind.MissingAction, f.ItemId, FishingIds.AmbitiousLure));
            if (lure == LureKind.Modest && !Has(FishingIds.ModestLure)) Fail(new CatchProblem(CatchProblemKind.MissingAction, f.ItemId, FishingIds.ModestLure));
        }

        // Fisher's Intuition only builds from predators caught at this very hole. (Chain fish aren't checked:
        // the data leaves some mooch fish out of spots where they do bite.)
        if (request.SpotFish.Count > 0)
            foreach (var p in predators)
                if (request.SpotFish.All(s => s.ItemId != p.Fish.ItemId))
                    Fail(new CatchProblem(CatchProblemKind.NotAtSpot, p.Fish.ItemId));

        // Surface Slap: GatherBuddy Reborn's curated fish, hooked on the target's own tackle casts so it can be slapped.
        uint slap = 0;
        if (request.UseBuffs && Has(FishingIds.SurfaceSlap) && target.SurfaceSlap != 0 && target.SurfaceSlap != target.ItemId && !targetRoute.Mooches
            && request.FishData(target.SurfaceSlap) is { Tug: not Tug.Unknown } slapFish)
            slap = slapFish.ItemId;

        var rules = BuildRules(request, targetRoute, predators, slap);
        var buffs = BuildBuffs(request, routes, predators, rules, slap);
        var intuitionSeconds = target.IntuitionSeconds > 0 ? target.IntuitionSeconds : DefaultIntuitionSeconds;
        var lead = PrepLeadFor(predators, intuitionSeconds);
        var predatorWindows = new Dictionary<uint, CatchWindow>();
        foreach (var p in predators)
            if (p.Fish.Restricted) predatorWindows[p.Fish.ItemId] = new CatchWindow(p.Fish, request.WeatherTable);

        return new CatchPlan(target, targetRoute, predators, predators.Count > 0 ? intuitionSeconds : 0, rules, buffs,
            new CatchWindow(target, request.WeatherTable), predatorWindows, lead, lead + EarlyWait, problem);
    }

    /// <summary>
    /// A rough time to catch a fish's predators (for Fisher's Intuition): two casts per catch, each its
    /// average bite plus 10 seconds of casting and reeling. Zero for fish without predators.
    /// </summary>
    public static TimeSpan PredatorTime(FishInfo target, Func<uint, FishInfo?> fishData)
    {
        double seconds = 0;
        foreach (var (id, count) in target.Predators)
        {
            if (count <= 0 || fishData(id) is not { } fish) continue;
            var perCatch = SecondsPerCatch(fish);
            foreach (var link in fish.Mooch)
                if (fishData(link) is { } l) perCatch += SecondsPerCatch(l);
            seconds += count * perCatch;
        }
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>The lure a lure fish only comes to (the data's "Ambitious" or "Modest").</summary>
    public static LureKind LureOf(FishInfo fish) => fish.Lure switch
    {
        "Ambitious" => LureKind.Ambitious,
        "Modest" => LureKind.Modest,
        _ => LureKind.None,
    };

    /// <summary>The bite window [min, max + 1] in seconds; null when the data has none (a max of 0 is no data).</summary>
    public static (double? From, double? Until) BiteWindow(FishInfo fish)
        => fish.BiteSeconds is { } b && b.Max > 0 && b.Min >= 0 && b.Max >= b.Min ? (b.Min, b.Max + 1.0) : (null, null);

    private static CatchRoute BuildRoute(int index, FishInfo fish, uint bait, int count, CatchRequest request, Action<CatchProblem> fail)
    {
        var chain = new List<FishInfo>();
        foreach (var id in fish.Mooch)
        {
            if (request.FishData(id) is { } link) chain.Add(link);
            else fail(new CatchProblem(CatchProblemKind.UnknownFish, id));
        }
        var snag = fish.Snag || chain.Any(c => c.Snag);
        return new CatchRoute(index, fish, bait, chain, count, snag, LureOf(fish));
    }

    private static Dictionary<CastContext, IReadOnlyList<HookRule>> BuildRules(CatchRequest request, CatchRoute target, List<CatchRoute> predators, uint slap)
    {
        var rules = new Dictionary<CastContext, List<HookRule>>();
        void Add(CastContext context, HookRule rule)
        {
            if (!rules.TryGetValue(context, out var list)) rules[context] = list = new List<HookRule>();
            if (!list.Any(r => r.FishId == rule.FishId && r.Route == rule.Route)) list.Add(rule);
        }

        HookRule Rule(FishInfo fish, CatchRole role, int route)
        {
            var (from, until) = BiteWindow(fish);
            return new HookRule(fish.ItemId, role, route, fish.Tug, fish.HookSet, from, until, OnlyOneWithTug(fish, request.SpotFish), LureOf(fish));
        }

        void AddRoute(CatchRoute route, bool intuition)
        {
            var finalRole = route.Index == 0 ? CatchRole.Target : CatchRole.Predator;
            Add(CastContext.Tackle(intuition), Rule(route.First, route.Mooches ? CatchRole.Chain : finalRole, route.Index));
            for (var i = 0; i < route.Chain.Count; i++)
            {
                var last = i + 1 == route.Chain.Count;
                Add(new CastContext(route.Chain[i].ItemId, intuition), Rule(last ? route.Fish : route.Chain[i + 1], last ? finalRole : CatchRole.Chain, route.Index));
            }
        }

        // The target's route first, so its rules win a shared tug. A target that needs Fisher's Intuition
        // only bites under it; one that doesn't is fished the same with or without.
        var targetIntuition = predators.Count > 0 ? new[] { true } : new[] { false, true };
        foreach (var intuition in targetIntuition)
        {
            AddRoute(target, intuition);
            if (slap != 0 && request.FishData(slap) is { } slapFish)
                Add(CastContext.Tackle(intuition), Rule(slapFish, CatchRole.Slap, 0));
        }
        foreach (var p in predators) AddRoute(p, false);

        return rules.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<HookRule>)kv.Value);
    }

    /// <summary>No other fish at the hole pulls with this tug (every other one's tug is known and different).</summary>
    private static bool OnlyOneWithTug(FishInfo fish, IReadOnlyList<FishInfo> spotFish)
    {
        if (spotFish.Count == 0 || fish.Tug == Tug.Unknown) return false;
        foreach (var other in spotFish)
        {
            if (other.ItemId == fish.ItemId || other.Spear) continue;
            if (other.Tug == Tug.Unknown || other.Tug == fish.Tug) return false;
        }
        return true;
    }

    private static BuffPolicy BuildBuffs(CatchRequest request, List<CatchRoute> routes, List<CatchRoute> predators,
        Dictionary<CastContext, IReadOnlyList<HookRule>> rules, uint slap)
    {
        bool Has(uint action) => request.Learned.Contains(action);
        var use = request.UseBuffs;
        var target = routes[0];
        var anyMooch = routes.Any(r => r.Mooches);

        var patience = new List<uint>();
        if (use && anyMooch)
        {
            if (Has(FishingIds.PatienceII)) patience.Add(FishingIds.PatienceII);
            if (Has(FishingIds.Patience)) patience.Add(FishingIds.Patience);
        }
        var prize = use && anyMooch && Has(FishingIds.PrizeCatch);
        var makeshift = use && anyMooch && Has(FishingIds.MakeshiftBait);
        var chum = use && Has(FishingIds.Chum);
        // Identical Cast "has no effect on some fish" (big fish); a mooched fish can't come from a tackle cast.
        var icPredators = use && Has(FishingIds.IdenticalCast) && predators.Any(p => !p.Mooches);
        var icTarget = use && Has(FishingIds.IdenticalCast) && !target.Mooches && !target.Fish.Big;
        var moochII = anyMooch && Has(FishingIds.MoochII);
        var anyLure = rules.Values.Any(l => l.Any(r => r.Lure != LureKind.None));

        // The GP worth having before a cast: the dearest buff the plan uses, plus the GP kept for hooking
        // (a Precision or Powerful Hookset under Patience) and for the lures of a cast.
        var hookset = patience.Count > 0 && routes.Any(r => r.Mooches && r.NeedsHookset) ? FishingIds.HooksetGp : 0;
        var lures = anyLure ? FishingIds.LureGp(0) + FishingIds.LureGp(1) + FishingIds.LureGp(2) : 0;
        var want = lures;
        if (patience.Count > 0) want = Math.Max(want, (patience[0] == FishingIds.PatienceII ? FishingIds.PatienceIIGp : FishingIds.PatienceGp) + hookset + lures);
        if (icPredators || icTarget) want = Math.Max(want, FishingIds.IdenticalCastGp + lures);
        if (slap != 0) want = Math.Max(want, FishingIds.SurfaceSlapGp + lures);
        if (moochII) want = Math.Max(want, FishingIds.MoochIIGp + hookset);
        if (request.MaxGp > 0) want = Math.Min(want, request.MaxGp);

        var thaliak = Has(FishingIds.ThaliaksFavor) && (want > 0 || chum);
        var cordials = request.UseCordials && want > 0;
        return new BuffPolicy(patience, prize, makeshift, chum, slap, icPredators, icTarget, thaliak, cordials, want, moochII);
    }

    /// <summary>
    /// How long before the target's window opens to start on the predators: as long as catching them is
    /// expected to take (so Fisher's Intuition comes as the window opens), but never so early that the
    /// intuition would run out before the window opens if the predators came quickly.
    /// </summary>
    private static TimeSpan PrepLeadFor(List<CatchRoute> predators, int intuitionSeconds)
    {
        if (predators.Count == 0) return TimeSpan.Zero;
        var margin = Math.Min(30.0, intuitionSeconds / 4.0);
        var expected = predators.Sum(p => p.Count * (SecondsPerCatch(p.Fish) + p.Chain.Sum(SecondsPerCatch)));
        return TimeSpan.FromSeconds(Math.Max(0, Math.Min(expected, intuitionSeconds - margin)));
    }

    private static double SecondsPerCatch(FishInfo fish)
    {
        var (from, until) = BiteWindow(fish);
        var average = from is { } f && until is { } u ? (f + u - 1) / 2 : 20;
        return 2 * (average + 10);
    }
}
