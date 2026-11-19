using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using Provenance.Core.Fishing;
using Provenance.Core.Gathering;
using Provenance.Data;

namespace Provenance.Integrations.Fishing;

/// <summary>Why a fishing session ended (the rod is reeled in by then).</summary>
internal enum AnglerSignal
{
    /// <summary>Still fishing, or never started.</summary>
    None,
    /// <summary>Enough of the target caught (<see cref="AnglerJob.Left"/> reached 0).</summary>
    Done,
    /// <summary>"The fish have become wary of your presence": cast from a stand at least 10 yalms away.</summary>
    Wary,
    /// <summary>"The fish sense something amiss": this fishing hole is done for a while.</summary>
    Amiss,
    /// <summary>The bait ran out, or the game says no bait is selected.</summary>
    NoBait,
    /// <summary>The line can't be cast from here (no water in front, a bad stand).</summary>
    CantCast,
    /// <summary>The target's time or weather window closed before enough were caught.</summary>
    WindowClosed,
    /// <summary>Stopped on request (<see cref="Angler.RequestStop"/> or <see cref="Angler.Stop"/>).</summary>
    Stopped,
}

/// <summary>One fish landed, as the game reports it after the catch.</summary>
/// <param name="ItemId">The fish's item id (collectables already mapped back from their +500,000 id).</param>
/// <param name="Amount">How many came in (Double and Triple Hook land several).</param>
/// <param name="Moochable">The game offers Mooch with it.</param>
internal readonly record struct FishCatch(uint ItemId, int Amount, bool Collectable, bool Large, bool Moochable);

/// <summary>A fishing session at the stand the player is on: catch <see cref="Target"/> until <see cref="Left"/> is 0.</summary>
/// <param name="Target">The fish the run wants. Its mooch chain and the fish needed for Fisher's Intuition are caught along the way.</param>
/// <param name="Spot">The fishing hole the player stands at.</param>
/// <param name="WeatherTable">The spot zone's weather table, for the target's window.</param>
/// <param name="Bait">Tackle to cast with: the chain's own bait, or Versatile Lure when that's all there is.</param>
/// <param name="FishData">The fish database (for predators and chain fish).</param>
/// <param name="Left">How many more of the target the run wants; the session ends at 0.</param>
/// <param name="UseBuffs">Chum, Patience, Surface Slap, Identical Cast and lures when the catch plan calls for them.</param>
/// <param name="UseCordials">Drink cordials for GP between casts.</param>
internal sealed record AnglerJob(
    FishInfo Target,
    FishingSpotInfo Spot,
    IReadOnlyList<(uint Weather, int Rate)> WeatherTable,
    uint Bait,
    Func<uint, FishInfo?> FishData,
    Func<int> Left,
    bool UseBuffs,
    bool UseCordials)
{
    /// <summary>
    /// Every fish living at the spot (<c>GatherData.FishAt(Spot.Id)</c>). Optional: with it, predators are
    /// checked to bite at this hole, and a tug no other fish there has is hooked whenever it comes.
    /// </summary>
    public IReadOnlyList<FishInfo> SpotFish { get; init; } = Array.Empty<FishInfo>();

    /// <summary>
    /// Bait for a predator (a fish caught for Fisher's Intuition) whose own bait isn't in the bags; 0 for
    /// none. The target's own tackle is always <see cref="Bait"/>: running out of it ends the session as NoBait.
    /// </summary>
    public uint FallbackBait { get; init; } = FishAccess.VersatileLure;
}

/// <summary>
/// Rod fishing at one stand, at AutoHook's level: selects the bait, casts, reads the bite (tug and time
/// since the cast), hooks only what the catch plan wants (the target, its mooch chain, the fish for
/// Fisher's Intuition) with the right hookset and Rests on everything else, mooches, keeps buffs up and
/// drinks cordials between casts, and watches the fishing hole's warnings. It never moves the player:
/// standing somewhere castable, on foot, as Fisher, with the bait in the bags is the caller's job.
/// The plan and every decision come from <see cref="CatchPlanner"/> and <see cref="CatchDecider"/>; this
/// class reads the game, applies AutoHook's delays (hook 100–200 ms after the bite, Rest 1.5–2 s after an
/// unwanted one, 0.6–1 s after a buff, decisions every 500 ms between casts) and sends one action per tick.
/// Ported from AutoHook (BSD-3, Puni.sh). Framework thread; <see cref="Tick"/> every frame.
/// </summary>
internal sealed unsafe class Angler : IDisposable
{
    // AutoHook's default delays (Configuration.cs: DelayBetweenHook, DelayBeforeCancel, DelayBetweenCasts).
    private const int HookDelayMin = 100, HookDelayMax = 200;
    private const int RestDelayMin = 1500, RestDelayMax = 2000;
    private const int LockMin = 600, LockMax = 1000;
    private static readonly TimeSpan ReadyThrottle = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LineOutThrottle = TimeSpan.FromMilliseconds(200);
    /// <summary>At a bite, how long to wait for the tug animation before deciding without it.</summary>
    private static readonly TimeSpan TugWait = TimeSpan.FromMilliseconds(500);
    /// <summary>How long a hook (or Rest) keeps being tried while the fish bites.</summary>
    private static readonly TimeSpan BiteRetry = TimeSpan.FromSeconds(3);
    /// <summary>A cast or mooch sent; the line should be going out by then.</summary>
    private static readonly TimeSpan CastConfirm = TimeSpan.FromSeconds(4);
    /// <summary>Cast unusable this long between casts: this stand can't cast.</summary>
    private static readonly TimeSpan CastBlockedLimit = TimeSpan.FromSeconds(12);
    /// <summary>A mooch on offer but unusable this long is let go (the opportunity lasts about 15 s).</summary>
    private static readonly TimeSpan MoochWaitLimit = TimeSpan.FromSeconds(5);
    /// <summary>All predators caught by our count and no intuition this long after: count again.</summary>
    private static readonly TimeSpan IntuitionGrace = TimeSpan.FromSeconds(10);
    /// <summary>Reeling in and stowing the rod never takes longer; past it the session ends anyway.</summary>
    private static readonly TimeSpan EndingLimit = TimeSpan.FromSeconds(30);
    private const int MaxFailedCasts = 3, MaxUnknownTugs = 3, MaxBaitTries = 5, MaxSwimbaitTries = 2, MaxIntuitionMisses = 3;

