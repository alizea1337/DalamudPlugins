using System;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Keystroke.Alerts;
using Keystroke.Game;
using Keystroke.Input;
using Keystroke.Rewards;

namespace Keystroke.Automation;

/// <summary>
/// The typing engine. Runs entirely on the framework tick: reads the board, picks a word, sends
/// one key at a time with humanized delays, verifies each key against the input field, recovers
/// from mismatches, submits with Enter. Queueing, rewards and overlays are layered on top later.
/// </summary>
internal sealed unsafe class KeyboundController : IAutomationController
{
    private enum Phase { Idle, Pick, Clearing, AwaitClear, Typing, AwaitKey, BeforeEnter, AwaitSubmit, BetweenWords, Blocked, Recovering }

    private static readonly TimeSpan KeyEchoTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan SubmitTimeout = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan RecoveryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BlockTail = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan BlockLead = TimeSpan.FromMilliseconds(500);

    private readonly Func<Configuration> config;
    private readonly KeySender keys = new();
    private readonly Typist typist;
    private readonly QueueDriver queue;
    private readonly Notifier notifier;
    private DateTime leaveAt = DateTime.MaxValue;
    private int loopStartGames;

    // Per-game bookkeeping for the history record and reward deltas.
    private DateTime gameStartedAt;
    private int gameStartMgp;
    private int gameStartMgc;
    private int gameStartKeys;
    private int gameStartWords;
    private int gameStartMistakes;
    private DateTime recordAt = DateTime.MaxValue;
    private bool mgcWarned;

    private Phase phase = Phase.Idle;
    private DateTime nextAt = DateTime.MinValue;
    private DateTime lastTick = DateTime.UtcNow;
    private DateTime blockedUntil = DateTime.MinValue;
    private KeyboundBoard board = KeyboundBoard.Empty;
    private WordSlot? target;
    private string answer = "";
    private int progress;
    private int echoFailures;
    private int clearAttempts;
    private bool reacted;
    private bool wasInDuty;
    private bool resultCounted;
    private bool loopActive;
    private bool wasInLobby;

    public AutomationState State { get; private set; } = AutomationState.Idle;
    public string StatusDetail { get; private set; } = "Ready";
    public string CurrentPrompt => target?.Word ?? "";
    public string CurrentAnswer => answer.Length == 0 ? "" : answer[..Math.Min(progress, answer.Length)];
    public int LoopRemaining { get; private set; }
    public SessionStats Session { get; } = new();
    public string? LastError { get; private set; }
    public CofferOpener Coffers { get; } = new();
    public RewardStore History { get; }
    public KeyboundBoard Board => board;

    // Rolling measurement of real key spacing so profile changes are visible in the UI.
    private readonly double[] keyIntervals = new double[12];
    private int keyIntervalCount;
    private int keyIntervalHead;
    private DateTime lastKeyAt = DateTime.MinValue;
    private DateTime lastKeyMeasure = DateTime.MinValue;

    public double MeasuredMsPerKey
    {
        get
        {
            if (keyIntervalCount == 0 || (DateTime.UtcNow - lastKeyAt).TotalSeconds > 5) return 0;
            double sum = 0;
            for (var i = 0; i < keyIntervalCount; i++) sum += keyIntervals[i];
            return sum / keyIntervalCount;
        }
    }

    private void RecordKey(DateTime now)
    {
        if (lastKeyAt != DateTime.MinValue && (now - lastKeyAt).TotalMilliseconds < 3000)
        {
            keyIntervals[keyIntervalHead] = (now - lastKeyAt).TotalMilliseconds;
            keyIntervalHead = (keyIntervalHead + 1) % keyIntervals.Length;
            if (keyIntervalCount < keyIntervals.Length) keyIntervalCount++;
        }
        lastKeyAt = now;
        lastKeyMeasure = now;
    }

    public KeyboundController(Func<Configuration> config)
    {
        this.config = config;
        typist = new Typist(() => config().Typing);
        queue = new QueueDriver(config);
        notifier = new Notifier(config);
        History = new RewardStore();
    }

    private static (int mgp, int mgc) Currency()
    {
        var inv = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        return inv == null ? (0, 0) : (inv->GetInventoryItemCount(Keybound.MgpItemId), inv->GetInventoryItemCount(Keybound.MgcItemId));
    }

    // ------------------------------------------------------------------ commands

