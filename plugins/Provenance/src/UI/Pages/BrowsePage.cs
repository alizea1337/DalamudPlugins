using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Provenance.Core.Catalog;
using Provenance.Core.Character;
using Provenance.Data;
using Provenance.UI.Theme;

namespace Provenance.UI.Pages;

/// <summary>
/// Search recipes and gatherables by name, job (crafters and gatherers) and job level, and add them
/// to the order. A gathered item in the order is simply gathered; a recipe is crafted. The Favourites
/// chip shows saved orders instead (BrowsePage.Favourites.cs).
/// </summary>
internal sealed partial class BrowsePage : Page
{
    private const float RowHeight = 54f;

    private string query = "";
    /// <summary>The clear button was used: the search field takes the keyboard again next frame.</summary>
    private bool refocusSearch;
    private List<BrowseEntry> results = new();
    /// <summary>Jobs left out by the job filter that would have items with the other filters (bit per job, like JobMask).</summary>
    private int otherJobs;
    private (string Query, int Jobs, int Places, int Lo, int Hi, int Count) filterKey;
    private readonly Dictionary<string, int> quantities = new();
    private readonly Dictionary<string, bool> hqChoice = new();
    private readonly ImportDialog import;

    public BrowsePage(Plugin plugin) : base(plugin) => import = new ImportDialog(plugin);

    /// <summary>Opens the Teamcraft import over this page.</summary>
    public void OpenImport() => import.Open();

    public override bool DrawOverlay(Vector2 windowPos, Vector2 windowSize, Vector2 pageMin, Vector2 pageSize)
        => import.Draw(windowPos, windowSize, pageMin, pageSize);

    public override string Title => "Browse";
    public override FontAwesomeIcon Icon => FontAwesomeIcon.Search;

    public override (string Text, Vector4 Color)? Status
        => (ImportDialog.OrderSummary(Config.Order), Config.Order.IsEmpty ? Palette.TextFaint : Palette.Accent);

    public override IEnumerable<FooterAction> Actions
    {
        get { yield return new FooterAction("Open plan", Ui.Kind.Primary, FontAwesomeIcon.Sitemap, Go<PlanPage>, Enabled: !Config.Order.IsEmpty, Tooltip: Config.Order.IsEmpty ? "Add something first" : null); }
    }

    public override void Draw()
    {
        var s = Ui.S;
        var catalog = Plans.Catalog;
        if (catalog == null)
        {
            Ui.Heading("Find items", Plans.LoadError is { } err ? "Game data couldn't be read: " + err : "Reading recipes and gathering data from the game…");
            return;
        }

        Ui.Heading("Find items");
        ImGui.Dummy(new Vector2(0, 2 * s));

        // Search. The padding keeps room on both sides: the magnifier left, the clear button right.
        var w = ImGui.GetContentRegionAvail().X;
        var searchW = MathF.Min(w, 520 * s);
        if (refocusSearch)
        {
            ImGui.SetKeyboardFocusHere();
            refocusSearch = false;
        }
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(38, 10) * s);
        ImGui.SetNextItemWidth(searchW);
        ImGui.InputTextWithHint("##search", Config.Browse.ShowFavourites ? "Search favourite orders by name or item" : "Search recipes and gatherables by name", ref query, 128);
        // Escape while typing empties the search (the field itself would only undo the last edit).
        if (ImGui.IsItemDeactivated() && ImGui.IsKeyPressed(ImGuiKey.Escape)) query = "";
        // The clear button sits on top of the field's right end.
        ImGui.SetItemAllowOverlap();
        var searchMin = ImGui.GetItemRectMin();
        var searchH = ImGui.GetItemRectSize().Y;
        ImGui.PopStyleVar();
        var searchDl = ImGui.GetWindowDrawList();
        using (Ui.F.Icon.Push())
        {
            var icon = FontAwesomeIcon.Search.ToIconString();
            searchDl.AddText(searchMin + new Vector2(14 * s, (searchH - ImGui.GetTextLineHeight()) / 2), Palette.U32(Palette.TextFaint), icon);
        }
        if (query.Length > 0)
        {
            // One click empties the search and puts the cursor back in it.
            var d = 24 * s;
            var at = new Vector2(searchMin.X + searchW - 8 * s - d, searchMin.Y + (searchH - d) / 2);
            var after = ImGui.GetCursorScreenPos();
            ImGui.SetCursorScreenPos(at);
            if (ImGui.InvisibleButton("##clearsearch", new Vector2(d)))
            {
                query = "";
                refocusSearch = true;
            }
            var hovered = ImGui.IsItemHovered();
            Ui.Tooltip("Clear the search");
            if (hovered) searchDl.AddCircleFilled(at + new Vector2(d / 2), d / 2, Palette.U32(Palette.RaisedHover));
            using (Ui.F.IconSmall.Push())
            {
                var glyph = FontAwesomeIcon.Times.ToIconString();
                var gs = ImGui.CalcTextSize(glyph);
                searchDl.AddText(at + (new Vector2(d) - gs) / 2, Palette.U32(hovered ? Palette.Text : Palette.TextMuted), glyph);
            }
            ImGui.SetCursorScreenPos(after);
        }

