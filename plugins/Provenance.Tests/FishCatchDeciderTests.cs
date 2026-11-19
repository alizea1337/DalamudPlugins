using System;
using System.Collections.Generic;
using Provenance.Core.Fishing;
using Provenance.Core.Gathering;
using Xunit;
using static Provenance.Tests.FishCases;

namespace Provenance.Tests;

public class FishCatchDeciderTests
{
    private static readonly CastContext Tackle = CastContext.Tackle(false);
    private static readonly CastContext TackleUnderIntuition = CastContext.Tackle(true);

    private static RodMove Next(CatchPlan plan, RodSnapshot s) => CatchDecider.Next(plan, s);

    private static void IsUse(RodMove move, uint action)
    {
        Assert.Equal(RodMoveKind.Use, move.Kind);
        Assert.Equal(action, move.Id);
    }

    // ------------------------------------------------------------------ a straight fish

    [Fact]
    public void A_straight_fish_is_cast_for_with_its_bait()
    {
        var plan = Plan(Straight, buffs: false);
        Assert.Equal(new RodMove(RodMoveKind.Cast, FishingIds.Cast, 100), Next(plan, Ready()));
        Assert.Equal(new RodMove(RodMoveKind.ChangeBait, Lugworm, 100), Next(plan, Ready(bait: Shrimp)));
        // A selected swimbait would go out instead of the bait.
        Assert.Equal(RodMoveKind.ChangeBait, Next(plan, Ready() with { Swimbait = true }).Kind);
        // Cast not usable yet (the catch animation): wait.
        Assert.Equal(RodMove.Idle(RodWait.Action, FishingIds.Cast), Next(plan, Ready() with { Ready = id => id != FishingIds.Cast }));
    }

    [Fact]
    public void The_wanted_tug_inside_its_window_is_hooked_and_everything_else_rested()
    {
        var plan = Plan(Straight, buffs: false);
        Assert.Equal(new RodMove(RodMoveKind.Hook, FishingIds.Hook, 100), Next(plan, Biting(Tug.Weak, 8, Tackle)));
        Assert.Equal(RodMoveKind.Hook, Next(plan, Biting(Tug.Weak, 13, Tackle)).Kind);   // max + 1
        foreach (var (tug, seconds) in new[] { (Tug.Strong, 8.0), (Tug.Legendary, 8.0), (Tug.Weak, 3.0), (Tug.Weak, 13.5), (Tug.Unknown, 8.0) })
        {
            var rest = Next(plan, Biting(tug, seconds, Tackle));
            Assert.Equal(RodMoveKind.Rest, rest.Kind);
            Assert.True(rest.AfterBite);
        }
        // An unknown cast context (a cast the plan doesn't know) hooks nothing.
        Assert.Equal(RodMoveKind.Rest, Next(plan, Biting(Tug.Weak, 8, Tackle) with { Cast = null }).Kind);
    }

    [Fact]
    public void Under_chum_bites_come_early_and_are_still_hooked()
    {
        var plan = Plan(Straight, buffs: false);
        Assert.Equal(RodMoveKind.Hook, Next(plan, Biting(Tug.Weak, 2, Tackle, chum: true)).Kind);
        Assert.Equal(RodMoveKind.Rest, Next(plan, Biting(Tug.Weak, 14, Tackle, chum: true)).Kind);
    }

    [Fact]
    public void Hooksets_need_patience_and_gp_else_hook()
    {
        var plan = Plan(Straight, buffs: false);
        var fortune = Biting(Tug.Weak, 8, Tackle) with { Statuses = Statuses((FishingIds.AnglersFortune, 0)) };
        Assert.Equal(FishingIds.PrecisionHookset, Next(plan, fortune).Id);
        Assert.Equal(FishingIds.Hook, Next(plan, fortune with { Gp = 40 }).Id);
        Assert.Equal(FishingIds.Hook, Next(plan, fortune with { Ready = id => id != FishingIds.PrecisionHookset }).Id);
        Assert.Equal(FishingIds.Hook, Next(plan, Biting(Tug.Weak, 8, Tackle)).Id);   // no Patience

        var powerful = Fish(720, Tug.Strong, HookSet.Powerful, (5, 12));
        Assert.Equal(FishingIds.PowerfulHookset, Next(Plan(powerful, buffs: false, data: _ => powerful), fortune with { Tug = Tug.Strong }).Id);
        var unknown = Fish(721, Tug.Weak, HookSet.Unknown, (5, 12));
        Assert.Equal(FishingIds.Hook, Next(Plan(unknown, buffs: false, data: _ => unknown), fortune).Id);
    }

    [Fact]
    public void Without_bite_data_the_tug_is_hooked_at_any_time_and_never_given_up_early()
    {
        var fish = Fish(701, Tug.Weak, HookSet.Precise, bite: null);
        var plan = Plan(fish, buffs: false, data: _ => fish);
        Assert.Equal(RodMoveKind.Hook, Next(plan, Biting(Tug.Weak, 0.5, Tackle)).Kind);
        Assert.Equal(RodMoveKind.Hook, Next(plan, Biting(Tug.Weak, 300, Tackle)).Kind);
        Assert.Equal(RodMoveKind.Rest, Next(plan, Biting(Tug.Strong, 30, Tackle)).Kind);
        Assert.Equal(RodMoveKind.Wait, Next(plan, LineOut(500, Tackle)).Kind);
    }

