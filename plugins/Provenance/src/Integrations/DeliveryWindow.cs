using System;
using System.Collections.Generic;
using ECommons.Automation;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Provenance.Integrations;

/// <summary>
/// The game's custom-delivery windows, one step at a time: the delivery window (<c>SatisfactionSupply</c>), the
/// hand-over (<c>Request</c> and its item picker <c>ContextIconMenu</c>), and the rank-up rewards
/// (<c>SatisfactionSupplyResult</c>).
/// </summary>
/// <remarks>
/// Facts from the custom-delivery research: nothing is sent while the game has scene tasks pending (doing so resumes
/// a finished event script and crashes the game, found by ffxiv_satisfy); the hand-over always uses request 0, not
/// the delivery slot; the item picker's callback needs 1021003. The Request and ContextIconMenu callbacks follow
/// PandorasBox's AutoSelectTurnin (BSD-3), the buttons ECommons' AddonMaster (MIT).
/// </remarks>
internal static unsafe class DeliveryWindow
{
    /// <summary>The game is between event-script steps: send nothing now.</summary>
    public static bool SceneBusy
    {
        get
        {
            var ef = EventFramework.Instance();
            return ef != null && ef->EventSceneModule.TaskManager.Tasks.LongCount > 0;
        }
    }

    /// <summary>
    /// The delivery window is open, filled in, for this client, and safe to use: nothing on top of it (talk, the
    /// hand-over, a menu, a question, a cutscene), as ffxiv_satisfy found it has to be.
    /// </summary>
    public static bool SupplyReady(uint client)
    {
        var agent = AgentSatisfactionSupply.Instance();
        if (agent == null || !agent->IsAgentActive() || agent->NpcInfo.Id != client || !agent->NpcInfo.Valid || !agent->NpcInfo.Initialized) return false;
        if (Interact.Ready("SatisfactionSupply") == null || SceneBusy) return false;
        if (Interact.IsOpen("Talk") || Interact.IsOpen("Request") || Interact.IsOpen("SelectString") || Interact.IsOpen("SelectYesno")) return false;
        var trade = AgentNpcTrade.Instance();
        if (trade != null && trade->IsAgentActive()) return false;
        return !Services.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInCutSceneEvent];
    }

    /// <summary>Picks a request in the delivery window (0 crafted, 1 gathered, 2 fish).</summary>
    public static bool PickRequest(int slot)
    {
        if (SceneBusy || Interact.Ready("SatisfactionSupply") is var addon && addon == null) return false;
        Callback.Fire(addon, true, 1, slot);
        return true;
    }

    public static bool RequestOpen => Interact.Ready("Request") != null;

    /// <summary>
    /// Fills the hand-over with the item: opens the picker, then picks the copy that meets the collectability (the
    /// lowest that does, or the highest with <paramref name="highest"/>). False while waiting, or when none qualifies
    /// (<paramref name="noneFits"/>).
    /// </summary>
    public static bool Fill(uint itemId, int minCollectability, bool highest, out bool noneFits)
    {
        noneFits = false;
        if (SceneBusy) return false;
        var trade = AgentNpcTrade.Instance();
        if (Interact.Ready("ContextIconMenu") is var menu && menu != null)
        {
            if (trade == null) return false;
            var pick = -1;
            var pickValue = highest ? -1 : int.MaxValue;
            for (var i = 0; i < trade->SelectedTurnInSlotItemOptions && i < 140; i++)
            {
                var item = trade->SelectedTurnInSlotItemOptionValues[i].Value;
                if (item == null || item->ItemId != itemId || !item->Flags.HasFlag(InventoryItem.ItemFlags.Collectable)) continue;
                int value = item->SpiritbondOrCollectability;
                if (value < minCollectability) continue;
                if (highest ? value > pickValue : value < pickValue) { pick = i; pickValue = value; }
            }
            if (pick < 0)
            {
                noneFits = true;
                Interact.Close("ContextIconMenu");
                return false;
            }
            Callback.Fire(menu, true, 0, pick, 1021003, 0, 0);
            return true;
        }
        if (Interact.Ready("Request") is var request && request != null && (trade == null || trade->SelectedTurnInSlot < 0))
            Callback.Fire(request, false, 2, 0, 0, 0);
        return false;
    }

    /// <summary>The hand-over button can be pressed (the item is in).</summary>
    public static bool CanHandOver
    {
        get
        {
            if (SceneBusy || Interact.Ready("Request") is var addon && addon == null) return false;
            return ButtonReady(new AddonMaster.Request((nint)addon).HandOverButton);
        }
    }

    public static bool HandOver()
    {
        if (!CanHandOver) return false;
        new AddonMaster.Request((nint)Interact.Ready("Request")).HandOver();
        return true;
    }

    /// <summary>The rank-up rewards window is up and can be accepted.</summary>
    public static bool RankUpReady
    {
        get
        {
            if (SceneBusy || Interact.Ready("SatisfactionSupplyResult") is var addon && addon == null) return false;
            return ButtonReady(new AddonMaster.SatisfactionSupplyResult((nint)addon).AcceptButton);
        }
    }

    public static bool AcceptRankUp()
    {
        if (!RankUpReady) return false;
        new AddonMaster.SatisfactionSupplyResult((nint)Interact.Ready("SatisfactionSupplyResult")).Accept();
        return true;
    }

    /// <summary>A button that's there, enabled and wired to its event (each pointer checked before it's followed).</summary>
    private static bool ButtonReady(AtkComponentButton* b)
        => b != null && b->AtkComponentBase.OwnerNode != null && b->AtkResNode != null && b->IsEnabled && b->AtkResNode->IsVisible()
           && b->AtkComponentBase.OwnerNode->AtkResNode.AtkEventManager.Event != null;

    // ------------------------------------------------------------------ YesAlready

    private const string YesAlreadyStop = "YesAlready.StopRequests";
    private static HashSet<string>? yesAlready;

    /// <summary>
    /// Asks YesAlready to stand aside while deliveries are handed in (its own turn-in races ours), through its shared
    /// stop list. Fact from xiv-mcp (MIT).
    /// </summary>
    public static void HoldYesAlready(bool hold)
    {
        try
        {
            if (hold)
            {
                if (yesAlready != null) return;
                yesAlready = Services.PluginInterface.GetOrCreateData(YesAlreadyStop, () => new HashSet<string>());
                lock (yesAlready) yesAlready.Add("Provenance");
            }
            else if (yesAlready != null)
            {
                lock (yesAlready) yesAlready.Remove("Provenance");
                yesAlready = null;
                Services.PluginInterface.RelinquishData(YesAlreadyStop);
            }
        }
        catch (Exception e)
        {
            Services.Log.Debug("YesAlready's stop list: {Message}", e.Message);
        }
    }
}