        // Bringing in a whole Teamcraft list instead: a quieter button at the right end of the search row,
        // centred on the field. It opens the import over the page.
        {
            var after = ImGui.GetCursorScreenPos();
            const string label = "Import list";
            var bw = Ui.ButtonWidth(label, FontAwesomeIcon.FileImport);
            var rowRight = searchMin.X + w;
            if (rowRight - bw >= searchMin.X + searchW + 12 * s)
            {
                ImGui.SetCursorScreenPos(new Vector2(rowRight - bw, searchMin.Y + (searchH - 34 * s) / 2));
                if (Ui.Button(label, Ui.Kind.Secondary, FontAwesomeIcon.FileImport, tooltip: "Bring in a Teamcraft list: a link, an import string or copied text.", id: "##openimport"))
                    import.Open();
                ImGui.SetCursorScreenPos(after);
            }
            else if (Ui.Button(label, Ui.Kind.Secondary, FontAwesomeIcon.FileImport, tooltip: "Bring in a Teamcraft list: a link, an import string or copied text.", id: "##openimport"))
                import.Open();
        }

        // Job chips: all, the eight crafters, then the three gatherers. They wrap when the window is narrow. With the
        // favourites showing they don't apply: faded, and left as they are for the items.
        var b = Config.Browse;
        var jobsRight = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        ImGui.BeginDisabled(b.ShowFavourites);
        if (Ui.ToggleChip("##job_all", "All jobs", Palette.Accent, b.JobMask == 0)) { b.JobMask = 0; Config.Save(); }
        foreach (var job in ClassJobs.Crafters.Concat(ClassJobs.Gatherers))
        {
            var gap = (job == ClassJobs.Gatherers[0] ? 18 : 6) * s;
            if (ImGui.GetItemRectMax().X + gap + Ui.ToggleChipWidth(ClassJobs.Abbreviation(job), job) <= jobsRight) ImGui.SameLine(0, gap);
            JobChip(job, b);
        }
        ImGui.EndDisabled();

        // Content chips: all, the open world a run goes to, then the special content it can't, and last the
        // favourite orders. They wrap when the window is narrow. Picking content leaves the favourites.
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        if (Ui.ToggleChip("##place_all", "All content", Palette.Accent, b.PlaceMask == 0 && !b.ShowFavourites)) { b.PlaceMask = 0; b.ShowFavourites = false; Config.Save(); }
        foreach (var place in PlaceOrder)
        {
            var label = PlaceLabel(place);
            var gap = (place == Place.Diadem ? 18 : 6) * s;
            if (ImGui.GetItemRectMax().X + gap + Ui.ToggleChipWidth(label) <= right) ImGui.SameLine(0, gap);
            var bit = (int)place;
            if (Ui.ToggleChip("##place_" + bit, label, PlaceColor(place), (b.PlaceMask & bit) != 0 && !b.ShowFavourites))
            {
                if (b.ShowFavourites) { b.ShowFavourites = false; b.PlaceMask |= bit; }
                else b.PlaceMask ^= bit;
                Config.Save();
            }
            Ui.Tooltip(PlaceHint(place));
        }
        {
            var label = Config.Favourites.Count > 0 ? $"Favourites  {Config.Favourites.Count}" : "Favourites";
            var gap = 18 * s;
            if (ImGui.GetItemRectMax().X + gap + Ui.ToggleChipWidth(label) <= right) ImGui.SameLine(0, gap);
            if (Ui.ToggleChip("##favourites", label, Palette.Accent, b.ShowFavourites)) { b.ShowFavourites = !b.ShowFavourites; armed = null; Config.Save(); }
            Ui.Tooltip("Orders you saved, to load again with one click, rename, or copy out as Teamcraft text.");
        }

