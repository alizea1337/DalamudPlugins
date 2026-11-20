using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Provenance.Core.Fishing;
using Provenance.Data;
using AddonSpearFishing = FFXIVClientStructs.FFXIV.Client.UI.AddonSpearFishing;
using GatheringPointObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GatheringPointObject;

namespace Provenance.Integrations.Fishing;

/// <summary>
/// Spearfishing: finding the spearfishing nodes of a spot in the object table, and working the
/// SpearFishing window once a node is open: reads the three lanes every frame, recognises fish by size
/// and speed, and gigs a wanted fish the moment it crosses the middle of its lane. Moving to a node and
/// opening it is the caller's job (with <see cref="Usable"/> and <see cref="NearestNode"/>).
/// The window never says what was caught, so catches are counted from the bags: a look-alike landed
/// instead of the wanted fish shows up under its own item id. One action per frame at most, then nothing
/// for a random 600–1000 ms (Gig has its own 1 s recast too). No GP is spent (Nature's Bounty, Baited
/// Breath, Veteran Trade, Electric Current, cordials): the order only needs the fish. The one other action
/// is the Collect toggle, set the way <see cref="Collect"/> asks (off for an order's fish, so they come in as normal
/// items; on for fish gigged to be reduced). Gigging is ported from AutoHook's AutoGig (BSD-3, Puni.sh); node recognition
/// follows GatherBuddy Reborn's AutoGather spearfishing (Apache-2.0).
/// Framework thread; <see cref="Tick"/> every frame.
/// </summary>
internal sealed unsafe class SpearGame
{
    /// <summary>Collect's status: catches come in as collectables while it's on.</summary>
    private const uint CollectStatus = 805;
    // SpearFishing node ids (AutoHook AutoGig.cs:14-17 and SpearfishingUpdateModule.cs:37-44; GBR
    // AddonSpearFishingExtensions.cs:12-15): 43 is the lane area, 61, 60 and 59 are the fish sprites of
    // Fish[0], Fish[1] and Fish[2], which ClientStructs documents as bottom to top.
    private const uint LaneArea = 43;
    private static readonly uint[] FishSprites = { 61, 60, 59 };
    // AutoHook's wait after any action (Configuration.cs:27-28, ActionExecutor.cs:183-202).
    private const int MinDelayMs = 600, MaxDelayMs = 1000;
    /// <summary>After the window closes: the last catch reaches the bags and the gathering state ends meanwhile.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1), SettleMax = TimeSpan.FromSeconds(5);
    /// <summary>Begun before the window opened: how long to wait for it.</summary>
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(6);
    /// <summary>Collect left on: how long gigs wait for the toggle to go off before going ahead anyway.</summary>
    private static readonly TimeSpan CollectHold = TimeSpan.FromSeconds(2);
    /// <summary>Begun again this soon after the last look at the bags: keep counting from that look.</summary>
    private static readonly TimeSpan CarryOver = TimeSpan.FromSeconds(5);
    /// <summary>Between two presses of the window's close or of the question's Yes (see <see cref="Leave"/>).</summary>
    private const int LeaveRetryMs = 1500;
    /// <summary>
    /// Addon row of the game's question when a spearfishing window is closed: "Abandon the teeming waters?" then
    /// "※Doing so will cause the teeming waters to dissipate." (English client; read in the client's language).
    /// </summary>
    private const uint AbandonQuestion = 12683;

    /// <summary>What the spearfishing is doing, in a few words for the Run page.</summary>
    public string Status { get; private set; } = "";
    /// <summary>Gigging in an open window.</summary>
    public bool Active { get; private set; }
    /// <summary>The window has closed since <see cref="Begin"/> (the node is used up, or it was left).</summary>
    public bool Finished { get; private set; }

    /// <summary>Fish gigged (item id, amount), counted from the bags.</summary>
    public event Action<uint, int>? Caught;

    /// <summary>
    /// Gig the fish as collectables (for aetherial reduction): the Collect toggle is switched to match once per
    /// <see cref="Begin"/>. Off by default, so the fish come in as normal items (what an order counts).
    /// </summary>
    public bool Collect { get; set; }
    /// <summary>Gigs thrown since <see cref="Begin"/>.</summary>
    public int Gigs { get; private set; }
    /// <summary>Fish caught since <see cref="Begin"/>, wanted or not.</summary>
    public int Catches { get; private set; }

