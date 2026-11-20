using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiNotification;
using Provenance.Core.Orders;
using Provenance.Core.Teamcraft;
using Provenance.UI.Pages;
using Provenance.UI.Theme;

namespace Provenance.UI;

/// <summary>
/// Bringing in a Teamcraft list, as a dialog over the Browse page: paste a list link, an import string or
/// copied text, pick how it joins the order, import, and see what came in. A floating panel in the same
/// style as the dropdowns (raised slate, soft shadow, a short fade in); the game dims the page behind it.
/// </summary>
internal sealed class ImportDialog
{
    private const string PopupId = "##importlist";
    private const double OpenSeconds = 0.16;
    private const float Slide = 10f;
    private static readonly string[] ModeLabels = { "Replace the order", "Add to the order" };

    private readonly Plugin plugin;
    private string input = "";
    private Task<ImportResult>? pending;
    private CancellationTokenSource? cancel;
    private ImportResult? last;
    private bool openRequested;
    private bool focusField;
    private double openedAt;

    public ImportDialog(Plugin plugin) => this.plugin = plugin;

    private Configuration Config => plugin.Config;

    /// <summary>Opens the dialog on the next frame, with a fresh form after a finished import.</summary>
    public void Open()
    {
        openRequested = true;
        if (last is { Ok: true })
        {
            last = null;
            input = "";
        }
    }