    [Fact]
    public void Without_tug_data_any_tug_inside_the_window_is_hooked()
    {
        var fish = Fish(702, Tug.Unknown, HookSet.Powerful, (5, 12));
        var plan = Plan(fish, buffs: false, data: _ => fish);
        Assert.Equal(RodMoveKind.Hook, Next(plan, Biting(Tug.Strong, 8, Tackle)).Kind);
        Assert.Equal(RodMoveKind.Hook, Next(plan, Biting(Tug.Legendary, 8, Tackle)).Kind);
        Assert.Equal(RodMoveKind.Rest, Next(plan, Biting(Tug.Strong, 20, Tackle)).Kind);
    }

    [Fact]
    public void The_only_fish_with_its_tug_at_the_hole_is_hooked_whenever_it_bites()
    {
        var plan = Plan(Straight, buffs: false, spot: new[] { Straight, Fish(800, Tug.Strong) });
        Assert.Equal(RodMoveKind.Hook, Next(plan, Biting(Tug.Weak, 2, Tackle)).Kind);
        // The window still says when to give up waiting.
        Assert.Equal(RodMove.RestNow(), Next(plan, LineOut(14, Tackle)));
    }

    [Fact]
    public void Once_the_window_is_past_the_line_comes_in()
    {
        var plan = Plan(Straight, buffs: false);
        Assert.Equal(RodMove.Idle(), Next(plan, LineOut(12, Tackle)));
        Assert.Equal(RodMove.RestNow(), Next(plan, LineOut(13.1, Tackle)));
    }

    [Fact]
    public void Identical_cast_brings_its_fish_whenever_it_bites()
    {
        var plan = Plan(Straight, buffs: false);
        Assert.Equal(RodMoveKind.Hook, Next(plan, Biting(Tug.Weak, 30, Tackle, identical: 100)).Kind);
        Assert.Equal(RodMove.Idle(), Next(plan, LineOut(30, Tackle) with { Cast = new RodCast(Tackle, IdenticalFish: 100) }));
    }

    [Fact]
    public void Out_of_bait_ends_the_session()
    {
        var plan = Plan(Straight, buffs: false);
        var empty = Ready() with { BaitCount = _ => 0 };
        Assert.Equal(new RodMove(RodMoveKind.Quit, FishingIds.Quit, Lugworm, CatchEnd.NoBait), Next(plan, empty));
        Assert.Equal(new RodMove(RodMoveKind.End, 0, Lugworm, CatchEnd.NoBait), Next(plan, empty with { Phase = RodPhase.Idle, RodOut = false }));
        // The target's own tackle has no fallback: running out of it is the run's to fix.
        Assert.Equal(CatchEnd.NoBait, Next(plan, empty with { FallbackBait = Versatile, BaitCount = id => id == Versatile ? 9 : 0 }).End);
    }

    [Fact]
    public void Enough_caught_ends_the_session_even_mid_cast()
    {
        var plan = Plan(Straight, buffs: false);
        Assert.Equal(new RodMove(RodMoveKind.Quit, FishingIds.Quit, End: CatchEnd.Done), Next(plan, Ready() with { Left = 0 }));
        Assert.Equal(RodMove.RestNow(CatchEnd.Done), Next(plan, LineOut(5, Tackle) with { Left = 0 }));
        // A bite the order no longer wants is let go.
        Assert.Equal(RodMoveKind.Rest, Next(plan, Biting(Tug.Weak, 8, Tackle) with { Left = 0 }).Kind);
    }

    [Fact]
    public void Ending_reels_in_then_stows_the_rod()
    {
        var plan = Plan(Straight, buffs: false);
        Assert.Equal(RodMove.RestNow(CatchEnd.Wary), Next(plan, LineOut(3, Tackle) with { Ending = CatchEnd.Wary }));
        Assert.Equal(new RodMove(RodMoveKind.Quit, FishingIds.Quit, End: CatchEnd.Wary), Next(plan, Ready() with { Ending = CatchEnd.Wary }));
        Assert.Equal(RodMove.Idle(RodWait.Action, FishingIds.Quit, end: CatchEnd.Wary),
            Next(plan, Ready() with { Ending = CatchEnd.Wary, Ready = id => id != FishingIds.Quit }));
        Assert.Equal(new RodMove(RodMoveKind.End, End: CatchEnd.Amiss), Next(plan, Ready() with { Phase = RodPhase.Idle, RodOut = false, Ending = CatchEnd.Amiss }));
        // Quit sent, the state is None but the stance hasn't dropped yet: wait, don't Quit again.
        Assert.Equal(RodMove.Idle(RodWait.Action, FishingIds.Quit, end: CatchEnd.Amiss), Next(plan, Ready() with { Phase = RodPhase.Idle, Ending = CatchEnd.Amiss }));
        // Stop now: the bite is let go at once. Stop after the catch: a wanted bite is still landed.
        Assert.Equal(RodMove.RestNow(CatchEnd.Stopped), Next(plan, Biting(Tug.Weak, 8, Tackle) with { Ending = CatchEnd.Stopped, LetGo = true }));
        Assert.Equal(RodMoveKind.Hook, Next(plan, Biting(Tug.Weak, 8, Tackle) with { Ending = CatchEnd.Stopped }).Kind);
    }

