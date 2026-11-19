using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Fishing;
using Xunit;
using static Provenance.Tests.FishCases;

namespace Provenance.Tests;

public class FishCatchPlannerTests
{
    private static HookRule Single(CatchPlan plan, CastContext context) => Assert.Single(plan.RulesFor(context));

    [Fact]
    public void A_straight_fish_hooks_only_its_tug_inside_its_window()
    {
        var plan = Plan(Straight);
        Assert.Null(plan.Problem);
        Assert.Equal(Lugworm, plan.TargetRoute.Bait);
        Assert.Empty(plan.TargetRoute.Chain);
        Assert.False(plan.NeedsIntuition);
        foreach (var intuition in new[] { false, true })
        {
            var rule = Single(plan, CastContext.Tackle(intuition));
            Assert.Equal(new HookRule(100, CatchRole.Target, 0, Tug.Weak, HookSet.Precise, 5, 13, false, LureKind.None), rule);
        }
        Assert.Equal(2, plan.Rules.Count); // no mooch contexts
    }

    [Fact]
    public void The_run_s_bait_replaces_the_fish_s_own()
        => Assert.Equal(Versatile, Plan(Straight, bait: Versatile).TargetRoute.Bait);

    [Fact]
    public void A_one_link_chain_hooks_the_link_on_tackle_and_the_target_on_the_mooch()
    {
        var plan = Plan(OneLink);
        Assert.Equal(new[] { 200u }, plan.TargetRoute.Chain.Select(f => f.ItemId));
        Assert.Equal(Lugworm, plan.TargetRoute.Bait);
        var tackle = Single(plan, CastContext.Tackle(false));
        Assert.Equal((200u, CatchRole.Chain, Tug.Weak, 2.0, 21.0), (tackle.FishId, tackle.Role, tackle.Tug, tackle.From!.Value, tackle.Until!.Value));
        var mooch = Single(plan, new CastContext(200, false));
        Assert.Equal((211u, CatchRole.Target, Tug.Legendary, HookSet.Powerful, 12.0, 26.0), (mooch.FishId, mooch.Role, mooch.Tug, mooch.HookSet, mooch.From!.Value, mooch.Until!.Value));
    }

    [Fact]
    public void Every_link_of_a_three_link_chain_is_wired()
    {
        // Our data's order: caught on the tackle first, the direct predecessor last.
        var plan = Plan(Goose);
        Assert.Equal(200u, Single(plan, CastContext.Tackle(true)).FishId);
        Assert.Equal((201u, CatchRole.Chain), (Single(plan, new CastContext(200, true)).FishId, Single(plan, new CastContext(200, true)).Role));
        Assert.Equal((202u, CatchRole.Chain), (Single(plan, new CastContext(201, true)).FishId, Single(plan, new CastContext(201, true)).Role));
        Assert.Equal((210u, CatchRole.Target), (Single(plan, new CastContext(202, true)).FishId, Single(plan, new CastContext(202, true)).Role));
        Assert.Equal(202u, plan.TargetRoute.After(201)!.ItemId);
        Assert.Equal(210u, plan.TargetRoute.After(202)!.ItemId);
        Assert.Null(plan.TargetRoute.After(210));
    }

    [Fact]
    public void Predators_come_on_their_own_tackle_without_intuition_and_the_target_only_under_it()
    {
        var plan = Plan(Big);
        Assert.True(plan.NeedsIntuition);
        Assert.Equal(120, plan.IntuitionSeconds);
        Assert.Equal(new[] { (300u, 3, Shrimp), (301u, 2, Fly) }, plan.Predators.Select(p => (p.Fish.ItemId, p.Count, p.Bait)));
        var without = plan.RulesFor(CastContext.Tackle(false));
        Assert.Equal(new[] { (300u, CatchRole.Predator, 1), (301u, CatchRole.Predator, 2) }, without.Select(r => (r.FishId, r.Role, r.Route)));
        var with = Single(plan, CastContext.Tackle(true));
        Assert.Equal((310u, CatchRole.Target, Tug.Legendary), (with.FishId, with.Role, with.Tug));
    }

