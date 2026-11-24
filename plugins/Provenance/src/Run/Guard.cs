using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using ECommons;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Provenance.Core.Run;
using Provenance.Integrations;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Provenance.Run;

/// <summary>
/// Security checks that run every frame: cancels trade windows someone else opened (a common scam is to trade a
/// moment before a run moves items around), and while a run works, stops everything when the character is moved
/// and nothing explains the move (a game master can summon a character, for example), when someone may be
/// watching (a game master around, a player appearing close by, a player close by the game doesn't draw), or when
/// Questionable starts running its quests (two plugins can't drive the character at once).
/// </summary>
internal sealed unsafe class Guard
{
    private readonly Plugin plugin;
    private DateTime lastTrade;
    private readonly MoveWatch moves = new();
    private readonly PlayerWatch players = new();
    private bool watching;

    public Guard(Plugin plugin) => this.plugin = plugin;

    public void Tick()
    {
        WatchOtherPlugins();
        Watch();
        var s = plugin.Config.Security;
        if (!s.DeclineTrades || (s.TradesOnlyDuringRuns && !(plugin.Runs.Active || plugin.CosmicLoop.Active || plugin.DeliveryLoop.Active || plugin.TribeLoop.Active))) return;
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("Trade", out var trade) || !trade->IsVisible) return;
        if (DateTime.UtcNow - lastTrade < TimeSpan.FromSeconds(1)) return;
        lastTrade = DateTime.UtcNow;
        Callback.Fire(trade, true, -1);
        trade->Close(true);
        plugin.Runs.Note("Declined a trade request.");
        Services.Chat.Print("[Provenance] Declined a trade request (Security tab).");
    }

    /// <summary>
    /// Questionable can't be paused from outside, so while a run works and it starts running its quests, everything
    /// stops as with the tripwire (whatever the tripwire's settings). A run that isn't working is left as it is.
    /// </summary>
    private void WatchOtherPlugins()
    {
        var working = plugin.Runs.Working || plugin.CosmicLoop.Working || plugin.DeliveryLoop.Working || plugin.TribeLoop.Working;
        if (OtherPlugins.StopReason(working) is { } reason) StopAll(reason, "Questionable is running");
    }

    /// <summary>
    /// While the run or the Cosmic loop works (not paused, not waiting out a duty or a knock-out): a frame of where
    /// the character is, whether anything explains a move, and who is around. Starting, resuming or a pause starts
    /// the watch afresh.
    /// </summary>
    private void Watch()
    {
        var sec = plugin.Config.Security;
        var look = new WatchFor(sec.StopForGameMaster, sec.StopWhenPlayerAppears, sec.StopForHiddenPlayer, Math.Clamp(sec.NearbyYalms, 5, 50));
        var lookAround = look.GameMasters || look.Appearing || look.Hidden;
        var working = (sec.StopWhenMoved || lookAround) && (plugin.Runs.Working || plugin.CosmicLoop.Working || plugin.DeliveryLoop.Working || plugin.TribeLoop.Working);
        if (!working)
        {
            if (watching) { moves.Reset(); players.Reset(); }
            watching = false;
            return;
        }
        watching = true;
        var now = DateTime.UtcNow;
        var c = Services.Condition;
        var me = Services.Objects.LocalPlayer;
        var loading = me == null || c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51];
        var explained = MoveIntent.Expected
                        || c[ConditionFlag.Casting] || c[ConditionFlag.Casting87]
                        || c[ConditionFlag.OccupiedInEvent] || c[ConditionFlag.OccupiedInQuestEvent]
                        || c[ConditionFlag.OccupiedInCutSceneEvent] || c[ConditionFlag.WatchingCutscene] || c[ConditionFlag.WatchingCutscene78]
                        || c[ConditionFlag.Unconscious]
                        || c[ConditionFlag.BoundByDuty] || c[ConditionFlag.BoundByDuty56] || c[ConditionFlag.BoundByDuty95];

        if (sec.StopWhenMoved
            && moves.Feed(new MoveSample(now, Services.ClientState.TerritoryType, me?.Position ?? default, loading, explained)) is { } move)
        {
            var what = move.ZoneChanged
                ? $"the character was moved to {ZoneName(move.To)}"
                : $"the character was moved {move.Yalms:N0} yalms in an instant";
            StopEverything($"{what} without a teleport, the aethernet, a door or anything Provenance did",
                $"unexpected move: zone {move.From} -> {move.To}, {move.Yalms:0.0} yalms");
            return;
        }

        if (!lookAround) return;
        // A zone change or a trip brings new players around without anyone having appeared.
        if (loading || explained) players.Unsettle(now);
        if (me == null) return;
        if (players.Feed(now, me.Position, Around(me), look) is not { } who) return;
        var obj = Services.Objects.SearchById(who.Id);
        var name = obj?.Name.TextValue is { Length: > 0 } n ? n : "someone";
        var seen = who.Kind switch
        {
            WatcherKind.GameMaster => $"a game master ({name}) is around",
            WatcherKind.Appeared => $"{name} appeared {who.Yalms:N0} yalms away without walking there",
            _ => $"a player the game doesn't draw ({name}) is {who.Yalms:N0} yalms away",
        };
        StopEverything(seen, $"{who.Kind} player {who.Yalms:0.0} yalms away, ref {Reference(obj)}");
    }

    /// <summary>
    /// Who set the tripwire off, for the debug log: "Name@World" in base64. The debug log goes into bug reports, so the
    /// name doesn't read at a glance there, but it decodes for debugging. Base64 is an encoding, not a hash: anyone who
    /// decodes it reads the name.
    /// </summary>
    private static string Reference(Dalamud.Game.ClientState.Objects.Types.IGameObject? obj)
    {
        if (obj?.Name.TextValue is not { Length: > 0 } name) return "-";
        var world = (obj as IPlayerCharacter)?.HomeWorld.ValueNullable?.Name.ExtractText();
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(world is { Length: > 0 } ? $"{name}@{world}" : name));
    }

    /// <summary>The other players in the object table, with what the watch needs to know about each.</summary>
    private static List<SeenPlayer> Around(IPlayerCharacter me)
    {
        var crystals = new List<Vector3>();
        foreach (var o in Services.Objects)
            if (Interact.IsCrystal(o)) crystals.Add(o.Position);
        var list = new List<SeenPlayer>();
        foreach (var o in Services.Objects)
        {
            if (o is not IPlayerCharacter pc || pc.GameObjectId == me.GameObjectId) continue;
            var go = (GameObjectStruct*)pc.Address;
            var hidden = go != null && (go->RenderFlags & VisibilityFlags.Model) != 0;
            var gm = pc.OnlineStatus.RowId is >= 1 and <= 3;
            var party = false;
            foreach (var m in Services.Party)
                if (m.EntityId == pc.EntityId) { party = true; break; }
            var atCrystal = false;
            foreach (var at in crystals)
                if (Vector3.Distance(at, pc.Position) <= 12f) { atCrystal = true; break; }
            list.Add(new SeenPlayer(pc.GameObjectId, pc.Position, hidden, gm, party, atCrystal));
        }
        return list;
    }

    /// <summary>The tripwire went off: everything stops (<see cref="StopAll"/>), with what set it off.</summary>
    private void StopEverything(string what, string forLog) => StopAll($"Stopped everything: {what} (Security tab).", forLog);

    /// <summary>Stops the run and the loops at once (even mid-craft), stops walking and flying, and says why.</summary>
    private void StopAll(string reason, string forLog)
    {
        moves.Reset();
        players.Reset();
        watching = false;
        DebugLog.Write("guard", $"[!] {forLog}; run working {plugin.Runs.Working}, cosmic working {plugin.CosmicLoop.Working}, deliveries working {plugin.DeliveryLoop.Working}, tribes working {plugin.TribeLoop.Working}");
        if (plugin.Runs.Working) plugin.Runs.Stop(reason, problem: true, now: true);
        if (plugin.CosmicLoop.Working) plugin.CosmicLoop.Stop(reason, problem: true);
        if (plugin.DeliveryLoop.Working) plugin.DeliveryLoop.Stop(reason, problem: true);
        if (plugin.TribeLoop.Working) plugin.TribeLoop.Stop(reason, problem: true);
        Vnav.Stop();
        Services.Chat.PrintError("[Provenance] " + reason);
    }

    private static string ZoneName(uint territory)
        => Data.FishAccess.Zone(territory) is { Length: > 0 } name ? name : "another zone";
}
