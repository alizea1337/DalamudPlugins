using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using Provenance.Core.Fishing;
using GamePlayerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState;

namespace Provenance.Integrations.Fishing;

/// <summary>The fishing handler's state in one frame (rod-engine §2.1).</summary>
/// <param name="Handler">The fishing event handler exists (a null handler reads as state None).</param>
/// <param name="Bait">The bait on the line (PlayerState.FishingBait).</param>
/// <param name="Swimbait">The selected swimbait fish, 0 when none is selected.</param>
/// <param name="MoochCast">The cast in the water went out with a fish (Mooch or swimbait).</param>
internal readonly record struct RodRead(FishingState State, bool Handler, uint Bait, uint Swimbait, bool CanMooch, bool CanMoochII,
    bool CanIdenticalCast, bool CanSurfaceSlap, bool MoochCast);

/// <summary>
/// Reads and acts on the rod: the fishing handler (EventFramework → EventHandlerModule.FishingEventHandler,
/// state at +0x1C8, the previous catch's flags at +0x1D1–0x1D6, cast bait flags at +0x1D8), action readiness
/// (status 0 and not recasting, as AutoHook's ActionAvailable), the bait change (ExecuteCommand 701, 4, bait)
/// and the player's statuses. Ported from AutoHook (BSD-3, Puni.sh).
/// </summary>
internal static unsafe class RodGame
{
    /// <summary>ExecuteCommand 701 ("fishing bait"), sub-command 4 equips a bait by item id (AutoHook's BaitComponent).</summary>
    private const int BaitCommand = 701, EquipBait = 4;

    /// <summary>The fishing state now; a missing handler reads as state None.</summary>
    public static RodRead Read()
    {
        uint bait = 0;
        var player = GamePlayerState.Instance();
        if (player != null) bait = player->FishingBait;
        var framework = EventFramework.Instance();
        var handler = framework != null ? framework->EventHandlerModule.FishingEventHandler : null;
        if (handler == null) return new RodRead(FishingState.None, false, bait, 0, false, false, false, false, false);
        uint swimbait = 0;
        var slot = handler->CurrentSelectedSwimBait;
        if (slot is >= 0 and < 3) swimbait = handler->SwimBaitItemIds[slot];
        var mooch = (handler->CurrentCastBaitFlags & (FishingBaitFlags.Mooch | FishingBaitFlags.Swimbait)) != 0;
        return new RodRead(handler->State, true, bait, swimbait, handler->CanMoochPreviousCatch, handler->CanMooch2PreviousCatch,
            handler->CanIdenticalCastPreviousCatch, handler->CanSurfaceSlapPreviousCatch, mooch);
    }

    /// <summary>The decider's view of a fishing state (the lure animations count as the line being out).</summary>
    public static RodPhase Phase(FishingState state) => state switch
    {
        FishingState.None => RodPhase.Idle,
        FishingState.PoleReady => RodPhase.Ready,
        FishingState.CastingOut => RodPhase.CastingOut,
        FishingState.LineInWater or FishingState.AmbitiousLure or FishingState.ModestLure => RodPhase.LineOut,
        FishingState.Bite => RodPhase.Bite,
        _ => RodPhase.Busy,
    };

    /// <summary>The game would take the action now: its status is 0 (GP, level, state, "only on a bite"…) and it isn't recasting.</summary>
    public static bool Ready(uint actionId)
    {
        var am = ActionManager.Instance();
        return am != null && am->GetActionStatus(ActionType.Action, actionId) == 0 && Cooldown(actionId) <= 0;
    }

    /// <summary>The action status alone (0: usable).</summary>
    public static uint Status(uint actionId)
    {
        var am = ActionManager.Instance();
        return am == null ? uint.MaxValue : am->GetActionStatus(ActionType.Action, actionId);
    }

    /// <summary>Seconds until the action's recast is over (0 when it isn't recasting).</summary>
    public static double Cooldown(uint actionId)
    {
        var am = ActionManager.Instance();
        if (am == null || !am->IsRecastTimerActive(ActionType.Action, actionId)) return 0;
        return Math.Max(0, am->GetRecastTime(ActionType.Action, actionId) - am->GetRecastTimeElapsed(ActionType.Action, actionId));
    }

    /// <summary>Uses a Fisher action (as AutoHook's executor: plain UseAction). True when the game took it.</summary>
    public static bool Use(uint actionId)
    {
        var am = ActionManager.Instance();
        return am != null && am->UseAction(ActionType.Action, actionId);
    }

    /// <summary>Puts a bait from the bags on the line (only between casts).</summary>
    public static bool ChangeBait(uint baitId) => GameMain.ExecuteCommand(BaitCommand, EquipBait, (int)baitId, 0, 0);

    /// <summary>The player's statuses: seconds left and stacks (the status param).</summary>
    public static Dictionary<uint, (double Seconds, int Stacks)> Statuses()
    {
        var result = new Dictionary<uint, (double, int)>();
        if (Services.Objects.LocalPlayer is not { } me) return result;
        foreach (var s in me.StatusList)
            if (s.StatusId != 0) result[s.StatusId] = (Math.Abs(s.RemainingTime), s.Param);
        return result;
    }
}
