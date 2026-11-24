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

    /// <summary>
    /// One step of getting the chocobo ready before a fight, when it's wanted: summoned when it isn't out (and can be),
    /// then put in its stance. Returns how many seconds to give the step, or 0 when there's nothing (more) to do.
    /// </summary>
    /// <param name="stanceSet">Kept by the caller: the stance was set since the chocobo came out.</param>
    public static double Ready(bool wanted, uint stance, ref bool stanceSet)
    {
        if (!wanted) return 0;
        if (!Out)
        {
            stanceSet = false;
            if (!CanSummon) return 0;
            Summon();
            return 2.5;
        }
        if (stanceSet) return 0;
        stanceSet = SetStance(stance);
        return 1;
    }
}