    [Fact]
    public void The_collect_toggle_is_kept_off()
    {
        var plan = Plan(Straight, buffs: false);
        var collect = Ready() with { Statuses = Statuses((FishingIds.CollectStatus, 0)) };
        IsUse(Next(plan, collect), FishingIds.Collect);
        // Should it not toggle, fishing goes on (the collectable prompt is accepted).
        Assert.Equal(RodMoveKind.Cast, Next(plan, collect with { Ready = id => id != FishingIds.Collect }).Kind);
    }

    [Fact]
    public void Nothing_happens_between_casts_until_cast_is_usable_again()
    {
        // The catch animation: Cast isn't usable yet, so no mooch, bait change or buff goes out either.
        var notYet = Ready() with { Ready = id => id != FishingIds.Cast };
        var wait = RodMove.Idle(RodWait.Action, FishingIds.Cast);
        Assert.Equal(wait, Next(Plan(Goose, buffs: false), notYet with { JustCaught = true, LastCatch = 200, CanMooch = true }));
        Assert.Equal(wait, Next(Plan(Straight, buffs: false), notYet with { Bait = Shrimp }));
        Assert.Equal(wait, Next(Plan(Goose), notYet with { Cooldown = id => id == FishingIds.MoochII ? 100 : 0 }));
        // Ending and waiting for the window don't need it.
        Assert.Equal(RodMoveKind.Quit, Next(Plan(Straight), notYet with { Left = 0 }).Kind);
        Assert.Equal(RodWait.Window, Next(Plan(Straight), notYet with { Window = new CatchWindowState(false, TimeSpan.FromMinutes(5), TimeSpan.Zero) }).Wait);
    }

    // ------------------------------------------------------------------ mooch chains

    [Fact]
    public void Each_link_of_a_chain_is_mooched_on()
    {
        var plan = Plan(Goose, buffs: false);
        var caught = Ready() with { JustCaught = true, CanMooch = true };
        Assert.Equal(new RodMove(RodMoveKind.Mooch, FishingIds.Mooch, 201), Next(plan, caught with { LastCatch = 200 }));
        Assert.Equal(new RodMove(RodMoveKind.Mooch, FishingIds.Mooch, 202), Next(plan, caught with { LastCatch = 201 }));
        Assert.Equal(new RodMove(RodMoveKind.Mooch, FishingIds.Mooch, 210), Next(plan, caught with { LastCatch = 202 }));
        // The target itself, or a fish off the chain, isn't mooched: cast again with the tackle.
        Assert.Equal(new RodMove(RodMoveKind.Cast, FishingIds.Cast, 200), Next(plan, caught with { LastCatch = 210 }));
        Assert.Equal(RodMoveKind.Cast, Next(plan, caught with { LastCatch = 999 }).Kind);
        // Mooch usable in a moment (the catch animation): wait rather than cast the fish away.
        Assert.Equal(RodMove.Idle(RodWait.Action, FishingIds.Mooch), Next(plan, caught with { LastCatch = 200, Ready = id => id != FishingIds.Mooch }));
        // Nothing more wanted: no mooch.
        Assert.Equal(RodMoveKind.Quit, Next(plan, caught with { LastCatch = 200, Left = 0 }).Kind);
    }

    [Fact]
    public void Each_mooch_hooks_the_next_link_with_its_own_tug_and_window()
    {
        var plan = Plan(Goose, buffs: false);
        Assert.Equal(new RodMove(RodMoveKind.Hook, FishingIds.Hook, 201), Next(plan, Biting(Tug.Strong, 10, new CastContext(200, false))));
        Assert.Equal(RodMoveKind.Rest, Next(plan, Biting(Tug.Weak, 10, new CastContext(200, false))).Kind);
        Assert.Equal(new RodMove(RodMoveKind.Hook, FishingIds.Hook, 210), Next(plan, Biting(Tug.Strong, 15, new CastContext(202, false))));
        Assert.Equal(RodMoveKind.Rest, Next(plan, Biting(Tug.Strong, 8, new CastContext(202, false))).Kind);   // before 10 s
        Assert.Equal(RodMove.RestNow(), Next(plan, LineOut(23.5, new CastContext(202, false))));
    }