    private static readonly uint[] FisherActions =
    {
        FishingIds.Mooch, FishingIds.MoochII, FishingIds.Patience, FishingIds.PatienceII, FishingIds.Chum, FishingIds.Snagging,
        FishingIds.Collect, FishingIds.PrecisionHookset, FishingIds.PowerfulHookset, FishingIds.SurfaceSlap, FishingIds.IdenticalCast,
        FishingIds.PrizeCatch, FishingIds.ThaliaksFavor, FishingIds.MakeshiftBait, FishingIds.AmbitiousLure, FishingIds.ModestLure,
    };

    private readonly RodHooks hooks = new();
    private readonly RodMessages messages = new();
    private readonly Random rng = new();
    private bool wired;
    private bool disposed;

    // The session.
    private AnglerJob? job;
    private CatchPlan? plan;
    private uint territory;
    private CatchEnd ending;
    private DateTime? endingSince;
    private bool letGo;
    private bool windowSeenOpen;
    private readonly Dictionary<uint, int> progress = new();
    private bool hadIntuition;
    private DateTime? predatorsDoneAt;
    private int intuitionMisses;
    private DateTime? castBlockedSince;
    private uint blockedAction;
    private DateTime? moochWaitSince;
    private int failedCasts;
    private int unknownTugs;
    private uint baitTried;
    private int baitTries;
    private int swimbaitTries;
    private DateTime lockUntil;
    private DateTime nextDecision;
    private (RodMove Move, DateTime At, DateTime Until)? scheduled;

    // The rod, followed across sessions (a cast and its catch are the game's, whoever started them).
    private FishingState state;
    private RodCast? cast;
    private bool castFromAction;
    private DateTime castAt;
    private DateTime? castSentUntil;
    private uint moochedFish;
    private Tug tug;
    private DateTime? biteAt;
    private bool biteDecided;
    private uint lastCatch;
    private bool justCaught;
    private DateTime lureAt = DateTime.MinValue;
    private bool lureSuccess;

    public string Status { get; private set; } = "";
    /// <summary>Something that a retry won't fix (hooks unavailable, the job has no usable plan); the run stops on it.</summary>
    public string? Failure { get; private set; }
    /// <summary>Why the last session ended; <see cref="AnglerSignal.None"/> while one runs.</summary>
    public AnglerSignal Signal { get; private set; }
    /// <summary>A session runs (including reeling in at its end).</summary>
    public bool Active { get; private set; }
    /// <summary>The fisher stance is up (the rod is out: <c>ConditionFlag.Fishing</c>).</summary>
    public bool RodOut => Services.Condition[ConditionFlag.Fishing];
    /// <summary>
    /// Waiting at the stand with the rod in for a window: the target's opens soon and nothing has to be caught
    /// first, or the fish Fisher's Intuition needs first are out of theirs. Not fishing time.
    /// </summary>
    public bool WaitingForWindow { get; private set; }
    /// <summary>Waiting at the stand for GP the catch plan can't do without.</summary>
    public bool WaitingForGp { get; private set; }
    /// <summary>Casts in the current session.</summary>
    public int Casts { get; private set; }
    /// <summary>Fisher's Intuition is up, and how long it lasts.</summary>
    public TimeSpan? IntuitionLeft
        => Services.Objects.LocalPlayer?.StatusList.FirstOrDefault(s => s.StatusId == FishingIds.IntuitionStatus) is { } s
            ? TimeSpan.FromSeconds(Math.Max(0, s.RemainingTime))
            : null;

    /// <summary>The bait that ran out when the session ended as <see cref="AnglerSignal.NoBait"/> (a predator's may differ from the job's); 0 otherwise.</summary>
    public uint MissingBait { get; private set; }

    /// <summary>The catch plan of the current (or last) session.</summary>
    public CatchPlan? Plan => plan;

    /// <summary>Every fish landed (the target or anything else).</summary>
    public event Action<FishCatch>? Caught;
    /// <summary>A caught fish (item id) left the bags as bait: mooched, or banked as swimbait.</summary>
    public event Action<uint>? UsedAsBait;

