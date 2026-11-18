using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Provenance.Core.Catalog;
using Provenance.Data;
using Provenance.UI.Theme;

namespace Provenance.UI.Pages;

/// <summary>Search recipes by name, crafter and crafting level, and add them to the order.</summary>
internal sealed class BrowsePage : Page
{
    private const float RowHeight = 54f;

    private string query = "";
    private List<BrowseEntry> results = new();
    private (string Query, int Mask, int Lo, int Hi, int Count) filterKey;
    private readonly Dictionary<uint, int> quantities = new();

    public BrowsePage(Plugin plugin) : base(plugin) { }

    public override string Title => "Browse";

    public override (string Text, Vector4 Color)? Status
        => (ImportPage.OrderSummary(Config.Order), Config.Order.IsEmpty ? Palette.TextFaint : Palette.Accent);

    public override IEnumerable<FooterAction> Actions
    {
        get { yield return new FooterAction("Open plan", Ui.Kind.Primary, FontAwesomeIcon.Sitemap, Go<PlanPage>, Enabled: !Config.Order.IsEmpty, Tooltip: Config.Order.IsEmpty ? "Add a recipe first" : null); }
    }

    public override void Draw()
    {
        var s = Ui.S;
        var catalog = Plans.Catalog;
        if (catalog == null)
        {
            Ui.Heading("Find recipes", Plans.LoadError is { } err ? "Game data couldn't be read: " + err : "Reading recipes from the game data…");
            return;
        }

        Ui.Heading("Find recipes");
        ImGui.Dummy(new Vector2(0, 2 * s));

        // Search
        var w = ImGui.GetContentRegionAvail().X;
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(38, 10) * s);
        ImGui.SetNextItemWidth(MathF.Min(w, 520 * s));
        ImGui.InputTextWithHint("##search", "Search recipes by name", ref query, 128);
        var searchMin = ImGui.GetItemRectMin();
        var searchH = ImGui.GetItemRectSize().Y;
        ImGui.PopStyleVar();
        using (Ui.F.Icon.Push())
        {
            var icon = FontAwesomeIcon.Search.ToIconString();
            ImGui.GetWindowDrawList().AddText(searchMin + new Vector2(14 * s, (searchH - ImGui.GetTextLineHeight()) / 2), Palette.U32(Palette.TextFaint), icon);
        }

        // Crafter chips
        var b = Config.Browse;
        if (Ui.ToggleChip("##job_all", "All crafters", Palette.Accent, b.JobMask == 0)) { b.JobMask = 0; Config.Save(); }
        foreach (var job in Jobs.All)
        {
            ImGui.SameLine(0, 6 * s);
            var bit = 1 << (int)job;
            var lvl = PlanService.JobLevel(job);
            if (Ui.ToggleChip("##job_" + job, job.Abbreviation(), Palette.JobColor(job), (b.JobMask & bit) != 0)) { b.JobMask ^= bit; Config.Save(); }
            Ui.Tooltip(lvl > 0 ? $"{job}, your level {lvl}" : job.ToString());
        }

        // Level range
        using (Ui.F.Strong.Push()) Ui.Text("Crafting level");
        ImGui.SameLine(0, 10 * s);
        Ui.Muted($"{b.MinLevel} to {b.MaxLevel}");
        int lo = b.MinLevel, hi = b.MaxLevel;
        if (Ui.RangeSlider("##levels", ref lo, ref hi, 1, 100, MathF.Min(w, 420 * s))) { b.MinLevel = lo; b.MaxLevel = hi; }
        if (ImGui.IsItemDeactivated()) Config.Save();

        Refilter(catalog);
        ImGui.Dummy(new Vector2(0, 2 * s));
        Ui.Muted(results.Count == 1 ? "1 recipe" : $"{results.Count:N0} recipes");