    [Fact]
    public void An_average_sized_link_goes_out_with_mooch_ii_when_gp_and_its_recast_allow()
    {
        var plan = Plan(Goose, buffs: false);
        var average = Ready() with { JustCaught = true, LastCatch = 200, CanMoochII = true };
        Assert.Equal(new RodMove(RodMoveKind.Mooch, FishingIds.MoochII, 201), Next(plan, average));
        Assert.Equal(RodMoveKind.Cast, Next(plan, average with { Gp = 90 }).Kind);
        Assert.Equal(RodMoveKind.Cast, Next(plan, average with { Ready = id => id != FishingIds.MoochII, Cooldown = id => id == FishingIds.MoochII ? 120 : 0 }).Kind);
        Assert.Equal(RodMoveKind.Cast, Next(Plan(Goose, Without(FishingIds.MoochII), buffs: false), average).Kind);
        // Under Patience, Mooch II leaves the 50 GP of the Powerful Hookset.
        Assert.Equal(RodMoveKind.Cast, Next(plan, average with { Gp = 120, Statuses = Statuses((FishingIds.AnglersFortune, 0)) }).Kind);
    }

    [Fact]
    public void A_chain_that_needs_snagging_turns_it_on_before_the_mooch()
    {
        var fish = Fish(730, Tug.Strong, HookSet.Powerful, (5, 20), mooch: new uint[] { 200 }, snag: true);
        var plan = Plan(fish, buffs: false, data: id => id == 730 ? fish : Data(id));
        IsUse(Next(plan, Ready() with { JustCaught = true, LastCatch = 200, CanMooch = true }), FishingIds.Snagging);
        Assert.Equal(RodMoveKind.Mooch, Next(plan, Ready() with { JustCaught = true, LastCatch = 200, CanMooch = true, Statuses = Statuses((FishingIds.SnaggingStatus, 0)) }).Kind);
    }

    // ------------------------------------------------------------------ Fisher's Intuition

    [Fact]
    public void Predators_come_first_on_their_own_bait_then_the_target_under_intuition()
    {
        var plan = Plan(Big, buffs: false);
        Assert.Equal(new RodMove(RodMoveKind.ChangeBait, Shrimp, 300), Next(plan, Ready()));
        Assert.Equal(new RodMove(RodMoveKind.Cast, FishingIds.Cast, 300), Next(plan, Ready(bait: Shrimp)));
        // The first predator done: on to the second.
        var oneDone = Ready(bait: Shrimp) with { Progress = new Dictionary<uint, int> { [300] = 3 } };
        Assert.Equal(new RodMove(RodMoveKind.ChangeBait, Fly, 301), Next(plan, oneDone));
        // Already on the second's bait: fish for it first.
        Assert.Equal(new RodMove(RodMoveKind.Cast, FishingIds.Cast, 301), Next(plan, Ready(bait: Fly) with { Progress = new Dictionary<uint, int> { [300] = 1 } }));
        // Intuition up: the target.
        var intuition = Ready(bait: Shrimp) with { Statuses = Statuses((FishingIds.IntuitionStatus, 0)) };
        Assert.Equal(new RodMove(RodMoveKind.ChangeBait, Spoon, 310), Next(plan, intuition));
        Assert.Equal(new RodMove(RodMoveKind.Cast, FishingIds.Cast, 310), Next(plan, intuition with { Bait = Spoon }));
        // All caught by our count: the game grants the intuition; wait for it.
        var all = Ready(bait: Fly) with { Progress = new Dictionary<uint, int> { [300] = 3, [301] = 2 } };
        Assert.Equal(RodMove.Idle(RodWait.Intuition, fish: 310), Next(plan, all));
    }

    [Fact]
    public void Predator_and_target_bites_depend_on_the_intuition_at_the_cast()
    {
        var plan = Plan(Big, buffs: false);
        Assert.Equal(new RodMove(RodMoveKind.Hook, FishingIds.Hook, 300), Next(plan, Biting(Tug.Strong, 10, Tackle)));
        Assert.Equal(new RodMove(RodMoveKind.Hook, FishingIds.Hook, 301), Next(plan, Biting(Tug.Weak, 10, Tackle)));
        Assert.Equal(RodMoveKind.Rest, Next(plan, Biting(Tug.Legendary, 20, Tackle)).Kind);
        Assert.Equal(new RodMove(RodMoveKind.Hook, FishingIds.Hook, 310), Next(plan, Biting(Tug.Legendary, 20, TackleUnderIntuition)));
        Assert.Equal(RodMoveKind.Rest, Next(plan, Biting(Tug.Strong, 10, TackleUnderIntuition)).Kind);
        // A predator whose count is reached isn't hooked again.
        Assert.Equal(RodMoveKind.Rest, Next(plan, Biting(Tug.Strong, 10, Tackle) with { Progress = new Dictionary<uint, int> { [300] = 3 } }).Kind);
        // Nothing this cast could bring is wanted any more: reel in.
        Assert.Equal(RodMove.RestNow(), Next(plan, LineOut(2, Tackle) with { Progress = new Dictionary<uint, int> { [300] = 3, [301] = 2 } }));
    }