    public void Start()
    {
        LastError = null;
        loopActive = false;
        LoopRemaining = 0;
        loopStartGames = Session.GamesCompleted;
        Arm();
    }

    public void StartLoop(int games)
    {
        LastError = null;
        LoopRemaining = Math.Max(1, games);
        loopActive = true;
        loopStartGames = Session.GamesCompleted;
        Arm();
    }

    public void StartUnlimited()
    {
        LastError = null;
        LoopRemaining = -1;
        loopActive = true;
        loopStartGames = Session.GamesCompleted;
        Arm();
    }

    /// <summary>Begin playing if in the duty, queue if in the lobby, otherwise wait.</summary>
    private void Arm()
    {
        autoQueueSuppressed = false;
        if (board.InDuty) { BeginPlaying(); return; }
        if (InLobby) { BeginQueue(); return; }
        SetState(AutomationState.WaitingForDuty, "Enter Keybound Square or the Keybound Brawler duty to begin");
    }

    /// <summary>Set when the user stops in the lobby so auto-queue doesn't immediately restart.</summary>
    private bool autoQueueSuppressed;

    private bool InLobby => Keybound.IsLobby((ushort)Services.ClientState.TerritoryType);

    private void BeginQueue()
    {
        queue.Begin();
        SetState(AutomationState.Queued, queue.Status);
    }

    public void Pause()
    {
        if (State is AutomationState.Playing or AutomationState.WaitingForDuty or AutomationState.Queued)
        {
            if (State == AutomationState.Queued) queue.Reset();
            SetState(AutomationState.Paused, "Paused");
        }
    }

    public void Resume()
    {
        if (State != AutomationState.Paused) return;
        Arm();
    }

    public void Stop()
    {
        loopActive = false;
        LoopRemaining = 0;
        target = null;
        answer = "";
        progress = 0;
        phase = Phase.Idle;
        leaveAt = DateTime.MaxValue;
        queue.Reset();
        autoQueueSuppressed = true;
        SetState(AutomationState.Idle, "Ready");
    }

    // ------------------------------------------------------------------ tick

    public void Tick()
    {
        var now = DateTime.UtcNow;
        var dt = now - lastTick;
        lastTick = now;

        board = KeyboundReader.Read();
        Coffers.Tick();
        HandleTransitions();
        TickQueue(now);
        TickResult(now);

        if (State != AutomationState.Playing) return;
        Session.TypingTime += dt;

        if (!board.InDuty) return;
        if (board.ResultVisible)
        {
            SetState(AutomationState.Finishing, "Result screen");
            return;
        }

        if (!config().Typing.BackgroundInput && !keys.GameIsForeground())
        {
            StatusDetail = "Waiting for the game window to be focused";
            return;
        }

        if (CheckBlockingCasts(now)) return;
        if (now < nextAt) return;

        try
        {
            Step(now);
        }
        catch (Exception e)
        {
            Services.Log.Error(e, "Typing step failed");
            Fail("Typing step failed: " + e.Message);
        }
    }

