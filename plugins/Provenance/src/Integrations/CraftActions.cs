using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace Provenance.Integrations;

/// <summary>
/// Raphael answers with one job's action ids. Every crafter has its own copy of each action, with the
/// same name: crafting actions (ids from 100000) live in CraftAction under ClassJobCategory 9–16,
/// the rest (Innovation, Great Strides, …) in Action under the crafter's ClassJob.
/// </summary>
internal static class CraftActions
{
    private static readonly Dictionary<(uint Id, uint Job), (ActionType Type, uint Id)> Cache = new();

    public static (ActionType Type, uint Id) For(uint raphaelId, uint classJob)
    {
        if (Cache.TryGetValue((raphaelId, classJob), out var hit)) return hit;
        var result = Resolve(raphaelId, classJob);
        Cache[(raphaelId, classJob)] = result;
        return result;
    }

    public static string Name(uint raphaelId)
        => raphaelId >= 100000
            ? Services.Data.GetExcelSheet<CraftAction>().GetRowOrDefault(raphaelId)?.Name.ExtractText() ?? "#" + raphaelId
            : Services.Data.GetExcelSheet<Action>().GetRowOrDefault(raphaelId)?.Name.ExtractText() ?? "#" + raphaelId;

    private static (ActionType, uint) Resolve(uint id, uint classJob)
    {
        if (id >= 100000)
        {
            var sheet = Services.Data.GetExcelSheet<CraftAction>();
            var name = sheet.GetRowOrDefault(id)?.Name.ExtractText();
            var category = classJob + 1; // ClassJobCategory 9 = Carpenter … 16 = Culinarian
            var row = sheet.FirstOrDefault(r => r.ClassJobCategory.RowId == category && r.Name.ExtractText() == name);
            return (ActionType.CraftAction, row.RowId != 0 ? row.RowId : id);
        }
        var actions = Services.Data.GetExcelSheet<Action>();
        var actionName = actions.GetRowOrDefault(id)?.Name.ExtractText();
        var match = actions.FirstOrDefault(r => r.ClassJob.RowId == classJob && r.IsPlayerAction && r.Name.ExtractText() == actionName);
        return (ActionType.Action, match.RowId != 0 ? match.RowId : id);
    }
}