    [Fact]
    public void Identical_cast_goes_on_the_next_to_last_predator()
    {
        var plan = Plan(Big);
        var caught = Ready(bait: Shrimp) with { JustCaught = true, LastCatch = 300, CanIdenticalCast = true };
        Assert.Equal(new RodMove(RodMoveKind.Use, FishingIds.IdenticalCast, 300), Next(plan, caught with { Progress = new Dictionary<uint, int> { [300] = 2 } }));
        Assert.NotEqual(FishingIds.IdenticalCast, Next(plan, caught with { Progress = new Dictionary<uint, int> { [300] = 1 } }).Id);
        Assert.NotEqual(FishingIds.IdenticalCast, Next(plan, caught with { Progress = new Dictionary<uint, int> { [300] = 2 }, Gp = 300 }).Id);
        Assert.NotEqual(FishingIds.IdenticalCast, Next(plan, caught with { Progress = new Dictionary<uint, int> { [300] = 2 }, CanIdenticalCast = false }).Id);
        // Not right after an Identical Cast catch either (the game doesn't allow consecutive ones).
        Assert.NotEqual(FishingIds.IdenticalCast, Next(plan, caught with { Progress = new Dictionary<uint, int> { [300] = 2 }, Ready = id => id != FishingIds.IdenticalCast }).Id);
    }

    [Fact]
    public void Predators_wait_until_the_intuition_would_last_into_the_window()
    {
        var plan = Plan(Big, buffs: false); // lead 226 s
        RodSnapshot Closed(TimeSpan? opensIn) => Ready(bait: Shrimp) with { Window = new CatchWindowState(false, opensIn, TimeSpan.Zero) };
        Assert.Equal(RodMove.Idle(RodWait.Window, fish: 310), Next(plan, Closed(TimeSpan.FromMinutes(10))));
        Assert.Equal(RodMoveKind.Cast, Next(plan, Closed(TimeSpan.FromSeconds(60))).Kind);
        // Opens too late, or not in the forecast: the session ends.
        Assert.Equal(RodMove.Idle(RodWait.Window, fish: 310), Next(plan, Closed(TimeSpan.FromMinutes(55))));
        Assert.Equal(CatchEnd.WindowClosed, Next(plan, Closed(TimeSpan.FromMinutes(70))).End);
        Assert.Equal(CatchEnd.WindowClosed, Next(plan, Closed(null)).End);
        // An intuition that came early is held until the window opens.
        var early = Closed(TimeSpan.FromSeconds(30)) with { Statuses = Statuses((FishingIds.IntuitionStatus, 0)) };
        Assert.Equal(RodMove.Idle(RodWait.Window, fish: 310), Next(plan, early));
        Assert.Equal(RodMoveKind.ChangeBait, Next(plan, early with { Window = CatchWindowState.Always }).Kind);
    }

    [Fact]
    public void The_last_predator_catch_waits_until_the_intuition_lasts_into_the_window()
    {
        // Big: predators expected to take 226 s; its 120 s intuition less a 30 s margin leaves 90 s.
        var plan = Plan(Big, buffs: false);
        RodSnapshot Closed(double seconds, int a, int b) => Ready(bait: Fly) with
        {
            Window = new CatchWindowState(false, TimeSpan.FromSeconds(seconds), TimeSpan.Zero),
            Progress = new Dictionary<uint, int> { [300] = a, [301] = b },
        };
        // More than one catch to go: fish on.
        Assert.Equal(new RodMove(RodMoveKind.Cast, FishingIds.Cast, 301), Next(plan, Closed(200, 3, 0)));
        // The last one would bring the intuition too early: hold it.
        Assert.Equal(RodMove.Idle(RodWait.Window, fish: 301), Next(plan, Closed(200, 3, 1)));
        Assert.Equal(RodMove.Idle(RodWait.Window, fish: 301), Next(plan, Closed(91, 3, 1)));
        Assert.Equal(RodMove.Idle(RodWait.Window, fish: 300), Next(plan, Closed(200, 2, 2) with { Bait = Shrimp }));
        // Close enough, or the window open already: catch it.
        Assert.Equal(new RodMove(RodMoveKind.Cast, FishingIds.Cast, 301), Next(plan, Closed(90, 3, 1)));
        Assert.Equal(new RodMove(RodMoveKind.Cast, FishingIds.Cast, 301), Next(plan, Closed(200, 3, 1) with { Window = CatchWindowState.Always }));

        // A last predator reached by mooching isn't mooched on while it's held either.
        var top = Plan(Top, buffs: false); // 6 × (58 + 42) s expected
        Assert.Equal(TimeSpan.FromSeconds(600), top.PrepLead);
        var caught = Ready() with { JustCaught = true, LastCatch = 400, CanMooch = true, Progress = new Dictionary<uint, int> { [401] = 5 } };
        Assert.Equal(RodMove.Idle(RodWait.Window, fish: 401), Next(top, caught with { Window = new CatchWindowState(false, TimeSpan.FromSeconds(200), TimeSpan.Zero) }));
        Assert.Equal(new RodMove(RodMoveKind.Mooch, FishingIds.Mooch, 401), Next(top, caught with { Window = new CatchWindowState(false, TimeSpan.FromSeconds(80), TimeSpan.Zero) }));
    }