    private void HandleTransitions()
    {
        var inDuty = board.InDuty;
        if (inDuty && !wasInDuty)
        {
            resultCounted = false;
            gameStartedAt = DateTime.Now;
            (gameStartMgp, gameStartMgc) = Currency();
            gameStartKeys = Session.KeysTyped;
            gameStartWords = Session.WordsTyped;
            gameStartMistakes = Session.Mistakes;
            recordAt = DateTime.MaxValue;
            if (State == AutomationState.WaitingForDuty || (State == AutomationState.Idle && config().Automation.AutoPlayOnEntry))
                BeginPlaying();
        }
        else if (!inDuty && wasInDuty)
        {
            if (recordAt != DateTime.MaxValue) FinalizeGame();
            target = null;
            answer = "";
            progress = 0;
            leaveAt = DateTime.MaxValue;
            if (State is AutomationState.Playing or AutomationState.Finishing)
            {
                if (loopActive && LoopRemaining != 0)
                    SetState(AutomationState.WaitingForDuty, LoopRemaining > 0 ? $"Waiting for the next game ({LoopRemaining} left)" : "Waiting for the next game");
                else
                    SetState(AutomationState.Idle, "Ready");
            }
        }
        wasInDuty = inDuty;

        var inLobby = InLobby;
        if (inLobby && !wasInLobby)
        {
            autoQueueSuppressed = false;
            // Arrived in Keybound Square with a loop in progress: queue the next game.
            if (State == AutomationState.WaitingForDuty && loopActive) BeginQueue();
        }
        wasInLobby = inLobby;

        // Auto-queue applies whenever we're idle in the lobby with the option on (also when the
        // option is switched on while already standing there), until the user presses Stop.
        if (inLobby && State == AutomationState.Idle && config().Automation.AutoQueueOnLobbyEntry && !autoQueueSuppressed)
        {
            LoopRemaining = -1;
            loopActive = true;
            loopStartGames = Session.GamesCompleted;
            BeginQueue();
        }

        if (inDuty && board.ResultVisible && !resultCounted)
        {
            resultCounted = true;
            Session.GamesCompleted++;
            if (loopActive && LoopRemaining > 0) LoopRemaining--;
            Services.Log.Information("Game completed (session total {Count})", Session.GamesCompleted);

            // Currency settles within a couple of seconds of the result screen; record then.
            recordAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(2000);

            if (loopActive)
            {
                var why = LoopRemaining == 0 ? "count reached" : "";
                if (why.Length > 0 || StopConditionMet(out why))
                {
                    loopActive = false;
                    LoopRemaining = 0;
                    Services.Log.Information("Loop finished: {Why}", why);
                    notifier.Notify(AlertKind.LoopFinished, "Loop finished", $"Stopped: {why}. {Session.GamesCompleted} games this session, {Session.MgpGained:N0} MGP.");
                }
            }
            if (config().Automation.AutoLeaveAfterResult)
                leaveAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(2500 + Random.Shared.Next(2500));
        }

        if (recordAt != DateTime.MaxValue && DateTime.UtcNow >= recordAt) FinalizeGame();
    }

    /// <summary>Writes the history record for the game that just ended and runs reward alerts.</summary>
    private void FinalizeGame()
    {
        recordAt = DateTime.MaxValue;
        var (mgp, mgc) = Currency();
        var mgpGain = Math.Max(0, mgp - gameStartMgp);
        var mgcGain = Math.Max(0, mgc - gameStartMgc);
        var keys = Session.KeysTyped - gameStartKeys;
        var minutes = Math.Max(0.1, (DateTime.Now - gameStartedAt).TotalMinutes);
        var record = new GameRecord
        {
            At = gameStartedAt,
            DurationSec = (DateTime.Now - gameStartedAt).TotalSeconds,
            Score = board.Score,
            MgpGained = mgpGain,
            MgcGained = mgcGain,
            Keys = keys,
            Words = Session.WordsTyped - gameStartWords,
            Mistakes = Session.Mistakes - gameStartMistakes,
            Wpm = keys / 5.0 / minutes,
            Profile = config().Typing.Profile.ToString(),
            Instant = config().Typing.InstantMode,
        };
        Session.MgpGained += mgpGain;
        Session.MgcGained += mgcGain;
        History.Append(record);
        Services.Log.Information("Recorded game: +{Mgp} MGP, +{Mgc} MGC, score {Score}, {Wpm:0} WPM", mgpGain, mgcGain, record.Score, record.Wpm);
        notifier.Notify(AlertKind.GameResult, "Game finished", $"+{mgpGain:N0} MGP, +{mgcGain:N0} MGC, score {record.Score:N0}");
        CheckMgcCap(mgc);
    }

    private void CheckMgcCap(int mgc)
    {
        var alerts = config().Alerts;
        if (!alerts.MgcCapWarning) return;
        var cm = FFXIVClientStructs.FFXIV.Client.Game.CurrencyManager.Instance();
        var cap = cm == null ? 0u : cm->GetItemMaxCount(Keybound.MgcItemId);
        if (cap == 0) return;
        var threshold = cap * alerts.MgcCapWarningPercent / 100.0;
        if (mgc >= cap)
        {
            if (!mgcWarned || mgcWarnedAtCap == false)
                notifier.Notify(AlertKind.MgcWarning, "MGC is capped", $"You hold {mgc:N0} / {cap:N0} MGC. Further games won't add MGC until you spend some.");
            mgcWarned = true;
            mgcWarnedAtCap = true;
        }
        else if (mgc >= threshold)
        {
            if (!mgcWarned)
                notifier.Notify(AlertKind.MgcWarning, "MGC near the cap", $"{mgc:N0} / {cap:N0} MGC ({mgc * 100.0 / cap:0}%). Consider spending before the cap.");
            mgcWarned = true;
            mgcWarnedAtCap = false;
        }
        else
        {
            mgcWarned = false;
            mgcWarnedAtCap = false;
        }
    }

