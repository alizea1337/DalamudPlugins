using System;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Keystroke.Game;
using Keystroke.Input;

namespace Keystroke.Automation;

/// <summary>
/// The typing engine. Runs entirely on the framework tick: reads the board, picks a word, sends
/// one key at a time with humanized delays, verifies each key against the input field, recovers
/// from mismatches, submits with Enter. Queueing, rewards and overlays are layered on top later.
/// </summary>
internal sealed unsafe class KeyboundController : IAutomationController
{
    private enum Phase { Idle, Pick, Clearing, Typing, AwaitKey, BeforeEnter, AwaitSubmit, BetweenWords, Blocked, Recovering }

    private static readonly TimeSpan KeyEchoTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan SubmitTimeout = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan RecoveryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BlockTail = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan BlockLead = TimeSpan.FromMilliseconds(500);

    private readonly Func<Configuration> config;
    private readonly KeySender keys = new();
    private readonly Typist typist;
    private readonly QueueDriver queue;
    private DateTime leaveAt = DateTime.MaxValue;
    private int loopStartGames;

    private Phase phase = Phase.Idle;
    private DateTime nextAt = DateTime.MinValue;
    private DateTime lastTick = DateTime.UtcNow;
    private DateTime blockedUntil = DateTime.MinValue;
    private KeyboundBoard board = KeyboundBoard.Empty;
    private WordSlot? target;
    private string answer = "";
    private int progress;
    private int echoFailures;
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
    public KeyboundBoard Board => board;

    public KeyboundController(Func<Configuration> config)
    {
        this.config = config;
        typist = new Typist(() => config().Typing);
        queue = new QueueDriver(config);
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
        if (board.InDuty) { BeginPlaying(); return; }
        if (InLobby && (loopActive || config().Automation.AutoQueueOnLobbyEntry)) { BeginQueue(); return; }
        SetState(AutomationState.WaitingForDuty, InLobby ? "Press Start again or enable auto-queue to register" : "Enter the Keybound Brawler duty to begin");
    }

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
            if (State == AutomationState.WaitingForDuty || (State == AutomationState.Idle && config().Automation.AutoPlayOnEntry))
                BeginPlaying();
        }
        else if (!inDuty && wasInDuty)
        {
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
            // Arrived in Keybound Square: continue a loop, or auto-queue if the user asked for that.
            if (State == AutomationState.WaitingForDuty && loopActive) BeginQueue();
            else if (State == AutomationState.Idle && config().Automation.AutoQueueOnLobbyEntry)
            {
                LoopRemaining = -1;
                loopActive = true;
                loopStartGames = Session.GamesCompleted;
                BeginQueue();
            }
        }
        wasInLobby = inLobby;

        if (inDuty && board.ResultVisible && !resultCounted)
        {
            resultCounted = true;
            Session.GamesCompleted++;
            if (loopActive && LoopRemaining > 0) LoopRemaining--;
            Services.Log.Information("Game completed (session total {Count})", Session.GamesCompleted);

            if (loopActive)
            {
                var why = LoopRemaining == 0 ? "count reached" : "";
                if (why.Length > 0 || StopConditionMet(out why))
                {
                    loopActive = false;
                    LoopRemaining = 0;
                    Services.Log.Information("Loop finished: {Why}", why);
                }
            }
            if (config().Automation.AutoLeaveAfterResult)
                leaveAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(2500 + Random.Shared.Next(2500));
        }
    }

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
                    if (InputField.SetText(answer))
                    {
                        progress = answer.Length;
                        Session.KeysTyped += answer.Length;
                        phase = Phase.BeforeEnter;
                        nextAt = now + TimeSpan.FromMilliseconds(80);
                    }
                    else
                    {
                        phase = Phase.Typing; // fall back to key-by-key if the field couldn't be written
                    }
                    return;
                }
                phase = Phase.Typing;
                return;

            case Phase.Clearing:
                // Typed text diverged from the target: backspace to the common prefix.
                if (typed.Length <= progress)
                {
                    phase = Phase.Typing;
                    return;
                }
                keys.SendBackspace(typing.BackgroundInput);
                Session.Mistakes++;
                nextAt = now + typist.NextKeyDelay();
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
                progress++;
                Session.KeysTyped++;
                phase = Phase.AwaitKey;
                nextAt = now + TimeSpan.FromMilliseconds(16);
                return;

            case Phase.AwaitKey:
                // Wait for the game to echo the key into the field before sending the next one.
                if (string.Equals(typed, answer[..progress], StringComparison.OrdinalIgnoreCase))
                {
                    echoFailures = 0;
                    phase = Phase.Typing;
                    nextAt = now + typist.NextKeyDelay();
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
                    echoFailures++;
                    progress = CommonPrefix(typed, answer);
                    if (echoFailures >= 3)
                    {
                        if (config().Advanced.AutomaticRecovery)
                        {
                            phase = Phase.Recovering;
                            nextAt = now + RecoveryDelay;
                            StatusDetail = "Keys aren't reaching the game; retrying";
                            Services.Log.Warning("Key echo timed out three times; recovering");
                        }
                        else Fail("Keys aren't reaching the game");
                        return;
                    }
                    phase = Phase.Typing;
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
    }

    /// <summary>Writes directly into the KTGInput text field (Instant mode).</summary>
    private static class InputField
    {
        public static bool SetText(string text)
        {
            try
            {
                var ptr = Services.GameGui.GetAddonByName(Keybound.InputAddon);
                if (ptr.IsNull) return false;
                var addon = (AtkUnitBase*)ptr.Address;
                var node = addon->GetComponentNodeById(7);
                if (node == null || node->Component == null) return false;
                var input = (AtkComponentTextInput*)node->Component;
                input->SetText(text);
                return true;
            }
            catch (Exception e)
            {
                Services.Log.Warning(e, "Instant insert failed");
                return false;
            }
        }
    }
}