    [Fact]
    public void A_predator_out_of_its_own_window_is_skipped_and_waited_for_when_none_bites()
    {
        var plan = Plan(Big, buffs: false);
        var closed = new CatchWindowState(false, TimeSpan.FromMinutes(10), TimeSpan.Zero);
        var later = closed with { OpensIn = TimeSpan.FromMinutes(25) };
        Assert.Equal(new RodMove(RodMoveKind.ChangeBait, Fly, 301), Next(plan, Ready(bait: Shrimp) with { PredatorWindow = id => id == 300 ? closed : CatchWindowState.Always }));
        // A bite of a predator out of its window isn't hooked.
        Assert.Equal(RodMoveKind.Rest, Next(plan, Biting(Tug.Strong, 10, Tackle) with { PredatorWindow = id => id == 300 ? closed : CatchWindowState.Always }).Kind);
        // Every one still needed out of its window: a window wait for the one that bites first.
        Assert.Equal(RodMove.Idle(RodWait.Predator, fish: 301), Next(plan, Ready() with { PredatorWindow = id => id == 300 ? later : closed }));
        Assert.Equal(RodMove.Idle(RodWait.Predator, fish: 300), Next(plan, Ready() with
        {
            PredatorWindow = id => id == 300 ? later : closed,
            Progress = new Dictionary<uint, int> { [301] = 2 },
        }));
        // One that doesn't bite within the forecast: no intuition to be had, the session ends.
        Assert.Equal(CatchEnd.WindowClosed, Next(plan, Ready() with { PredatorWindow = id => id == 300 ? closed with { OpensIn = null } : CatchWindowState.Always }).End);
        // Once caught, its window doesn't matter any more.
        Assert.Equal(RodMove.Idle(RodWait.Intuition, fish: 310), Next(plan, Ready() with
        {
            PredatorWindow = _ => closed with { OpensIn = null },
            Progress = new Dictionary<uint, int> { [300] = 3, [301] = 2 },
        }));
    }

    [Fact]
    public void A_predator_without_its_bait_uses_the_fallback()
    {
        var plan = Plan(Big, buffs: false);
        var onlyVersatile = Ready() with { FallbackBait = Versatile, BaitCount = id => id == Versatile ? 5 : 0 };
        Assert.Equal(new RodMove(RodMoveKind.ChangeBait, Versatile, 300), Next(plan, onlyVersatile));
        Assert.Equal(new RodMove(RodMoveKind.Quit, FishingIds.Quit, Shrimp, CatchEnd.NoBait), Next(plan, onlyVersatile with { FallbackBait = 0 }));
    }

    [Fact]
    public void A_predator_in_the_chain_is_mooched_on_only_under_intuition()
    {
        var plan = Plan(Top, buffs: false);
        var caught = Ready() with { JustCaught = true, CanMooch = true };
        Assert.Equal(new RodMove(RodMoveKind.Mooch, FishingIds.Mooch, 401), Next(plan, caught with { LastCatch = 400 }));
        Assert.Equal(new RodMove(RodMoveKind.Cast, FishingIds.Cast, 400), Next(plan, caught with { LastCatch = 401 }));
        // The catch that brings the intuition is mooched straight on to the target.
        var intuition = caught with { LastCatch = 401, Statuses = Statuses((FishingIds.IntuitionStatus, 0)) };
        Assert.Equal(new RodMove(RodMoveKind.Mooch, FishingIds.Mooch, 410), Next(plan, intuition));
        Assert.Equal(new RodMove(RodMoveKind.Hook, FishingIds.Hook, 401), Next(plan, Biting(Tug.Strong, 10, new CastContext(400, false))));
        Assert.Equal(new RodMove(RodMoveKind.Hook, FishingIds.Hook, 410), Next(plan, Biting(Tug.Legendary, 20, new CastContext(401, true))));
    }

    // ------------------------------------------------------------------ the window

    [Fact]
    public void A_closing_window_lets_the_cast_finish_then_ends_the_session()
    {
        var plan = Plan(Straight with { Window = new Uptime(1, 1, 0) }, buffs: false);
        var closed = new CatchWindowState(false, TimeSpan.FromMinutes(20), TimeSpan.Zero);
        Assert.Equal(RodMove.Idle(), Next(plan, LineOut(5, Tackle) with { Window = closed, WindowSeenOpen = true }));
        Assert.Equal(RodMoveKind.Hook, Next(plan, Biting(Tug.Weak, 8, Tackle) with { Window = closed, WindowSeenOpen = true }).Kind);
        Assert.Equal(new RodMove(RodMoveKind.Quit, FishingIds.Quit, End: CatchEnd.WindowClosed), Next(plan, Ready() with { Window = closed, WindowSeenOpen = true }));
        // Not open yet: wait for it.
        Assert.Equal(RodMove.Idle(RodWait.Window, fish: 100), Next(plan, Ready() with { Window = closed }));
    }

    // ------------------------------------------------------------------ lures and snagging