    /// <summary>Starts a session (ends any running one). Signal goes back to None.</summary>
    public void Begin(AnglerJob job)
    {
        this.job = job;
        Signal = AnglerSignal.None;
        Failure = null;
        Active = false;
        Casts = 0;
        MissingBait = 0;
        ResetSession();
        if (disposed) return;
        if (!Wire())
        {
            Failure = hooks.Failure ?? "Rod fishing can't run: the fishing hooks aren't available.";
            Status = Failure;
            return;
        }

        var me = Services.Objects.LocalPlayer;
        var request = new CatchRequest(job.Target, job.Bait, job.SpotFish, job.FishData, LearnedActions(GameState.JobLevel(FishAccess.Fisher)),
            (int)(me?.MaxGp ?? 0), job.WeatherTable, job.UseBuffs, job.UseCordials);
        plan = CatchPlanner.Plan(request);
        if (plan.Problem is { } problem)
        {
            Failure = Describe(problem, job);
            Status = Failure;
            Services.Log.Warning($"[Angler] No plan for {Name(job.Target.ItemId)}: {Failure}");
            return;
        }
        messages.WatchLureFish(plan.Routes.SelectMany(r => r.Chain.Append(r.Fish)).Where(f => CatchPlanner.LureOf(f) != LureKind.None).Select(f => f.ItemId));
        territory = Services.ClientState.TerritoryType;
        hadIntuition = HasStatus(FishingIds.IntuitionStatus);
        Active = true;
        Status = $"Fishing for {Name(job.Target.ItemId)}";
        Services.Log.Information($"[Angler] Fishing for {Name(job.Target.ItemId)} at {job.Spot.Name} ({job.Spot.Id}) with {Name(plan.TargetRoute.Bait)}: "
            + $"chain [{string.Join(", ", plan.TargetRoute.Chain.Select(f => f.ItemId))}], predators [{string.Join(", ", plan.Predators.Select(p => $"{p.Fish.ItemId}x{p.Count}"))}], "
            + $"chain windows [{string.Join(", ", plan.Window.Links.Select(f => f.ItemId))}], predator windows [{string.Join(", ", plan.PredatorWindows.Keys)}], "
            + $"lead {plan.PrepLead.TotalSeconds:0} s (last catch {plan.LastCatchLead.TotalSeconds:0} s), GP target {plan.Buffs.GpTarget}");
    }

    /// <summary>Every frame.</summary>
    public void Tick()
    {
        if (!Active || plan == null || job == null) return;
        try
        {
            Step(DateTime.UtcNow);
        }
        catch (Exception e)
        {
            Services.Log.Error(e, "[Angler] Tick");
            Fail("Rod fishing stopped on an unexpected error (see the Dalamud log).");
        }
    }

    /// <summary>Lands the fish on the line if one is hooked, then reels in; the session ends as <see cref="AnglerSignal.Stopped"/>.</summary>
    public void RequestStop()
    {
        if (!Active) return;
        BeginEnding(CatchEnd.Stopped, true);
        nextDecision = DateTime.MinValue;
    }

    /// <summary>Reels in now (a bite is let go) and ends the session as <see cref="AnglerSignal.Stopped"/>.</summary>
    public void Stop()
    {
        if (!Active) return;
        BeginEnding(CatchEnd.Stopped, true);
        letGo = true;
        // A hook about to go out is called off and the bite decided again (Rest now); a Rest waiting out its
        // delay goes out now.
        if (scheduled is { Move.Kind: RodMoveKind.Hook })
        {
            scheduled = null;
            biteDecided = false;
        }
        else if (scheduled is { Move.Kind: RodMoveKind.Rest } rest)
            scheduled = (rest.Move, DateTime.UtcNow, rest.Until);
        nextDecision = DateTime.MinValue;
    }

    /// <summary>The Cast action can be used where the player stands.</summary>
    public static bool CanCastHere() => RodGame.Status(FishingIds.Cast) == 0;

