using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Provenance.Core.Tribes;
using Provenance.Integrations;

namespace Provenance.Run.Tribes;

/// <summary>Where the stepper is with its quest.</summary>
internal enum StepperState
{
    /// <summary>Not started, or stopped.</summary>
    Idle,
    Working,
    /// <summary>The run crafts what <see cref="QuestStepper.Craft"/> says, then calls <see cref="QuestStepper.Supplied"/>.</summary>
    NeedsCraft,
    /// <summary>The run gathers what <see cref="QuestStepper.Gather"/> says, then calls <see cref="QuestStepper.Supplied"/>.</summary>
    NeedsGather,
    /// <summary>The player has to do something (<see cref="QuestStepper.Problem"/> says what); Resume carries on.</summary>
    WaitingForUser,
    Done,
    Failed,
}

/// <param name="PauseForCutscenes">Pause the quest at a cutscene (otherwise skip it).</param>
/// <param name="LeaveOutSay">Don't /say: hand such a step to the player.</param>
internal sealed record StepOptions(bool PauseForCutscenes, bool LeaveOutSay);

/// <summary>What the quest wants crafted.</summary>
/// <param name="Product">The item to craft (its recipe for <paramref name="ClassJob"/>, the class the quest was accepted as).</param>
/// <param name="Materials">The quest's materials item it's made from (0 when unknown).</param>
/// <param name="Remaining">How many are still wanted.</param>
/// <param name="RequiredStatus">A status the recipe needs (the Ixal facility access), 0 for none. Lost mid-run, or
/// about to run out: the stepper leaves NeedsCraft to get it renewed.</param>
/// <param name="RequiredEquip">An item to wear while crafting (the Ixal's Ehcatl wristgloves), 0 for none.</param>
internal sealed record CraftNeed(uint Product, uint Materials, int Remaining, uint ClassJob, uint RequiredStatus, uint RequiredEquip);

/// <summary>What the quest wants gathered, and where.</summary>
/// <param name="PointBase">The gathering point base that holds the item (0 when unknown).</param>
/// <param name="Territory">The zone to gather in; the character is already there when the stepper asks.</param>
/// <param name="Centre">The area's centre (the quest's map marker, else the game's exported gathering area).</param>
/// <param name="Radius">The area's radius in yalms (0 when unknown).</param>
internal sealed record GatherNeed(uint ItemId, int Remaining, uint PointBase, uint Territory, Vector3 Centre, float Radius, uint ClassJob);

/// <summary>
/// Runs one accepted allied-society quest from after it's accepted until it's reported: four times a second it reads
/// what the game shows, lets <see cref="StepRules"/> decide the step (talk, hand over, /say, an emote, a key item, the
/// quest mount, an area, a portal, the report), travels there, does it once and waits to see it count. Crafting and
/// gathering are the run's: the stepper says what's needed (<see cref="Craft"/>, <see cref="Gather"/>), watches the item
/// counts meanwhile, and carries on when the run calls <see cref="Supplied"/>. Ticked on the framework thread.
/// </summary>
/// <remarks>
/// Safety, from the research: the quest's script is asked nothing while the game is busy or just after a zone change
/// (<see cref="QuestSteps.Snapshot"/>); nothing that's an addon callback is sent while the game has scene tasks pending
/// (only clicks on enabled buttons); a /say is sent once per step and an emote once more only after 15 seconds with
/// nothing changing, both for the whole day (<see cref="PublicAttemptStore.Shared"/>), then the step is the player's;
/// Yes is answered only to questions on the game's own list (the high-quality hand-over, the trade confirmation, the
/// warp the stepper is taking); the character doesn't move while a cast is under way; a menu is answered only when it's
/// the quest's own question or its title; every wait for the game is bounded (<see cref="BlockWatch"/>).
/// </remarks>
internal sealed unsafe class QuestStepper
{
    private enum Phase { Decide, Travel, Settle, SwitchJob, Dismount }

    /// <summary>After an action, how long to wait for it to count before trying again.</summary>
    private static readonly TimeSpan SettleTalk = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SettlePublic = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan LookAround = TimeSpan.FromSeconds(3);
    /// <summary>How often the quest is looked at (its script asked): four times a second.</summary>
    private static readonly TimeSpan LookEvery = TimeSpan.FromSeconds(0.25);
    /// <summary>The hand-over window open this long without handing over: close it.</summary>
    private static readonly TimeSpan TradePatience = TimeSpan.FromSeconds(15);
    /// <summary>Times a hand-over slot's item picker is asked for before the window is given up on.</summary>
    private const int TradeOpens = 3;

    private readonly Plugin plugin;
    private readonly Action<string, bool> log;
    private readonly QuestTravel travel = new();
    private readonly BlockWatch blocked = new();

    private QuestScript? script;
    private StepOptions options = new(false, false);
    private StepMemory memory = new() { Public = PublicAttemptStore.Shared };
    private Phase phase;
    private StepPlan? plan;
    private string lastPlan = "";
    private DateTime waitUntil;
    private DateTime settleUntil;
    private DateTime nextLook;
    private DateTime nextSnap;
    private DateTime nextBlockCheck;
    private int tries;
    private bool faced;
    private ulong lastTalkedKey;
    private uint lastTalkedBase;
    private bool wasRiding;
    private bool wasCarrying;
    private bool sawEvent;
    private int tradeSlot;
    private int tradeOpenSlot;
    private int tradeOpened;
    private DateTime tradeSince;
    private DateTime resultSince;
    private bool resultNoted;
    private bool rewardSent;
    private bool cutscenePaused;
    private bool cutsceneNoted;
    /// <summary>While crafting or gathering: the item, and how many of it the bags should hold when it's enough.</summary>
    private uint needItem;
    private int needTarget;

