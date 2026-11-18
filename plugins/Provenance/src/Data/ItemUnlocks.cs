using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.Exd;

namespace Provenance.Data;

/// <summary>Whether an item that teaches something (a folklore tome, a book) has already been used.</summary>
internal static unsafe class ItemUnlocks
{
    public static bool IsLearned(uint itemId)
    {
        var ui = UIState.Instance();
        if (ui == null || itemId == 0) return false;
        var row = ExdModule.GetItemRowById(itemId);
        return row != null && ui->IsItemActionUnlocked(row) == 1;
    }
}
