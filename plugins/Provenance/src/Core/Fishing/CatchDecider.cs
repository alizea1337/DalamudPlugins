using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Gathering;

namespace Provenance.Core.Fishing;

/// <summary>Where the rod is, from the game's fishing state.</summary>
public enum RodPhase
{
    /// <summary>Not fishing (state None): before the first cast, or after quitting.</summary>
    Idle,
    /// <summary>Between casts, rod out (PoleReady).</summary>
    Ready,
    /// <summary>The line is going out.</summary>
    CastingOut,
    /// <summary>The line is in the water (including the lure animations).</summary>
    LineOut,
    /// <summary>Something bites.</summary>
    Bite,
    /// <summary>Hooking, reeling in, quitting: nothing to do but wait.</summary>
    Busy,
}

/// <summary>Why a session ends (the runtime maps it to its signal).</summary>
public enum CatchEnd { None, Done, Wary, Amiss, NoBait, CantCast, WindowClosed, Stopped }

/// <summary>What a <see cref="RodMove"/> does.</summary>
public enum RodMoveKind
{
    /// <summary>Nothing to do now (<see cref="RodMove.Wait"/> says why).</summary>
    Wait,
    /// <summary>Use action <see cref="RodMove.Id"/> (a buff, a lure, Snagging, Collect).</summary>
    Use,
    /// <summary>Drink <see cref="RodMove.Cordial"/>.</summary>
    Drink,
    /// <summary>Put bait <see cref="RodMove.Id"/> on the line.</summary>
    ChangeBait,
    Cast,
    /// <summary>Mooch (<see cref="RodMove.Id"/> is Mooch or Mooch II) with the fish just landed.</summary>
    Mooch,
    /// <summary>Hook the bite with action <see cref="RodMove.Id"/> (Hook or a hookset), after a short human delay.</summary>
    Hook,
    /// <summary>Reel in without hooking; after a bite (<see cref="RodMove.AfterBite"/>) only after a longer delay.</summary>
    Rest,
    /// <summary>Stow the rod.</summary>
    Quit,
    /// <summary>The session is over (rod stowed).</summary>
    End,
}

/// <summary>What a <see cref="RodMoveKind.Wait"/> waits for.</summary>
public enum RodWait
{
    None,
    /// <summary>The target's window (or, with <see cref="RodMove.Fish"/> a predator, the window to come close enough for the last predator catch).</summary>
    Window,
    /// <summary>Fisher's Intuition, after every predator was caught.</summary>
    Intuition,
    /// <summary>The window of predator <see cref="RodMove.Fish"/>: every predator still needed is out of its window.</summary>
    Predator,
    Gp,
    /// <summary>Action <see cref="RodMove.Id"/> to become usable.</summary>
    Action,
}

/// <summary>One move of the rod: at most one game action per tick.</summary>
/// <param name="Id">The action (or bait) id.</param>
/// <param name="Fish">The item the move is about: the fish hooked, mooched for or cast for, or the bait that ran out.</param>
/// <param name="End">Set when the move starts or carries on ending the session.</param>
public readonly record struct RodMove(RodMoveKind Kind, uint Id = 0, uint Fish = 0, CatchEnd End = CatchEnd.None, RodWait Wait = RodWait.None,
    Cordial? Cordial = null, bool AfterBite = false)
{
    public static RodMove Idle(RodWait why = RodWait.None, uint id = 0, uint fish = 0, CatchEnd end = CatchEnd.None) => new(RodMoveKind.Wait, id, fish, end, why);
    public static RodMove Use(uint action, uint fish = 0) => new(RodMoveKind.Use, action, fish);
    public static RodMove RestNow(CatchEnd end = CatchEnd.None) => new(RodMoveKind.Rest, FishingIds.Rest, End: end);
    public static RodMove RestBite() => new(RodMoveKind.Rest, FishingIds.Rest, AfterBite: true);
}

