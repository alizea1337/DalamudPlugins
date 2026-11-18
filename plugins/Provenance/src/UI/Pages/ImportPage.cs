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
using Provenance.UI.Theme;

namespace Provenance.UI.Pages;

internal sealed class ImportPage : Page
{
    private static readonly string[] ModeLabels = { "Replace the order", "Add to the order" };

    private string input = "";
    private Task<ImportResult>? pending;
    private CancellationTokenSource? cancel;
    private ImportResult? last;

    public ImportPage(Plugin plugin) : base(plugin) { }

    public override string Title => "Import";

    public override (string Text, Vector4 Color)? Status => pending != null
        ? ("Importing from Teamcraft", Palette.Warn)
        : (OrderSummary(Config.Order), Config.Order.IsEmpty ? Palette.TextFaint : Palette.Accent);

    public override IEnumerable<FooterAction> Actions
    {
        get
        {
            if (last is { Ok: true }) yield return new FooterAction("Open plan", Ui.Kind.Secondary, FontAwesomeIcon.Sitemap, Go<PlanPage>);
            var ready = Plans.Catalog != null;
            yield return new FooterAction(
                pending != null ? "Importing" : "Import list", Ui.Kind.Primary, FontAwesomeIcon.FileImport, StartImport,
                Enabled: pending == null && ready && input.Trim().Length > 0,
                Tooltip: !ready ? "Still reading game data" : input.Trim().Length == 0 ? "Paste something from Teamcraft first" : null);
        }
    }

    internal static string OrderSummary(Order order)
    {
        var n = order.Lines.Count(l => l.Quantity > 0);
        return n == 0 ? "The order is empty" : n == 1 ? "1 item in the order" : $"{n} items in the order";
    }

