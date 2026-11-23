using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
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
/// <param name="RequiredStatus">A status the recipe needs (the Ixal facility access), 0 for none. Lost mid-run: the
/// stepper leaves NeedsCraft to get it back.</param>
/// <param name="RequiredEquip">An item to wear while crafting (the Ixal's Ehcatl wristgloves), 0 for none.</param>
internal sealed record CraftNeed(uint Product, uint Materials, int Remaining, uint ClassJob, uint RequiredStatus, uint RequiredEquip);

/// <summary>What the quest wants gathered, and where.</summary>
/// <param name="PointBase">The gathering point base that holds the item (0 when unknown).</param>
/// <param name="Territory">The zone to gather in; the character is already there when the stepper asks.</param>
/// <param name="Centre">The area's centre (the quest's map marker, else the game's exported gathering area).</param>
/// <param name="Radius">The area's radius in yalms (0 when unknown).</param>
internal sealed record GatherNeed(uint ItemId, int Remaining, uint PointBase, uint Territory, Vector3 Centre, float Radius, uint ClassJob);

/// <summary>
/// Runs one accepted allied-society quest from after it's accepted until it's reported: every tick it reads what the
/// game shows, lets <see cref="StepRules"/> decide the step (talk, hand over, /say, an emote, a key item, the quest
/// mount, an area, a portal, the report), travels there, does it once and waits to see it count. Crafting and gathering
/// are the run's: the stepper says what's needed (<see cref="Craft"/>, <see cref="Gather"/>) and carries on when the
/// run calls <see cref="Supplied"/>. Ticked on the framework thread.
/// </summary>
/// <remarks>
/// Safety, from the research: nothing that's an addon callback is sent while the game has scene tasks pending (only
/// clicks on enabled buttons); a /say or an emote is sent once, and once more only after 15 seconds with nothing
/// changing, then the step is the player's; Yes is answered only to questions on the game's own list (the
/// high-quality hand-over, the trade confirmation, the warp the stepper is taking); the character doesn't move while a
/// cast is under way; a menu is answered only when it's the quest's own question or its title.
/// </remarks>
internal sealed unsafe class QuestStepper
{
    private enum Phase { Decide, Travel, Settle, SwitchJob, Dismount }

    /// <summary>After an action, how long to wait for it to count before trying again.</summary>
    private static readonly TimeSpan SettleTalk = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SettlePublic = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan LookAround = TimeSpan.FromSeconds(3);
    /// <summary>The hand-over window open this long without handing over: close it.</summary>
    private static readonly TimeSpan TradePatience = TimeSpan.FromSeconds(15);

    private readonly Plugin plugin;
    private readonly Action<string, bool> log;
    private readonly QuestTravel travel = new();

    private QuestScript? script;
    private StepOptions options = new(false, false);
    private StepMemory memory = new();
    private Phase phase;
    private StepPlan? plan;
    private string lastPlan = "";
    private DateTime waitUntil;
    private DateTime settleUntil;
    private DateTime nextLook;
    private int tries;
    private bool faced;
    private ulong lastTalked;
    private uint lastTalkedBase;
    private bool wasRiding;
    private int tradeSlot;
    private DateTime tradeSince;
    private DateTime resultSince;
    private bool rewardSent;
    private bool cutscenePaused;
    private bool cutsceneNoted;

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