    /// <summary>The SpearFishing window is open.</summary>
    public static bool WindowOpen => Window() != null;

    /// <summary>The game asks whether to abandon the teeming waters (after the window's close; <see cref="Leave"/> answers it).</summary>
    public static bool AbandonAsked => AbandonPrompt() != null;

    private List<FishInfo> wanted = new();
    private string watching = "";
    private bool begun, stopped, seenOpen, collectToggled;
    private DateTime begunAt, openedAt;
    private DateTime? closedAt;
    /// <summary>No action before this (Environment.TickCount64): the wait after the last one.</summary>
    private long blockUntil;
    private long nextLeave;
    private readonly Random rng = new();
    private readonly SpearLaneFish[] lanes = new SpearLaneFish[3];

    // The bags: every spearfish and the wanted fish, counted at the last look.
    private readonly HashSet<uint> tracked = new();
    private Dictionary<uint, int> baseline = new(), counts = new();
    private bool baselineReady;
    private DateTime lastLook;

    /// <summary>
    /// Starts gigging in the open window: only fish whose size and speed match one of <paramref name="wanted"/>
    /// (every fish when it is empty). Fish that pass by unwanted are left alone.
    /// </summary>
    public void Begin(IReadOnlyList<FishInfo> wanted)
    {
        // Begun again while a node is worked (another wanted list in the same window, or the next node while
        // the last one settles): keep counting from the last look, so a catch on its way to the bags still
        // counts, and is told on the next tick rather than from in here.
        var carryOn = begun && !Finished && baselineReady && DateTime.UtcNow - lastLook < CarryOver;
        var trackedBefore = carryOn ? new HashSet<uint>(tracked) : null;
        this.wanted = wanted.ToList();
        begun = true;
        stopped = false;
        seenOpen = false;
        collectToggled = false;
        Finished = false;
        closedAt = null;
        begunAt = DateTime.UtcNow;
        Gigs = 0;
        Catches = 0;
        tracked.Clear();
        tracked.UnionWith(AllSpearfish.Value);
        tracked.UnionWith(this.wanted.Select(f => f.ItemId));
        if (trackedBefore == null)
        {
            baselineReady = ReadBags(baseline);
            lastLook = DateTime.UtcNow;
        }
        else if (!tracked.IsSubsetOf(trackedBefore) && ReadBags(counts))
        {
            // Fish newly looked for start from what the bags hold now.
            foreach (var id in tracked.Where(id => !trackedBefore.Contains(id)))
                baseline[id] = counts.GetValueOrDefault(id);
        }

        foreach (var f in this.wanted.Where(f => !SpearFish.Recognisable(f)))
            Services.Log.Warning("Spearfishing: {Fish} can't be recognised in the window (its size or speed isn't known), so it won't be gigged", Name(f.ItemId));
        var gigLearned = FishAccess.Learned(FishAccess.Gig, GameState.JobLevel(FishAccess.Fisher));
        if (!gigLearned) Services.Log.Warning("Spearfishing: Gig isn't learned yet (Fisher level 61 and its quest)");
        watching = Watching(gigLearned);
        Active = WindowOpen;
        Status = Active ? watching : "Waiting for the spearfishing window to open";
        Services.Log.Debug("Spearfishing: gigging {Fish}", this.wanted.Count == 0
            ? "every fish"
            : string.Join(", ", this.wanted.Select(f => $"{f.ItemId} ({f.Size}, {f.Speed})")));
    }

    /// <summary>Every frame.</summary>
    public void Tick()
    {
        if (!begun || Finished) return;
        var c = Services.Condition;
        var loading = c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51];
        if (!loading) CountCatches();
        // A Caught handler may have stopped or begun again; carry on with what it left.
        if (!begun || Finished) return;