/// <summary>The cast in the water: its context, and what was up when it went out.</summary>
/// <param name="Chum">Chum was up: bites come sooner than the data's window.</param>
/// <param name="IdenticalFish">Identical Cast was up for this fish: it comes, whenever it bites.</param>
public readonly record struct RodCast(CastContext Context, bool Chum = false, uint IdenticalFish = 0);

/// <summary>Everything the decider looks at, read from the game by the runtime each tick.</summary>
public sealed record RodSnapshot
{
    /// <summary>Where the rod is (from the fishing state).</summary>
    public RodPhase Phase { get; init; }
    /// <summary>The fisher stance is up.</summary>
    public bool RodOut { get; init; }
    /// <summary>The bait on the line.</summary>
    public uint Bait { get; init; }
    /// <summary>A swimbait slot is selected instead of the bait.</summary>
    public bool Swimbait { get; init; }
    /// <summary>Bait for a predator whose own isn't in the bags (Versatile Lure); 0 for none.</summary>
    public uint FallbackBait { get; init; }
    public Func<uint, int> BaitCount { get; init; } = _ => 0;
    /// <summary>The fish landed last (0 for none).</summary>
    public uint LastCatch { get; init; }
    /// <summary>No cast since the last catch (actions on "the fish you just caught" apply).</summary>
    public bool JustCaught { get; init; }
    /// <summary>The game offers Mooch / Mooch II / Identical Cast / Surface Slap with the previous catch.</summary>
    public bool CanMooch { get; init; }
    public bool CanMoochII { get; init; }
    public bool CanIdenticalCast { get; init; }
    public bool CanSurfaceSlap { get; init; }
    /// <summary>The player's statuses: seconds left and stacks.</summary>
    public IReadOnlyDictionary<uint, (double Seconds, int Stacks)> Statuses { get; init; } = new Dictionary<uint, (double, int)>();
    /// <summary>Current and maximum GP.</summary>
    public int Gp { get; init; }
    public int MaxGp { get; init; }
    /// <summary>The action can be used right now (the game's own checks: GP, state, level, recast).</summary>
    public Func<uint, bool> Ready { get; init; } = _ => false;
    /// <summary>Seconds until the action's recast is over (0 when it isn't recasting).</summary>
    public Func<uint, double> Cooldown { get; init; } = _ => 0;
    /// <summary>How many more of the target the order wants.</summary>
    public int Left { get; init; }
    /// <summary>Predator catches toward the next Fisher's Intuition, by fish.</summary>
    public IReadOnlyDictionary<uint, int> Progress { get; init; } = new Dictionary<uint, int>();
    /// <summary>The target's window (<see cref="CatchPlan.Window"/>: its own, narrowed by its mooch chain's).</summary>
    public CatchWindowState Window { get; init; } = CatchWindowState.Always;
    /// <summary>The target's window has been open during this session.</summary>
    public bool WindowSeenOpen { get; init; }
    /// <summary>A predator's window (<see cref="CatchPlan.PredatorWindows"/>): open now, or when it opens. Always open without one.</summary>
    public Func<uint, CatchWindowState> PredatorWindow { get; init; } = _ => CatchWindowState.Always;
    /// <summary>The cast in the water or biting; null when unknown.</summary>
    public RodCast? Cast { get; init; }
    /// <summary>Seconds since that cast went out (the bite-time data's reference point).</summary>
    public double SinceCast { get; init; }
    /// <summary>The bite's tug (at <see cref="RodPhase.Bite"/>).</summary>
    public Tug Tug { get; init; }
    /// <summary>This cast's lure brought the fish it was for.</summary>
    public bool LureSuccess { get; init; }
    public double SinceLure { get; init; } = double.MaxValue;
    /// <summary>Cordials in the bags that can be drunk now.</summary>
    public IReadOnlyList<Cordial> Cordials { get; init; } = Array.Empty<Cordial>();
    /// <summary>The session is ending for this reason: reel in and stow the rod.</summary>
    public CatchEnd Ending { get; init; }
    /// <summary>Ending now: a bite is let go too (otherwise a wanted bite is still landed).</summary>
    public bool LetGo { get; init; }