    /// <summary>Carries on after a pause or after the player did a step (a waiting step is decided afresh).</summary>
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
        Craft = null;
        Gather = null;
        State = StepperState.Working;
        memory.Touch(DateTime.UtcNow);
        phase = Phase.Decide;
        lastPlan = "";
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
        memory = new StepMemory();
        phase = Phase.Decide;
        plan = null;
        lastPlan = "";
        waitUntil = default;
        tries = 0;
        lastTalked = 0;
        lastTalkedBase = 0;
        wasRiding = false;
        tradeSince = default;
        resultSince = default;
        rewardSent = false;
        cutscenePaused = false;
        cutsceneNoted = false;
    }

    // ------------------------------------------------------------------ the tick

    public void Tick()
    {
        if (script == null || Paused || State is StepperState.Idle or StepperState.Done or StepperState.Failed or StepperState.WaitingForUser) return;
        if (!Services.ClientState.IsLoggedIn || Services.Objects.LocalPlayer == null) return;
        var now = DateTime.UtcNow;
        if (now < waitUntil) return;

        if (State is StepperState.NeedsCraft or StepperState.NeedsGather)
        {
            if (QuestSteps.Snapshot(script) is { } s) TickNeeds(s, now);
            return;
        }

        if (HandleWindows(now)) return;

        // During a zone change only the trip goes on (it watches the loading); the quest is read again after it.
        if (QuestSteps.Loading)
        {
            if (phase == Phase.Travel) travel.Tick();
            return;
        }
        if (QuestSteps.Snapshot(script) is not { } snap) return;
        if (memory.Observe(snap.Signature(script), now))
        {
            Note($"progress: {memory.Signature}");
            if (phase == Phase.Settle) phase = Phase.Decide;
        }
        var riding = script.Mount is { } qm && snap.Mounted && snap.Mount == qm.Mount;
        if (riding && !wasRiding && lastTalkedBase != 0)
        {
            memory.MountGiver = lastTalkedBase;
            Note($"on the quest mount {snap.Mount} (from {lastTalkedBase}); flying allowed {script.Mount!.CanFly}");
        }
        wasRiding = riding;

        switch (phase)
        {
            case Phase.Travel: TickTravel(snap, now); return;
            case Phase.Settle: TickSettle(now); return;
            case Phase.SwitchJob: TickSwitchJob(snap); return;
            case Phase.Dismount: TickDismount(snap); return;
            default: Decide(snap, now); return;
        }
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
                State = StepperState.NeedsCraft;
                Status = $"Crafting {next.Count}x {QuestSteps.ItemName(next.Id)}";
                Note($"needs craft: {Craft}");
                return;
            case StepKind.Gather:
                if (next.Territory != 0 && next.Territory != snap.Territory && next.Spot != null)
                {
                    // The gathering area is in a sub-zone (Elysion): get there first.
                    Approach(next with { Kind = StepKind.Go, What = "the gathering area" }, snap);
                    return;
                }
                var place = QuestSteps.GatherPlace(next.Id);
                var centre = next.Spot ?? (place is { } pl ? new Vector3(pl.Centre.X, snap.Player.Y, pl.Centre.Y) : snap.Player);
                Gather = new GatherNeed(next.Id, next.Count, place?.Base ?? 0, snap.Territory, centre, next.Spot != null ? next.Within : place?.Radius ?? 0, snap.AcceptJob);
                State = StepperState.NeedsGather;
                Status = $"Gathering {next.Count}x {QuestSteps.ItemName(next.Id)}";
                Note($"needs gather: {Gather}");
                return;
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
        var targetId = p.Target?.Id ?? 0;
        var targetBase = p.Target?.BaseId ?? 0;
        travel.GoTo(Describe(p), zone, to, p.Within, targetId == 0 ? null : () => Live(targetId, targetBase)?.Position);
        Status = Capital(Describe(p));
        phase = Phase.Travel;
        nextLook = DateTime.UtcNow.AddSeconds(2);
    }

    private void TickTravel(StepSnapshot snap, DateTime now)
    {
        // Not on the move while a cast is under way (it would be cancelled).
        if (Services.Condition[ConditionFlag.Casting] || Services.Condition[ConditionFlag.Casting87]) return;
        travel.Tick();
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

        var target = p.Target != null ? Live(p.Target.Id, p.Target.BaseId) : null;
        if (p.Target != null && target == null)
        {
            Note($"{p.Target.Name} ({p.Target.BaseId}) isn't there any more");
            memory.Tried.Add(p.Target.Id);
            phase = Phase.Decide;
            return;
        }

        switch (p.Kind)
        {
            case StepKind.Talk:
                Status = $"Talking to {target!.Name.TextValue}";
                lastTalked = target.GameObjectId;
                lastTalkedBase = target.BaseId;
                if (!QuestSteps.Talk(target, travels: script!.Territories.Count > 0)) Note($"talking to {target.Name.TextValue} didn't start");
                memory.Note(p.Key, now);
                Note($"talk {target.Name.TextValue} ({target.BaseId}) {Interact.Distance(target):0.0} yalms away, try {memory.Count(p.Key)}");
                return;

            case StepKind.Say:
                if (!QuestSteps.Say(p.Text!)) { AskUser($"Couldn't say “{p.Text}”; say it yourself, then resume."); return; }
                memory.Note(p.Key, now, isPublic: true);
                settleUntil = now + SettlePublic;
                Status = $"Said “{p.Text}”";
                var said = memory.PublicAttempts[p.Key].Count;
                Note($"say \"{p.Text}\" near {p.Target?.Name ?? "the spot"} ({(target != null ? Interact.Distance(target).ToString("0.0") : "?")} yalms), attempt {said} of 2");
                Log($"Said “{p.Text}”{(target != null ? $" to {target.Name.TextValue}" : "")}{(said > 1 ? " again" : "")}.", false);
                return;

            case StepKind.Emote:
                if (target == null || !QuestSteps.Emote(target, p.Text!)) { AskUser($"Couldn't do {p.Text}; do it yourself at {p.Target?.Name}, then resume."); return; }
                memory.Note(p.Key, now, isPublic: true);
                settleUntil = now + SettlePublic;
                Status = $"{p.Text} at {target.Name.TextValue}";
                var emoted = memory.PublicAttempts[p.Key].Count;
                Note($"emote {p.Text} ({p.Id}) at {target.Name.TextValue} ({target.BaseId}) {Interact.Distance(target):0.0} yalms away, attempt {emoted} of 2");
                Log($"Did {p.Text} at {target.Name.TextValue}{(emoted > 1 ? " again" : "")}.", false);
                return;

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

    private void TickSettle(DateTime now)
    {
        var p = plan!;
        if (pendingFire)
        {
            Fire(p, p.Target != null ? Live(p.Target.Id, p.Target.BaseId) : null, now);
            return;
        }
        // A cast, an event or its talk is still playing out: the wait starts after it.
        if (QuestSteps.Busy)
        {
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
        else if (!p.Public && p.Target != null && memory.Count(p.Key) >= StepRules.InteractTries)
        {
            memory.Tried.Add(p.Target.Id);
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
        // The game didn't let go of the quest mount: a teleport drops it (as other tools do).
        if (tries == 4 && Teleporter.NearestTo(snap.Territory, snap.Player) is { } dest && Teleporter.Teleport(dest) == null)
        {
            Note($"the quest mount stays; teleporting to {dest.Name} to drop it");
            MoveIntent.Expect(40, "dropping the quest mount");
            Status = "Teleporting to drop the quest mount";
            Wait(8);
            return;
        }
        if (QuestSteps.Busy) { Wait(1); return; }
        AskUser("Couldn't get off the quest mount; get off (or teleport), then resume.");
    }

    /// <summary>While the run crafts or gathers: back to work once enough are in the bags, or the facility access is lost.</summary>
    private void TickNeeds(StepSnapshot snap, DateTime now)
    {
        memory.Touch(now);
        if (State == StepperState.NeedsCraft)
        {
            var c = StepRules.Craft(script!, snap);
            if (c == null || c.Count <= 0)
            {
                Note("enough crafted");
                Supplied();
                return;
            }
            if (Craft != null && Craft.Remaining != c.Count) Craft = Craft with { Remaining = c.Count };
            if (script!.CraftStatus != 0 && !snap.Statuses.Contains(script.CraftStatus) && !GameState.InCraftingStance)
            {
                Note($"the crafting status {script.CraftStatus} is gone; getting it back");
                Supplied();
            }
            return;
        }
        var g = StepRules.Gather(script!, snap, memory);
        if (g == null || g.Count <= 0)
        {
            Note("enough gathered");
            Supplied();
            return;
        }
        if (Gather != null && Gather.Remaining != g.Count) Gather = Gather with { Remaining = g.Count };
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

        if (Interact.SkipTalk()) { Wait(0.2); return true; }

        if (QuestSteps.Question() is { } question)
        {
            var known = QuestSteps.KnownQuestions.ToList();
            if (travel.ExpectedQuestion is { Length: > 0 } warp) known.Add(warp);
            if (StepRules.KnownQuestion(question, known))
            {
                if (QuestSteps.Yes())
                {
                    Note($"question \"{question}\" -> Yes");
                    if (travel.AtPortal) MoveIntent.Expect(30, "a portal");
                }
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
            QuestSteps.Decline();
            Wait(1);
            return true;
        }

        if (Interact.Ready("JournalResult") != null)
        {
            if (resultSince == default) resultSince = now;
            Status = "Completing " + script!.Name;
            if (QuestSteps.Complete(script.OptionalRewards, ref rewardSent, out var how))
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
            if (tradeSince == default) { tradeSince = now; tradeSlot = 0; }
            Status = "Handing over";
            var step = QuestSteps.Trade(ref tradeSlot, out var detail);
            switch (step)
            {
                case QuestSteps.TradeStep.HandedOver:
                    Note("handed over");
                    Log("Handed over the quest's items.", false);
                    tradeSince = default;
                    Wait(1);
                    return true;
                case QuestSteps.TradeStep.Picked or QuestSteps.TradeStep.Opened:
                    Note("hand-over: " + detail);
                    Wait(0.5);
                    return true;
                case QuestSteps.TradeStep.Missing:
                    Note("[!] hand-over: " + detail);
                    QuestSteps.CancelTrade();
                    if (lastTalked != 0) memory.Tried.Add(lastTalked);
                    tradeSince = default;
                    Wait(1);
                    return true;
                default:
                    if (now - tradeSince > TradePatience)
                    {
                        Note("[!] hand-over window didn't take the items; closing it");
                        QuestSteps.CancelTrade();
                        if (lastTalked != 0) memory.Tried.Add(lastTalked);
                        tradeSince = default;
                    }
                    Wait(0.3);
                    return true;
            }
        }
        tradeSince = default;

        if (QuestSteps.Menu() is { } menu)
        {
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

    // ------------------------------------------------------------------ helpers

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
            StepKind.UseItem when p.Use == ItemUse.Self => flat <= p.Within,
            _ => Vector3.Distance(me, a) <= p.Within,
        };
    }

    /// <summary>The quest's object as loaded now: by its id, else (it was reloaded) the nearest with its base id.</summary>
    private static IGameObject? Live(ulong id, uint baseId)
    {
        if (QuestSteps.Find(id) is { } o && o.BaseId == baseId) return o;
        return Interact.Nearest(ObjectKind.EventNpc, b => b == baseId) ?? Interact.Nearest(ObjectKind.EventObj, b => b == baseId)
               ?? Interact.Nearest(ObjectKind.BattleNpc, b => b == baseId);
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
