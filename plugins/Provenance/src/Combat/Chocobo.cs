using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using Provenance.Integrations;

namespace Provenance.Combat;

/// <summary>The chocobo companion: summoned with Gysahl Greens from the bags, then put in the chosen stance.</summary>
internal static unsafe class Chocobo
{
    public const uint GysahlGreens = 4868;

    public static bool Out => Services.Buddies.CompanionBuddy != null;

    /// <summary>Greens in the bags, in an open zone, not mounted, and the game allows using them now.</summary>
    public static bool CanSummon
    {
        get
        {
            if (Out || GameState.BagCount(GysahlGreens) <= 0) return false;
            var c = Services.Condition;
            if (c[ConditionFlag.Mounted] || c[ConditionFlag.InCombat] || c[ConditionFlag.Casting] || c[ConditionFlag.BetweenAreas]) return false;
            var territory = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().GetRowOrDefault(Services.ClientState.TerritoryType);
            if (territory?.TerritoryIntendedUse.RowId != 1) return false;
            var am = ActionManager.Instance();
            return am != null && am->GetActionStatus(ActionType.Item, GysahlGreens) == 0;
        }
    }

    public static bool Summon() => GameState.UseItem(GysahlGreens, false);

    /// <summary>Free (4), Defender (5), Attacker (6) or Healer (7) stance.</summary>
    public static bool SetStance(uint buddyAction)
    {
        var am = ActionManager.Instance();
        return am != null && Out && am->UseAction(ActionType.BuddyAction, buddyAction);
    }
}