    public bool Has(uint status) => Statuses.ContainsKey(status);
    public int Stacks(uint status) => Statuses.TryGetValue(status, out var s) ? s.Stacks : 0;
    public bool Intuition => Has(FishingIds.IntuitionStatus);
    public int Caught(uint fish) => Progress.TryGetValue(fish, out var n) ? n : 0;
}

/// <summary>
/// The next move of the rod from a <see cref="CatchPlan"/> and a <see cref="RodSnapshot"/>: AutoHook's
/// between-casts order (stop conditions, mooch, caught-fish actions, bait, buffs, cast), its while-in-the-water
/// actions (lures, the early Rest) and its bite rule (the wanted tug inside its window with its hookset, falling
/// back to Hook; Rest for everything else). One move per call, at most one game action.
/// Ported from AutoHook (BSD-3, Puni.sh). Pure.
/// </summary>
public static class CatchDecider
{
    /// <summary>Seconds between two lures on a cast (AutoHook's lure throttle).</summary>
    public const double LureGap = 2.5;

    /// <summary>The next move for the rod's phase: the bite, the line in the water, or between casts.</summary>
    public static RodMove Next(CatchPlan plan, RodSnapshot s) => s.Phase switch
    {
        RodPhase.Bite => AtBite(plan, s),
        RodPhase.LineOut => WhileLineOut(plan, s),
        RodPhase.Ready or RodPhase.Idle => BetweenCasts(plan, s),
        _ => RodMove.Idle(),
    };

    // ------------------------------------------------------------------ the bite

    /// <summary>Hook the bite when the cast context wants its tug at this time, else Rest.</summary>
    public static RodMove AtBite(CatchPlan plan, RodSnapshot s)
    {
        if (s.LetGo) return RodMove.RestNow(s.Ending);
        if (s.Cast is not { } cast || Match(plan, s, cast) is not { } rule) return RodMove.RestBite();
        return new RodMove(RodMoveKind.Hook, HooksetFor(rule.HookSet, s), rule.FishId);
    }

    /// <summary>The wanted rule a bite matches (the first, most important one), or null.</summary>
    public static HookRule? Match(CatchPlan plan, RodSnapshot s, RodCast cast)
    {
        foreach (var rule in plan.RulesFor(cast.Context))
            if (Wanted(plan, rule, s) && Bites(rule, s.Tug, s.SinceCast, cast)) return rule;
        return null;
    }

    /// <summary>
    /// A bite with this tug, this many seconds after the cast, is the rule's fish: the tug matches (an
    /// unknown tug in the data matches any), and it came inside [min, max + 1]. Under Chum bites come
    /// sooner, so only the upper bound holds. A fish with no bite data, the only fish with its tug at the
    /// hole, or the fish Identical Cast was used for, is hooked whenever it bites.
    /// </summary>
    public static bool Bites(HookRule rule, Tug tug, double seconds, RodCast cast)
    {
        if (rule.Tug != Tug.Unknown && tug != rule.Tug) return false;
        if (rule.OnlyOneWithTug || cast.IdenticalFish == rule.FishId) return true;
        if (rule.From is not { } from || rule.Until is not { } until) return true;
        return seconds >= (cast.Chum ? 0 : from) && seconds <= until;
    }