        if (results.Count == 0)
        {
            ImGui.Dummy(new Vector2(0, 8 * s));
            Ui.Wrapped("No recipe matches. Widen the level range or pick other crafters.", Palette.TextMuted);
            return;
        }

        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(results.Count, RowHeight * s);
        while (clipper.Step())
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                DrawRow(results[i], w, s);
        clipper.End();
        clipper.Destroy();
    }

    private void DrawRow(BrowseEntry e, float width, float s)
    {
        var h = RowHeight * s;
        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        var hovered = ImGui.IsMouseHoveringRect(pos, pos + new Vector2(width, h - 4 * s));
        if (hovered) dl.AddRectFilled(pos, pos + new Vector2(width, h - 4 * s), Palette.U32(Palette.Surface), Ui.InputRadius * s);

        Ui.DrawItemIcon(dl, e.Icon, pos + new Vector2(8 * s, (h - 4 * s - 36 * s) / 2), 36 * s);
        var tx = pos.X + 56 * s;
        using (Ui.F.Strong.Push())
            dl.AddText(new Vector2(tx, pos.Y + 7 * s), Palette.U32(Palette.Text), e.Yield > 1 ? $"{e.Name}  ×{e.Yield}" : e.Name);

        ImGui.SetCursorScreenPos(new Vector2(tx, pos.Y + 28 * s));
        Ui.Chip(e.Job.Abbreviation(), Palette.JobColor(e.Job));
        ImGui.SameLine(0, 6 * s);
        Ui.Chip("level " + e.Level, Palette.TextMuted);
        if (e.Stars > 0) { ImGui.SameLine(0, 6 * s); Ui.Chip(e.Stars == 1 ? "1 star" : $"{e.Stars} stars", Palette.Warn, FontAwesomeIcon.Star); }
        if (e.Expert) { ImGui.SameLine(0, 6 * s); Ui.Chip("expert", Palette.Danger); }
        if (e.MasterBook) { ImGui.SameLine(0, 6 * s); Ui.Chip("master book", Palette.TextMuted, FontAwesomeIcon.Book); }

        // Right side: amount + add
        var inOrder = Config.Order.Lines.Where(l => l.ItemId == e.ItemId).Sum(l => l.Quantity);
        var addW = Ui.ButtonWidth("Add", FontAwesomeIcon.Plus);
        var qtyW = 96 * s;
        var right = pos.X + width - 8 * s;
        var y = pos.Y + (h - 4 * s - 34 * s) / 2;
        if (inOrder > 0)
        {
            var label = $"{inOrder} in the order";
            float lw;
            using (Ui.F.Small.Push()) lw = ImGui.CalcTextSize(label).X;
            using (Ui.F.Small.Push())
                dl.AddText(new Vector2(right - addW - qtyW - 20 * s - lw, pos.Y + (h - 4 * s - ImGui.GetTextLineHeight()) / 2), Palette.U32(Palette.Accent), label);
        }
        ImGui.SetCursorScreenPos(new Vector2(right - addW - qtyW - 8 * s, y + 1 * s));
        var qty = quantities.GetValueOrDefault(e.RecipeId, 1);
        ImGui.SetNextItemWidth(qtyW);
        if (ImGui.InputInt("##qty" + e.RecipeId, ref qty, 1, 10)) quantities[e.RecipeId] = Math.Clamp(qty, 1, 9999);
        ImGui.SetCursorScreenPos(new Vector2(right - addW, y));
        if (Ui.Button("Add", Ui.Kind.Secondary, FontAwesomeIcon.Plus, id: "##add" + e.RecipeId))
        {
            Config.Order.Add(e.ItemId, quantities.GetValueOrDefault(e.RecipeId, 1), e.RecipeId);
            Changed();
        }

        ImGui.SetCursorScreenPos(pos);
        ImGui.Dummy(new Vector2(width, h));
    }

    private void Refilter(GameCatalog catalog)
    {
        var b = Config.Browse;
        var key = (query.Trim().ToLowerInvariant(), b.JobMask, b.MinLevel, b.MaxLevel, catalog.Browse.Count);
        if (key == filterKey) return;
        filterKey = key;
        var q = key.Item1;
        results = catalog.Browse
            .Where(e => (b.JobMask == 0 || (b.JobMask & (1 << (int)e.Job)) != 0)
                        && e.Level >= b.MinLevel && e.Level <= b.MaxLevel
                        && (q.Length == 0 || e.NameLower.Contains(q, StringComparison.Ordinal)))
            .ToList();
    }
}