    private bool mgcWarnedAtCap;

    private void TickQueue(DateTime now)
    {
        if (State != AutomationState.Queued) return;
        if (!InLobby)
        {
            if (board.InDuty) return; // HandleTransitions switches to Playing
            SetState(AutomationState.WaitingForDuty, "Return to Keybound Square to queue");
            queue.Reset();
            return;
        }
        queue.Tick();
        StatusDetail = queue.Status;
        if (queue.Current == QueueDriver.Phase.Failed) Fail(queue.Error ?? "Queueing failed");
    }

    private void TickResult(DateTime now)
    {
        if (!board.InDuty || !board.ResultVisible || now < leaveAt) return;
        leaveAt = DateTime.MaxValue;
        if (!AtkClicker.ClickButton(Keybound.ResultAddon, Keybound.ResultExitButton))
            Services.Log.Warning("Could not press Exit on the result screen; the game will time out on its own");
    }

    private bool StopConditionMet(out string why)
    {
        var sc = config().Automation.StopConditions;
        why = "";
        if (sc.StopAfterGamesEnabled && Session.GamesCompleted - loopStartGames >= sc.StopAfterGames) { why = $"{sc.StopAfterGames} games played"; return true; }
        if (sc.StopAtMgpEnabled)
        {
            var mgp = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance()->GetInventoryItemCount(Keybound.MgpItemId);
            if (mgp >= sc.StopAtMgp) { why = $"MGP reached {mgp:N0}"; return true; }
        }
        if (sc.StopAtTimeEnabled)
        {
            var local = DateTime.Now;
            var target = new DateTime(local.Year, local.Month, local.Day, sc.StopAtHour, sc.StopAtMinute, 0);
            if (local >= target) { why = $"it is past {sc.StopAtHour:00}:{sc.StopAtMinute:00}"; return true; }
        }
        return false;
    }

    private void BeginPlaying()
    {
        phase = Phase.Pick;
        nextAt = DateTime.MinValue;
        echoFailures = 0;
        SetState(AutomationState.Playing, "Playing");
    }

    // ------------------------------------------------------------------ typing state machine