    /// <summary>
    /// Every frame from the Browse page, open or not: an import that finishes after the dialog was closed
    /// still lands in the order.
    /// </summary>
    public void Draw()
    {
        CollectResult();
        var s = Ui.S;
        if (openRequested)
        {
            openRequested = false;
            ImGui.OpenPopup(PopupId);
            openedAt = ImGui.GetTime();
            focusField = true;
        }

        // Centred over the page, a little narrower than the window; it fades in and settles upwards.
        var host = ImGui.GetWindowPos();
        var hostSize = ImGui.GetWindowSize();
        var width = MathF.Min(560 * s, hostSize.X - 48 * s);
        var t = (float)Math.Clamp((ImGui.GetTime() - openedAt) / OpenSeconds, 0, 1);
        var eased = 1f - MathF.Pow(1f - t, 3f);
        ImGui.SetNextWindowPos(host + new Vector2(hostSize.X / 2, hostSize.Y * 0.42f + (1f - eased) * Slide * s), ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(width, 0));

        // The fade and the row spacing hold for the whole dialog; the frame's own look is read when it begins.
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, MathF.Max(0.01f, eased));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 10) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(22, 20) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 14 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Palette.Raised);
        ImGui.PushStyleColor(ImGuiCol.Border, Palette.WithAlpha(Palette.Text, 0.08f));
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                                       | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.AlwaysAutoResize;
        var open = ImGui.BeginPopupModal(PopupId, flags);
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
        if (!open)
        {
            ImGui.PopStyleVar(2);
            return;
        }

        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetWindowPos();
        Ui.PanelShadow(dl, min, min + ImGui.GetWindowSize(), 14 * s);
        var inner = width - 44 * s;

        if (last is { Ok: true } done) DrawImported(done, inner);
        else DrawForm(inner);
        ImGui.EndPopup();
        ImGui.PopStyleVar(2);
    }

    // ------------------------------------------------------------------ the form

    private void DrawForm(float width)
    {
        var s = Ui.S;
        Header("Import a Teamcraft list", width);
        Ui.Wrapped("Paste a list link, an import string, or the text Teamcraft gives you under Copy as text.", Palette.TextMuted, width);

        if (focusField)
        {
            ImGui.SetKeyboardFocusHere();
            focusField = false;
        }
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(14, 12) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 10 * s);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Palette.Surface);
        ImGui.InputTextMultiline("##teamcraft", ref input, 400_000, new Vector2(width, 150 * s));
        // Escape inside the field only lets go of it (this frame); a second Escape closes the dialog.
        var typing = ImGui.IsItemActive() || ImGui.IsItemDeactivated();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
        if (input.Length == 0)
        {
            // Multiline fields have no hint text of their own.
            ImGui.GetWindowDrawList().AddText(ImGui.GetItemRectMin() + new Vector2(14, 12) * s, Palette.U32(Palette.TextFaint), "https://ffxivteamcraft.com/list/…");
        }
        DrawDetection(width);

        var ignore = Config.Import.IgnoreTeamcraftProgress;
        if (Ui.Checkbox("Ignore Teamcraft progress", ref ignore, "Make the full amounts, even for items the list marks as done. Applies to list links."))
        {
            Config.Import.IgnoreTeamcraftProgress = ignore;
            plugin.Changed();
        }
        var mode = Config.Import.Replace ? 0 : 1;
        if (Ui.Segmented("##importmode", ModeLabels, ref mode)) { Config.Import.Replace = mode == 0; plugin.Changed(); }

        if (last is { Ok: false } failed) DrawFailure(failed, width);

        // Footer: Cancel on the left of the primary action, both on the right edge.
        ImGui.Dummy(new Vector2(0, 2 * s));
        var ready = plugin.Plans.Catalog != null;
        var canImport = pending == null && ready && input.Trim().Length > 0;
        var importLabel = pending != null ? "Importing" : "Import";
        var cancelW = Ui.ButtonWidth("Cancel");
        var importW = Ui.ButtonWidth(importLabel, FontAwesomeIcon.FileImport);
        var row = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(new Vector2(row.X + width - importW - cancelW - 10 * s, row.Y));
        if (Ui.Button("Cancel", Ui.Kind.Ghost, tooltip: pending != null ? "Stop the download and keep the order as it is." : null, id: "##importcancel"))
        {
            if (pending != null) cancel?.Cancel();
            else ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine(0, 10 * s);
        var tip = !ready ? "Still reading game data" : input.Trim().Length == 0 ? "Paste something from Teamcraft first" : "Ctrl+Enter";
        if (Ui.Button(importLabel, Ui.Kind.Primary, FontAwesomeIcon.FileImport, enabled: canImport, tooltip: tip, id: "##importgo")) StartImport();

        // Keyboard: Ctrl+Enter imports, Escape closes once the field has let go of it.
        if (canImport && ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Enter)) StartImport();
        if (!typing && pending == null && ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();
    }

    /// <summary>The dialog's title with a round close button on the right.</summary>
    private static void Header(string title, float width)
    {
        var s = Ui.S;
        var start = ImGui.GetCursorScreenPos();
        Ui.Icon(FontAwesomeIcon.FileImport, Palette.Accent);
        ImGui.SameLine(0, 10 * s);
        float h;
        using (Ui.F.Strong.Push())
        {
            Ui.Text(title);
            h = ImGui.GetTextLineHeight();
        }
        var after = ImGui.GetCursorScreenPos();
        const float d = 28;
        ImGui.SetCursorScreenPos(new Vector2(start.X + width - d * s, start.Y + (h - d * s) / 2));
        if (Ui.IconButton("##importclose", FontAwesomeIcon.Times, "Close", Ui.Kind.Ghost, d)) ImGui.CloseCurrentPopup();
        ImGui.SetCursorScreenPos(after);
    }

    private void DrawDetection(float width)
    {
        var (kind, payload) = TeamcraftFormats.Detect(input);
        if (kind == InputKind.Empty) return;
        var (icon, color, text) = kind switch
        {
            InputKind.ListLink => (FontAwesomeIcon.Link, Palette.Accent, "Teamcraft list link. The list is downloaded when you import it."),
            InputKind.ImportString => TeamcraftFormats.TryParseImportString(payload, out var lines)
                ? (FontAwesomeIcon.Barcode, Palette.Accent, $"Teamcraft import string with {lines.Count} {(lines.Count == 1 ? "item" : "items")}.")
                : (FontAwesomeIcon.ExclamationTriangle, Palette.Warn, "This looks like an import link, but the string inside can't be read."),
            InputKind.Text => (FontAwesomeIcon.AlignLeft, Palette.Accent, $"Copied list with {TeamcraftFormats.ParseText(payload).Count} lines. Materials are dropped and only the final items are kept."),
            _ => (FontAwesomeIcon.QuestionCircle, Palette.Warn, "This doesn't look like a Teamcraft link, import string or copied list."),
        };
        Ui.Icon(icon, color, small: true);
        ImGui.SameLine(0, 8 * Ui.S);
        Ui.Wrapped(text, Palette.TextMuted, width - 24 * Ui.S);
    }

    private static void DrawFailure(ImportResult r, float width)
    {
        var s = Ui.S;
        Ui.BeginCard(Palette.WithAlpha(Palette.Danger, 0.08f), width: width);
        Ui.Icon(FontAwesomeIcon.ExclamationCircle, Palette.Danger);
        ImGui.SameLine(0, 10 * s);
        using (Ui.F.Strong.Push()) Ui.Text("Nothing was imported", Palette.Danger);
        Ui.Wrapped(r.Error ?? "", Palette.TextMuted);
        Ui.EndCard();
    }

    // ------------------------------------------------------------------ after an import

    private void DrawImported(ImportResult r, float width)
    {
        var s = Ui.S;
        var catalog = plugin.Plans.Catalog;
        Header(r.ListName.Length > 0 ? $"Imported {r.Lines.Count} items from “{r.ListName}”" : $"Imported {r.Lines.Count} items", width);
        var notes = new List<string>();
        if (r.MaterialsSkipped > 0) notes.Add($"{r.MaterialsSkipped} pasted materials were left out because the final items already need them.");
        if (r.AlreadyDone > 0) notes.Add($"{r.AlreadyDone} items Teamcraft marks as done were skipped.");
        foreach (var n in notes) Ui.Wrapped(n, Palette.TextMuted, width);

        foreach (var line in r.Lines.Take(6))
        {
            Ui.ItemIcon(catalog?.ItemIcon(line.ItemId) ?? 0, 24);
            ImGui.SameLine(0, 10 * s);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 3 * s);
            Ui.Text(catalog?.ItemName(line.ItemId) ?? "#" + line.ItemId);
            ImGui.SameLine(0, 8 * s);
            Ui.Muted("×" + line.Quantity);
        }
        if (r.Lines.Count > 6) Ui.Muted($"and {r.Lines.Count - 6} more");

        if (r.Unmatched.Count > 0)
        {
            Ui.Icon(FontAwesomeIcon.ExclamationTriangle, Palette.Warn, small: true);
            ImGui.SameLine(0, 8 * s);
            Ui.Wrapped($"{r.Unmatched.Count} names didn't match an item in your game language, so they weren't imported: " +
                       string.Join(", ", r.Unmatched.Take(12)) + (r.Unmatched.Count > 12 ? ", …" : "."), Palette.Warn, width - 24 * s);
        }

        ImGui.Dummy(new Vector2(0, 2 * s));
        var againW = Ui.ButtonWidth("Import another");
        var planW = Ui.ButtonWidth("Open plan", FontAwesomeIcon.Sitemap);
        var row = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(new Vector2(row.X + width - planW - againW - 10 * s, row.Y));
        if (Ui.Button("Import another", Ui.Kind.Ghost, id: "##importagain"))
        {
            last = null;
            input = "";
            focusField = true;
        }
        ImGui.SameLine(0, 10 * s);
        if (Ui.Button("Open plan", Ui.Kind.Primary, FontAwesomeIcon.Sitemap, id: "##importplan"))
        {
            ImGui.CloseCurrentPopup();
            plugin.Main.Select<PlanPage>();
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();
    }

    // ------------------------------------------------------------------ importing

    private void StartImport()
    {
        var catalog = plugin.Plans.Catalog;
        if (catalog == null || pending != null || input.Trim().Length == 0) return;
        cancel?.Dispose();
        cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        last = null;
        pending = TeamcraftImporter.ImportAsync(input, Config.Import.IgnoreTeamcraftProgress, catalog, Data.PlanService.FetchAsync, cancel.Token);
    }

    /// <summary>Applies a finished import on the UI thread.</summary>
    private void CollectResult()
    {
        if (pending is not { IsCompleted: true } task) return;
        pending = null;
        last = task.IsCompletedSuccessfully
            ? task.Result
            : new ImportResult { Error = task.IsCanceled ? "The import was stopped, or Teamcraft took too long to answer. Try again." : "The import failed: " + task.Exception?.GetBaseException().Message };
        if (!last.Ok) return;

        if (Config.Import.Replace)
            Config.Order = new Order { Name = last.ListName.Length > 0 ? last.ListName : "Teamcraft list" };
        else if (Config.Order.IsEmpty && last.ListName.Length > 0)
            Config.Order.Name = last.ListName;
        foreach (var line in last.Lines) line.Hq = Config.Crafting.AimForHq;
        Config.Order.Merge(last.Lines);
        plugin.Changed();
        Services.Notifications.AddNotification(new Notification
        {
            Title = "Provenance",
            Content = $"Imported {last.Lines.Count} items into the order.",
            Type = NotificationType.Success,
        });
    }

    /// <summary>"3 items in the order", for the action bar.</summary>
    internal static string OrderSummary(Order order)
    {
        var n = order.Lines.Count(l => l.Quantity > 0);
        return n == 0 ? "The order is empty" : n == 1 ? "1 item in the order" : $"{n} items in the order";
    }
}