    /// <summary>Removes the hooks. Always called on plugin unload, whether or not a run is active.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Active = false;
        if (wired)
        {
            hooks.CastStarted -= OnCastStarted;
            hooks.Animation -= OnAnimation;
            hooks.Landed -= OnLanded;
            messages.Seen -= OnMessage;
        }
        messages.Dispose();
        hooks.Dispose();
    }

    private void Raise(FishCatch c) => Caught?.Invoke(c);
    private void RaiseBait(uint id) => UsedAsBait?.Invoke(id);

    // ------------------------------------------------------------------ the tick

    private void Step(DateTime now)
    {
        var me = Services.Objects.LocalPlayer;
        var c = Services.Condition;
        if (me == null || me.IsDead || c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51]
            || c[ConditionFlag.OccupiedInCutSceneEvent] || c[ConditionFlag.WatchingCutscene] || c[ConditionFlag.WatchingCutscene78])
            return;
        if (Services.ClientState.TerritoryType != territory)
        {
            // Teleported or carried off: no rod can be out any more.
            End(ending != CatchEnd.None ? ending : CatchEnd.CantCast);
            return;
        }
        if (ending != CatchEnd.None && endingSince is { } since && now - since > EndingLimit)
        {
            Services.Log.Warning($"[Angler] Reeling in took over {EndingLimit.TotalSeconds:0} s; ending the session anyway");
            End(ending);
            return;
        }

        var read = RodGame.Read();
        var phase = RodGame.Phase(read.State);
        Observe(read, now);
        FollowIntuition(now);

        // Collect is kept off, but should the collectable prompt come up, keep the fish.
        if (read.State == FishingState.ConfirmingCollectable)
        {
            if (now >= lockUntil && Interact.Yes())
            {
                Services.Log.Debug("[Angler] Kept the catch at the collectable prompt");
                Lock(now);
            }
            return;
        }

        if (scheduled is { } plannedMove)
        {
            if (now >= plannedMove.At) RunScheduled(plannedMove, phase, now);
            return;
        }

        if (phase == RodPhase.Bite)
        {
            // The bite is decided at once (the hook itself still waits out an action lock).
            if (biteDecided || tug == Tug.Unknown && biteAt is { } bite && now - bite < TugWait) return;
            biteDecided = true;
            CountTug();
            if (!Active) return;
        }
        else
        {
            if (now < lockUntil) return;
            // Entering battle cancels fishing (LogMessage 1113) and no line goes out until the fight is over:
            // wait it out between casts instead of counting it against this stand. Ending (a pause or a stop)
            // still goes on.
            if (ending == CatchEnd.None && phase is (RodPhase.Ready or RodPhase.Idle) && c[ConditionFlag.InCombat])
            {
                ForgetPendingCast();
                castBlockedSince = null;
                failedCasts = 0;
                // A window that opens (and closes) during the fight still counts as seen.
                if (plan!.Window.IsOpen(DateTimeOffset.UtcNow)) windowSeenOpen = true;
                Status = "In combat; fishing after the fight";
                return;
            }
            if (castSentUntil is { } until)
            {
                if (phase is RodPhase.Ready or RodPhase.Idle && now < until) return;
                castSentUntil = null;
                if (phase is RodPhase.Ready or RodPhase.Idle) CastDidntGoOut("the line didn't go out");
            }
            if (phase is not (RodPhase.LineOut or RodPhase.Ready or RodPhase.Idle) || now < nextDecision) return;
            nextDecision = now + (phase == RodPhase.LineOut ? LineOutThrottle : ReadyThrottle);
        }

        var snapshot = Snapshot(read, phase, me, now);
        Apply(CatchDecider.Next(plan!, snapshot), phase, snapshot, now);
    }

    /// <summary>Follows the fishing state: a new cast, a bite, the end of a cast.</summary>
    private void Observe(RodRead read, DateTime now)
    {
        if (read.State == state) return;
        var previous = RodGame.Phase(state);
        state = read.State;
        switch (RodGame.Phase(read.State))
        {
            case RodPhase.CastingOut:
            case RodPhase.LineOut when previous is RodPhase.Ready or RodPhase.Idle:
                LineWentOut(read, now);
                break;
            case RodPhase.Bite:
                biteAt = now;
                biteDecided = false;
                break;
            case RodPhase.Ready or RodPhase.Idle:
                biteAt = null;
                break;
        }
    }

    private void LineWentOut(RodRead read, DateTime now)
    {
        castSentUntil = null;
        failedCasts = 0;
        castBlockedSince = null;
        // Normally the cast was seen going out at UseAction (the bite timer's start); otherwise start it now.
        if (!castFromAction)
        {
            var mooched = read.MoochCast ? lastCatch : 0;
            StartCast(mooched, mooched, now);
        }
        castFromAction = false;
        if (moochedFish != 0)
        {
            var fish = moochedFish;
            moochedFish = 0;
            if (Active) RaiseBait(fish);
        }
        if (Active) Casts++;
    }

    /// <summary>A cast goes out: with tackle (<paramref name="withFish"/> 0) or a fish; <paramref name="usedUp"/> leaves the bags with it.</summary>
    private void StartCast(uint withFish, uint usedUp, DateTime now)
    {
        cast = new RodCast(new CastContext(withFish, HasStatus(FishingIds.IntuitionStatus)), HasStatus(FishingIds.ChumStatus),
            HasStatus(FishingIds.IdenticalCastStatus) ? lastCatch : 0);
        castAt = now;
        tug = Tug.Unknown;
        biteAt = null;
        biteDecided = false;
        lureAt = DateTime.MinValue;
        lureSuccess = false;
        justCaught = false;
        moochedFish = usedUp;
        moochWaitSince = null;
    }

    /// <summary>
    /// Fisher's Intuition coming or going starts a new count of the predators (catches made under it don't
    /// count). All predators caught by our count but no intuition after a grace: the game counted
    /// differently (an earlier zone change), so count again.
    /// </summary>
    private void FollowIntuition(DateTime now)
    {
        var has = HasStatus(FishingIds.IntuitionStatus);
        if (has != hadIntuition)
        {
            hadIntuition = has;
            progress.Clear();
            predatorsDoneAt = null;
            if (has) intuitionMisses = 0;
            Services.Log.Debug($"[Angler] Fisher's Intuition {(has ? "gained" : "lost")}");
        }
        if (plan is { NeedsIntuition: true } p && !has && p.Predators.All(r => progress.GetValueOrDefault(r.Fish.ItemId) >= r.Count))
        {
            predatorsDoneAt ??= now;
            if (now - predatorsDoneAt > IntuitionGrace)
            {
                progress.Clear();
                predatorsDoneAt = null;
                if (++intuitionMisses >= MaxIntuitionMisses)
                    Fail($"Fisher's Intuition for {Name(p.Target.ItemId)} didn't come after catching its fish {MaxIntuitionMisses} times over.");
                else Services.Log.Information("[Angler] Every predator caught but no Fisher's Intuition; counting them again");
            }
        }
        else predatorsDoneAt = null;
    }

    private RodSnapshot Snapshot(RodRead read, RodPhase phase, IPlayerCharacter me, DateTime now)
    {
        var at = DateTimeOffset.UtcNow;
        var window = plan!.Window.At(at);
        if (window.Open) windowSeenOpen = true;
        var predatorWindows = plan.PredatorWindows.ToDictionary(kv => kv.Key, kv => kv.Value.At(at));
        if (read.Bait == baitTried) baitTries = 0;
        var useCordials = job!.UseCordials && plan.Buffs.Cordials;
        var moochGone = moochWaitSince is { } waited && now - waited > MoochWaitLimit;
        return new RodSnapshot
        {
            Phase = phase,
            RodOut = RodOut,
            Bait = read.Bait,
            Swimbait = read.Swimbait != 0 && swimbaitTries < MaxSwimbaitTries,
            FallbackBait = job.FallbackBait,
            BaitCount = GameState.BagCount,
            LastCatch = lastCatch,
            JustCaught = justCaught,
            CanMooch = read.CanMooch && !moochGone,
            CanMoochII = read.CanMoochII && !moochGone,
            CanIdenticalCast = read.CanIdenticalCast,
            CanSurfaceSlap = read.CanSurfaceSlap,
            Statuses = RodGame.Statuses(),
            Gp = (int)me.CurrentGp,
            MaxGp = (int)me.MaxGp,
            Ready = RodGame.Ready,
            Cooldown = RodGame.Cooldown,
            Left = Math.Max(0, job.Left()),
            Progress = new Dictionary<uint, int>(progress),
            Window = window,
            WindowSeenOpen = windowSeenOpen,
            PredatorWindow = id => predatorWindows.TryGetValue(id, out var w) ? w : CatchWindowState.Always,
            Cast = cast,
            SinceCast = (now - castAt).TotalSeconds,
            Tug = tug,
            LureSuccess = lureSuccess,
            SinceLure = lureAt == DateTime.MinValue ? double.MaxValue : (now - lureAt).TotalSeconds,
            Cordials = useCordials ? Cordials.Ready().ToList() : Array.Empty<Cordial>(),
            Ending = ending,
            LetGo = letGo,
        };
    }

    // ------------------------------------------------------------------ carrying out a move

    private void Apply(RodMove move, RodPhase phase, RodSnapshot s, DateTime now)
    {
        if (move.End != CatchEnd.None) BeginEnding(move.End);
        if (move.End == CatchEnd.NoBait && move.Fish != 0) MissingBait = move.Fish;
        WaitingForWindow = move.Kind == RodMoveKind.Wait && move.Wait is RodWait.Window or RodWait.Predator;
        WaitingForGp = move.Kind == RodMoveKind.Wait && move.Wait == RodWait.Gp;
        if (move is not { Kind: RodMoveKind.Wait, Wait: RodWait.Action }) castBlockedSince = null;
        if (move is not { Kind: RodMoveKind.Wait, Wait: RodWait.Action, Id: FishingIds.Mooch or FishingIds.MoochII }) moochWaitSince = null;

        switch (move.Kind)
        {
            case RodMoveKind.Wait:
                Waiting(move, phase, s, now);
                return;
            case RodMoveKind.Use:
                if (!RodGame.Use(move.Id))
                {
                    nextDecision = now + LineOutThrottle;
                    return;
                }
                Status = $"Using {FishAccess.ActionName(move.Id)}";
                Services.Log.Debug($"[Angler] {FishAccess.ActionName(move.Id)} (GP {s.Gp})");
                if (move.Id is FishingIds.AmbitiousLure or FishingIds.ModestLure) lureAt = now;
                else Lock(now);
                return;
            case RodMoveKind.Drink:
                if (move.Cordial is { } cordial && Cordials.Drink(cordial))
                {
                    Status = $"Drinking {Cordials.Name(cordial)}";
                    Services.Log.Debug($"[Angler] Drank {Cordials.Name(cordial)} at {s.Gp} GP");
                }
                Lock(now);
                return;
            case RodMoveKind.ChangeBait:
                if (s.Bait == move.Id)
                {
                    // Only a selected swimbait is in the way; should the bait command not clear it, the next
                    // cast simply uses the swimbait up (see Snapshot).
                    swimbaitTries++;
                }
                else
                {
                    if (baitTried != move.Id)
                    {
                        baitTried = move.Id;
                        baitTries = 0;
                    }
                    if (++baitTries > MaxBaitTries)
                    {
                        Fail($"Couldn't put {Name(move.Id)} on the line.");
                        return;
                    }
                }
                RodGame.ChangeBait(move.Id);
                Status = $"Putting {Name(move.Id)} on the line";
                Lock(now);
                return;
            case RodMoveKind.Cast or RodMoveKind.Mooch:
                var failedBefore = failedCasts;
                if (!RodGame.Use(move.Id))
                {
                    // Once, also when the game already said so while the action was pressed.
                    if (failedCasts == failedBefore) CastDidntGoOut("the game refused it");
                    nextDecision = now + TimeSpan.FromSeconds(1);
                    return;
                }
                castSentUntil ??= now + CastConfirm;
                Status = move.Kind == RodMoveKind.Mooch ? $"Mooching for {Name(move.Fish)}" : CastStatus(move.Fish, s);
                return;
            case RodMoveKind.Hook:
                var hookAt = now + TimeSpan.FromMilliseconds(rng.Next(HookDelayMin, HookDelayMax));
                scheduled = (move, hookAt > lockUntil ? hookAt : lockUntil, now + BiteRetry);
                Status = $"Hooking {Name(move.Fish)}";
                Services.Log.Debug($"[Angler] {s.Tug} bite at {s.SinceCast:0.0} s: {FishAccess.ActionName(move.Id)} for {Name(move.Fish)}");
                return;
            case RodMoveKind.Rest when move.AfterBite:
                scheduled = (move, now + TimeSpan.FromMilliseconds(rng.Next(RestDelayMin, RestDelayMax)), now + BiteRetry);
                Status = "Letting the bite go";
                Services.Log.Debug($"[Angler] {s.Tug} bite at {s.SinceCast:0.0} s: not wanted, Rest");
                return;
            case RodMoveKind.Rest:
                if (!RodGame.Use(FishingIds.Rest)) return;
                Status = ending != CatchEnd.None ? "Reeling in" : "Reeling in to cast again";
                Services.Log.Debug($"[Angler] Rest at {s.SinceCast:0.0} s{(ending != CatchEnd.None ? $" ({ending})" : "")}");
                return;
            case RodMoveKind.Quit:
                if (RodGame.Use(FishingIds.Quit)) Status = "Stowing the rod";
                Lock(now);
                return;
            case RodMoveKind.End:
                End(move.End);
                return;
        }
    }

    private void Waiting(RodMove move, RodPhase phase, RodSnapshot s, DateTime now)
    {
        // A mooch that doesn't become usable is let go after a while (the snapshot stops offering it).
        if (move is { Wait: RodWait.Action, Id: FishingIds.Mooch or FishingIds.MoochII })
            moochWaitSince ??= now;
        // Cast (or another action the next cast needs) unusable for long: this stand can't fish.
        else if (move is { Wait: RodWait.Action } && move.Id != FishingIds.Quit && ending == CatchEnd.None)
        {
            if (blockedAction != move.Id) castBlockedSince = null;
            blockedAction = move.Id;
            castBlockedSince ??= now;
            if (now - castBlockedSince > CastBlockedLimit)
            {
                Services.Log.Information($"[Angler] {FishAccess.ActionName(move.Id)} unusable for {CastBlockedLimit.TotalSeconds:0} s (status {RodGame.Status(move.Id)})");
                BeginEnding(CatchEnd.CantCast);
                return;
            }
        }
        var target = Name(plan!.Target.ItemId);
        Status = move.Wait switch
        {
            RodWait.Window when s.Intuition => $"Holding Fisher's Intuition until {target} bites ({Until(s.Window.OpensIn)})",
            // The last catch for Fisher's Intuition waits until the intuition would last into the window.
            RodWait.Window when move.Fish != 0 && move.Fish != plan.Target.ItemId => $"Holding back the last {Name(move.Fish)} for Fisher's Intuition: {target} bites {Until(s.Window.OpensIn)}",
            RodWait.Window => $"Waiting for {target} to bite ({Until(s.Window.OpensIn)})",
            RodWait.Predator => $"Waiting for {Name(move.Fish)} to bite ({Until(s.PredatorWindow(move.Fish).OpensIn)}), caught first for Fisher's Intuition",
            RodWait.Intuition => "Waiting for Fisher's Intuition",
            RodWait.Gp => $"Waiting for GP for the lure ({s.Gp} GP)",
            RodWait.Action when move.Id == FishingIds.Quit => "Stowing the rod",
            RodWait.Action when move.Id == FishingIds.Cast => "Waiting until a cast is possible",
            RodWait.Action when move.Id is FishingIds.Mooch or FishingIds.MoochII => "Mooching",
            _ when phase == RodPhase.LineOut => $"Waiting for a bite ({s.SinceCast:0} s)",
            _ => Status,
        };
    }

    /// <summary>"in 8 min", "in under a minute", "in 2 h 05 min" (a mooch chain's or a predator's window can be hours away).</summary>
    private static string Until(TimeSpan? opensIn)
    {
        if (opensIn is not { } t) return "not soon";
        if (t < TimeSpan.FromMinutes(1)) return "in under a minute";
        var minutes = (int)Math.Ceiling(t.TotalMinutes);
        return minutes < 60 ? $"in {minutes} min" : $"in {minutes / 60} h {minutes % 60:00} min";
    }

    private string CastStatus(uint fish, RodSnapshot s)
    {
        if (plan is { NeedsIntuition: true } p && !s.Intuition)
        {
            var have = p.Predators.Sum(r => Math.Min(r.Count, s.Caught(r.Fish.ItemId)));
            return $"Casting for {Name(fish)}: Fisher's Intuition {have} of {p.Predators.Sum(r => r.Count)}";
        }
        return $"Casting for {Name(fish)}";
    }

    /// <summary>A hook or Rest that waited out its delay: sent while the fish still bites (a Rest also while the line is out).</summary>
    private void RunScheduled((RodMove Move, DateTime At, DateTime Until) planned, RodPhase phase, DateTime now)
    {
        scheduled = null;
        var move = planned.Move;
        if (move.Kind == RodMoveKind.Hook)
        {
            if (phase != RodPhase.Bite) return;
            // The hookset may have become unusable meanwhile (GP, Patience ran out): plain Hook then.
            var id = RodGame.Ready(move.Id) ? move.Id : FishingIds.Hook;
            if (RodGame.Ready(id) && RodGame.Use(id)) return;
        }
        else
        {
            if (phase is not (RodPhase.Bite or RodPhase.LineOut)) return;
            if (RodGame.Use(FishingIds.Rest)) return;
        }
        if (now < planned.Until) scheduled = (move, now + TimeSpan.FromMilliseconds(100), planned.Until);
    }

    /// <summary>Three bites in a row without a readable tug: the bite hook doesn't work with this game version.</summary>
    private void CountTug()
    {
        if (tug != Tug.Unknown)
        {
            unknownTugs = 0;
            return;
        }
        Services.Log.Warning($"[Angler] A bite without a readable tug at {(DateTime.UtcNow - castAt).TotalSeconds:0.0} s");
        if (++unknownTugs >= MaxUnknownTugs)
            Fail("Rod fishing can't read the bites (the bite animation hook isn't working; the game may have been updated).");
    }

    /// <summary>A cast that didn't go out counts once toward <see cref="MaxFailedCasts"/> (the 4-second check doesn't count it again).</summary>
    private void CastDidntGoOut(string why)
    {
        Services.Log.Debug($"[Angler] Cast failed: {why}");
        ForgetPendingCast();
        // A fight stops every cast (LogMessage 1113); that says nothing about this stand.
        if (Services.Condition[ConditionFlag.InCombat]) return;
        if (++failedCasts >= MaxFailedCasts && ending == CatchEnd.None) BeginEnding(CatchEnd.CantCast);
    }

    /// <summary>A cast sent but not seen going out is dropped: its bite timer and the fish it would have mooched.</summary>
    private void ForgetPendingCast()
    {
        castSentUntil = null;
        castFromAction = false;
        moochedFish = 0;
    }

    private void Lock(DateTime now) => lockUntil = now + TimeSpan.FromMilliseconds(rng.Next(LockMin, LockMax + 1));

    // ------------------------------------------------------------------ game events

    private void OnCastStarted(uint actionId)
    {
        // A queued repeat of the same press while the line is already going out doesn't start a new cast.
        var read = RodGame.Read();
        if (RodGame.Phase(read.State) is not (RodPhase.Ready or RodPhase.Idle)) return;
        var now = DateTime.UtcNow;
        // A mooch casts with the fish just landed, which leaves the bags. A cast with a swimbait selected goes
        // out with that fish (banked earlier: nothing more leaves the bags).
        if (actionId == FishingIds.Cast) StartCast(read.Swimbait, 0, now);
        else StartCast(lastCatch, lastCatch, now);
        castFromAction = true;
        castSentUntil = now + CastConfirm;
    }

    private void OnAnimation(Tug seen)
    {
        // Other animations reset the game's tug; the bite's own stays known until the next cast.
        if (seen != Tug.Unknown) tug = seen;
    }

    private void OnLanded(uint itemId, int amount, bool collectable, bool large, bool moochable)
    {
        lastCatch = itemId;
        justCaught = true;
        if (!Active || plan == null || amount <= 0) return;
        if (!HasStatus(FishingIds.IntuitionStatus) && plan.Predators.Any(p => p.Fish.ItemId == itemId))
            progress[itemId] = progress.GetValueOrDefault(itemId) + amount;
        Services.Log.Debug($"[Angler] Landed {Name(itemId)} x{amount}{(large ? " (large)" : "")}{(collectable ? " (collectable)" : "")}");
        Raise(new FishCatch(itemId, amount, collectable, large, moochable));
    }

    private void OnMessage(RodMessage message)
    {
        if (!Active) return;
        switch (message)
        {
            case RodMessage.Wary:
                Services.Log.Information("[Angler] The fish became wary");
                BeginEnding(CatchEnd.Wary);
                break;
            case RodMessage.Amiss:
                Services.Log.Information("[Angler] The fish sense something amiss");
                BeginEnding(CatchEnd.Amiss);
                break;
            case RodMessage.NoBait:
                if (ending == CatchEnd.None) MissingBait = RodGame.Read().Bait is var bait and not 0 ? bait : job?.Bait ?? 0;
                BeginEnding(CatchEnd.NoBait);
                break;
            case RodMessage.NoFishHere:
                BeginEnding(CatchEnd.CantCast);
                break;
            case RodMessage.CastFailed:
                // Only for a cast still waiting to go out: one already counted (refused, or timed out) isn't counted again.
                if (castSentUntil != null) CastDidntGoOut("unable to fish at this time");
                break;
            case RodMessage.BaitTooHigh:
                // The refused change leaves the old bait on the line: name the one that was being put on.
                var equipped = RodGame.Read().Bait;
                var tooHigh = baitTried != 0 && baitTried != equipped ? baitTried : equipped != 0 ? equipped : job?.Bait ?? 0;
                Fail($"Your Fisher level is too low for {Name(tooHigh)}.");
                break;
            case RodMessage.LureHit:
                // The lure's general success counts only when the lure fish's own line is unknown.
                if (!messages.KnowsLureFish) lureSuccess = true;
                break;
            case RodMessage.LureFish:
                lureSuccess = true;
                break;
        }
    }

    // ------------------------------------------------------------------ session state

    private bool Wire()
    {
        if (!wired)
        {
            wired = true;
            hooks.CastStarted += OnCastStarted;
            hooks.Animation += OnAnimation;
            hooks.Landed += OnLanded;
            messages.Seen += OnMessage;
            messages.Attach();
        }
        return hooks.Ensure();
    }

    private void ResetSession()
    {
        plan = null;
        ending = CatchEnd.None;
        endingSince = null;
        letGo = false;
        windowSeenOpen = false;
        progress.Clear();
        predatorsDoneAt = null;
        intuitionMisses = 0;
        castBlockedSince = null;
        blockedAction = 0;
        moochWaitSince = null;
        failedCasts = 0;
        unknownTugs = 0;
        baitTried = 0;
        baitTries = 0;
        swimbaitTries = 0;
        scheduled = null;
        lockUntil = DateTime.MinValue;
        nextDecision = DateTime.MinValue;
        WaitingForWindow = false;
        WaitingForGp = false;
    }

    /// <summary>The session ends for this reason once the rod is in (the first reason wins unless forced).</summary>
    private void BeginEnding(CatchEnd reason, bool force = false)
    {
        if (reason == CatchEnd.None || ending != CatchEnd.None && !force) return;
        if (ending == CatchEnd.None) endingSince = DateTime.UtcNow;
        ending = reason;
        nextDecision = DateTime.MinValue;
    }

    /// <summary>A failure the run stops on; the rod is still reeled in and stowed first.</summary>
    private void Fail(string message)
    {
        Failure ??= message;
        Status = message;
        Services.Log.Warning($"[Angler] {message}");
        BeginEnding(CatchEnd.Stopped, true);
        letGo = true;
        scheduled = null;
    }

    private void End(CatchEnd reason)
    {
        Active = false;
        Signal = reason switch
        {
            CatchEnd.Done => AnglerSignal.Done,
            CatchEnd.Wary => AnglerSignal.Wary,
            CatchEnd.Amiss => AnglerSignal.Amiss,
            CatchEnd.NoBait => AnglerSignal.NoBait,
            CatchEnd.CantCast => AnglerSignal.CantCast,
            CatchEnd.WindowClosed => AnglerSignal.WindowClosed,
            _ => AnglerSignal.Stopped,
        };
        WaitingForWindow = false;
        WaitingForGp = false;
        scheduled = null;
        if (Failure == null)
            Status = Signal switch
            {
                AnglerSignal.Done => "Caught enough",
                AnglerSignal.Wary => "The fish became wary",
                AnglerSignal.Amiss => "The fish sense something amiss",
                AnglerSignal.NoBait => "Out of bait",
                AnglerSignal.CantCast => "Can't cast from here",
                AnglerSignal.WindowClosed => "The fish stopped biting",
                _ => "Stopped",
            };
        Services.Log.Information($"[Angler] Session over: {Signal} after {Casts} casts");
    }

    // ------------------------------------------------------------------ helpers

    private static bool HasStatus(uint status)
        => Services.Objects.LocalPlayer?.StatusList.Any(s => s.StatusId == status) == true;

    private static HashSet<uint> LearnedActions(int level) => FisherActions.Where(id => FishAccess.Learned(id, level)).ToHashSet();

    private static string Name(uint item)
    {
        if (item == 0) return "no bait";
        var name = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(item)?.Name.ExtractText();
        return string.IsNullOrEmpty(name) ? $"item {item}" : name;
    }

    private static string Describe(CatchProblem problem, AnglerJob job)
    {
        var target = Name(job.Target.ItemId);
        var spot = job.Spot.Name.Length > 0 ? job.Spot.Name : "this fishing hole";
        return problem.Kind switch
        {
            CatchProblemKind.Spear => $"{Name(problem.Item)} is caught by spearfishing, not with a rod.",
            CatchProblemKind.Ocean => $"{Name(problem.Item)} is only caught on ocean fishing voyages.",
            CatchProblemKind.NoBait => $"No bait is known for {Name(problem.Item)}.",
            CatchProblemKind.UnknownFish => $"{Name(problem.Item)} is missing from the fish data, so {target} can't be fished for.",
            CatchProblemKind.MissingAction => $"Catching {Name(problem.Item)} needs {FishAccess.ActionName(problem.Action)}, which this Fisher hasn't learned yet.",
            CatchProblemKind.NestedIntuition => $"{Name(problem.Item)} needs Fisher's Intuition of its own, so {target} can't be fished for.",
            CatchProblemKind.NotAtSpot => $"{Name(problem.Item)} doesn't bite at {spot}, so Fisher's Intuition for {target} can't be built there.",
            _ => $"{target} can't be fished for.",
        };
    }
}