    /// <summary>
    /// The hookset for a bite. Precision and Powerful Hookset only work under Patience (Angler's Fortune)
    /// and cost 50 GP; otherwise, and for every other hookset, plain Hook.
    /// </summary>
    public static uint HooksetFor(HookSet hookset, RodSnapshot s)
    {
        if (s.Has(FishingIds.AnglersFortune) && s.Gp >= FishingIds.HooksetGp)
        {
            if (hookset == HookSet.Precise && s.Ready(FishingIds.PrecisionHookset)) return FishingIds.PrecisionHookset;
            if (hookset == HookSet.Powerful && s.Ready(FishingIds.PowerfulHookset)) return FishingIds.PowerfulHookset;
        }
        return FishingIds.Hook;
    }

    /// <summary>
    /// The rule's fish is still wanted: the target while the order wants more (the slap fish only while
    /// Surface Slap is affordable and not up), a predator in its window until its count toward Fisher's
    /// Intuition is reached.
    /// </summary>
    public static bool Wanted(CatchPlan plan, HookRule rule, RodSnapshot s)
    {
        if (rule.Route == 0)
        {
            if (s.Left <= 0) return false;
            // Under Surface Slap the slap fish doesn't bite (status 1803), so a bite with its tug is another
            // fish; hooking it would end the slap.
            return rule.Role != CatchRole.Slap || !s.Has(FishingIds.SurfaceSlapStatus) && s.Gp >= FishingIds.SurfaceSlapGp;
        }
        if (rule.Route > plan.Predators.Count) return false;
        var route = plan.Route(rule.Route);
        return s.Caught(route.Fish.ItemId) < route.Count && s.PredatorWindow(route.Fish.ItemId).Open;
    }

    // ------------------------------------------------------------------ the line in the water

    /// <summary>Lures for lure fish, and Rest once nothing wanted can bite on this cast any more.</summary>
    public static RodMove WhileLineOut(CatchPlan plan, RodSnapshot s)
    {
        if (s.Ending != CatchEnd.None) return RodMove.RestNow(s.Ending);
        if (s.Left <= 0) return RodMove.RestNow(CatchEnd.Done);
        if (s.Cast is not { } cast) return RodMove.Idle();
        var wanted = plan.RulesFor(cast.Context).Where(r => Wanted(plan, r, s)).ToList();
        // Nothing this cast can bring is wanted any more (a predator's count filled up meanwhile).
        if (wanted.Count == 0) return RodMove.RestNow();

        var lure = wanted.Select(r => r.Lure).FirstOrDefault(l => l != LureKind.None);
        if (lure != LureKind.None && !s.LureSuccess && s.SinceLure >= LureGap)
        {
            var (action, status) = lure == LureKind.Ambitious
                ? (FishingIds.AmbitiousLure, FishingIds.AmbitiousLureStatus)
                : (FishingIds.ModestLure, FishingIds.ModestLureStatus);
            var stacks = s.Stacks(status);
            // Three lures and the fish didn't show: reel in and try again (AutoHook's CancelAttempt).
            if (stacks >= FishingIds.MaxLures) return RodMove.RestNow();
            // Under Patience, keep the GP of the hookset the bite will need.
            var keep = s.Has(FishingIds.AnglersFortune) && wanted.Any(r => r.HookSet is HookSet.Precise or HookSet.Powerful) ? FishingIds.HooksetGp : 0;
            if (s.Ready(action) && s.Gp >= FishingIds.LureGp(stacks) + keep)
                return RodMove.Use(action, wanted.First(r => r.Lure != LureKind.None).FishId);
        }

        if (CancelAfter(wanted, cast) is { } cancel && s.SinceCast > cancel) return RodMove.RestNow();
        return RodMove.Idle();
    }

    /// <summary>
    /// Seconds after the cast when no wanted fish can bite any more (the latest end of their windows);
    /// null when one of them has no bite data, or Identical Cast brings it.
    /// </summary>
    public static double? CancelAfter(IReadOnlyList<HookRule> wanted, RodCast cast)
    {
        double latest = 0;
        foreach (var r in wanted)
        {
            if (r.Until is not { } until || cast.IdenticalFish == r.FishId) return null;
            latest = Math.Max(latest, until);
        }
        return latest;
    }