    [Fact]
    public void A_lure_fish_gets_up_to_three_lures_then_rest()
    {
        var plan = Plan(LureFish, buffs: false);
        var line = LineOut(1, Tackle);
        Assert.Equal(new RodMove(RodMoveKind.Use, FishingIds.AmbitiousLure, 500), Next(plan, line));
        Assert.Equal(RodMove.Idle(), Next(plan, line with { SinceLure = 1 }));                       // 2.5 s apart
        var two = line with { SinceLure = 3, Statuses = Statuses((FishingIds.AmbitiousLureStatus, 2)) };
        Assert.Equal(RodMoveKind.Use, Next(plan, two).Kind);
        Assert.Equal(RodMove.Idle(), Next(plan, two with { Gp = 25 }));                              // the third costs 30
        var three = line with { SinceLure = 3, Statuses = Statuses((FishingIds.AmbitiousLureStatus, 3)) };
        Assert.Equal(RodMove.RestNow(), Next(plan, three));                                          // it didn't come: recast
        Assert.Equal(RodMove.Idle(), Next(plan, three with { LureSuccess = true }));                 // it came: wait for the bite
        Assert.Equal(RodMove.Idle(), Next(plan, line with { LureSuccess = true }));
        // Under Patience the Powerful Hookset's 50 GP stay for the bite.
        var fortune = line with { Statuses = Statuses((FishingIds.AnglersFortune, 0)) };
        Assert.Equal(RodMove.Idle(), Next(plan, fortune with { Gp = 55 }));
        Assert.Equal(RodMoveKind.Use, Next(plan, fortune with { Gp = 60 }).Kind);
        // Between casts: at least the first lure's GP before casting.
        Assert.Equal(RodMove.Idle(RodWait.Gp, fish: 500), Next(plan, Ready() with { Gp = 5 }));
        Assert.Equal(RodMoveKind.Cast, Next(plan, Ready() with { Gp = 10 }).Kind);
    }

    [Fact]
    public void Snagging_is_on_exactly_for_fish_that_need_it()
    {
        IsUse(Next(Plan(SnagFish, buffs: false), Ready()), FishingIds.Snagging);
        Assert.Equal(RodMoveKind.Cast, Next(Plan(SnagFish, buffs: false), Ready() with { Statuses = Statuses((FishingIds.SnaggingStatus, 0)) }).Kind);
        IsUse(Next(Plan(Straight, buffs: false), Ready() with { Statuses = Statuses((FishingIds.SnaggingStatus, 0)) }), FishingIds.Snagging);
    }

    // ------------------------------------------------------------------ buffs and GP

    [Fact]
    public void Mooch_routes_make_large_fish_with_the_buff_the_gp_allows()
    {
        var plan = Plan(Goose);
        var mooch2Busy = Ready() with { Cooldown = id => id == FishingIds.MoochII ? 100 : 0 };
        IsUse(Next(plan, mooch2Busy with { Gp = 700 }), FishingIds.PatienceII);
        IsUse(Next(plan, mooch2Busy with { Gp = 400 }), FishingIds.Patience);
        IsUse(Next(plan, mooch2Busy with { Gp = 700, Statuses = Statuses((FishingIds.AnglersArt, 5)) }), FishingIds.MakeshiftBait);
        // A one-link chain: Prize Catch is cheaper than Patience II for one large fish.
        var one = Plan(OneLink);
        IsUse(Next(one, mooch2Busy with { Gp = 400 }), FishingIds.PrizeCatch);
        // With Mooch II ready an average-sized link still goes on: no Patience, Chum instead (keeping Mooch II's 100 GP).
        IsUse(Next(one, Ready() with { Gp = 400 }), FishingIds.Chum);
        Assert.Equal(RodMoveKind.Cast, Next(one, Ready() with { Gp = 240 }).Kind);
        // Under Patience nothing more for large fish; Chum while its GP leaves the hookset's.
        var fortune = mooch2Busy with { Gp = 700, Statuses = Statuses((FishingIds.AnglersFortune, 0)) };
        IsUse(Next(plan, fortune), FishingIds.Chum);
        Assert.Equal(RodMoveKind.Cast, Next(plan, fortune with { Statuses = Statuses((FishingIds.AnglersFortune, 0), (FishingIds.ChumStatus, 0)) }).Kind);
        Assert.Equal(RodMoveKind.Cast, Next(plan, fortune with { Gp = 140 }).Kind);
    }

    [Fact]
    public void Chum_never_takes_the_gp_patience_still_needs()
    {
        var plan = Plan(Goose);
        var low = Ready() with { Gp = 200, Cooldown = id => id == FishingIds.MoochII ? 100 : 0 };
        Assert.Equal(RodMoveKind.Cast, Next(plan, low).Kind);
        IsUse(Next(Plan(Straight), Ready() with { Gp = 100 }), FishingIds.Chum);
        Assert.Equal(RodMoveKind.Cast, Next(Plan(Straight), Ready() with { Gp = 99 }).Kind);
    }

