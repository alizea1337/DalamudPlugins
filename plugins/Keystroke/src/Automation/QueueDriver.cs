using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Keystroke.Game;

namespace Keystroke.Automation;

/// <summary>
/// Gets the player from the Keybound Square lobby into the duty: talk to the Registrar, press
/// Register, wait for the pop, press Commence. Framework thread only; one step per tick.
/// </summary>
internal sealed unsafe class QueueDriver
{
    public enum Phase { Idle, FindRegistrar, Interact, WaitDialog, Register, WaitQueue, WaitPop, Commence, Entering, Done, Failed }

    private const float InteractRange = 6.5f;
    private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan QueueTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan EnterTimeout = TimeSpan.FromSeconds(90);
    private static readonly Random Rng = new();

    private readonly Func<Configuration> config;
    private DateTime phaseStarted = DateTime.UtcNow;
    private DateTime nextAt = DateTime.MinValue;
    private int interactAttempts;

    public Phase Current { get; private set; } = Phase.Idle;
    public string Status { get; private set; } = "";
    public string? Error { get; private set; }

    public QueueDriver(Func<Configuration> config) => this.config = config;

    public void Begin()
    {
        interactAttempts = 0;
        Error = null;
        Set(Phase.FindRegistrar, "Looking for the Keybound Registrar");
    }

    public void Reset()
    {
        Set(Phase.Idle, "");
        Error = null;
    }

    /// <summary>Advance by one step. Call only while in the lobby.</summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;
        if (Current is Phase.Idle or Phase.Done or Phase.Failed) return;
        if (now < nextAt) return;