    public override void Draw()
    {
        CollectResult();
        var s = Ui.S;
        var w = MathF.Min(ImGui.GetContentRegionAvail().X, 760 * s);

        Ui.Heading("Bring in a Teamcraft list", "Paste a list link, an import string, or the text Teamcraft gives you under Copy as text.");
        ImGui.Dummy(new Vector2(0, 4 * s));

        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(14, 12) * s);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Palette.Surface);
        ImGui.InputTextMultiline("##teamcraft", ref input, 400_000, new Vector2(w, 190 * s));
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
        if (input.Length == 0)
        {
            // Placeholder: multiline inputs have no hint text of their own.
            var min = ImGui.GetItemRectMin();
            ImGui.GetWindowDrawList().AddText(min + new Vector2(14, 12) * s, Palette.U32(Palette.TextFaint), "https://ffxivteamcraft.com/list/…");
        }

        DrawDetection(w);
        ImGui.Dummy(new Vector2(0, 2 * s));

        var ignore = Config.Import.IgnoreTeamcraftProgress;
        ImGui.PushItemWidth(w);
        if (Ui.Checkbox("Ignore Teamcraft progress", ref ignore, "Make the full amounts, even for items the list marks as done. Applies to list links.")) { Config.Import.IgnoreTeamcraftProgress = ignore; Changed(); }
        ImGui.PopItemWidth();

        var mode = Config.Import.Replace ? 0 : 1;
        if (Ui.Segmented("##importmode", ModeLabels, ref mode)) { Config.Import.Replace = mode == 0; Changed(); }

        ImGui.Dummy(new Vector2(0, 6 * s));
        if (last != null) DrawResult(w);
    }

    private void DrawDetection(float width)
    {
        var (kind, payload) = TeamcraftFormats.Detect(input);
        var (icon, color, text) = kind switch
        {
            InputKind.ListLink => (FontAwesomeIcon.Link, Palette.Accent, "Teamcraft list link. The list is downloaded when you import it."),
            InputKind.ImportString => TeamcraftFormats.TryParseImportString(payload, out var lines)
                ? (FontAwesomeIcon.Barcode, Palette.Accent, $"Teamcraft import string with {lines.Count} {(lines.Count == 1 ? "item" : "items")}.")
                : (FontAwesomeIcon.ExclamationTriangle, Palette.Warn, "This looks like an import link, but the string inside can't be read."),
            InputKind.Text => (FontAwesomeIcon.AlignLeft, Palette.Accent, $"Copied list with {TeamcraftFormats.ParseText(payload).Count} lines. Materials are dropped and only the final items are kept."),
            InputKind.Unknown => (FontAwesomeIcon.QuestionCircle, Palette.Warn, "This doesn't look like a Teamcraft link, import string or copied list."),
            _ => (FontAwesomeIcon.None, Palette.TextFaint, ""),
        };
        if (kind == InputKind.Empty) return;
        Ui.Icon(icon, color, small: true);
        ImGui.SameLine(0, 8 * Ui.S);
        Ui.Wrapped(text, Palette.TextMuted, width - 24 * Ui.S);
    }

    private void DrawResult(float width)
    {
        var r = last!;
        var s = Ui.S;
        var catalog = Plans.Catalog;
        Ui.BeginCard(Palette.WithAlpha(r.Ok ? Palette.Accent : Palette.Danger, 0.08f), width: width);
        if (!r.Ok)
        {
            Ui.Icon(FontAwesomeIcon.ExclamationCircle, Palette.Danger);
            ImGui.SameLine(0, 10 * s);
            using (Ui.F.Strong.Push()) Ui.Text("Nothing was imported", Palette.Danger);
            Ui.Wrapped(r.Error ?? "", Palette.TextMuted);
            Ui.EndCard();
            return;
        }

        Ui.Icon(FontAwesomeIcon.CheckCircle, Palette.Accent);
        ImGui.SameLine(0, 10 * s);
        using (Ui.F.Strong.Push())
            Ui.Text(r.ListName.Length > 0 ? $"Imported {r.Lines.Count} items from “{r.ListName}”" : $"Imported {r.Lines.Count} items");
        var notes = new List<string>();
        if (r.MaterialsSkipped > 0) notes.Add($"{r.MaterialsSkipped} pasted materials were left out because the final items already need them.");
        if (r.AlreadyDone > 0) notes.Add($"{r.AlreadyDone} items Teamcraft marks as done were skipped.");
        foreach (var n in notes) Ui.Wrapped(n, Palette.TextMuted);

        ImGui.Dummy(new Vector2(0, 2 * s));
        foreach (var line in r.Lines.Take(8))
        {
            Ui.ItemIcon(catalog?.ItemIcon(line.ItemId) ?? 0, 24);
            ImGui.SameLine(0, 10 * s);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 3 * s);
            Ui.Text(catalog?.ItemName(line.ItemId) ?? "#" + line.ItemId);
            ImGui.SameLine(0, 8 * s);
            Ui.Muted("×" + line.Quantity);
        }
        if (r.Lines.Count > 8) Ui.Muted($"and {r.Lines.Count - 8} more");

        if (r.Unmatched.Count > 0)
        {
            ImGui.Dummy(new Vector2(0, 4 * s));
            Ui.Icon(FontAwesomeIcon.ExclamationTriangle, Palette.Warn, small: true);
            ImGui.SameLine(0, 8 * s);
            Ui.Wrapped($"{r.Unmatched.Count} names didn't match an item in your game language, so they weren't imported:", Palette.Warn);
            Ui.Wrapped(string.Join(", ", r.Unmatched.Take(12)) + (r.Unmatched.Count > 12 ? ", …" : ""), Palette.TextMuted);
        }
        Ui.EndCard();
    }

    private void StartImport()
    {
        var catalog = Plans.Catalog;
        if (catalog == null || pending != null) return;
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
            : new ImportResult { Error = task.IsCanceled ? "Teamcraft took too long to answer. Try again." : "The import failed: " + task.Exception?.GetBaseException().Message };
        if (!last.Ok) return;

        if (Config.Import.Replace)
            Config.Order = new Order { Name = last.ListName.Length > 0 ? last.ListName : "Teamcraft list" };
        else if (Config.Order.IsEmpty && last.ListName.Length > 0)
            Config.Order.Name = last.ListName;
        foreach (var line in last.Lines) line.Hq = Config.Crafting.AimForHq;
        Config.Order.Merge(last.Lines);
        Changed();
        Services.Notifications.AddNotification(new Notification
        {
            Title = "Provenance",
            Content = $"Imported {last.Lines.Count} items into the order.",
            Type = NotificationType.Success,
        });
    }
}