    // ------------------------------------------------------------------ between casts

    /// <summary>What can be fished now: a route to cast for, or why not.</summary>
    /// <param name="Wanted">Routes whose chain fish are worth mooching now.</param>
    /// <param name="Fish">A wait's fish when it isn't the target: the predator waited for, or the one whose last catch is held back.</param>
    public readonly record struct Step(CatchRoute? Route, IReadOnlyList<CatchRoute> Wanted, RodWait Wait, CatchEnd End, uint Fish = 0);

    /// <summary>
    /// Between casts (rod ready, or before the first cast), in AutoHook's order: stop conditions, the window,
    /// mooching the fish just landed, the caught-fish actions, bait, Snagging, GP, buffs, then the cast.
    /// </summary>
    public static RodMove BetweenCasts(CatchPlan plan, RodSnapshot s)
    {
        if (s.Ending != CatchEnd.None) return Finish(s, s.Ending);
        if (s.Left <= 0) return Finish(s, CatchEnd.Done);

        var step = StepNow(plan, s);
        if (step.End != CatchEnd.None) return Finish(s, step.End);
        if (step.Route is null) return RodMove.Idle(step.Wait, fish: step.Fish != 0 ? step.Fish : plan.Target.ItemId);
        // Like AutoHook, act between casts only once Cast is usable: the catch animation is over.
        if (!s.Ready(FishingIds.Cast)) return RodMove.Idle(RodWait.Action, FishingIds.Cast);
        // Fish come in as normal items: keep the Collect toggle off (should it stick, the collectable prompt is accepted).
        if (s.Has(FishingIds.CollectStatus) && s.Ready(FishingIds.Collect)) return RodMove.Use(FishingIds.Collect);

        // Mooch the fish just landed when it leads on to a wanted fish (Mooch, else Mooch II for an average-sized one).
        if (MoochFor(s, step) is { } mooch)
        {
            if (mooch.Route.Snag != s.Has(FishingIds.SnaggingStatus))
                return s.Ready(FishingIds.Snagging) ? RodMove.Use(FishingIds.Snagging) : RodMove.Idle(RodWait.Action, FishingIds.Snagging);
            if (s.CanMooch)
                return s.Ready(FishingIds.Mooch) ? new RodMove(RodMoveKind.Mooch, FishingIds.Mooch, mooch.Next.ItemId) : RodMove.Idle(RodWait.Action, FishingIds.Mooch);
            if (s.CanMoochII && plan.Buffs.MoochII && s.Gp >= FishingIds.MoochIIGp + HooksetReserve(mooch.Route, plan, s))
            {
                if (s.Ready(FishingIds.MoochII)) return new RodMove(RodMoveKind.Mooch, FishingIds.MoochII, mooch.Next.ItemId);
                if (s.Cooldown(FishingIds.MoochII) <= 0) return RodMove.Idle(RodWait.Action, FishingIds.MoochII);
            }
        }

        if (AfterCatch(plan, s, step) is { } after) return after;

        var route = step.Route;
        var bait = Tackle(route, s);
        if (bait == 0) return Finish(s, CatchEnd.NoBait) with { Fish = route.Bait != 0 ? route.Bait : s.FallbackBait };
        if (s.Bait != bait || s.Swimbait) return new RodMove(RodMoveKind.ChangeBait, bait, route.First.ItemId);
        if (route.Snag != s.Has(FishingIds.SnaggingStatus))
            return s.Ready(FishingIds.Snagging) ? RodMove.Use(FishingIds.Snagging) : RodMove.Idle(RodWait.Action, FishingIds.Snagging);

        if (RestoreGp(plan, s, route) is { } gp) return gp;
        if (Buff(plan, s, route) is { } buff) return buff;
        // A lure fish only comes with a lure: wait for the GP of the first one.
        if (CatchPlanner.LureOf(route.First) != LureKind.None && s.Gp < FishingIds.LureGp(0)) return RodMove.Idle(RodWait.Gp, fish: route.First.ItemId);
        return new RodMove(RodMoveKind.Cast, FishingIds.Cast, route.First.ItemId);
    }