    [Fact]
    public void A_predator_reached_by_mooching_is_wired_on_its_chain_and_mooched_on_to_the_target_under_intuition()
    {
        var plan = Plan(Top);
        // No intuition: Lugworm hooks 400, mooching 400 hooks the predator 401.
        Assert.Equal((400u, CatchRole.Chain, 1), (Single(plan, CastContext.Tackle(false)).FishId, Single(plan, CastContext.Tackle(false)).Role, Single(plan, CastContext.Tackle(false)).Route));
        Assert.Equal((401u, CatchRole.Predator), (Single(plan, new CastContext(400, false)).FishId, Single(plan, new CastContext(400, false)).Role));
        Assert.Empty(plan.RulesFor(new CastContext(401, false)));
        // Under intuition: the same links lead on to the target.
        Assert.Equal((401u, CatchRole.Chain, 0), (Single(plan, new CastContext(400, true)).FishId, Single(plan, new CastContext(400, true)).Role, Single(plan, new CastContext(400, true)).Route));
        Assert.Equal((410u, CatchRole.Target), (Single(plan, new CastContext(401, true)).FishId, Single(plan, new CastContext(401, true)).Role));
    }

    [Theory]
    [InlineData(0f, 0f)]   // no data in the source: hooked at any time (AutoHook's [0, 1] never hooked it)
    [InlineData(9f, 4f)]   // nonsense
    public void Missing_bite_data_means_any_time(float min, float max)
    {
        var fish = Fish(700, bite: (min, max));
        var rule = Single(Plan(fish, data: id => id == 700 ? fish : null), CastContext.Tackle(false));
        Assert.Null(rule.From);
        Assert.Null(rule.Until);
        Assert.Null(Single(Plan(Fish(701), data: id => Fish(701)), CastContext.Tackle(false)).Until);
    }

    [Fact]
    public void A_tug_no_other_fish_at_the_hole_has_is_hooked_at_any_time()
    {
        var others = new[] { Fish(800, Tug.Strong), Fish(801, Tug.Legendary) };
        Assert.True(Single(Plan(Straight, spot: others.Append(Straight).ToList()), CastContext.Tackle(false)).OnlyOneWithTug);
        // Another "!" fish, or one whose tug isn't known, rules it out.
        Assert.False(Single(Plan(Straight, spot: new[] { Straight, Fish(802, Tug.Weak) }), CastContext.Tackle(false)).OnlyOneWithTug);
        Assert.False(Single(Plan(Straight, spot: new[] { Straight, Fish(803, Tug.Unknown) }), CastContext.Tackle(false)).OnlyOneWithTug);
        Assert.False(Single(Plan(Straight), CastContext.Tackle(false)).OnlyOneWithTug);
    }

    [Fact]
    public void Lure_and_snag_fish_carry_their_needs_and_fail_without_the_action()
    {
        var lure = Plan(LureFish);
        Assert.Equal(LureKind.Ambitious, Single(lure, CastContext.Tackle(false)).Lure);
        Assert.Equal(LureKind.Ambitious, lure.TargetRoute.Lure);
        Assert.Equal(60, Plan(LureFish, buffs: false).Buffs.GpTarget);  // three lures (10 + 20 + 30)
        Assert.Equal(410, lure.Buffs.GpTarget);                          // Identical Cast, keeping the three lures
        Assert.Equal(new CatchProblem(CatchProblemKind.MissingAction, 500, FishingIds.AmbitiousLure), Plan(LureFish, Without(FishingIds.AmbitiousLure)).Problem);

        Assert.True(Plan(SnagFish).TargetRoute.Snag);
        Assert.Equal(new CatchProblem(CatchProblemKind.MissingAction, 510, FishingIds.Snagging), Plan(SnagFish, Without(FishingIds.Snagging)).Problem);
        Assert.Equal(new CatchProblem(CatchProblemKind.MissingAction, 210, FishingIds.Mooch), Plan(Goose, Without(FishingIds.Mooch)).Problem);
    }