    [Fact]
    public void Thaliaks_favor_fills_gp_without_overcapping_or_starving_makeshift_bait()
    {
        IsUse(Next(Plan(Straight), Ready() with { Gp = 700, Statuses = Statuses((FishingIds.AnglersArt, 3)) }), FishingIds.ThaliaksFavor);
        IsUse(Next(Plan(Straight), Ready() with { Gp = 800, Statuses = Statuses((FishingIds.AnglersArt, 3)) }), FishingIds.Chum);
        var goose = Plan(Goose);
        var busy = Ready() with { Gp = 300, Cooldown = id => id == FishingIds.MoochII ? 100 : 0 };
        IsUse(Next(goose, busy with { Statuses = Statuses((FishingIds.AnglersArt, 6)) }), FishingIds.MakeshiftBait);
        IsUse(Next(goose, busy with { Statuses = Statuses((FishingIds.AnglersArt, 8)) }), FishingIds.ThaliaksFavor);
    }

    [Fact]
    public void A_cordial_tops_up_the_gp_the_plan_wants()
    {
        var hi = new Cordial(12669, false, 400, 180);
        var plan = Plan(Goose);
        var busy = Ready() with { Cooldown = id => id == FishingIds.MoochII ? 100 : 0, Cordials = new[] { hi } };
        Assert.Equal(new RodMove(RodMoveKind.Drink, Cordial: hi), Next(plan, busy with { Gp = 300 }));
        IsUse(Next(plan, busy with { Gp = 650 }), FishingIds.PatienceII);
        Assert.NotEqual(RodMoveKind.Drink, Next(Plan(Goose, cordials: false), busy with { Gp = 300 }).Kind);
        Assert.NotEqual(RodMoveKind.Drink, Next(Plan(Straight, buffs: false), busy with { Gp = 0 }).Kind);   // nothing to spend GP on
    }

    [Fact]
    public void Surface_slap_follows_a_catch_of_the_slap_fish()
    {
        var plan = Plan(Bombard);
        var slapped = Ready(bait: Shrimp) with { JustCaught = true, LastCatch = 600, CanSurfaceSlap = true };
        Assert.Equal(new RodMove(RodMoveKind.Use, FishingIds.SurfaceSlap, 600), Next(plan, slapped));
        Assert.NotEqual(FishingIds.SurfaceSlap, Next(plan, slapped with { Gp = 150 }).Id);
        Assert.NotEqual(FishingIds.SurfaceSlap, Next(plan, slapped with { CanSurfaceSlap = false }).Id);
        Assert.NotEqual(FishingIds.SurfaceSlap, Next(plan, slapped with { Statuses = Statuses((FishingIds.SurfaceSlapStatus, 0)) }).Id);
        Assert.NotEqual(FishingIds.SurfaceSlap, Next(plan, slapped with { JustCaught = false }).Id);
        // The slap fish is hooked only while a slap is affordable.
        Assert.Equal(new RodMove(RodMoveKind.Hook, FishingIds.Hook, 600), Next(plan, Biting(Tug.Strong, 10, Tackle)));
        Assert.Equal(RodMoveKind.Rest, Next(plan, Biting(Tug.Strong, 10, Tackle) with { Gp = 150 }).Kind);
        Assert.Equal(new RodMove(RodMoveKind.Hook, FishingIds.Hook, 610), Next(plan, Biting(Tug.Legendary, 10, Tackle)));
    }

    [Fact]
    public void Under_surface_slap_the_slap_fish_is_neither_hooked_nor_waited_for()
    {
        var plan = Plan(Bombard);
        var slap = Statuses((FishingIds.SurfaceSlapStatus, 0));
        // It can't bite now: a "!!" is another fish, and hooking that would end the slap.
        Assert.Equal(RodMoveKind.Rest, Next(plan, Biting(Tug.Strong, 10, Tackle) with { Statuses = slap }).Kind);
        Assert.Equal(new RodMove(RodMoveKind.Hook, FishingIds.Hook, 610), Next(plan, Biting(Tug.Legendary, 10, Tackle) with { Statuses = slap }));
        // The line comes in once the target's window is past (17 s), not the slap fish's (33 s).
        Assert.Equal(RodMove.RestNow(), Next(plan, LineOut(17.5, Tackle) with { Statuses = slap }));
        Assert.Equal(RodMove.Idle(), Next(plan, LineOut(17.5, Tackle)));
    }

    [Fact]
    public void Identical_cast_follows_a_target_catch_while_more_are_wanted()
    {
        var plan = Plan(Straight);
        var caught = Ready() with { JustCaught = true, LastCatch = 100, CanIdenticalCast = true, Left = 2 };
        Assert.Equal(new RodMove(RodMoveKind.Use, FishingIds.IdenticalCast, 100), Next(plan, caught));
        Assert.Equal(RodMoveKind.Quit, Next(plan, caught with { Left = 0 }).Kind);
        Assert.NotEqual(FishingIds.IdenticalCast, Next(plan, caught with { Gp = 349 }).Id);
        Assert.NotEqual(FishingIds.IdenticalCast, Next(Plan(Straight with { Big = true }), caught).Id);
    }
}