    /// <summary>
    /// The route to cast for now. The target's window decides: closed after being open (or opening too
    /// late) ends the session; closed but opening soon waits. A target that needs Fisher's Intuition gets
    /// its predators first, started so the intuition comes as the window opens (<see cref="CatchPlan.PrepLead"/>),
    /// with the last catch held back until the intuition it brings lasts into the window
    /// (<see cref="CatchPlan.LastCatchLead"/>); an intuition that came early is held until the window opens.
    /// Predators are fished in their own windows: with all those still needed out of theirs, the session waits
    /// for the first to open (ends when one doesn't open within the forecast).
    /// </summary>
    public static Step StepNow(CatchPlan plan, RodSnapshot s)
    {
        var none = Array.Empty<CatchRoute>();
        var w = s.Window;
        if (!w.Open && (s.WindowSeenOpen || w.OpensIn is not { } opensIn || opensIn > plan.EarlyLimit))
            return new Step(null, none, RodWait.None, CatchEnd.WindowClosed);

        if (!plan.NeedsIntuition || s.Intuition)
            return w.Open
                ? new Step(plan.TargetRoute, new[] { plan.TargetRoute }, RodWait.None, CatchEnd.None)
                : new Step(null, none, RodWait.Window, CatchEnd.None);

        if (!w.Open && w.OpensIn > plan.PrepLead) return new Step(null, none, RodWait.Window, CatchEnd.None);
        var missing = plan.Predators.Where(p => s.Caught(p.Fish.ItemId) < p.Count).ToList();
        // Every predator caught by our count: the game grants the intuition on the last catch.
        if (missing.Count == 0) return new Step(null, none, RodWait.Intuition, CatchEnd.None);
        // Fisher's Intuition needs every one of them: one that doesn't bite within the forecast rules it out.
        if (missing.Any(p => s.PredatorWindow(p.Fish.ItemId).OpensIn is null)) return new Step(null, none, RodWait.None, CatchEnd.WindowClosed);
        // The last catch brings the intuition: not before it would last into the window.
        if (!w.Open && w.OpensIn > plan.LastCatchLead && missing.Sum(p => p.Count - s.Caught(p.Fish.ItemId)) == 1)
            return new Step(null, none, RodWait.Window, CatchEnd.None, missing[0].Fish.ItemId);

        var open = missing.Where(p => s.PredatorWindow(p.Fish.ItemId).Open)
            // Stay with the fish just caught (Identical Cast follows it), then with the bait on the line.
            .OrderBy(p => s.JustCaught && p.Fish.ItemId == s.LastCatch ? 0 : 1)
            .ThenBy(p => Tackle(p, s) == s.Bait ? 0 : 1)
            .ThenBy(p => p.Index)
            .ToList();
        if (open.Count > 0) return new Step(open[0], open, RodWait.None, CatchEnd.None);
        // Every predator still needed is out of its window: wait for the first to bite (the target's own window,
        // when it has one, still ends the session once it has closed).
        var next = missing.OrderBy(p => s.PredatorWindow(p.Fish.ItemId).OpensIn).ThenBy(p => p.Index).First();
        return new Step(null, none, RodWait.Predator, CatchEnd.None, next.Fish.ItemId);
    }

    /// <summary>The wanted route the fish just landed is a chain link of, and the fish mooching it hooks.</summary>
    public static (CatchRoute Route, FishInfo Next)? MoochFor(RodSnapshot s, Step step)
    {
        if (s.LastCatch == 0 || !(s.CanMooch || s.CanMoochII)) return null;
        foreach (var route in step.Wanted)
            if (route.After(s.LastCatch) is { } next) return (route, next);
        return null;
    }