    [Fact]
    public void Problems_are_reported()
    {
        Assert.Equal(CatchProblemKind.Spear, Plan(Straight with { Spear = true }).Problem?.Kind);
        Assert.Equal(CatchProblemKind.Ocean, Plan(Straight with { Ocean = true }).Problem?.Kind);
        Assert.Equal(new CatchProblem(CatchProblemKind.NoBait, 100), Plan(Straight with { Bait = 0 }).Problem);
        Assert.Equal(new CatchProblem(CatchProblemKind.UnknownFish, 999), Plan(Straight with { Mooch = new uint[] { 999 } }).Problem);
        Assert.Equal(new CatchProblem(CatchProblemKind.UnknownFish, 998), Plan(Straight with { Predators = new (uint, int)[] { (998, 2) } }).Problem);
        // A predator that doesn't live at the hole can't build intuition there.
        Assert.Equal(new CatchProblem(CatchProblemKind.NotAtSpot, 301), Plan(Big, spot: new[] { Big, PredA }).Problem);
        Assert.Null(Plan(Big, spot: new[] { Big, PredA, PredB }).Problem);
        var nested = Fish(320, predators: new (uint, int)[] { (310, 1) });
        Assert.Equal(new CatchProblem(CatchProblemKind.NestedIntuition, 310), Plan(nested).Problem);
    }

    [Fact]
    public void Mooch_routes_get_patience_and_keep_gp_for_the_hookset()
    {
        var plan = Plan(Goose);
        Assert.Equal(new[] { FishingIds.PatienceII, FishingIds.Patience }, plan.Buffs.Patience);
        Assert.True(plan.Buffs.MakeshiftBait);
        Assert.True(plan.Buffs.PrizeCatch);
        Assert.True(plan.Buffs.MoochII);
        Assert.Equal(610, plan.Buffs.GpTarget); // Patience II + a Powerful Hookset
        Assert.Equal(400, Plan(Goose, maxGp: 400).Buffs.GpTarget);
        Assert.Equal(new[] { FishingIds.Patience }, Plan(Goose, Without(FishingIds.PatienceII)).Buffs.Patience);
        // A straight fish doesn't need large fish.
        Assert.Empty(Plan(Straight).Buffs.Patience);
        Assert.False(Plan(Straight).Buffs.MakeshiftBait);
    }

    [Fact]
    public void Buffs_off_leaves_only_what_the_fish_needs()
    {
        var plan = Plan(Goose, buffs: false, cordials: false);
        Assert.Empty(plan.Buffs.Patience);
        Assert.False(plan.Buffs.Chum || plan.Buffs.PrizeCatch || plan.Buffs.MakeshiftBait || plan.Buffs.IdenticalCastTarget || plan.Buffs.Cordials);
        Assert.True(plan.Buffs.MoochII); // mooching an average-sized link is part of the chain
        Assert.Equal(0u, Plan(Bombard, buffs: false).Buffs.SlapFish);
    }

    [Fact]
    public void Surface_slap_goes_on_the_curated_fish_hooked_on_the_target_s_tackle()
    {
        var plan = Plan(Bombard);
        Assert.Equal(600u, plan.Buffs.SlapFish);
        var rules = plan.RulesFor(CastContext.Tackle(false));
        Assert.Equal(new[] { (610u, CatchRole.Target), (600u, CatchRole.Slap) }, rules.Select(r => (r.FishId, r.Role)));
        Assert.Equal((2.0, 33.0), (rules[1].From!.Value, rules[1].Until!.Value));
        Assert.Equal(0u, Plan(Bombard, Without(FishingIds.SurfaceSlap)).Buffs.SlapFish);
        Assert.Single(Plan(Bombard, Without(FishingIds.SurfaceSlap)).RulesFor(CastContext.Tackle(false)));
    }

    [Fact]
    public void Identical_cast_goes_on_tackle_predators_and_small_straight_targets()
    {
        Assert.True(Plan(Big).Buffs.IdenticalCastPredators);
        Assert.False(Plan(Big).Buffs.IdenticalCastTarget);       // a big fish
        Assert.True(Plan(Straight).Buffs.IdenticalCastTarget);
        Assert.False(Plan(Goose).Buffs.IdenticalCastTarget);     // mooched, not from a tackle cast
        Assert.False(Plan(Top).Buffs.IdenticalCastPredators);    // its predator is mooched
        Assert.False(Plan(Straight, Without(FishingIds.IdenticalCast)).Buffs.IdenticalCastTarget);
    }

