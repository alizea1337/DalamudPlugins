using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using Provenance.Core.Gathering;

namespace Provenance.Integrations;

/// <summary>
/// GP potions in the bags: Hi-Cordial, Cordial and Watered Cordial (normal and high quality). What
/// each gives back and its cooldown come from the item sheet. Never bought, only drunk.
/// </summary>
internal static unsafe class Cordials
{
    private static readonly uint[] ItemIds = { 12669, 6141, 16911 };
    private static List<Cordial>? kinds;

    /// <summary>Every kind, high-quality ones as their own entry.</summary>
    public static IReadOnlyList<Cordial> Kinds => kinds ??= Load();

    private static List<Cordial> Load()
    {
        var result = new List<Cordial>();
        var items = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        foreach (var id in ItemIds)
        {
            if (items.GetRowOrDefault(id) is not { } row || row.ItemAction.ValueNullable is not { } action) continue;
            int gp = action.Data[0], gpHq = action.DataHQ[0];
            if (gp > 0) result.Add(new Cordial(id, false, gp, row.Cooldowns));
            if (row.CanBeHq && gpHq > 0) result.Add(new Cordial(id, true, gpHq, row.Cooldowns));
        }
        return result;
    }

    private static int Count(Cordial c) => GameState.BagCount(c.ItemId, c.Hq);

    public static bool AnyInBags => Kinds.Any(c => Count(c) > 0);

    /// <summary>The kinds in the main bags, cooldown or not.</summary>
    public static IEnumerable<Cordial> KindsInBags() => Kinds.Where(c => Count(c) > 0);

    /// <summary>One entry per potion in the main bags, for estimates; capped per kind so every kind stays in.</summary>
    public static List<Cordial> InBags(int capPerKind = 20)
    {
        var result = new List<Cordial>();
        foreach (var c in Kinds)
            for (var n = System.Math.Min(Count(c), capPerKind); n > 0; n--) result.Add(c);
        return result;
    }

    /// <summary>
    /// Seconds until the cordials' shared cooldown is over (0 when ready). Unlike <see cref="Ready"/>,
    /// this can be read on a mount.
    /// </summary>
    public static double SecondsUntilReady()
    {
        var am = ActionManager.Instance();
        if (am == null) return 0;
        foreach (var c in KindsInBags())
        {
            var id = ActionId(c);
            return am->IsRecastTimerActive(ActionType.Item, id) ? System.Math.Max(0, am->GetRecastTime(ActionType.Item, id) - am->GetRecastTimeElapsed(ActionType.Item, id)) : 0;
        }
        return 0;
    }

    private static uint ActionId(Cordial c) => c.Hq ? c.ItemId + 1_000_000 : c.ItemId;

    /// <summary>Kinds in the bags the game lets you drink right now (off cooldown, not mounted).</summary>
    public static IEnumerable<Cordial> Ready() => Kinds.Where(c => Count(c) > 0 && GameState.CanUse(ActionType.Item, ActionId(c)));

    public static bool Drink(Cordial c) => GameState.UseItem(c.ItemId, c.Hq);

    public static string Name(Cordial c)
    {
        var name = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(c.ItemId)?.Name.ExtractText() ?? "cordial";
        return c.Hq ? name + " (high quality)" : name;
    }
}