        if (b.ShowFavourites)
        {
            ImGui.Dummy(new Vector2(0, 4 * s));
            DrawFavourites(catalog, w);
            return;
        }

        // Level range
        using (Ui.F.Strong.Push()) Ui.Text("Job level");
        ImGui.SameLine(0, 10 * s);
        Ui.Muted($"{b.MinLevel} to {b.MaxLevel}");
        int lo = b.MinLevel, hi = b.MaxLevel;
        if (Ui.RangeSlider("##levels", ref lo, ref hi, 1, 100, MathF.Min(w, 420 * s))) { b.MinLevel = lo; b.MaxLevel = hi; }
        if (ImGui.IsItemDeactivated()) Config.Save();

        Refilter(catalog);
        ImGui.Dummy(new Vector2(0, 2 * s));
        Ui.Muted(results.Count == 1 ? "1 item" : $"{results.Count:N0} items");

        if (results.Count == 0)
        {
            ImGui.Dummy(new Vector2(0, 8 * s));
            if (otherJobs != 0)
            {
                // The picked jobs have nothing here but others do, like Ocean Fishing with MIN and BTN.
                Ui.Wrapped($"Nothing for {JobList(b.JobMask)} here. {JobList(otherJobs)} {(JobCount(otherJobs) == 1 ? "has" : "have")} items.", Palette.TextMuted);
                ImGui.Dummy(new Vector2(0, 4 * s));
                if (Ui.Button("Show all jobs", Ui.Kind.Secondary, id: "##show_all_jobs")) { b.JobMask = 0; Config.Save(); }
            }
            else Ui.Wrapped("Nothing matches. Widen the level range, try another name, or pick other content.", Palette.TextMuted);
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

    /// <summary>The browser's content, in chip order. Quest nodes and content that's gone aren't listed at all.</summary>
    private static readonly Place[] PlaceOrder = { Place.OpenWorld, Place.Diadem, Place.CosmicExploration, Place.OceanFishing };

    /// <summary>The same words on the filter chips and on the rows' hints.</summary>
    private static string PlaceLabel(Place place) => place switch
    {
        Place.OpenWorld => "Open world",
        Place.Diadem => "The Diadem",
        Place.CosmicExploration => "Cosmic Exploration",
        _ => "Ocean Fishing",
    };

    /// <summary>Amber marks content a run can't go to, on the filter and on the rows.</summary>
    private static Vector4 PlaceColor(Place place) => place == Place.OpenWorld ? Palette.Accent : Palette.Warn;

    private static string PlaceHint(Place place) => place switch
    {
        Place.OpenWorld => "What a run can get: gathering and fishing in field zones, cities and housing districts, and regular recipes.",
        Place.Diadem => "Only gathered or fished in the Diadem.",
        Place.CosmicExploration => "Only gathered, fished or crafted in Cosmic Exploration.",
        _ => "Only fished on Ocean Fishing voyages (Fisher).",
    };

    private void JobChip(uint job, BrowseSettings b)
    {
        var bit = 1 << (int)(job - 8);
        if (Ui.ToggleChip("##job_" + job, ClassJobs.Abbreviation(job), Palette.ClassJobColor(job), (b.JobMask & bit) != 0, job)) { b.JobMask ^= bit; Config.Save(); }
        var lvl = PlanService.JobLevel(job);
        var name = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>().GetRowOrDefault(job)?.Name.ExtractText() ?? ClassJobs.Abbreviation(job);
        Ui.Tooltip(lvl > 0 ? $"{name}, your level {lvl}" : name);
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
        Ui.Chip(ClassJobs.Abbreviation(e.ClassJobId), Palette.ClassJobColor(e.ClassJobId), job: e.ClassJobId);
        ImGui.SameLine(0, 6 * s);
        Ui.Chip("level " + e.Level, Palette.TextMuted);
        switch (e.Kind)
        {
            case BrowseKind.Recipe:
                if (e.Stars > 0) { ImGui.SameLine(0, 6 * s); Ui.Chip(e.Stars == 1 ? "1 star" : $"{e.Stars} stars", Palette.Warn, FontAwesomeIcon.Star); }
                if (e.Expert) { ImGui.SameLine(0, 6 * s); Ui.Chip("expert", Palette.Danger); }
                if (e.MasterBook != 0)
                {
                    ImGui.SameLine(0, 6 * s);
                    var catalog = Plans.Catalog!;
                    var bookName = catalog.MasterBookName(e.MasterBook);
                    var learned = PlanService.IsBookLearned(e.MasterBook);
                    Ui.Chip(learned ? "master book learned" : "needs " + bookName, learned ? Palette.TextMuted : Palette.Warn, FontAwesomeIcon.Book);
                    if (!learned)
                    {
                        var item = catalog.MasterBookItem(e.MasterBook);
                        var where = catalog.SoldAt(item) is { } shop ? $" It's sold at the {shop} exchange." : "";
                        var have = item != 0 && Plans.Owned.GetValueOrDefault(item) > 0;
                        Ui.Tooltip($"This recipe needs {bookName}, which you haven't learned." +
                                   (have ? " It's in your bags: a run reads it before anything else." : where + " Add the recipe anyway: the plan lists the book first, and once it's in your bags the run reads it before anything else."));
                    }
                }
                break;
            case BrowseKind.Gather or BrowseKind.Fish when e.Reduced:
            {
                ImGui.SameLine(0, 6 * s);
                var cat = Plans.Catalog!;
                var from = cat.ReductionSources(e.ItemId);
                var names = string.Join(", ", from.Take(4).Select(cat.ItemName)) + (from.Count > 4 ? $" and {from.Count - 4} more" : "");
                if (e.ReducedByRun)
                {
                    Ui.Chip("aetherial reduction", Palette.SupplyColor(Core.Planning.Supply.Reduce), FontAwesomeIcon.Magic);
                    Ui.Tooltip($"Comes from reducing collectables: {names}. A run gathers or fishes them as collectables and reduces them.");
                }
                else
                {
                    Ui.Chip("aetherial reduction", Palette.Warn, FontAwesomeIcon.Magic);
                    Ui.Tooltip($"Comes from reducing collectables: {names}. They come from content a run can't go to: added to the order, it's listed under things to get yourself.");
                }
                break;
            }
            case BrowseKind.Gather:
                ImGui.SameLine(0, 6 * s);
                Ui.Chip(e.TimedOnly ? "timed node" : "gather", e.TimedOnly ? Palette.SupplyColor(Core.Planning.Supply.GatherTimed) : Palette.SupplyColor(Core.Planning.Supply.Gather));
                break;
            case BrowseKind.Fish:
                ImGui.SameLine(0, 6 * s);
                Ui.Chip("fish", Palette.SupplyColor(Core.Planning.Supply.Fish));
                break;
        }
        if (e.Places != Place.OpenWorld)
        {
            var how = e.Kind switch { BrowseKind.Recipe => "Crafted", BrowseKind.Fish => "Fished", _ => "Gathered" };
            foreach (var place in PlaceOrder)
            {
                if (place == Place.OpenWorld || (e.Places & place) == 0) continue;
                ImGui.SameLine(0, 6 * s);
                Ui.Chip(PlaceLabel(place), PlaceColor(place), FontAwesomeIcon.MapMarkerAlt);
                Ui.Tooltip($"{how} only in {Places.Describe(e.Places)}. A run can't get it there: added to the order, it's listed under things to get yourself.");
            }
        }

        // Right side: amount + add
        var inOrder = Config.Order.Lines.Where(l => l.ItemId == e.ItemId).Sum(l => l.Quantity);
        var addW = Ui.ButtonWidth("Add", FontAwesomeIcon.Plus);
        var qtyW = 96 * s;
        var hqW = e.CanHq ? 62 * s : 0;
        var right = pos.X + width - 8 * s;
        var y = pos.Y + (h - 4 * s - 34 * s) / 2;
        if (inOrder > 0)
        {
            var label = $"{inOrder} in the order";
            using (Ui.F.Small.Push())
            {
                var lw = ImGui.CalcTextSize(label).X;
                dl.AddText(new Vector2(right - addW - qtyW - hqW - 20 * s - lw, pos.Y + (h - 4 * s - ImGui.GetTextLineHeight()) / 2), Palette.U32(Palette.Accent), label);
            }
        }
        var hq = e.CanHq && hqChoice.GetValueOrDefault(e.Key, Config.Crafting.AimForHq);
        if (e.CanHq)
        {
            ImGui.SetCursorScreenPos(new Vector2(right - addW - qtyW - 8 * s - hqW, y + 2 * s));
            if (Ui.ToggleChip("##hq" + e.Key, "HQ", Palette.Accent, hq)) hqChoice[e.Key] = !hq;
            Ui.Tooltip(hq ? "Crafted aiming for high quality." : "Crafted with the fastest rotation, normal quality.");
        }
        ImGui.SetCursorScreenPos(new Vector2(right - addW - qtyW - 8 * s, y + 1 * s));
        var qty = quantities.GetValueOrDefault(e.Key, 1);
        ImGui.SetNextItemWidth(qtyW);
        if (ImGui.InputInt("##qty" + e.Key, ref qty, 1, 10)) quantities[e.Key] = Math.Clamp(qty, 1, 9999);
        ImGui.SetCursorScreenPos(new Vector2(right - addW, y));
        if (Ui.Button("Add", Ui.Kind.Secondary, FontAwesomeIcon.Plus, id: "##add" + e.Key))
        {
            Config.Order.Add(e.ItemId, quantities.GetValueOrDefault(e.Key, 1), e.RecipeId, hq);
            Changed();
        }

        ImGui.SetCursorScreenPos(pos);
        ImGui.Dummy(new Vector2(width, h));
    }

    private void Refilter(GameCatalog catalog)
    {
        var b = Config.Browse;
        var key = (query.Trim().ToLowerInvariant(), b.JobMask, b.PlaceMask, b.MinLevel, b.MaxLevel, catalog.Browse.Count);
        if (key == filterKey) return;
        filterKey = key;
        var q = key.Item1;
        results = new List<BrowseEntry>();
        otherJobs = 0;
        foreach (var e in catalog.Browse)
        {
            if (e.Level < b.MinLevel || e.Level > b.MaxLevel) continue;
            if (b.PlaceMask != 0 && (b.PlaceMask & (int)e.Places) == 0) continue;
            if (q.Length > 0 && !e.NameLower.Contains(q, StringComparison.Ordinal)) continue;
            var job = 1 << (int)(e.ClassJobId - 8);
            if (b.JobMask == 0 || (b.JobMask & job) != 0) results.Add(e);
            else otherJobs |= job;
        }
    }

    /// <summary>"MIN", "MIN and BTN", "CRP, BSM and ARM".</summary>
    private static string JobList(int mask)
    {
        var names = ClassJobs.Crafters.Concat(ClassJobs.Gatherers).Where(j => (mask & (1 << (int)(j - 8))) != 0).Select(ClassJobs.Abbreviation).ToList();
        return names.Count <= 1 ? string.Concat(names) : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
    }

    private static int JobCount(int mask) => System.Numerics.BitOperations.PopCount((uint)mask);
}