    public QuestStepper(Plugin plugin, Action<string, bool> log)
    {
        this.plugin = plugin;
        this.log = log;
    }

    public StepperState State { get; private set; } = StepperState.Idle;
    public string Status { get; private set; } = "";
    /// <summary>Why the stepper waits for the player or failed (plain English, for the log and the page).</summary>
    public string? Problem { get; private set; }
    public CraftNeed? Craft { get; private set; }
    public GatherNeed? Gather { get; private set; }
    public bool Paused { get; private set; }
    /// <summary>The quest (Quest sheet row) being run, 0 when none.</summary>
    public uint Quest => script?.Quest ?? 0;
    /// <summary>The current decision, for the page and the log.</summary>
    public StepPlan? Plan => plan;
    /// <summary>
    /// Skip cutscenes now (for ECommons' AutoCutsceneSkipper, which Plugin.cs combines with the other loops'): working
    /// on a quest and not set to pause for them.
    /// </summary>
    public bool SkipCutscenesNow => State == StepperState.Working && !Paused && !options.PauseForCutscenes;
    /// <summary>The character is about to jump through a portal (the Security check expects it).</summary>
    public bool TakingPortal => travel.AtPortal;
    /// <summary>Waiting because a cutscene plays and the options say to pause for them (not a problem: Resume carries on).</summary>
    public bool PausedForCutscene => State == StepperState.WaitingForUser && cutscenePaused;

    // ------------------------------------------------------------------ control