    /// <summary>Surface Slap on the slap fish; Identical Cast on a predator's next-to-last catch or on the target.</summary>
    private static RodMove? AfterCatch(CatchPlan plan, RodSnapshot s, Step step)
    {
        if (!s.JustCaught || s.LastCatch == 0 || step.Route is not { } route) return null;
        if (s.Has(FishingIds.SurfaceSlapStatus) || s.Has(FishingIds.IdenticalCastStatus)) return null;
        var last = s.LastCatch;
        var reserve = Reserve(route, plan, s);
        if (plan.Buffs.SlapFish == last && route.Index == 0 && s.CanSurfaceSlap && s.Gp >= FishingIds.SurfaceSlapGp + reserve && s.Ready(FishingIds.SurfaceSlap))
            return RodMove.Use(FishingIds.SurfaceSlap, last);

        if (!s.CanIdenticalCast || s.Gp < FishingIds.IdenticalCastGp + reserve || !s.Ready(FishingIds.IdenticalCast)) return null;
        if (plan.Buffs.IdenticalCastPredators && route.Index > 0
            && step.Wanted.Any(p => p.Index > 0 && p.Fish.ItemId == last && !p.Mooches && s.Caught(last) == p.Count - 1))
            return RodMove.Use(FishingIds.IdenticalCast, last);
        if (plan.Buffs.IdenticalCastTarget && route.Index == 0 && last == plan.Target.ItemId && s.Left >= 1)
            return RodMove.Use(FishingIds.IdenticalCast, last);
        return null;
    }

    /// <summary>Thaliak's Favor when its 150 GP fit (and the Angler's Art isn't kept for Makeshift Bait), else a cordial.</summary>
    private static RodMove? RestoreGp(CatchPlan plan, RodSnapshot s, CatchRoute route)
    {
        var art = s.Stacks(FishingIds.AnglersArt);
        if (plan.Buffs.ThaliaksFavor && art >= FishingIds.ThaliaksFavorArt && s.Gp + FishingIds.ThaliaksFavorGp <= s.MaxGp && s.Ready(FishingIds.ThaliaksFavor))
        {
            var keepForMakeshift = plan.Buffs.MakeshiftBait && route.Mooches && !s.Has(FishingIds.MakeshiftBaitStatus)
                                   && art < FishingIds.ThaliaksFavorArt + FishingIds.MakeshiftBaitArt;
            if (!keepForMakeshift) return RodMove.Use(FishingIds.ThaliaksFavor);
        }
        var want = s.MaxGp > 0 ? Math.Min(plan.Buffs.GpTarget, s.MaxGp) : plan.Buffs.GpTarget;
        if (plan.Buffs.Cordials && s.Gp < want && s.Cordials.Count > 0 && GpBudget.Pick(s.Cordials, s.Gp, want, s.MaxGp) is { } cordial)
            return new RodMove(RodMoveKind.Drink, Cordial: cordial);
        return null;
    }