    [Fact]
    public void Predators_start_as_late_as_their_expected_time_but_inside_the_intuition()
    {
        // 3 × 2 × (15 + 10) + 2 × 2 × (9 + 10) = 226 s expected; the intuition lasts 120 s (30 s margin).
        Assert.Equal(TimeSpan.FromSeconds(90), Plan(Big).PrepLead);
        Assert.Equal(Plan(Big).PrepLead + CatchPlanner.EarlyWait, Plan(Big).EarlyLimit);
        var quick = Fish(330, predators: new (uint, int)[] { (300, 1) }, intuition: 600, bait: Spoon);
        Assert.Equal(TimeSpan.FromSeconds(2 * (15 + 10)), Plan(quick).PrepLead);
        Assert.Equal(TimeSpan.Zero, Plan(Straight).PrepLead);
        Assert.Equal(TimeSpan.FromSeconds(3 * 50 + 2 * 38), CatchPlanner.PredatorTime(Big, Data));
    }

    [Fact]
    public void The_window_follows_the_fish_and_the_zone_weather()
    {
        const long hour = 175_000;
        var timed = Straight with { Window = new Uptime(2 * hour, 22 * hour, 10 * hour) }; // 10:00–12:00
        var plan = Plan(timed);
        var closed = plan.Window.At(DateTimeOffset.FromUnixTimeMilliseconds(8 * hour));
        Assert.False(closed.Open);
        Assert.Equal(TimeSpan.FromMilliseconds(2 * hour), closed.OpensIn);
        var open = plan.Window.At(DateTimeOffset.FromUnixTimeMilliseconds(11 * hour));
        Assert.True(open.Open);
        Assert.Equal(TimeSpan.FromMilliseconds(hour), open.Left);
        Assert.Equal(CatchWindowState.Always, Plan(Straight).Window.At(DateTimeOffset.UtcNow));
    }
}

/// <summary>Fish, plans and rod snapshots shared by the catch planner and decider tests.</summary>
internal static class FishCases
{
    public const uint Lugworm = 2585, Shrimp = 2600, Fly = 2601, Spoon = 2602, Versatile = 29717;

    public static readonly IReadOnlySet<uint> AllLearned = new HashSet<uint>
    {
        FishingIds.Mooch, FishingIds.MoochII, FishingIds.Patience, FishingIds.PatienceII, FishingIds.Chum, FishingIds.Snagging,
        FishingIds.Collect, FishingIds.PrecisionHookset, FishingIds.PowerfulHookset, FishingIds.SurfaceSlap, FishingIds.IdenticalCast,
        FishingIds.PrizeCatch, FishingIds.ThaliaksFavor, FishingIds.MakeshiftBait, FishingIds.AmbitiousLure, FishingIds.ModestLure,
    };

    public static FishInfo Fish(uint id, Tug tug = Tug.Weak, HookSet hook = HookSet.Precise, (float, float)? bite = null, uint bait = Lugworm,
        uint[]? mooch = null, (uint, int)[]? predators = null, int intuition = 0, Uptime? window = null, uint[]? weather = null,
        bool snag = false, string lure = "None", uint slap = 0, bool big = false)
        => new(id, false, bait, mooch ?? Array.Empty<uint>(), predators ?? Array.Empty<(uint, int)>(), intuition, window,
            weather ?? Array.Empty<uint>(), Array.Empty<uint>(), hook, tug, snag, lure, SpearSize.Unknown, SpearSpeed.Unknown,
            slap, false, big, new uint[] { 1 }, bite);

    // A straight fish: "!" Precision, bites 5-12 s (window 5-13).
    public static readonly FishInfo Straight = Fish(100, Tug.Weak, HookSet.Precise, (5, 12));

    // A three-link chain like Goosefish: Lugworm → 200 (!) → 201 (!!) → 202 (!!) → 210 (!!).
    public static readonly FishInfo Goby = Fish(200, Tug.Weak, HookSet.Precise, (2, 20));
    public static readonly FishInfo Wahoo = Fish(201, Tug.Strong, HookSet.Powerful, (4, 30));
    public static readonly FishInfo Link3 = Fish(202, Tug.Strong, HookSet.Powerful, (6, 18));
    public static readonly FishInfo Goose = Fish(210, Tug.Strong, HookSet.Powerful, (10, 22), mooch: new uint[] { 200, 201, 202 });
    public static readonly FishInfo OneLink = Fish(211, Tug.Legendary, HookSet.Powerful, (12, 25), mooch: new uint[] { 200 });