    /// <summary>Starts on an accepted quest (Quest sheet row: 65536 + the quest number).</summary>
    public void Begin(uint questRowId, StepOptions stepOptions)
    {
        if (questRowId is > 0 and < 65536) questRowId += 65536;
        Reset();
        options = stepOptions;
        Travel.AdvancedUnstuck = plugin.Config.Security.AdvancedUnstuck;
        Travel.StuckAfter = TimeSpan.FromSeconds(Math.Clamp(plugin.Config.Security.StuckSeconds, 3, 30));
        script = QuestSteps.Script(questRowId);
        if (script == null) { Finish(StepperState.Failed, $"Couldn't read quest {questRowId - 65536}'s data."); return; }
        State = StepperState.Working;
        Status = "Starting " + script.Name;
        Note($"begin {script.Quest - 65536} \"{script.Name}\": giver {script.Giver}, product {script.Product}/{script.Materials}"
             + $"{(script.CraftStatus != 0 ? $" status {script.CraftStatus}" : "")}{(script.CraftEquip != 0 ? $" wear {script.CraftEquip}" : "")}, "
             + $"gather [{string.Join(",", script.GatherItems.Select(kv => $"{kv.Key}:{kv.Value}"))}], "
             + $"key items [{string.Join(",", script.KeyItems.Select(k => $"{k.Id}{(k.Usable ? $" act {k.Action} {(k.Ground ? "ground" : k.Self ? "self" : "target")} r{k.Range}" : "")}"))}], "
             + $"mount {(script.Mount is { } m ? $"{m.Mount} action {m.Action} r{m.Range} e{m.EffectRange}{(m.Line ? " line" : "")}{(m.CanFly ? " flies" : "")}" : "none")}, "
             + $"say [{string.Join(",", script.Say.Select(kv => $"{kv.Key}:\"{kv.Value}\""))}], emotes [{string.Join(",", script.Emotes.Select(kv => $"{kv.Key}:{kv.Value.Command}"))}], "
             + $"carry {script.CarryStatus}, rewards [{string.Join(",", script.OptionalRewards)}], options cutscenes {(options.PauseForCutscenes ? "pause" : "skip")}, say {(options.LeaveOutSay ? "off" : "on")}");
    }

    public void Pause()
    {
        if (State is StepperState.Idle or StepperState.Done or StepperState.Failed || Paused) return;
        Paused = true;
        travel.Cancel();
        Status = "Paused";
        Note("paused");
    }

    /// <summary>
    /// Carries on after a pause or after the player did a step (a waiting step is decided afresh; the hand-over and
    /// reward windows and the busy watch start their clocks again).
    /// </summary>
    public void Resume()
    {
        if (State is StepperState.Idle or StepperState.Done or StepperState.Failed) return;
        Paused = false;
        cutscenePaused = false;
        if (State == StepperState.WaitingForUser)
        {
            State = StepperState.Working;
            Problem = null;
        }
        memory.Touch(DateTime.UtcNow);
        phase = Phase.Decide;
        lastPlan = "";
        tradeSince = default;
        resultSince = default;
        blocked.Reset();
        nextSnap = default;
        Note("resumed");
    }

    public void Stop()
    {
        if (State == StepperState.Idle) return;
        travel.Cancel();
        Note($"stopped while {State}");
        Reset();
    }

    /// <summary>The run crafted or gathered what was asked: carry on (the hand-over and the rest).</summary>
    public void Supplied()
    {
        if (State is not (StepperState.NeedsCraft or StepperState.NeedsGather)) return;
        Note($"supplied ({State})");
        BackToWork();
    }

    /// <summary>The run won't craft now (the facility access runs out soon): decide again (renew it first).</summary>
    public void Recheck(string why)
    {
        if (State is not (StepperState.NeedsCraft or StepperState.NeedsGather)) return;
        Note($"deciding again ({State}): {why}");
        BackToWork();
    }

    private void BackToWork()
    {
        Craft = null;
        Gather = null;
        State = StepperState.Working;
        memory.Touch(DateTime.UtcNow);
        phase = Phase.Decide;
        lastPlan = "";
        nextSnap = default;
    }

    private void Reset()
    {
        State = StepperState.Idle;
        Status = "";
        Problem = null;
        Craft = null;
        Gather = null;
        Paused = false;
        script = null;
        memory = new StepMemory { Public = PublicAttemptStore.Shared };
        phase = Phase.Decide;
        plan = null;
        lastPlan = "";
        waitUntil = default;
        nextSnap = default;
        nextBlockCheck = default;
        blocked.Reset();
        tries = 0;
        lastTalkedKey = 0;
        lastTalkedBase = 0;
        wasRiding = false;
        wasCarrying = false;
        sawEvent = false;
        tradeSince = default;
        resultSince = default;
        resultNoted = false;
        rewardSent = false;
        cutscenePaused = false;
        cutsceneNoted = false;
        needItem = 0;
        needTarget = 0;
    }

    // ------------------------------------------------------------------ the tick

    public void Tick()
    {
        if (script == null || Paused || State is StepperState.Idle or StepperState.Done or StepperState.Failed or StepperState.WaitingForUser) return;
        if (!Services.ClientState.IsLoggedIn || Services.Objects.LocalPlayer == null) return;
        var now = DateTime.UtcNow;

        // While the run crafts or gathers, only the item counts (and the facility access) are looked at.
        if (State is StepperState.NeedsCraft or StepperState.NeedsGather)
        {
            if (now < nextSnap) return;
            nextSnap = now + LookEvery;
            TickNeeds(now);
            return;
        }

        // The game busy (an event, a cutscene, a cast, scene tasks) for long with nothing about the quest changing.
        if (now >= nextBlockCheck)
        {
            nextBlockCheck = now + LookEvery;
            if (blocked.Feed(QuestSteps.Busy, QuestSteps.Progress(script.Quest), now))
            {
                AskUser($"The game has been busy (an event, a cutscene or a cast) for over {BlockWatch.Limit.TotalSeconds:0} seconds with nothing changing; finish it yourself, then resume.");
                return;
            }
        }

        if (now < waitUntil) return;
        if (HandleWindows(now)) return;

        // The trip goes on every frame (it watches the loading too); the quest is looked at four times a second.
        if (phase == Phase.Travel && !Services.Condition[ConditionFlag.Casting] && !Services.Condition[ConditionFlag.Casting87]) travel.Tick();
        if (QuestSteps.Loading || now < nextSnap) return;
        nextSnap = now + LookEvery;
        if (QuestSteps.Snapshot(script) is not { } snap) return;
        if (snap.ScriptRead && memory.Observe(snap.Signature(), now))
        {
            Note($"progress: {memory.Signature}");
            if (phase == Phase.Settle) phase = Phase.Decide;
        }
        if (phase == Phase.Settle && InEvent) sawEvent = true;
        NoteHolds(snap);

        switch (phase)
        {
            case Phase.Travel: TickTravel(snap, now); return;
            case Phase.Settle: TickSettle(snap, now); return;
            case Phase.SwitchJob: TickSwitchJob(snap); return;
            case Phase.Dismount: TickDismount(snap); return;
            default: Decide(snap, now); return;
        }
    }

    /// <summary>Getting on the quest mount, or picking something up to carry: counted (the rules cap getting them back), with who gave it.</summary>
    private void NoteHolds(StepSnapshot snap)
    {
        var riding = script!.Mount is { } qm && snap.Mounted && snap.Mount == qm.Mount;
        if (riding && !wasRiding)
        {
            memory.Mounts++;
            if (lastTalkedBase != 0) memory.MountGiver = lastTalkedBase;
            Note($"on the quest mount {snap.Mount} (from {lastTalkedBase}, time {memory.Mounts}); flying allowed {script.Mount!.CanFly}");
        }
        else if (!riding && wasRiding) Note($"off the quest mount at sequence {snap.Sequence}");
        wasRiding = riding;

        var carrying = script.CarryStatus != 0 && snap.Statuses.Contains(script.CarryStatus);
        if (carrying && !wasCarrying)
        {
            memory.Pickups++;
            if (lastTalkedBase != 0) memory.CarryGiver = lastTalkedBase;
            Note($"carrying (status {script.CarryStatus}, from {lastTalkedBase}, time {memory.Pickups})");
        }
        else if (!carrying && wasCarrying) Note($"no longer carrying at sequence {snap.Sequence}");
        wasCarrying = carrying;
    }

    private void Decide(StepSnapshot snap, DateTime now)
    {
        var next = StepRules.Decide(script!, snap, memory, now, options.LeaveOutSay);
        var text = next.ToString();
        if (text != lastPlan)
        {
            lastPlan = text;
            Note($"seq {snap.Sequence} -> {text}");
        }
        plan = next;
        tries = 0;
        faced = false;
        pendingFire = false;
        switch (next.Kind)
        {
            case StepKind.Wait:
                Status = next.What == "busy" ? Status : Capital(next.What);
                Wait(0.3);
                return;
            case StepKind.Done:
                Finish(StepperState.Done, null);
                Log($"Finished {script!.Name}.", false);
                return;
            case StepKind.Failed:
                Finish(StepperState.Failed, next.What);
                return;
            case StepKind.AskUser:
                AskUser(next.What);
                return;
            case StepKind.Craft:
                Craft = new CraftNeed(next.Id, next.Other, next.Count, snap.AcceptJob, script!.CraftStatus, script.CraftEquip);
                needItem = next.Id;
                needTarget = snap.Count(next.Id) + next.Count;
                State = StepperState.NeedsCraft;
                Status = $"Crafting {next.Count}x {QuestSteps.ItemName(next.Id)}";
                Note($"needs craft: {Craft}");
                return;
            case StepKind.Gather:
            {
                var place = QuestSteps.GatherPlace(next.Id);
                if (next.Territory != 0 && next.Territory != snap.Territory)
                {
                    // The gathering area is in another zone (Elysion): get there first.
                    var spot = next.Spot ?? (place is { } at ? new Vector3(at.Centre.X, 0, at.Centre.Y) : null);
                    if (spot == null) { AskUser($"Couldn't tell where to gather {QuestSteps.ItemName(next.Id)}; gather it yourself, then resume."); return; }
                    Approach(next with { Kind = StepKind.Go, What = "the gathering area", Spot = spot, Within = Math.Max(10f, next.Within) }, snap);
                    return;
                }
                var centre = next.Spot ?? (place is { } pl ? new Vector3(pl.Centre.X, snap.Player.Y, pl.Centre.Y) : snap.Player);
                Gather = new GatherNeed(next.Id, next.Count, place?.Base ?? 0, snap.Territory, centre, next.Spot != null ? next.Within : place?.Radius ?? 0, snap.AcceptJob);
                needItem = next.Id;
                needTarget = snap.Count(next.Id) + next.Count;
                State = StepperState.NeedsGather;
                Status = $"Gathering {next.Count}x {QuestSteps.ItemName(next.Id)}";
                Note($"needs gather: {Gather}");
                return;
            }
            case StepKind.SwitchJob:
                phase = Phase.SwitchJob;
                return;
            case StepKind.Dismount:
                phase = Phase.Dismount;
                return;
            default:
                Approach(next, snap);
                return;
        }
    }

    /// <summary>Travels to the plan's target or spot, or acts right away when it's already in reach.</summary>
    private void Approach(StepPlan p, StepSnapshot snap)
    {
        plan = p;
        var zone = p.Territory != 0 ? p.Territory : snap.Territory;
        if (zone == snap.Territory && InReach(p, snap.Player)) { Act(p, snap); return; }
        var to = p.Target?.Position ?? p.Spot ?? snap.Player;
        travel.OnFoot = p.OnFoot;
        travel.NoFly = p.NoFly;
        travel.KeepMount = script!.Mount is { } m && snap.Mounted && snap.Mount == m.Mount;
        var target = p.Target;
        travel.GoTo(Describe(p), zone, to, p.Within, target == null ? null : () => Live(target)?.Position);
        Status = Capital(Describe(p));
        phase = Phase.Travel;
        nextLook = DateTime.UtcNow.AddSeconds(2);
    }

    /// <summary>On the way (the trip itself is ticked every frame in <see cref="Tick"/>).</summary>
    private void TickTravel(StepSnapshot snap, DateTime now)
    {
        Status = travel.Status;
        var p = plan!;
        if (p.Kind != StepKind.Go && p.Target != null && !travel.AtPortal && snap.Territory == (p.Territory != 0 ? p.Territory : snap.Territory) && InReach(p, snap.Player))
        {
            travel.Cancel();
            Act(p, snap);
            return;
        }
        if (travel.Current == QuestTravel.State.Failed)
        {
            AskUser($"Couldn't get to {Describe(p)}: {travel.Failure} Go there yourself, then resume.");
            return;
        }
        if (travel.Current == QuestTravel.State.Arrived)
        {
            Act(p, snap);
            return;
        }
        // On the way to a marker or spot: a target coming into view takes over.
        if (p.Kind == StepKind.Go && now >= nextLook && !travel.AtPortal)
        {
            nextLook = now.AddSeconds(2);
            var again = StepRules.Decide(script!, snap, memory, now, options.LeaveOutSay);
            if (again.Target != null && again.Kind != StepKind.Go)
            {
                Note($"on the way, {again.Target.Name} came into view: {again}");
                travel.Cancel();
                lastPlan = again.ToString();
                Approach(again, snap);
            }
        }
    }

    /// <summary>Does the plan's step once, then waits for it to count.</summary>
    private void Act(StepPlan p, StepSnapshot snap)
    {
        var now = DateTime.UtcNow;
        plan = p;
        phase = Phase.Settle;
        settleUntil = now + SettleTalk;
        sawEvent = false;
        if (p.Kind == StepKind.Go)
        {
            Status = "Looking around " + Describe(p);
            settleUntil = now + LookAround;
            return;
        }
        if (QuestSteps.Busy) { phase = Phase.Decide; Wait(0.3); return; }

        // Talking, emotes and key items don't work from one's own mount (the quest mount's action does).
        var onOwnMount = snap.Mounted && !(script!.Mount is { } qm && snap.Mount == qm.Mount);
        if (onOwnMount && p.Kind is StepKind.Talk or StepKind.Emote or StepKind.UseItem)
        {
            QuestSteps.GetOff();
            phase = Phase.Decide;
            Wait(1.5);
            return;
        }

        var target = p.Target != null ? Live(p.Target) : null;
        if (p.Target != null && target == null)
        {
            Note($"{p.Target.Name} ({p.Target.BaseId}) isn't there any more");
            memory.Tried.Add(p.Target.Key);
            phase = Phase.Decide;
            return;
        }

        switch (p.Kind)
        {
            case StepKind.Talk:
                Status = $"Talking to {target!.Name.TextValue}";
                lastTalkedKey = p.Target!.Key;
                lastTalkedBase = target.BaseId;
                if (!QuestSteps.Talk(target, travels: script!.Territories.Count > 0)) Note($"talking to {target.Name.TextValue} didn't start");
                memory.Note(p.Key, now);
                Note($"talk {target.Name.TextValue} ({target.BaseId}) {Interact.Distance(target):0.0} yalms away, try {memory.Count(p.Key)}");
                return;

            case StepKind.Say:
            {
                // Counted before it's sent: whatever happens next, it isn't sent twice.
                var said = memory.Public.Note(p.Key, now).Count;
                if (!QuestSteps.Say(p.Text!)) { AskUser($"Couldn't say “{p.Text}”; say it yourself, then resume."); return; }
                settleUntil = now + SettlePublic;
                Status = $"Said “{p.Text}”";
                Note($"say \"{p.Text}\" near {p.Target?.Name ?? "the spot"} ({(target != null ? Interact.Distance(target).ToString("0.0") : "?")} yalms), attempt {said} of {StepRules.PublicTries(StepKind.Say)}, key {p.Key}");
                Log($"Said “{p.Text}”{(target != null ? $" to {target.Name.TextValue}" : "")}.", false);
                return;
            }

            case StepKind.Emote:
            {
                if (target == null) { AskUser($"Couldn't do {p.Text}; do it yourself at {p.Target?.Name}, then resume."); return; }
                var emoted = memory.Public.Note(p.Key, now).Count;
                if (!QuestSteps.Emote(target, p.Text!)) { AskUser($"Couldn't do {p.Text}; do it yourself at {p.Target?.Name}, then resume."); return; }
                settleUntil = now + SettlePublic;
                Status = $"{p.Text} at {target.Name.TextValue}";
                Note($"emote {p.Text} ({p.Id}) at {target.Name.TextValue} ({target.BaseId}) {Interact.Distance(target):0.0} yalms away, attempt {emoted} of {StepRules.PublicTries(StepKind.Emote)}, key {p.Key}");
                Log($"Did {p.Text} at {target.Name.TextValue}{(emoted > 1 ? " again" : "")}.", false);
                return;
            }

            case StepKind.UseItem:
            {
                var used = QuestSteps.UseKeyItem(p.Id, p.Use, target, p.Spot, out var why);
                memory.Note(p.Key, now);
                Status = $"Using the {QuestSteps.ItemName(p.Id)}";
                settleUntil = now + SettleTalk + TimeSpan.FromSeconds(script!.KeyItems.FirstOrDefault(k => k.Id == p.Id)?.CastSeconds ?? 0);
                Note($"use item {p.Id} ({p.Use}) on {target?.Name.TextValue ?? "the spot"} {(target != null ? Interact.Distance(target).ToString("0.0") + " yalms" : "")}: {(used ? "sent" : "refused")}, status {why}, try {memory.Count(p.Key)}");
                return;
            }

            case StepKind.MountAction:
            {
                // An action that goes out in a line: turn to the target first, and let the turn settle.
                if (p.Face && target != null && !faced)
                {
                    QuestSteps.Face(target.Position);
                    faced = true;
                    pendingFire = true;
                    settleUntil = DateTime.MinValue;
                    Wait(0.4);
                    return;
                }
                Fire(p, target, now);
                return;
            }
        }
    }

    /// <summary>The quest mount's action, after turning when it needed that.</summary>
    private bool pendingFire;

    private void Fire(StepPlan p, IGameObject? target, DateTime now)
    {
        pendingFire = false;
        var self = script!.Mount is { } m && m.Range <= 0;
        if (p.Face && target != null && !QuestSteps.Facing(target.Position)) QuestSteps.Face(target.Position);
        var used = QuestSteps.UseMountAction(p.Id, self ? null : target, out var why);
        memory.Note(p.Key, now);
        phase = Phase.Settle;
        settleUntil = now + SettleTalk;
        Status = $"Using the mount's action on {target?.Name.TextValue ?? "the spot"}";
        Note($"mount action {p.Id} on {target?.Name.TextValue ?? "self"} ({target?.BaseId ?? 0}) {(target != null ? Interact.Distance(target).ToString("0.0") + " yalms" : "")}"
             + $"{(p.Face ? $", facing {(target != null && QuestSteps.Facing(target.Position) ? "yes" : "no")}" : "")}: {(used ? "sent" : "refused")}, status {why}, try {memory.Count(p.Key)}");
    }

    private void TickSettle(StepSnapshot snap, DateTime now)
    {
        var p = plan!;
        if (pendingFire)
        {
            Fire(p, p.Target != null ? Live(p.Target) : null, now);
            return;
        }
        // A cast, an event or its talk is still playing out: the wait starts after it (the busy watch bounds it).
        if (QuestSteps.Busy)
        {
            if (InEvent) sawEvent = true;
            var after = now.AddSeconds(1.5);
            if (settleUntil < after) settleUntil = after;
            return;
        }
        if (now < settleUntil) return;
        if (p.Kind == StepKind.Go)
        {
            if (p.Level != 0) memory.Looked.Add(p.Level);
            Note($"looked around {Describe(p)}: nothing to do there yet");
        }
        else if (p.Kind == StepKind.Talk && p.Target is { Kind: StepObjectKind.Object } decoy && sawEvent)
        {
            // An object whose scene played without moving the quest: "not this one". Not tried again on these lines.
            memory.Decoy(StepRules.LineKey(script!, snap), decoy.Key);
            Note($"{decoy.Name} ({decoy.BaseId}) played its scene without anything changing: a decoy on these lines");
        }
        else if (!p.Public && p.Target != null && memory.Count(p.Key) >= StepRules.InteractTries)
        {
            memory.Tried.Add(p.Target.Key);
            Note($"{p.Kind} on {p.Target.Name} ({p.Target.BaseId}) {StepRules.InteractTries} times without anything changing; trying something else");
        }
        phase = Phase.Decide;
    }

    private void TickSwitchJob(StepSnapshot snap)
    {
        var job = plan!.Id;
        if (snap.Job == job) { phase = Phase.Decide; return; }
        if (++tries > 3) { AskUser($"Couldn't change back to {Abbreviation(job)}, the class this quest was accepted as; change yourself, then resume."); return; }
        var set = GameState.BestGearset(job);
        if (set < 0) { AskUser($"No gearset for {Abbreviation(job)}, the class this quest was accepted as; change to it yourself, then resume."); return; }
        Note($"changing to gearset {set + 1} ({Abbreviation(job)}), the class the quest was accepted as");
        GameState.EquipGearset(set);
        Status = $"Changing to {Abbreviation(job)}";
        Wait(2);
    }

    /// <summary>Off the quest mount at the report (the rules ask the player before then); a teleport drops it when getting off doesn't, once per quest.</summary>
    private void TickDismount(StepSnapshot snap)
    {
        if (!snap.Mounted) { phase = Phase.Decide; Wait(0.5); return; }
        tries++;
        if (tries <= 3)
        {
            Note($"getting off the quest mount (try {tries})");
            QuestSteps.GetOff();
            Status = "Getting off the quest mount";
            Wait(2);
            return;
        }
        // The game didn't let go of the quest mount: a teleport drops it (as other tools do), once per quest.
        if (tries == 4 && memory.DropTeleports < StepRules.MaxDropTeleports
            && Teleporter.NearestTo(snap.Territory, snap.Player) is { } dest && Teleporter.Teleport(dest) == null)
        {
            memory.DropTeleports++;
            Note($"the quest mount stays; teleporting to {dest.Name} to drop it");
            MoveIntent.Expect(40, "dropping the quest mount");
            Status = "Teleporting to drop the quest mount";
            Wait(8);
            return;
        }
        if (QuestSteps.Busy) { Wait(1); return; }
        AskUser("Couldn't get off the quest mount; get off (or teleport), then resume.");
    }

    /// <summary>
    /// While the run crafts or gathers: back to work once enough are in the bags, or when the facility access is gone
    /// or about to run out (the run then stops after the item under way). Only item counts and statuses are read: the
    /// quest's script isn't asked anything while crafting or gathering.
    /// </summary>
    private void TickNeeds(DateTime now)
    {
        memory.Touch(now);
        var remaining = Math.Max(0, needTarget - QuestSteps.ItemCount(needItem));
        if (remaining <= 0)
        {
            Note(State == StepperState.NeedsCraft ? "enough crafted" : "enough gathered");
            Supplied();
            return;
        }
        if (State == StepperState.NeedsCraft)
        {
            if (Craft != null && Craft.Remaining != remaining) Craft = Craft with { Remaining = remaining };
            if (script!.CraftStatus != 0 && !QuestSteps.StatusLasts(script.CraftStatus, StepRules.StatusRenewBelow))
            {
                Note($"the crafting status {script.CraftStatus} is gone or runs out within {StepRules.StatusRenewBelow.TotalSeconds:0} seconds; renewing it");
                Supplied();
            }
            return;
        }
        if (Gather != null && Gather.Remaining != remaining) Gather = Gather with { Remaining = remaining };
    }

    // ------------------------------------------------------------------ windows

    /// <summary>The windows a step brings up, each answered by the rules above. True when it took the tick.</summary>
    private bool HandleWindows(DateTime now)
    {
        if (QuestSteps.InCutscene)
        {
            if (!cutsceneNoted)
            {
                cutsceneNoted = true;
                Note($"cutscene: {(options.PauseForCutscenes ? "pausing" : "skipping")}, cutscene skipper hooked {DeliveryWindow.CutsceneSkipperHooked?.ToString() ?? "unknown"}");
            }
            if (options.PauseForCutscenes)
            {
                if (!cutscenePaused)
                {
                    cutscenePaused = true;
                    travel.Cancel();
                    AskUser("Paused for the cutscene; resume carries on.");
                }
                return true;
            }
            // A choice inside the cutscene: the player's (its entries aren't read).
            if (Interact.IsOpen("CutSceneSelectString"))
            {
                AskUser("The quest's cutscene asks you to choose; choose yourself, then resume.");
                return true;
            }
            if (QuestSteps.Menu() is { } prompt && !DeliveryWindow.SceneBusy
                && StepRules.ChooseEntry(prompt.Prompt, prompt.Entries, script!, memory, QuestSteps.SkipQuestions) == 0
                && QuestSteps.SkipQuestions.Any(q => StepRules.Normal(q).Equals(StepRules.Normal(prompt.Prompt), StringComparison.CurrentCultureIgnoreCase)))
            {
                Note($"cutscene question \"{prompt.Prompt}\" -> \"{prompt.Entries[0]}\"");
                prompt.Select(0);
                Wait(1);
            }
            else Wait(0.3);
            return true;
        }
        cutsceneNoted = false;

        if (phase == Phase.Travel)
        {
            // On the way the windows are the trip's (an aetheryte's menu, the aethernet, a portal's question), except a
            // quest event that starts on the way (an event range walked into): stop and let its talk play out, then
            // plan again.
            if (travel.AtPortal || !(Services.Condition[ConditionFlag.OccupiedInQuestEvent] || Interact.IsOpen("Talk"))) return false;
            Note("a quest event started on the way; stopping to let it play out");
            travel.Cancel();
            Vnav.Stop();
            phase = Phase.Decide;
            lastPlan = "";
        }

        if (Interact.SkipTalk()) { Wait(0.2); return true; }

        if (QuestSteps.Question() is { } question)
        {
            if (StepRules.KnownQuestion(question, QuestSteps.KnownQuestions))
            {
                if (QuestSteps.Yes()) Note($"question \"{question}\" -> Yes");
                Wait(0.8);
                return true;
            }
            AskUser($"The game asks “{question}”; answer it yourself, then resume.");
            return true;
        }

        if (Interact.Ready("JournalAccept") != null)
        {
            // Another quest's offer (talking to a giver can bring one up): not the run's to take.
            Note("a quest offer opened; declining it");
            if (!QuestSteps.Decline()) { AskUser("A quest offer came up that can't be declined now; decline it yourself, then resume."); return true; }
            Wait(1);
            return true;
        }

        if (Interact.Ready("JournalResult") != null)
        {
            if (resultSince == default) resultSince = now;
            Status = "Completing " + script!.Name;
            var owner = QuestSteps.ResultFor(script, out var shown);
            if (owner == QuestSteps.ResultOwner.Other)
            {
                resultSince = default;
                AskUser($"The reward window is for another quest (“{shown}”); complete it yourself, then resume.");
                return true;
            }
            if (owner == QuestSteps.ResultOwner.Unknown && !resultNoted)
            {
                resultNoted = true;
                Note($"reward window: \"{script.Name}\" isn't among its texts; only its Complete button is pressed (no reward is picked)");
            }
            if (QuestSteps.Complete(script.OptionalRewards, owner == QuestSteps.ResultOwner.This, ref rewardSent, out var how))
            {
                Note($"completed in the reward window ({how})");
                Log($"Reported {script.Name}{(how.StartsWith("reward") ? $" and took {QuestSteps.ItemName(script.OptionalRewards[0])}" : "")}.", false);
                Wait(1.5);
                return true;
            }
            if (now - resultSince > TimeSpan.FromSeconds(20))
            {
                resultSince = default;
                AskUser("The quest's reward window didn't complete; complete it yourself, then resume.");
                return true;
            }
            Wait(0.3);
            return true;
        }
        resultSince = default;

        if (QuestSteps.TradeOpen)
        {
            if (tradeSince == default)
            {
                tradeSince = now;
                tradeSlot = 0;
                tradeOpenSlot = -1;
                tradeOpened = 0;
            }
            Status = "Handing over";
            // However the window behaves, it gets this long.
            if (now - tradeSince > TradePatience)
            {
                CloseTrade("[!] hand-over window didn't take the items in time; closing it");
                return true;
            }
            var step = QuestSteps.Trade(ref tradeSlot, out var detail);
            switch (step)
            {
                case QuestSteps.TradeStep.HandedOver:
                    Note("handed over");
                    Log("Handed over the quest's items.", false);
                    tradeSince = default;
                    Wait(1);
                    return true;
                case QuestSteps.TradeStep.Opened:
                    if (tradeSlot == tradeOpenSlot) tradeOpened++;
                    else
                    {
                        tradeOpenSlot = tradeSlot;
                        tradeOpened = 1;
                    }
                    if (tradeOpened > TradeOpens)
                    {
                        CloseTrade($"[!] hand-over: slot {tradeSlot + 1}'s item picker didn't open after {TradeOpens} tries; closing the window");
                        return true;
                    }
                    Note("hand-over: " + detail);
                    Wait(0.5);
                    return true;
                case QuestSteps.TradeStep.Picked:
                    Note("hand-over: " + detail);
                    Wait(0.5);
                    return true;
                case QuestSteps.TradeStep.Missing:
                    CloseTrade("[!] hand-over: " + detail);
                    return true;
                default:
                    Wait(0.3);
                    return true;
            }
        }
        tradeSince = default;

        if (QuestSteps.Menu() is { } menu)
        {
            // Scene tasks pending: wait (the busy watch bounds it).
            if (DeliveryWindow.SceneBusy) { Wait(0.2); return true; }
            var pick = StepRules.ChooseEntry(menu.Prompt, menu.Entries, script!, memory, QuestSteps.SkipQuestions);
            if (pick < 0)
            {
                Note($"[!] menu \"{menu.Prompt}\": [{string.Join(" / ", menu.Entries.Select(StepRules.Normal))}] isn't the quest's own");
                AskUser($"A menu came up ({string.Join(" / ", menu.Entries.Select(StepRules.Normal))}); choose yourself, then resume.");
                return true;
            }
            StepRules.Answered(menu.Prompt, menu.Entries, pick, memory);
            Note($"menu \"{menu.Prompt}\" -> \"{StepRules.Normal(menu.Entries[pick])}\" ({pick + 1} of {menu.Entries.Count})");
            menu.Select(pick);
            Wait(0.8);
            return true;
        }
        return false;
    }

    /// <summary>Gives up on the hand-over: its Cancel button; whoever opened it counts as tried. The player's when it won't close.</summary>
    private void CloseTrade(string why)
    {
        Note(why);
        tradeSince = default;
        if (lastTalkedKey != 0) memory.Tried.Add(lastTalkedKey);
        if (Interact.IsOpen("ContextIconMenu")) Interact.Close("ContextIconMenu");
        if (!QuestSteps.CancelTrade())
        {
            AskUser("The hand-over window didn't take the quest's items and can't be closed now; close it yourself, then resume.");
            return;
        }
        Wait(1);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>An event (an object's or an NPC's scene) or its cast is playing.</summary>
    private static bool InEvent
        => Services.Condition[ConditionFlag.OccupiedInEvent] || Services.Condition[ConditionFlag.OccupiedInQuestEvent]
           || Services.Condition[ConditionFlag.Casting] || Services.Condition[ConditionFlag.Casting87];

    /// <summary>The target is close enough to act on.</summary>
    private static bool InReach(StepPlan p, Vector3 me)
    {
        var at = p.Target?.Position ?? p.Spot;
        if (at is not { } a) return true;
        var flat = Vector2.Distance(new Vector2(me.X, me.Z), new Vector2(a.X, a.Z));
        return p.Kind switch
        {
            StepKind.Talk => flat <= p.Within && Math.Abs(me.Y - a.Y) <= 3f,
            StepKind.Go => flat <= p.Within,
            StepKind.Say when p.Target == null => flat <= p.Within,
            StepKind.UseItem when p.Use == ItemUse.Self => flat <= p.Within,
            _ => Vector3.Distance(me, a) <= p.Within,
        };
    }

    /// <summary>
    /// The quest's object as loaded now: by its id; when it was loaded again (a new id), the same placement in the
    /// zone's layout, or (one without a placement) the same NPC or object close to where it was. Never another one of
    /// the same kind elsewhere.
    /// </summary>
    private static IGameObject? Live(StepObject t)
    {
        if (QuestSteps.Find(t.Id) is { } o && o.BaseId == t.BaseId && (t.Instance == 0 || QuestSteps.Instance(o) == t.Instance)) return o;
        return Services.Objects
            .Where(x => x.BaseId == t.BaseId && x.IsTargetable
                        && (t.Instance != 0 ? QuestSteps.Instance(x) == t.Instance : Vector3.Distance(x.Position, t.Position) <= 10f))
            .OrderBy(x => Vector3.DistanceSquared(x.Position, t.Position))
            .FirstOrDefault();
    }

    private static string Describe(StepPlan p) => p.Kind switch
    {
        StepKind.Talk => $"talk to {p.Target?.Name}",
        StepKind.Say => $"say “{p.Text}” near {p.Target?.Name ?? "the spot"}",
        StepKind.Emote => $"{p.Text} at {p.Target?.Name}",
        StepKind.UseItem => $"use the {QuestSteps.ItemName(p.Id)}{(p.Target != null ? $" on {p.Target.Name}" : "")}",
        StepKind.MountAction => $"the mount's action on {p.Target?.Name}",
        StepKind.Go => p.What,
        _ => p.What,
    };

    private static string Abbreviation(uint job)
        => Services.Data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>().GetRowOrDefault(job)?.Abbreviation.ExtractText() is { Length: > 0 } a ? a : $"class {job}";

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private void AskUser(string why)
    {
        travel.Cancel();
        State = StepperState.WaitingForUser;
        Problem = why;
        Status = "Waiting for you: " + why;
        phase = Phase.Decide;
        blocked.Reset();
        Note("[!] waiting for the player: " + why);
        Log(why, true);
    }

    private void Finish(StepperState state, string? problem)
    {
        travel.Cancel();
        State = state;
        Problem = problem;
        Status = state == StepperState.Done ? "Done" : problem ?? "Failed";
        Note($"{state}{(problem != null ? ": " + problem : "")}");
        if (problem != null) Log(problem, true);
    }

    private void Wait(double seconds) => waitUntil = DateTime.UtcNow.AddSeconds(seconds);

    private void Log(string text, bool problem)
    {
        try { log(text, problem); }
        catch (Exception e) { Services.Log.Warning(e, "Quest stepper log"); }
    }

    private void Note(string text) => DebugLog.Write("steps", $"{(script != null ? script.Quest - 65536 : 0)}: {text}");
}
