using System;
using Dalamud.Game.ClientState.Conditions;
using ECommons;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Provenance.Core.Run;
using Provenance.Integrations;

namespace Provenance.Run;

/// <summary>
/// Security checks that run every frame: cancels trade windows someone else opened (a common scam is to trade a
/// moment before a run moves items around), and stops everything when the character is moved while a run works
/// and nothing explains the move (a game master can summon a character, for example).
/// </summary>
internal sealed unsafe class Guard
{
    private readonly Plugin plugin;
    private DateTime lastTrade;
    private readonly MoveWatch moves = new();
    private bool watching;

    public Guard(Plugin plugin) => this.plugin = plugin;

    public void Tick()
    {
        WatchMoves();
        var s = plugin.Config.Security;
        if (!s.DeclineTrades || (s.TradesOnlyDuringRuns && !plugin.Runs.Active)) return;
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("Trade", out var trade) || !trade->IsVisible) return;
        if (DateTime.UtcNow - lastTrade < TimeSpan.FromSeconds(1)) return;
        lastTrade = DateTime.UtcNow;
        Callback.Fire(trade, true, -1);
        trade->Close(true);
        plugin.Runs.Note("Declined a trade request.");
        Services.Chat.Print("[Provenance] Declined a trade request (Security tab).");
    }

    /// <summary>
    /// While the run or the Cosmic loop works (not paused, not waiting out a duty or a knock-out): a frame of where
    /// the character is, and whether anything explains a move. Starting, resuming or a pause starts the watch afresh.
    /// </summary>
    private void WatchMoves()
    {
        var working = plugin.Config.Security.StopWhenMoved && (plugin.Runs.Working || plugin.CosmicLoop.Working);
        if (!working)
        {
            if (watching) moves.Reset();
            watching = false;
            return;
        }
        watching = true;
        var c = Services.Condition;
        var me = Services.Objects.LocalPlayer;
        var loading = me == null || c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51];
        var explained = MoveIntent.Expected
                        || c[ConditionFlag.Casting] || c[ConditionFlag.Casting87]
                        || c[ConditionFlag.OccupiedInEvent] || c[ConditionFlag.OccupiedInQuestEvent]
                        || c[ConditionFlag.OccupiedInCutSceneEvent] || c[ConditionFlag.WatchingCutscene] || c[ConditionFlag.WatchingCutscene78]
                        || c[ConditionFlag.Unconscious]
                        || c[ConditionFlag.BoundByDuty] || c[ConditionFlag.BoundByDuty56] || c[ConditionFlag.BoundByDuty95];
        var sample = new MoveSample(DateTime.UtcNow, Services.ClientState.TerritoryType, me?.Position ?? default, loading, explained);
        if (moves.Feed(sample) is not { } move) return;

        moves.Reset();
        watching = false;
        var what = move.ZoneChanged
            ? $"the character was moved to {ZoneName(move.To)}"
            : $"the character was moved {move.Yalms:N0} yalms in an instant";
        var reason = $"Stopped everything: {what} without a teleport, the aethernet, a door or anything Provenance did (Security tab).";
        DebugLog.Write("guard", $"[!] unexpected move: zone {move.From} -> {move.To}, {move.Yalms:0.0} yalms; run working {plugin.Runs.Working}, cosmic working {plugin.CosmicLoop.Working}");
        if (plugin.Runs.Working) plugin.Runs.Stop(reason, problem: true, now: true);
        if (plugin.CosmicLoop.Working) plugin.CosmicLoop.Stop(reason, problem: true);
        Vnav.Stop();
        Services.Chat.PrintError("[Provenance] " + reason);
    }

    private static string ZoneName(uint territory)
        => Data.FishAccess.Zone(territory) is { Length: > 0 } name ? name : "another zone";
}