        var addon = Window();
        if (addon == null)
        {
            Closed();
            return;
        }
        if (!seenOpen) openedAt = DateTime.UtcNow;
        seenOpen = true;
        closedAt = null;
        Active = !stopped;
        var caught = Catches > 0 ? $" ({Catches} caught here)" : "";
        if (stopped)
        {
            Status = "Stopped gigging" + caught;
            return;
        }
        Status = watching + caught;
        if (Environment.TickCount64 < blockUntil) return;
        if (Collect && KeepCollectable()) return;
        if (!CanAct()) return;
        if (CollectFirst()) return;
        GigIfInHitbox(addon);
    }

    /// <summary>
    /// Stops gigging. The window stays open: it closes by itself once the node is used up, which gigging does, so
    /// to be done with a node without gigging, <see cref="Leave"/> it.
    /// </summary>
    public void Stop()
    {
        stopped = true;
        Active = false;
        if (begun && !Finished) Status = "Stopped gigging";
    }

    /// <summary>
    /// Leaves the node now: stops gigging and closes the window as its close button does; the game then asks
    /// "Abandon the teeming waters?" (Addon 12683), which this answers with Yes, and the node is gone. The game
    /// won't let go while an action plays out ("Unable to abandon spearfishing when performing actions"), so call
    /// every frame until neither <see cref="WindowOpen"/> nor <see cref="AbandonAsked"/>. True when something was
    /// pressed now.
    /// </summary>
    public bool Leave()
    {
        Stop();
        var now = Environment.TickCount64;
        if (now < blockUntil || now < nextLeave || !CanAct() || Services.Condition[ConditionFlag.ExecutingGatheringAction]) return false;
        if (AbandonPrompt() is var prompt && prompt != null)
        {
            nextLeave = now + LeaveRetryMs;
            new AddonMaster.SelectYesno((nint)prompt).Yes();
            Services.Log.Debug("Spearfishing: abandoning the teeming waters");
            return true;
        }
        var addon = Window();
        if (addon == null) return false;
        nextLeave = now + LeaveRetryMs;
        addon->AtkUnitBase.Close(true);
        Services.Log.Debug("Spearfishing: closing the window");
        return true;
    }

    /// <summary>A spearfishing node object that can be opened now (targetable, attempts left).</summary>
    public static bool Usable(IGameObject node)
        => node.IsValid() && node.IsTargetable && ReadNode(node) is { AttemptsLeft: > 0 };

    /// <summary>
    /// The closest usable spearfishing node among these GatheringPoint rows (by data id), skipping
    /// <paramref name="skip"/> (game object ids); null when none is in the object table.
    /// </summary>
    public static IGameObject? NearestNode(IReadOnlyCollection<uint> points, IReadOnlySet<ulong>? skip = null)
        => Nearest(points, skip, targetableOnly: true);

    /// <summary>
    /// Like <see cref="NearestNode"/>, including a node that isn't targetable yet: one comes into view a moment
    /// before the server lets it be targeted, and walking up to it meanwhile saves time (GatherBuddy Reborn
    /// moves to it with "Waiting for node to become targetable").
    /// </summary>
    public static IGameObject? NearestVisibleNode(IReadOnlyCollection<uint> points, IReadOnlySet<ulong>? skip = null)
        => Nearest(points, skip, targetableOnly: false);

    // ------------------------------------------------------------------ the window

    /// <summary>The SpearFishing window while it's open (AutoHook's test: loaded, with a window node).</summary>
    private static AddonSpearFishing* Window()
        => GenericHelpers.TryGetAddonByName<AddonSpearFishing>("SpearFishing", out var a) && a != null && a->AtkUnitBase.WindowNode != null ? a : null;

    /// <summary>
    /// The abandon question's first line in the client's language, without spaces (the second line comes in colour,
    /// and French puts a space before "?"); "" when the sheet can't be read, and then no question is answered.
    /// </summary>
    private static readonly Lazy<string> AbandonText = new(() =>
    {
        try
        {
            var text = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRowOrDefault(AbandonQuestion)?.Text.ExtractText() ?? "";
            return Squash(text.Split('\r', '\n').FirstOrDefault(line => line.Trim().Length > 0) ?? "");
        }
        catch (Exception e)
        {
            Services.Log.Warning(e, "Spearfishing: couldn't read the question asked when leaving a node");
            return "";
        }
    });

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    private static string Squash(string text) => Spaces.Replace(text, "");

    /// <summary>The "Abandon the teeming waters?" question while it's up and ready; any other yes/no is left alone.</summary>
    private static AtkUnitBase* AbandonPrompt()
    {
        if (AbandonText.Value.Length == 0) return null;
        var yesno = Interact.Ready("SelectYesno");
        if (yesno == null) return null;
        try
        {
            return Squash(new AddonMaster.SelectYesno((nint)yesno).Text).Contains(AbandonText.Value, StringComparison.Ordinal) ? yesno : null;
        }
        catch (Exception)
        {
            // A prompt whose text can't be read isn't ours to answer.
            return null;
        }
    }

    /// <summary>The window is gone: the node is done once the last catch had time to land, or it never opened.</summary>
    private void Closed()
    {
        Active = false;
        var now = DateTime.UtcNow;
        if (!seenOpen)
        {
            if (now - begunAt < OpenTimeout)
            {
                Status = "Waiting for the spearfishing window to open";
                return;
            }
            Services.Log.Warning("Spearfishing: the window didn't open");
            Finish("The spearfishing window didn't open");
            return;
        }
        closedAt ??= now;
        var c = Services.Condition;
        var gathering = c[ConditionFlag.Gathering] || c[ConditionFlag.ExecutingGatheringAction];
        var since = now - closedAt.Value;
        if (since < SettleMax && (since < Settle || gathering))
        {
            Status = "Finishing at the node";
            return;
        }
        Finish(Catches > 0 ? $"Done at this node ({Catches} caught)" : "Done at this node");
    }

    private void Finish(string status)
    {
        Finished = true;
        Active = false;
        Status = status;
        Services.Log.Debug("Spearfishing: {Status}, {Gigs} gigs", status, Gigs);
    }

    /// <summary>
    /// Reads the lanes (bottom to top) and gigs when a wanted fish is in the hitbox. AutoHook's
    /// GigComponent.TryGigFromWorldState and IsFishInHitbox: lane area node 43, fish sprite nodes 61/60/59,
    /// the window's scale; the first lane from the bottom with a wanted fish in the hitbox wins.
    /// </summary>
    private void GigIfInHitbox(AddonSpearFishing* addon)
    {
        var area = addon->AtkUnitBase.GetNodeById(LaneArea);
        if (area == null) return;
        var layout = new SpearLanes(area->X, area->Width, area->ScaleX, addon->AtkUnitBase.Scale);
        for (var i = 0; i < lanes.Length; i++)
        {
            var fish = addon->Fish[i];
            var sprite = addon->AtkUnitBase.GetNodeById(FishSprites[i]);
            lanes[i] = new SpearLaneFish(fish.Available, (sbyte)fish.Size, fish.Speed, fish.InverseDirection,
                sprite == null ? null : new SpearSprite(sprite->X, sprite->Width, sprite->ScaleX));
        }
        var lane = SpearAim.Pick(layout, lanes, wanted);
        if (lane < 0 || !Ready(FishAccess.Gig)) return;
        Use(FishAccess.Gig);
        Gigs++;
        var shown = lanes[lane];
        var takenFor = wanted.Count == 0 ? "any fish" : string.Join(" or ", SpearFish.Candidates(wanted, shown.Size, shown.Speed).Select(f => Name(f.ItemId)));
        Services.Log.Debug("Spearfishing: gig on lane {Lane} (size {Size}, speed {Speed}, {Direction}) for {Fish}",
            lane, shown.Size, shown.Speed, shown.RightToLeft ? "right to left" : "left to right", takenFor);
    }

    /// <summary>
    /// Collect left on: one press turns it off (once per node), so the catches come in as normal items. Until
    /// then gigs wait, for <see cref="CollectHold"/> at most (a fish caught meanwhile would be a collectable).
    /// True when this frame goes to Collect: the press, or waiting for it.
    /// </summary>
    private bool CollectFirst()
    {
        if (collectToggled || HasStatus(CollectStatus) == Collect) return false;
        if (!Ready(FishAccess.Collect)) return DateTime.UtcNow - openedAt < CollectHold;
        collectToggled = true;
        Use(FishAccess.Collect);
        Services.Log.Information(Collect ? "Spearfishing: turned Collect on, for collectables to reduce"
            : "Spearfishing: Collect was on; turned it off so the fish come in as normal items");
        return true;
    }

    /// <summary>
    /// With Collect on, a catch may ask whether to keep it as a collectable: keep it. Never the question about
    /// abandoning the teeming waters. True when it answered (then wait, as after any action).
    /// </summary>
    private bool KeepCollectable()
    {
        var yesno = Interact.Ready("SelectYesno");
        if (yesno == null || AbandonPrompt() != null) return false;
        new AddonMaster.SelectYesno((nint)yesno).Yes();
        blockUntil = Environment.TickCount64 + rng.Next(MinDelayMs, MaxDelayMs + 1);
        Services.Log.Debug("Spearfishing: kept a catch as a collectable");
        return true;
    }

    /// <summary>The game takes the action now: learned, nothing in the way, off its recast (AutoHook's ActionAvailable).</summary>
    private static bool Ready(uint actionId)
    {
        var am = ActionManager.Instance();
        return am != null && am->GetActionStatus(ActionType.Action, actionId) == 0 && !am->IsRecastTimerActive(ActionType.Action, actionId);
    }

    /// <summary>Uses an action, then nothing for a random 600–1000 ms, as AutoHook does after every action.</summary>
    private void Use(uint actionId)
    {
        var am = ActionManager.Instance();
        if (am != null) am->UseAction(ActionType.Action, actionId);
        blockUntil = Environment.TickCount64 + rng.Next(MinDelayMs, MaxDelayMs + 1);
    }

    /// <summary>Alive, as Fisher, and not changing areas or in a cutscene.</summary>
    private static bool CanAct()
    {
        if (Services.Objects.LocalPlayer is not { } me || me.IsDead || me.CurrentHp == 0 || me.ClassJob.RowId != FishAccess.Fisher) return false;
        var c = Services.Condition;
        return !(c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51] || c[ConditionFlag.Unconscious]
                 || c[ConditionFlag.OccupiedInCutSceneEvent] || c[ConditionFlag.WatchingCutscene] || c[ConditionFlag.WatchingCutscene78]);
    }

    private static bool HasStatus(uint statusId) => Services.Objects.LocalPlayer?.StatusList.Any(s => s.StatusId == statusId) == true;

    private string Watching(bool gigLearned)
    {
        if (!gigLearned) return "Gig isn't learned yet, so nothing can be caught";
        if (wanted.Count == 0) return "Gigging every fish that swims by";
        var names = wanted.Where(SpearFish.Recognisable).Select(f => Name(f.ItemId)).Distinct().ToList();
        if (names.Count == 0) return "Can't tell the wanted fish by size and speed, so not gigging";
        var list = names.Count switch
        {
            1 => names[0],
            2 => $"{names[0]} or {names[1]}",
            _ => $"{names[0]}, {names[1]} or {names.Count - 2} more",
        };
        return $"Waiting for {list} to swim by";
    }

    private static string Name(uint itemId)
        => Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(itemId)?.Name.ExtractText() is { Length: > 0 } name ? name : $"fish {itemId}";

    // ------------------------------------------------------------------ catches

    /// <summary>Every spearfish (SpearfishingItem sheet), so a look-alike or other by-catch is counted under its own id.</summary>
    private static readonly Lazy<HashSet<uint>> AllSpearfish = new(() =>
    {
        try
        {
            return Services.Data.GetExcelSheet<Lumina.Excel.Sheets.SpearfishingItem>().Select(r => r.Item.RowId).Where(id => id != 0).ToHashSet();
        }
        catch (Exception e)
        {
            Services.Log.Warning(e, "Couldn't read the spearfish list; counting only the wanted fish");
            return new HashSet<uint>();
        }
    });

    private static readonly InventoryType[] Bags = { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 };

    /// <summary>
    /// Counts the tracked fish in the main bags, normal and collectable alike (fish never come in high
    /// quality). False when the bags aren't loaded: nothing is compared then.
    /// </summary>
    private bool ReadBags(Dictionary<uint, int> into)
    {
        into.Clear();
        var inv = InventoryManager.Instance();
        if (inv == null) return false;
        foreach (var bag in Bags)
        {
            var container = inv->GetInventoryContainer(bag);
            if (container == null || container->Size == 0) return false;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null) continue;
                var id = slot->GetBaseItemId();
                if (id == 0 || !tracked.Contains(id)) continue;
                into[id] = into.GetValueOrDefault(id) + (int)slot->GetQuantity();
            }
        }
        return true;
    }

    /// <summary>
    /// Raises <see cref="Caught"/> for every fish that came into the bags since the last look. The new counts
    /// become the baseline before anyone hears of the catch, so a handler that begins again counts nothing twice.
    /// </summary>
    private void CountCatches()
    {
        if (!ReadBags(counts)) return;
        lastLook = DateTime.UtcNow;
        if (!baselineReady)
        {
            (baseline, counts) = (counts, baseline);
            baselineReady = true;
            return;
        }
        var gains = SpearCatches.Gains(baseline, counts);
        (baseline, counts) = (counts, baseline);
        foreach (var (item, amount) in gains)
        {
            Catches += amount;
            Services.Log.Debug("Spearfishing: caught {Amount}x {Fish}", amount, Name(item));
            try
            {
                Raise(item, amount);
            }
            catch (Exception e)
            {
                // The baseline has moved on: a failing handler mustn't cost the other catches their turn.
                Services.Log.Error(e, "Spearfishing: handling a catch failed");
            }
        }
    }

    // ------------------------------------------------------------------ nodes

    /// <summary>A spearfishing node as its event handler holds it: GatheringPoint row, GatheringPointBase row, attempts left.</summary>
    private readonly record struct NodeState(uint PointId, uint BaseId, byte AttemptsLeft);

    /// <summary>
    /// The node's state when the object is a spearfishing node. GatherBuddy Reborn's TryGetSpearfishingNodeState
    /// (AutoGather.Spearfishing.cs:40-72): try Impl, then ObjectImplBase, then ObjectImpl; the event handler's
    /// GatheringType is 4 or 5 (Spearfishing, Spearfishing2) and RemainingCount (0x30A) the attempts left.
    /// </summary>
    private static NodeState? ReadNode(IGameObject o)
    {
        if (o.ObjectKind != ObjectKind.GatheringPoint || o.Address == nint.Zero) return null;
        var point = (GatheringPointObject*)o.Address;
        return ReadImpl(point->Impl)
               ?? ReadImpl(&point->ObjectImplBase)
               ?? ReadImpl((GatheringPointObject.GatheringPointObjectImplBase*)&point->ObjectImpl);
    }

    private static NodeState? ReadImpl(GatheringPointObject.GatheringPointObjectImplBase* impl)
    {
        if (impl == null || impl->EventHandler == null) return null;
        var handler = impl->EventHandler;
        if ((byte)handler->GatheringType is not (4 or 5)) return null;
        return handler->RowId != 0 || handler->BaseRowId != 0 ? new NodeState(handler->RowId, handler->BaseRowId, handler->RemainingCount) : null;
    }

    /// <summary>
    /// The closest spearfishing node with attempts left whose GatheringPoint row is one of <paramref name="points"/>
    /// (the object's data id, or its event handler's row), by distance in 3D like GatherBuddy Reborn's FindClosestObject.
    /// </summary>
    private static IGameObject? Nearest(IReadOnlyCollection<uint> points, IReadOnlySet<ulong>? skip, bool targetableOnly)
    {
        if (points.Count == 0 || Services.Objects.LocalPlayer is not { } me) return null;
        IGameObject? best = null;
        var bestDistance = float.MaxValue;
        foreach (var o in Services.Objects)
        {
            if (o.ObjectKind != ObjectKind.GatheringPoint || (targetableOnly && !o.IsTargetable)) continue;
            if (skip != null && skip.Contains(o.GameObjectId)) continue;
            if (ReadNode(o) is not { AttemptsLeft: > 0 } node) continue;
            if (!points.Contains(o.BaseId) && !(node.PointId != 0 && points.Contains(node.PointId))) continue;
            var distance = Vector3.DistanceSquared(o.Position, me.Position);
            if (distance < bestDistance)
            {
                best = o;
                bestDistance = distance;
            }
        }
        return best;
    }

    private void Raise(uint item, int amount) => Caught?.Invoke(item, amount);
}
