using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Provenance.Core.Gathering;
using Provenance.Integrations;

namespace Provenance.Run.Gathering;

/// <summary>
/// Botanist and miner action ids and the statuses they give (ids as in GatherBuddy Reborn's
/// AutoGather.ActionIDs.cs, Apache-2.0), with level, GP cost and unlock quest read from the Action sheet.
/// </summary>
internal static unsafe class GatherActions
{
    // (botanist, miner, status the action gives)
    private static readonly Dictionary<GatherAction, (uint Btn, uint Min, uint Status)> Ids = new()
    {
        [GatherAction.TwelvesBounty] = (282, 280, 825),
        [GatherAction.Bountiful] = (4087, 4073, 756),
        [GatherAction.SolidAge] = (215, 232, 2765),
        [GatherAction.Yield1] = (222, 239, 219),
        [GatherAction.Yield2] = (224, 241, 219),
        [GatherAction.Gift1] = (21178, 21177, 2666),
        [GatherAction.Gift2] = (25590, 25589, 759),
        [GatherAction.Tidings] = (21204, 21203, 2667),
        [GatherAction.GivingLand] = (4590, 4589, 1802),
        [GatherAction.Wise] = (26522, 26521, 0),
        [GatherAction.Luck] = (4095, 4081, 0),
        [GatherAction.Collect] = (815, 240, 0),
        [GatherAction.Scour] = (22186, 22182, 0),
        [GatherAction.Brazen] = (22187, 22183, 0),
        [GatherAction.Meticulous] = (22188, 22184, 0),
        [GatherAction.Scrutiny] = (22189, 22185, 757),
    };

    /// <summary>Bountiful Yield/Harvest II: its status counts as Bountiful; its quest raises Bountiful's bonus.</summary>
    public const uint BountifulIIStatus = 1286;
    private const uint BountifulIIBtn = 273;

    // Out-of-node buffs: Triangulate / Prospect (shows nodes), Stealth, Truth of Forests / Mountains.
    public static (uint Action, uint Status) Prospect(uint job) => job == 17 ? (210u, 217u) : (227u, 225u);
    public static (uint Action, uint Status) Sneak(uint job) => job == 17 ? (304u, 47u) : (303u, 47u);
    public static (uint Action, uint Status) Truth(uint job) => job == 17 ? (221u, 221u) : (238u, 222u);

    /// <summary>Statuses: Solid Reason's proc for Wise to the World, Collector's (High) Standard for Brazen.</summary>
    public const uint EurekaMoment = 2765;
    public const uint CollectorsStandard = 2418;
    public const uint CollectorsHighStandard = 3911;
    /// <summary>"Gift of the Land" Giving Land buff, which turns every slot's crystals up.</summary>
    public const uint GivingLandStatus = 1802;

    public static uint Id(GatherAction action, uint job) => Ids.TryGetValue(action, out var a) ? job == 17 ? a.Btn : a.Min : 0;
    public static uint Status(GatherAction action) => Ids.TryGetValue(action, out var a) ? a.Status : 0;

    public static Lumina.Excel.Sheets.Action? Row(GatherAction action)
        => Ids.TryGetValue(action, out var a) ? Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(a.Btn) : null;

    public static int Level(GatherAction action) => Row(action)?.ClassJobLevel ?? 999;
    public static int Cost(GatherAction action) => Row(action)?.PrimaryCostValue ?? 0;

    /// <summary>The character has it: level high enough and its unlock quest done.</summary>
    public static bool Learned(GatherAction action, int level)
    {
        if (Row(action) is not { } row || row.ClassJobLevel > level) return false;
        return row.UnlockLink.RowId == 0 || UIState.Instance()->IsUnlockLinkUnlockedOrQuestCompleted(row.UnlockLink.RowId);
    }

    /// <summary>Learned yield/boon actions with their GP cost, for the solver.</summary>
    public static Dictionary<GatherAction, int> LearnedForSolver(int level)
        => new[] { GatherAction.TwelvesBounty, GatherAction.SolidAge, GatherAction.Yield1, GatherAction.Yield2, GatherAction.Gift1,
                   GatherAction.Gift2, GatherAction.Tidings, GatherAction.Bountiful, GatherAction.GivingLand }
            .Where(a => Learned(a, level)).ToDictionary(a => a, Cost);

    /// <summary>Bountiful's extra yield: 1 until Bountiful II's quest, then 1–3 by Gathering against the item's base.</summary>
    public static int BountifulBonus(int gathering, int baseGathering)
    {
        var row = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(BountifulIIBtn);
        if (row is { UnlockLink.RowId: > 0 } r && !UIState.Instance()->IsUnlockLinkUnlockedOrQuestCompleted(r.UnlockLink.RowId)) return 1;
        if (baseGathering <= 0) return 1;
        if (gathering >= baseGathering * 11 / 10) return 3;
        if (gathering >= baseGathering * 9 / 10) return 2;
        return 1;
    }

    /// <summary>Boon chance on a plain node, from Perception against the item's base (GatherBuddy Reborn's curve).</summary>
    public static int BaseBoonChance(int perception, int basePerception)
    {
        if (basePerception <= 0) return 0;
        var score = System.Math.Min(150, 100 * perception / basePerception);
        if (score >= 100) return (score - 100) * (60 - 35) / (150 - 100) + 35;
        if (score >= 80) return (score - 80) * (35 - 15) / (100 - 80) + 15;
        if (score >= 70) return (score - 70) * (15 - 10) / (80 - 70) + 10;
        if (score >= 60) return (score - 60) * 10 / 10;
        return 0;
    }

    /// <summary>Uses an action when the game allows it now. True when it went off.</summary>
    public static bool Use(GatherAction action, uint job)
    {
        var id = Id(action, job);
        var am = ActionManager.Instance();
        return id != 0 && am != null && am->GetActionStatus(ActionType.Action, id) == 0 && am->UseAction(ActionType.Action, id);
    }

    public static bool Use(uint actionId)
    {
        var am = ActionManager.Instance();
        return am != null && am->GetActionStatus(ActionType.Action, actionId) == 0 && am->UseAction(ActionType.Action, actionId);
    }

    /// <summary>Seconds until The Giving Land can be used again.</summary>
    public static float GivingLandCooldown(uint job)
    {
        var am = ActionManager.Instance();
        var id = Id(GatherAction.GivingLand, job);
        if (am == null || id == 0) return 0;
        return am->IsRecastTimerActive(ActionType.Action, id) ? am->GetRecastTime(ActionType.Action, id) - am->GetRecastTimeElapsed(ActionType.Action, id) : 0;
    }

    public static bool HasStatus(uint status) => status != 0 && Services.Objects.LocalPlayer?.StatusList.Any(s => s.StatusId == status) == true;
}