        try
        {
            Step(now);
        }
        catch (Exception e)
        {
            Services.Log.Error(e, "Queue step failed");
            Fail("Queue step failed: " + e.Message);
        }
    }

    private void Step(DateTime now)
    {
        var queue = QueueState();

        // Already queued or popped from a previous attempt: skip ahead.
        if (Current is Phase.FindRegistrar or Phase.Interact or Phase.WaitDialog or Phase.Register)
        {
            if (queue is ContentsFinderQueueState.Queued or ContentsFinderQueueState.Pending) { Set(Phase.WaitQueue, "In queue"); return; }
            if (queue == ContentsFinderQueueState.Ready) { Set(Phase.WaitPop, "Duty is ready"); return; }
        }

        switch (Current)
        {
            case Phase.FindRegistrar:
            {
                var npc = FindRegistrar(out var distance);
                if (npc == null) { Status = "The Keybound Registrar isn't nearby"; return; }
                if (distance > InteractRange)
                {
                    Status = $"Move closer to the Keybound Registrar ({distance:0.0} m, need {InteractRange:0} m)";
                    return;
                }
                if (Services.Condition[ConditionFlag.OccupiedInQuestEvent] || Services.Condition[ConditionFlag.Occupied])
                {
                    Status = "Waiting for the current dialog to close";
                    return;
                }
                Set(Phase.Interact, "Talking to the Keybound Registrar");
                nextAt = now + Human(300, 700);
                return;
            }

            case Phase.Interact:
            {
                var npc = FindRegistrar(out var distance);
                if (npc == null || distance > InteractRange) { Set(Phase.FindRegistrar, "Looking for the Keybound Registrar"); return; }
                interactAttempts++;
                var ts = TargetSystem.Instance();
                ts->Target = (GameObject*)npc.Address;
                var result = ts->InteractWithObject((GameObject*)npc.Address, false);
                Services.Log.Information("Interacting with the Keybound Registrar (attempt {N}, result {R})", interactAttempts, result);
                Set(Phase.WaitDialog, "Waiting for the registration window");
                return;
            }

            case Phase.WaitDialog:
                if (AtkClicker.IsVisible(Keybound.EnterDialogueAddon))
                {
                    Set(Phase.Register, "Registering");
                    nextAt = now + Human(500, 1100);
                    return;
                }
                if (now - phaseStarted > DialogTimeout)
                {
                    if (interactAttempts >= 3) { Fail("The Keybound Registrar did not open the registration window"); return; }
                    Set(Phase.FindRegistrar, "Retrying the Keybound Registrar");
                }
                return;

            case Phase.Register:
                if (!AtkClicker.ClickButton(Keybound.EnterDialogueAddon, Keybound.RegisterDialogRegisterButton))
                {
                    if (now - phaseStarted > DialogTimeout) Fail("Could not press Register");
                    return;
                }
                Set(Phase.WaitQueue, "Queued; waiting for the duty");
                return;

            case Phase.WaitQueue:
                if (queue == ContentsFinderQueueState.Ready || AtkClicker.IsVisible("ContentsFinderConfirm"))
                {
                    Set(Phase.WaitPop, "Duty is ready");
                    nextAt = now + Human(800, 1800);
                    return;
                }
                if (queue is ContentsFinderQueueState.Accepted or ContentsFinderQueueState.InContent) { Set(Phase.Entering, "Entering the duty"); return; }
                if (queue == ContentsFinderQueueState.None && now - phaseStarted > TimeSpan.FromSeconds(8))
                {
                    // Register didn't take (dialog closed without queueing).
                    if (interactAttempts >= 3) { Fail("Registration did not put you in the queue"); return; }
                    Set(Phase.FindRegistrar, "Registration didn't take; retrying");
                    return;
                }
                if (now - phaseStarted > QueueTimeout) Fail("Waited 30 minutes in queue");
                Status = $"In queue ({(now - phaseStarted).TotalMinutes:0}m)";
                return;

            case Phase.WaitPop:
                if (queue is ContentsFinderQueueState.Accepted or ContentsFinderQueueState.InContent) { Set(Phase.Entering, "Entering the duty"); return; }
                if (!AtkClicker.IsVisible("ContentsFinderConfirm"))
                {
                    if (queue == ContentsFinderQueueState.Queued) { Set(Phase.WaitQueue, "Back in queue"); return; }
                    if (now - phaseStarted > TimeSpan.FromSeconds(45)) Set(Phase.WaitQueue, "Pop window closed");
                    return;
                }
                if (!ShouldCommence())
                {
                    Status = "Waiting for the party leader to commence";
                    return;
                }
                Set(Phase.Commence, "Commencing");
                return;

            case Phase.Commence:
                if (AtkClicker.ClickButton("ContentsFinderConfirm", Keybound.ContentsFinderCommenceButton))
                {
                    Set(Phase.Entering, "Entering the duty");
                    return;
                }
                if (now - phaseStarted > TimeSpan.FromSeconds(10)) Set(Phase.WaitPop, "Duty is ready");
                return;

            case Phase.Entering:
                if (now - phaseStarted > EnterTimeout)
                {
                    if (queue == ContentsFinderQueueState.Queued) { Set(Phase.WaitQueue, "Back in queue"); return; }
                    Fail("The duty did not start");
                }
                return;
        }
    }

    private bool ShouldCommence()
    {
        var inParty = Services.Party.Length > 1;
        return !inParty || config().Automation.AutoCommenceParty;
    }

    private static ContentsFinderQueueState QueueState()
    {
        try
        {
            var cf = ContentsFinder.Instance();
            return cf == null ? ContentsFinderQueueState.None : cf->QueueInfo.QueueState;
        }
        catch { return ContentsFinderQueueState.None; }
    }

    private static IGameObject? FindRegistrar(out float distance)
    {
        distance = float.MaxValue;
        var player = Services.Objects.LocalPlayer;
        if (player == null) return null;
        IGameObject? best = null;
        foreach (var obj in Services.Objects)
        {
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc || obj.DataId != Keybound.RegistrarDataId) continue;
            var d = Vector3.Distance(player.Position, obj.Position);
            if (d < distance) { distance = d; best = obj; }
        }
        return best;
    }

    private void Set(Phase phase, string status)
    {
        if (Current != phase) Services.Log.Information("Queue {Old} -> {New}: {Status}", Current, phase, status);
        Current = phase;
        Status = status;
        phaseStarted = DateTime.UtcNow;
    }

    private void Fail(string message)
    {
        Error = message;
        Set(Phase.Failed, message);
        Services.Log.Error("Queue failed: {Message}", message);
    }

    private static TimeSpan Human(int minMs, int maxMs) => TimeSpan.FromMilliseconds(minMs + Rng.NextDouble() * (maxMs - minMs));
}
