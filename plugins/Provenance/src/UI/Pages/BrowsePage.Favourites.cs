using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Provenance.Core.Orders;
using Provenance.Data;
using Provenance.UI.Theme;

namespace Provenance.UI.Pages;

/// <summary>
/// Favourite orders on the Browse page, shown instead of the items while the Favourites chip is on: orders saved to
/// use again. Each can be loaded (replacing the order), added to the order, copied as Teamcraft text, renamed in
/// place, overwritten with the current order, or deleted. Replacing and deleting ask once (a second click within a
/// few seconds), like clearing the order on the Plan page.
/// </summary>
internal sealed partial class BrowsePage
{
    private const float FavouriteHeight = 76f;
    private const float CoverSize = 50f;

    /// <summary>A favourite's action waiting for its second click: which favourite, which action, until when.</summary>
    private (string Id, string Action, DateTime Until)? armed;
    /// <summary>The favourite just copied as text (its button shows a check for a moment).</summary>
    private (string Id, DateTime Until)? copied;
    /// <summary>A favourite whose name takes the keyboard next frame (just saved: name it straight away).</summary>
    private string? nameFocus;

    private bool IsArmed(FavouriteOrder f, string action) => armed is { } a && a.Id == f.Id && a.Action == action && DateTime.UtcNow < a.Until;

    private void Arm(FavouriteOrder f, string action) => armed = (f.Id, action, DateTime.UtcNow.AddSeconds(3));