    // An intuition fish: 3 of 300 (on Shrimp) and 2 of 301 (on Fly), then 310 (!!!) on Spoon for 120 s.
    public static readonly FishInfo PredA = Fish(300, Tug.Strong, HookSet.Powerful, (5, 25), bait: Shrimp);
    public static readonly FishInfo PredB = Fish(301, Tug.Weak, HookSet.Precise, (3, 15), bait: Fly);
    public static readonly FishInfo Big = Fish(310, Tug.Legendary, HookSet.Powerful, (15, 30), bait: Spoon,
        predators: new (uint, int)[] { (300, 3), (301, 2) }, intuition: 120, big: true);

    // Like 8764: 6 of 401 (Lugworm → 400 → mooch → 401) for intuition, then 401 → mooch → 410.
    public static readonly FishInfo Feeder = Fish(400, Tug.Weak, HookSet.Precise, (2, 20));
    public static readonly FishInfo Middle = Fish(401, Tug.Strong, HookSet.Powerful, (8, 30), mooch: new uint[] { 400 });
    public static readonly FishInfo Top = Fish(410, Tug.Legendary, HookSet.Powerful, (13, 31), mooch: new uint[] { 400, 401 },
        predators: new (uint, int)[] { (401, 6) }, intuition: 120, big: true);

    public static readonly FishInfo LureFish = Fish(500, Tug.Strong, HookSet.Powerful, (10, 40), lure: "Ambitious");
    public static readonly FishInfo SnagFish = Fish(510, Tug.Weak, HookSet.Precise, (4, 14), snag: true);

    // Bombardfish-like: slap 600 (!!) away from 610 (!!!).
    public static readonly FishInfo Balloon = Fish(600, Tug.Strong, HookSet.Powerful, (2, 32), bait: Spoon);
    public static readonly FishInfo Bombard = Fish(610, Tug.Legendary, HookSet.Powerful, (6, 16), bait: Shrimp, slap: 600);

    public static readonly FishInfo[] All =
    {
        Straight, Goby, Wahoo, Link3, Goose, OneLink, PredA, PredB, Big, Feeder, Middle, Top, LureFish, SnagFish, Balloon, Bombard,
    };

    public static FishInfo? Data(uint id) => All.FirstOrDefault(f => f.ItemId == id);

    public static CatchPlan Plan(FishInfo target, IReadOnlySet<uint>? learned = null, int maxGp = 900, bool buffs = true, bool cordials = true,
        IReadOnlyList<FishInfo>? spot = null, IReadOnlyList<(uint, int)>? table = null, uint bait = 0, Func<uint, FishInfo?>? data = null)
        => CatchPlanner.Plan(new CatchRequest(target, bait, spot ?? Array.Empty<FishInfo>(), data ?? Data, learned ?? AllLearned, maxGp,
            table ?? Array.Empty<(uint, int)>(), buffs, cordials));

    public static IReadOnlySet<uint> Without(params uint[] actions) => AllLearned.Where(a => !actions.Contains(a)).ToHashSet();

    /// <summary>Between casts, rod out, Lugworm on, plenty of everything, every action usable.</summary>
    public static RodSnapshot Ready(uint bait = Lugworm) => new()
    {
        Phase = RodPhase.Ready,
        RodOut = true,
        Bait = bait,
        BaitCount = _ => 50,
        Gp = 900,
        MaxGp = 900,
        Ready = _ => true,
        Left = 3,
    };

    public static Dictionary<uint, (double, int)> Statuses(params (uint Id, int Stacks)[] statuses)
        => statuses.ToDictionary(s => s.Id, s => (60.0, s.Stacks));

    public static RodSnapshot Biting(Tug tug, double seconds, CastContext context, bool chum = false, uint identical = 0)
        => Ready() with { Phase = RodPhase.Bite, Tug = tug, SinceCast = seconds, Cast = new RodCast(context, chum, identical) };

    public static RodSnapshot LineOut(double seconds, CastContext context)
        => Ready() with { Phase = RodPhase.LineOut, SinceCast = seconds, Cast = new RodCast(context) };
}