    /// <summary>
    /// Buffs before a tackle cast. Routes that mooch want large fish: Makeshift Bait (Angler's Art), then
    /// Patience II, Prize Catch or Patience as GP allows, keeping 50 GP for the hookset (none while Mooch II
    /// is ready for a one-link chain). Chum when its 100 GP leave that, and the Patience or Mooch II still
    /// wanted, covered.
    /// </summary>
    private static RodMove? Buff(CatchPlan plan, RodSnapshot s, CatchRoute route)
    {
        var reserve = Reserve(route, plan, s);
        var fortune = s.Has(FishingIds.AnglersFortune) || s.Has(FishingIds.MakeshiftBaitStatus) || s.Has(FishingIds.PrizeCatchStatus);
        var keep = 0;
        if (route.Mooches && !fortune)
        {
            if (plan.Buffs.MakeshiftBait && s.Stacks(FishingIds.AnglersArt) >= FishingIds.MakeshiftBaitArt && s.Ready(FishingIds.MakeshiftBait))
                return RodMove.Use(FishingIds.MakeshiftBait);
            // Mooch II ready takes an average-sized link of a one-link chain on too: no Patience, but keep its GP.
            var moochIICovers = route.Chain.Count == 1 && plan.Buffs.MoochII && s.Cooldown(FishingIds.MoochII) <= 0;
            if (moochIICovers) keep = FishingIds.MoochIIGp;
            else
                foreach (var (action, cost) in LargeFishOptions(plan, route))
                {
                    if (s.Gp >= cost + reserve && s.Ready(action)) return RodMove.Use(action);
                    keep = keep == 0 ? cost : Math.Min(keep, cost);
                }
        }
        if (plan.Buffs.Chum && !s.Has(FishingIds.ChumStatus) && s.Gp >= FishingIds.ChumGp + reserve + keep && s.Ready(FishingIds.Chum))
            return RodMove.Use(FishingIds.Chum);
        return null;
    }

    /// <summary>Ways to make the next catch large, best first: Patience II; Prize Catch for a one-link chain; Patience.</summary>
    private static IEnumerable<(uint Action, int Gp)> LargeFishOptions(CatchPlan plan, CatchRoute route)
    {
        if (plan.Buffs.Patience.Contains(FishingIds.PatienceII)) yield return (FishingIds.PatienceII, FishingIds.PatienceIIGp);
        if (plan.Buffs.PrizeCatch && route.Chain.Count == 1) yield return (FishingIds.PrizeCatch, FishingIds.PrizeCatchGp);
        if (plan.Buffs.Patience.Contains(FishingIds.Patience)) yield return (FishingIds.Patience, FishingIds.PatienceGp);
        if (plan.Buffs.PrizeCatch && route.Chain.Count > 1) yield return (FishingIds.PrizeCatch, FishingIds.PrizeCatchGp);
    }

    /// <summary>GP kept for the cast: the hookset under Patience, and a lure fish's three lures.</summary>
    private static int Reserve(CatchRoute route, CatchPlan plan, RodSnapshot s)
        => HooksetReserve(route, plan, s) + (CatchPlanner.LureOf(route.First) != LureKind.None ? FishingIds.LureGp(0) + FishingIds.LureGp(1) + FishingIds.LureGp(2) : 0);

    private static int HooksetReserve(CatchRoute route, CatchPlan plan, RodSnapshot s)
        => route.NeedsHookset && (s.Has(FishingIds.AnglersFortune) || route.Mooches && plan.Buffs.Patience.Count > 0) ? FishingIds.HooksetGp : 0;

    /// <summary>
    /// The route's own tackle when in the bags; for a predator, else the fallback bait when in the bags
    /// (the target's tackle is the run's choice, and running out of it ends the session); else 0.
    /// </summary>
    public static uint Tackle(CatchRoute route, RodSnapshot s)
    {
        if (route.Bait != 0 && s.BaitCount(route.Bait) > 0) return route.Bait;
        if (route.Index > 0 && s.FallbackBait != 0 && s.BaitCount(s.FallbackBait) > 0) return s.FallbackBait;
        return 0;
    }

    /// <summary>
    /// Ending: stow the rod (Quit) from between casts; once the fishing state is None, wait for the stance
    /// to drop, then the session is over.
    /// </summary>
    private static RodMove Finish(RodSnapshot s, CatchEnd end)
    {
        if (s.Phase == RodPhase.Ready)
            return s.Ready(FishingIds.Quit) ? new RodMove(RodMoveKind.Quit, FishingIds.Quit, End: end) : RodMove.Idle(RodWait.Action, FishingIds.Quit, end: end);
        if (s.RodOut) return RodMove.Idle(RodWait.Action, FishingIds.Quit, end: end);
        return new RodMove(RodMoveKind.End, End: end);
    }
}