    private void Step(DateTime now)
    {
        var typing = config().Typing;
        var typed = board.TypedText;

        switch (phase)
        {
            case Phase.Idle:
            case Phase.Pick:
                if (!board.InputVisible || board.Slots.Count == 0)
                {
                    StatusDetail = board.Slots.Count == 0 ? "Waiting for words" : "Waiting for the input field";
                    nextAt = now + TimeSpan.FromMilliseconds(100);
                    return;
                }
                target = PickTarget(typed);
                if (target == null)
                {
                    nextAt = now + TimeSpan.FromMilliseconds(100);
                    return;
                }
                answer = Normalize(target.Word, typing);
                progress = CommonPrefix(typed, answer);
                StatusDetail = $"Typing \"{target.Word}\"";
                if (progress < typed.Length)
                {
                    phase = Phase.Clearing;
                    return;
                }
                // A freshly chosen word gets a human reaction + reading window; a continued prefix doesn't.
                if (progress == 0 && !typing.InstantMode && !reacted)
                {
                    reacted = true;
                    nextAt = now + typist.ReactionDelay(answer.Length);
                    return;
                }
                reacted = false;
                if (typing.InstantMode)
                {
                    // The game only counts characters it receives as keystrokes, so "instant" means
                    // every remaining key in one burst, then Enter once the field echoes the word.
                    for (var i = progress; i < answer.Length; i++)
                    {
                        if (!keys.SendChar(answer[i], typing.BackgroundInput))
                        {
                            Fail($"Can't type '{answer[i]}' on the current keyboard layout");
                            return;
                        }
                    }
                    Session.KeysTyped += answer.Length - progress;
                    progress = answer.Length;
                    phase = Phase.AwaitKey;
                    nextAt = now + TimeSpan.FromMilliseconds(16);
                    return;
                }
                phase = Phase.Typing;
                return;

            case Phase.Clearing:
                // Typed text diverged from the target. Esc clears the whole field in one press
                // (only while it has text: on an empty field Esc opens the quit prompt). The word
                // is then retyped from the start.
                if (typed.Length == 0)
                {
                    progress = 0;
                    clearAttempts = 0;
                    phase = Phase.Typing;
                    return;
                }
                if (clearAttempts < 2)
                {
                    clearAttempts++;
                    keys.SendEscape(typing.BackgroundInput);
                    Session.Mistakes++;
                    phase = Phase.AwaitClear;
                    nextAt = now + TimeSpan.FromMilliseconds(16);
                    return;
                }
                // Esc didn't clear it: fall back to backspacing.
                keys.SendBackspace(typing.BackgroundInput);
                nextAt = now + typist.NextKeyDelay();
                return;

            case Phase.AwaitClear:
                if (typed.Length == 0)
                {
                    progress = 0;
                    clearAttempts = 0;
                    phase = Phase.Typing;
                    nextAt = now + typist.BetweenWordsDelay();
                    return;
                }
                if (now - nextAt > TimeSpan.FromMilliseconds(600)) phase = Phase.Clearing;
                return;

            case Phase.Typing:
                if (!TargetStillValid())
                {
                    phase = Phase.Pick;
                    return;
                }
                if (progress >= answer.Length)
                {
                    phase = Phase.BeforeEnter;
                    nextAt = now + typist.BeforeEnterDelay();
                    return;
                }
                if (!keys.SendChar(answer[progress], typing.BackgroundInput))
                {
                    Fail($"Can't type '{answer[progress]}' on the current keyboard layout");
                    return;
                }
                RecordKey(now);
                if (config().Advanced.VerboseLogging)
                    Services.Log.Information("key '{C}' {Idx}/{Len} profile={Profile} median={Median}ms measured={Measured:0}ms",
                        answer[progress], progress + 1, answer.Length, typing.Profile, typing.MedianKeyDelayMs, MeasuredMsPerKey);
                progress++;
                Session.KeysTyped++;
                phase = Phase.AwaitKey;
                nextAt = now + TimeSpan.FromMilliseconds(16);
                return;

            case Phase.AwaitKey:
                // Wait for the game to echo the key(s) into the field before continuing.
                if (!board.InputVisible)
                {
                    // Field hidden (phase transition, cutscene): start over when it returns.
                    target = null; answer = ""; progress = 0; echoFailures = 0;
                    phase = Phase.Pick;
                    return;
                }
                if (string.Equals(typed, answer[..progress], StringComparison.OrdinalIgnoreCase))
                {
                    echoFailures = 0;
                    phase = Phase.Typing;
                    nextAt = now + (progress >= answer.Length ? TimeSpan.Zero : typist.NextKeyDelay());
                    return;
                }
                if (typed.Length > 0 && !answer.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                {
                    progress = CommonPrefix(typed, answer);
                    phase = Phase.Clearing;
                    return;
                }
                if (now - nextAt > KeyEchoTimeout)
                {
                    // Nothing arrived: the game is ignoring input (Sensory Jammer / Cursed Gaze window,
                    // or a stall). Wait it out quietly rather than backspacing into a field that
                    // isn't listening; resend from the echoed prefix once it responds.
                    echoFailures++;
                    progress = CommonPrefix(typed, answer);
                    StatusDetail = $"Input isn't responding; waiting ({echoFailures * KeyEchoTimeout.TotalSeconds:0}s)";
                    if (echoFailures == 1) Services.Log.Information("Key echo timed out; waiting for input to respond");
                    if (echoFailures >= 10)
                    {
                        if (config().Advanced.AutomaticRecovery)
                        {
                            phase = Phase.Recovering;
                            nextAt = now + RecoveryDelay;
                            Services.Log.Warning("Input unresponsive for 15 s; recovering");
                        }
                        else Fail("Keys aren't reaching the game");
                        return;
                    }
                    phase = Phase.Typing;
                    nextAt = now + TimeSpan.FromMilliseconds(250);
                }
                return;

            case Phase.BeforeEnter:
                keys.SendEnter(typing.BackgroundInput);
                Session.KeysTyped++;
                phase = Phase.AwaitSubmit;
                nextAt = now + TimeSpan.FromMilliseconds(16);
                return;

            case Phase.AwaitSubmit:
                if (typed.Length == 0 || !TargetStillValid())
                {
                    Session.WordsTyped++;
                    target = null;
                    answer = "";
                    progress = 0;
                    phase = Phase.BetweenWords;
                    nextAt = now + typist.BetweenWordsDelay();
                    return;
                }
                if (now - nextAt > SubmitTimeout)
                {
                    // Enter didn't clear the field; the word may have been wrong for this slot.
                    Services.Log.Warning("Submit of \"{Word}\" was not accepted; clearing", answer);
                    Session.Mistakes++;
                    progress = 0;
                    phase = Phase.Clearing;
                }
                return;

            case Phase.BetweenWords:
                phase = Phase.Pick;
                return;

            case Phase.Recovering:
                echoFailures = 0;
                progress = CommonPrefix(typed, answer);
                phase = target != null && TargetStillValid() ? Phase.Typing : Phase.Pick;
                return;

            case Phase.Blocked:
                phase = Phase.Pick;
                return;
        }
    }

    private WordSlot? PickTarget(string typed)
    {
        var visible = board.Slots.Where(s => s.Visible && s.Word.Length > 0).ToList();
        if (visible.Count == 0) return null;

        // Continue whatever the field already matches, so a half-typed word is never thrown away.
        if (typed.Length > 0)
        {
            var cont = visible.FirstOrDefault(s => s.Word.StartsWith(typed, StringComparison.OrdinalIgnoreCase));
            if (cont != null) return cont;
        }

        // The barrier phrase (boss phase) protects the party and takes a while to type, so it comes
        // first unless Instant mode makes order irrelevant. Then enemies mid-cast (typing their word
        // interrupts the attack), furthest-along cast first; then the weakest enemy, nearest last.
        var instant = config().Typing.InstantMode;
        return visible
            .OrderByDescending(s => !instant && s.IsBarrier ? 1 : 0)
            .ThenByDescending(s => s.IsCasting ? 1 : 0)
            .ThenByDescending(s => s.CastProgress)
            .ThenBy(s => s.HpPercent)
            .ThenByDescending(s => s.ScreenY)
            .First();
    }

    private bool TargetStillValid()
        => target != null && board.Slots.Any(s => s.EntityId == target.EntityId && s.Visible && s.Word == target.Word);

    private static string Normalize(string word, TypingSettings t)
    {
        var w = word.Trim().ToLowerInvariant();
        if (t.SkipOptionalPunctuation)
            w = new string(w.Where(c => c is not ('?' or '!' or '-' or '\'')).ToArray());
        return w;
    }

    private static int CommonPrefix(string typed, string answer)
    {
        var n = 0;
        while (n < typed.Length && n < answer.Length && char.ToLowerInvariant(typed[n]) == char.ToLowerInvariant(answer[n])) n++;
        return n;
    }

    // ------------------------------------------------------------------ blocking casts

    private bool CheckBlockingCasts(DateTime now)
    {
        try
        {
            foreach (var obj in Services.Objects)
            {
                if (obj is not IBattleChara c || !c.IsCasting) continue;
                var id = c.CastActionId;
                if (id != Keybound.CursedGazeActionId && !Keybound.SensoryJammerActionIds.Contains(id)) continue;
                var remaining = TimeSpan.FromSeconds(Math.Max(0, c.TotalCastTime - c.CurrentCastTime));
                if (remaining <= BlockLead)
                {
                    var until = now + remaining + BlockTail;
                    if (until > blockedUntil) blockedUntil = until;
                }
            }
        }
        catch (Exception e)
        {
            Services.Log.Verbose(e, "Cast check failed");
        }

        // Fallback by cast name from the board (English names; the ids above are preferred).
        foreach (var slot in board.Slots)
        {
            if (!slot.IsCasting || !Keybound.IsBlockingCastName(slot.CastName)) continue;
            if (slot.CastProgress >= 95)
            {
                var until = now + BlockTail;
                if (until > blockedUntil) blockedUntil = until;
            }
        }

        if (now < blockedUntil)
        {
            StatusDetail = $"Input blocked by an attack ({(blockedUntil - now).TotalSeconds:0.0}s)";
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ helpers

    private void SetState(AutomationState state, string detail)
    {
        if (State != state) Services.Log.Debug("State {Old} -> {New}: {Detail}", State, state, detail);
        State = state;
        StatusDetail = detail;
    }

    private void Fail(string message)
    {
        LastError = message;
        phase = Phase.Idle;
        SetState(AutomationState.Error, message);
        Services.Log.Error("Automation stopped: {Message}", message);
        notifier.Notify(AlertKind.Error, "Keystroke stopped", message);
    }

}