    private void DrawFavourites(GameCatalog catalog, float w)
    {
        var s = Ui.S;
        var favourites = Config.Favourites;
        // Plain words only (filters narrow the items): every word in the name or in one of the items.
        var words = parsed.Words;
        var shown = words.Count == 0 ? favourites
            : favourites.Where(f => words.All(word => f.Name.Contains(word, StringComparison.OrdinalIgnoreCase)
                                                      || f.Lines.Any(l => catalog.ItemName(l.ItemId).Contains(word, StringComparison.OrdinalIgnoreCase)))).ToList();

        // How many on the left; saving the order as it is now on the right.
        var start = ImGui.GetCursorScreenPos();
        var bh = 34 * s;
        var count = favourites.Count == 1 ? "1 favourite order" : $"{favourites.Count} favourite orders";
        if (words.Count > 0 && favourites.Count > 0) count = $"{shown.Count} of {count}";
        if (favourites.Count > 0)
            ImGui.GetWindowDrawList().AddText(start + new Vector2(0, (bh - ImGui.GetTextLineHeight()) / 2), Palette.U32(Palette.TextMuted), count);
        const string save = "Save current order";
        var saveW = Ui.ButtonWidth(save, FontAwesomeIcon.Star);
        ImGui.SetCursorScreenPos(new Vector2(start.X + w - saveW, start.Y));
        var empty = Config.Order.IsEmpty;
        if (Ui.Button(save, favourites.Count == 0 ? Ui.Kind.Primary : Ui.Kind.Secondary, FontAwesomeIcon.Star, enabled: !empty,
                tooltip: empty ? "The order is empty: add items or import a list first." : $"Keeps \"{Config.Order.Name}\" as it is now, to load again later.", id: "##savefavourite"))
            SaveCurrentOrder();
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + bh + 10 * s));

        if (favourites.Count == 0)
        {
            Ui.BeginCard(width: w);
            Ui.Icon(FontAwesomeIcon.Star, Palette.Accent);
            ImGui.SameLine(0, 10 * s);
            using (Ui.F.Strong.Push()) Ui.Text("No favourite orders yet");
            Ui.Wrapped("Put an order together, here or with Import list, and save it. It comes back with one click whenever you want to make it again, "
                       + "and copies out as Teamcraft text.");
            Ui.EndCard();
            return;
        }
        if (shown.Count == 0)
        {
            Ui.Wrapped($"No favourite order is called that or has an item called that. Clear the search to see all {favourites.Count}.", Palette.TextMuted);
            return;
        }
        foreach (var f in shown.ToList()) DrawFavourite(f, catalog, w);
    }

    /// <summary>Keeps the order as it is now as a new favourite at the top, and puts the cursor in its name.</summary>
    private void SaveCurrentOrder()
    {
        var name = Favourites.UniqueName(Config.Order.Name, Config.Favourites.Select(f => f.Name));
        var f = FavouriteOrder.From(Config.Order, name, DateTime.UtcNow);
        Config.Favourites.Insert(0, f);
        Config.Save();
        nameFocus = f.Id;
    }

    private void DrawFavourite(FavouriteOrder f, GameCatalog catalog, float w)
    {
        var s = Ui.S;
        var h = FavouriteHeight * s;
        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        var hovered = ImGui.IsMouseHoveringRect(pos, pos + new Vector2(w, h));
        // The card; a faint edge while the pointer is on it (the buttons keep their own colour).
        dl.AddRectFilled(pos, pos + new Vector2(w, h), Palette.U32(Palette.Surface), Ui.CardRadius * s);
        if (hovered) dl.AddRect(pos, pos + new Vector2(w, h), Palette.U32(Palette.WithAlpha(Palette.Text, 0.08f)), Ui.CardRadius * s, ImDrawFlags.None, 1 * s);

        // The cover: the first items' icons, two by two, like a playlist's.
        var cover = CoverSize * s;
        var coverPos = pos + new Vector2(14 * s, (h - cover) / 2);
        DrawCover(dl, f, catalog, coverPos, cover);

        // The actions, laid out from the right edge.
        var onOrder = Favourites.SameLines(f.Lines, Config.Order.Lines);
        var replaceAsk = IsArmed(f, "load");
        var updateAsk = IsArmed(f, "update");
        var deleteAsk = IsArmed(f, "delete");
        var loadLabel = onOrder ? "On the order" : replaceAsk ? "Replace the order?" : "Load";
        var loadW = Ui.ButtonWidth(loadLabel, onOrder ? FontAwesomeIcon.Check : FontAwesomeIcon.FolderOpen);
        var addW = Ui.ButtonWidth("Add", FontAwesomeIcon.Plus);
        var icon = 30 * s;
        var gap = 8 * s;
        var updateW = updateAsk ? Ui.ButtonWidth("Overwrite?") : icon;
        var deleteW = deleteAsk ? Ui.ButtonWidth("Delete?") : icon;
        var right = pos.X + w - 14 * s;
        var by = pos.Y + (h - 34 * s) / 2;
        var iy = pos.Y + (h - icon) / 2;
        var x = right - deleteW;
        var deleteX = x;
        x -= gap + updateW;
        var updateX = x;
        x -= gap + icon;
        var copyX = x;
        x -= 14 * s + addW;
        var addX = x;
        x -= gap + loadW;
        var loadX = x;

        // Name, renamed in place: it reads as a title until it's clicked.
        var tx = coverPos.X + cover + 14 * s;
        var nameW = MathF.Max(80 * s, loadX - 16 * s - tx);
        var name = f.Name;
        ImGui.SetCursorScreenPos(new Vector2(tx - 4 * s, pos.Y + 13 * s));
        using (Ui.F.Strong.Push())
        {
            ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4, 3) * s);
            if (nameFocus == f.Id)
            {
                ImGui.SetKeyboardFocusHere();
                nameFocus = null;
            }
            ImGui.SetNextItemWidth(nameW);
            // Clicking the name selects all of it: typing renames.
            if (ImGui.InputText("##favname" + f.Id, ref name, 80, ImGuiInputTextFlags.AutoSelectAll)) f.Name = name;
            if (ImGui.IsItemDeactivated())
            {
                if (f.Name.Trim().Length == 0) f.Name = Favourites.UniqueName("", Config.Favourites.Where(o => o != f).Select(o => o.Name));
                Config.Save();
            }
            ImGui.PopStyleVar();
            ImGui.PopStyleColor();
        }
        Ui.Tooltip("Rename it");

        // What's on it, and when it was saved; every item on hover.
        var items = f.Lines.Select(l => l.ItemId).Distinct().Count();
        var total = f.Lines.Sum(l => l.Quantity);
        var summary = $"{items} {(items == 1 ? "item" : "items")}, {total:N0} in all, saved {Saved(f.SavedUtc)}";
        using (Ui.F.Small.Push())
        {
            var sy = pos.Y + 44 * s;
            var shown = summary;
            while (shown.Length > 4 && ImGui.CalcTextSize(shown).X > nameW) shown = shown[..^2].TrimEnd() + "…";
            dl.AddText(new Vector2(tx, sy), Palette.U32(onOrder ? Palette.Accent : Palette.TextMuted), onOrder ? "On the order now. " + shown : shown);
        }
        if (ImGui.IsMouseHoveringRect(pos, new Vector2(loadX - 8 * s, pos.Y + h)) && !ImGui.IsItemHovered())
            Ui.ShowTooltip(string.Join("\n", f.Lines.Where(l => l.Quantity > 0).Select(l =>
                $"{l.Quantity:N0}× {catalog.ItemName(l.ItemId)}{(l.Hq && catalog.RecipesFor(l.ItemId).Count > 0 ? " (HQ)" : "")}")));

        // Load: replaces the order (asks first when that would throw away a different one).
        ImGui.SetCursorScreenPos(new Vector2(loadX, by));
        if (onOrder) Ui.Button(loadLabel, Ui.Kind.Ghost, FontAwesomeIcon.Check, enabled: false, id: "##favload" + f.Id);
        else if (Ui.Button(loadLabel, replaceAsk ? Ui.Kind.Danger : Ui.Kind.Secondary, FontAwesomeIcon.FolderOpen,
                     tooltip: replaceAsk ? null : Config.Order.IsEmpty ? "Makes this the order." : $"Makes this the order instead of \"{Config.Order.Name}\".", id: "##favload" + f.Id))
        {
            if (replaceAsk || Config.Order.IsEmpty)
            {
                Config.Order = f.ToOrder();
                armed = null;
                Changed();
            }
            else Arm(f, "load");
        }

        // Add: its items join the order as it is.
        ImGui.SetCursorScreenPos(new Vector2(addX, by));
        if (Ui.Button("Add", Ui.Kind.Ghost, FontAwesomeIcon.Plus, tooltip: "Adds its items to the order.", id: "##favadd" + f.Id))
        {
            if (Config.Order.IsEmpty) Config.Order.Name = f.Name;
            Config.Order.Merge(Favourites.CopyLines(f.Lines));
            Changed();
        }

        // Copy as Teamcraft text: one "10x Item" per line.
        ImGui.SetCursorScreenPos(new Vector2(copyX, iy));
        var justCopied = copied is { } c && c.Id == f.Id && DateTime.UtcNow < c.Until;
        if (Ui.IconButton("##favcopy" + f.Id, justCopied ? FontAwesomeIcon.Check : FontAwesomeIcon.Copy,
                justCopied ? "Copied" : "Copy as Teamcraft text (one \"10x Item\" per line)"))
        {
            ImGui.SetClipboardText(Favourites.ToTeamcraftText(f.Lines, catalog.ItemName));
            copied = (f.Id, DateTime.UtcNow.AddSeconds(2));
        }

        // Overwrite with the order as it is now (asks first).
        ImGui.SetCursorScreenPos(new Vector2(updateX, updateAsk ? by : iy));
        var canUpdate = !Config.Order.IsEmpty && !onOrder;
        if (updateAsk)
        {
            if (Ui.Button("Overwrite?", Ui.Kind.Danger, id: "##favupdate" + f.Id))
            {
                f.Lines = Favourites.CopyLines(Config.Order.Lines);
                f.SavedUtc = DateTime.UtcNow;
                armed = null;
                Config.Save();
            }
        }
        else if (Ui.IconButton("##favupdate" + f.Id, FontAwesomeIcon.Sync,
                     canUpdate ? "Overwrite it with the current order" : onOrder ? "It's the same as the order now" : "The order is empty", enabled: canUpdate))
            Arm(f, "update");

        // Delete (asks first).
        ImGui.SetCursorScreenPos(new Vector2(deleteX, deleteAsk ? by : iy));
        if (deleteAsk)
        {
            if (Ui.Button("Delete?", Ui.Kind.Danger, id: "##favdelete" + f.Id))
            {
                Config.Favourites.Remove(f);
                armed = null;
                Config.Save();
            }
        }
        else if (Ui.IconButton("##favdelete" + f.Id, FontAwesomeIcon.TrashAlt, "Delete this favourite")) Arm(f, "delete");

        ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + h + 8 * s));
        ImGui.Dummy(new Vector2(w, 0));
    }

    /// <summary>Up to four item icons in a square: one fills it, more share it two by two.</summary>
    private static void DrawCover(ImDrawListPtr dl, FavouriteOrder f, GameCatalog catalog, Vector2 at, float size)
    {
        var s = Ui.S;
        var ids = f.Lines.Where(l => l.Quantity > 0).Select(l => l.ItemId).Distinct().Take(4).ToList();
        dl.AddRectFilled(at, at + new Vector2(size), Palette.U32(Palette.Window), Ui.IconRadius * s);
        if (ids.Count == 0) return;
        if (ids.Count == 1)
        {
            Ui.DrawItemIcon(dl, catalog.ItemIcon(ids[0]), at, size);
            return;
        }
        var gap = 2 * s;
        var cell = (size - gap) / 2;
        for (var i = 0; i < ids.Count; i++)
            Ui.DrawItemIcon(dl, catalog.ItemIcon(ids[i]), at + new Vector2(i % 2 * (cell + gap), i / 2 * (cell + gap)), cell);
    }

    /// <summary>"today", "yesterday", "20 Nov", or "20 Nov 2025" for an older year.</summary>
    private static string Saved(DateTime utc)
    {
        var local = utc.ToLocalTime().Date;
        var today = DateTime.Now.Date;
        if (local == today) return "today";
        if (local == today.AddDays(-1)) return "yesterday";
        return local.Year == today.Year ? local.ToString("d MMM") : local.ToString("d MMM yyyy");
    }
}
