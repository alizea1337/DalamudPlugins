using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Provenance.Core.Catalog;
using Provenance.Core.Estimating;
using Provenance.Core.Planning;
using Provenance.Data;
using Provenance.UI.Theme;

namespace Provenance.UI.Pages;

/// <summary>
/// The order as totals: what is being made, then every material once, summed over the whole order and
/// grouped by how it is obtained (gather, fish, buy, monsters, craft first), with have/need for each.
/// </summary>
internal sealed class PlanPage : Page
{
    private const float RowHeight = 36f;

    private DateTime clearArmedUntil = DateTime.MinValue;
    private static OrderEstimate currentEstimate = OrderEstimate.Empty;

    /// <summary>", about 3 min" for a recipe's crafts in this order.</summary>
    private static string CraftTime(RecipeInfo recipe)
        => currentEstimate.Crafts.FirstOrDefault(c => c.Recipe.RecipeId == recipe.RecipeId) is { } c ? ", " + Estimator.Describe(c.Duration) : "";

    public PlanPage(Plugin plugin) : base(plugin) { }

    public override string Title => "Plan";

    public override (int Count, Vector4 Color)? Badge => Plans.Plan.Missing.Count > 0 ? (Plans.Plan.Missing.Count, Palette.Danger) : null;

    public override (string Text, Vector4 Color)? Status
    {
        get
        {
            var plan = Plans.Plan;
            if (Config.Order.IsEmpty) return ("The order is empty", Palette.TextFaint);
            if (plan.Missing.Count > 0) return (plan.Missing.Count == 1 ? "1 item to get yourself before starting" : $"{plan.Missing.Count} items to get yourself before starting", Palette.Danger);
            var crafts = plan.CraftSteps.Sum(l => l.Crafts);
            var total = Plans.Estimate.Total;
            return ($"Ready: {crafts} {(crafts == 1 ? "craft" : "crafts")} across {plan.CraftSteps.Count} {(plan.CraftSteps.Count == 1 ? "recipe" : "recipes")}" +
                    (total > TimeSpan.Zero ? ", " + Estimator.Describe(total) + " in total" : ""), Palette.Accent);
        }
    }

    public override IEnumerable<FooterAction> Actions
    {
        get
        {
            var runs = Plugin.Runs;
            if (runs.Active)
            {
                yield return new FooterAction("Show the run", Ui.Kind.Primary, FontAwesomeIcon.Running, Go<RunPage>);
                yield break;
            }
            var why = runs.StartBlocker();
            yield return new FooterAction("Start", Ui.Kind.Primary, FontAwesomeIcon.Play, () => { runs.Start(); Go<RunPage>(); },
                Enabled: why == null, Tooltip: why ?? "Gathers everything this order needs with GatherBuddy Reborn. Buying, monster drops and crafting follow in the next milestones.");
        }
    }

    public override void Draw()
    {
        var catalog = Plans.Catalog;
        if (catalog == null)
        {
            Ui.Heading("Plan", Plans.LoadError is { } err ? "Game data couldn't be read: " + err : "Reading recipes from the game data…");
            return;
        }
        if (Config.Order.IsEmpty)
        {
            DrawEmpty();
            return;
        }

        var plan = Plans.Plan;
        currentEstimate = Plans.Estimate;
        DrawOrderHeader();
        DrawLearnFirst(plan, catalog);
        DrawMissing(plan, catalog);
        DrawTargets(plan, catalog);

        // Ordered items that aren't crafted (gathered, fished, bought) also show up with their progress.
        var materials = plan.Lines.Where(l => (!l.IsTarget || l.Supply != Supply.Craft) && !Items.IsCrystal(l.ItemId)).ToList();
        Section("To gather", FontAwesomeIcon.Leaf, Palette.SupplyColor(Supply.Gather),
            materials.Where(l => l.Supply is Supply.Gather or Supply.GatherTimed), catalog, null);
        Section("To fish", FontAwesomeIcon.Fish, Palette.SupplyColor(Supply.Fish),
            materials.Where(l => l.Supply == Supply.Fish), catalog, null);
        Section("To buy", FontAwesomeIcon.Store, Palette.SupplyColor(Supply.Vendor),
            materials.Where(l => l.Supply == Supply.Vendor), catalog, plan.VendorGil > 0 ? $"about {plan.VendorGil:N0} gil" : null);
        Section("Monster drops", FontAwesomeIcon.Paw, Palette.SupplyColor(Supply.Drop),
            materials.Where(l => l.Supply == Supply.Drop), catalog, null);
        Section("To craft first", FontAwesomeIcon.Hammer, Palette.Accent,
            materials.Where(l => l.Supply == Supply.Craft), catalog, CraftSummary(plan));
        DrawCrystals(plan, catalog);
    }

    // ------------------------------------------------------------------ header and checks

    private void DrawEmpty()
    {
        var s = Ui.S;
        Ui.Heading("Nothing planned yet", "Import a Teamcraft list or pick recipes in Browse. The plan lists every material down to the crystals and where each one comes from.");
        ImGui.Dummy(new Vector2(0, 6 * s));
        if (Ui.Button("Import a list", Ui.Kind.Primary, FontAwesomeIcon.FileImport)) Go<ImportPage>();
        ImGui.SameLine(0, 10 * s);
        if (Ui.Button("Browse recipes", Ui.Kind.Secondary, FontAwesomeIcon.Search)) Go<BrowsePage>();
    }

    /// <summary>One line under the order name: the whole order, split into gathering and crafting.</summary>
    private void DrawEstimateLine()
    {
        var e = Plans.Estimate;
        if (e.Total <= TimeSpan.Zero) return;
        var parts = new List<string>();
        if (e.Gathering > TimeSpan.Zero) parts.Add(Estimator.Describe(e.Gathering).Replace("about ", "") + " gathering");
        if (e.Crafting > TimeSpan.Zero) parts.Add(Estimator.Describe(e.Crafting).Replace("about ", "") + " crafting");
        var text = $"{Capitalize(Estimator.Describe(e.Total))} for the whole order ({string.Join(", ", parts)}).";
        if (e.HasTimedNodes) text += " Waiting for timed nodes comes on top.";
        if (e.Approximate) text += Plans.Profile == null ? " Check your character for closer crafting times." : " Still working out some rotations.";
        Ui.Icon(FontAwesomeIcon.Clock, Palette.TextMuted, small: true);
        ImGui.SameLine(0, 8 * Ui.S);
        Ui.Wrapped(text, Palette.TextMuted);
        Ui.Tooltip("Estimates follow your own runs: each craft and gathering session adjusts the timings.");
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private void DrawOrderHeader()
    {
        var s = Ui.S;
        var name = Config.Order.Name;
        using (Ui.F.Title.Push())
        {
            ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0, 4) * s);
            ImGui.SetNextItemWidth(MathF.Min(ImGui.GetContentRegionAvail().X - 160 * s, 520 * s));
            if (ImGui.InputText("##ordername", ref name, 80)) Config.Order.Name = name;
            if (ImGui.IsItemDeactivatedAfterEdit()) Config.Save();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor();
        }
        Ui.Tooltip("Rename the order");
        DrawEstimateLine();

        var armed = DateTime.UtcNow < clearArmedUntil;
        var label = armed ? "Click again to clear" : "Clear order";
        ImGui.SameLine(ImGui.GetContentRegionMax().X - Ui.ButtonWidth(label, FontAwesomeIcon.TrashAlt));
        if (Ui.Button(label, armed ? Ui.Kind.Danger : Ui.Kind.Ghost, FontAwesomeIcon.TrashAlt, id: "##clear"))
        {
            if (armed)
            {
                Config.Order.Lines.Clear();
                clearArmedUntil = DateTime.MinValue;
                Changed();
            }
            else clearArmedUntil = DateTime.UtcNow.AddSeconds(3);
        }
    }

    /// <summary>Master books and folklore tomes the order needs, with what the run does about them.</summary>
    private static void DrawLearnFirst(Plan plan, GameCatalog catalog)
    {
        if (plan.Learn.Count == 0) return;
        var s = Ui.S;
        Ui.BeginCard(Palette.WithAlpha(Palette.Warn, 0.07f));
        Ui.Icon(FontAwesomeIcon.Book, Palette.Warn);
        ImGui.SameLine(0, 10 * s);
        using (Ui.F.Strong.Push()) Ui.Text("Learn first", Palette.Warn);
        Ui.Wrapped("Some recipes or legendary nodes in this order need a book you haven't learned. The run reads it before anything else once it's in your bags.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        foreach (var line in plan.Learn)
        {
            Ui.ItemIcon(catalog.ItemIcon(line.ItemId), 28);
            ImGui.SameLine(0, 10 * s);
            ImGui.BeginGroup();
            Ui.Text(catalog.ItemName(line.ItemId));
            ImGui.SameLine(0, 8 * s);
            if (line.Remaining == 0) Ui.Chip("in your bags, read first", Palette.Accent);
            else Ui.Chip("get it first", Palette.Danger);
            var forText = "Needed for " + string.Join(", ", line.NeededFor.Take(4).Select(catalog.ItemName)) + (line.NeededFor.Count > 4 ? $" and {line.NeededFor.Count - 4} more" : "") + ".";
            if (line.Remaining > 0 && catalog.SoldAt(line.ItemId) is { } shop) forText += $" Sold at the {shop} exchange.";
            using (Ui.F.Small.Push()) Ui.Muted(forText);
            ImGui.EndGroup();
        }
        Ui.EndCard();
    }

    private static void DrawMissing(Plan plan, GameCatalog catalog)
    {
        var missing = plan.Missing.Where(l => l.Supply != Supply.Learn).ToList();
        if (missing.Count == 0) return;
        var s = Ui.S;
        Ui.BeginCard(Palette.WithAlpha(Palette.Danger, 0.08f));
        Ui.Icon(FontAwesomeIcon.HandPaper, Palette.Danger);
        ImGui.SameLine(0, 10 * s);
        using (Ui.F.Strong.Push()) Ui.Text("Get these yourself before starting", Palette.Danger);
        Ui.Wrapped("The run can't get these by itself. Put them in your inventory or saddlebag and this list updates on its own.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        foreach (var line in missing)
        {
            Ui.ItemIcon(catalog.ItemIcon(line.ItemId), 28);
            ImGui.SameLine(0, 10 * s);
            ImGui.BeginGroup();
            Ui.Text(catalog.ItemName(line.ItemId));
            using (Ui.F.Small.Push()) Ui.Muted($"Need {line.Remaining} more. {line.ManualReason}.");
            ImGui.EndGroup();
        }
        Ui.EndCard();
    }

    // ------------------------------------------------------------------ what is being made

    private void DrawTargets(Plan plan, GameCatalog catalog)
    {
        var s = Ui.S;
        Ui.BeginCard();
        SectionHeader("Making", FontAwesomeIcon.Box, Palette.Accent, Config.Order.Lines.Count == 1 ? "1 item" : $"{Config.Order.Lines.Count} items");
        var inner = Ui.CardInnerWidth;

        foreach (var orderLine in Config.Order.Lines.ToList())
        {
            var line = plan[orderLine.ItemId];
            var start = ImGui.GetCursorScreenPos();
            var h = 44 * s;
            var dl = ImGui.GetWindowDrawList();

            Ui.DrawItemIcon(dl, catalog.ItemIcon(orderLine.ItemId), start + new Vector2(0, (h - 32 * s) / 2), 32 * s);
            var tx = start.X + 44 * s;
            using (Ui.F.Strong.Push())
                dl.AddText(new Vector2(tx, start.Y + 4 * s), Palette.U32(Palette.Text), catalog.ItemName(orderLine.ItemId));
            ImGui.SetCursorScreenPos(new Vector2(tx, start.Y + 24 * s));
            if (line?.Recipe is { } r)
            {
                Ui.Chip($"{r.Job.Abbreviation()} level {r.Level}", Palette.JobColor(r.Job));
                if (line.Crafts == 0) { ImGui.SameLine(0, 6 * s); Ui.Chip("covered by what you own", Palette.Accent); }
                else if (r.Yield > 1) { ImGui.SameLine(0, 6 * s); Ui.Chip($"{line.Crafts} crafts make {line.Produced}", Palette.TextMuted); }
                ReadyChip(orderLine.ItemId);
            }
            else if (line != null)
            {
                Ui.Chip(Palette.SupplyLabel(line.Supply), Palette.SupplyColor(line.Supply));
                ReadyChip(orderLine.ItemId);
            }

            var qtyW = 104 * s;
            if (line?.Recipe is { CanHq: true })
            {
                ImGui.SetCursorScreenPos(new Vector2(start.X + inner - qtyW - 40 * s - 70 * s, start.Y + (h - 30 * s) / 2));
                if (Ui.ToggleChip("##hq" + orderLine.ItemId + "_" + orderLine.RecipeId, "HQ", Palette.Accent, orderLine.Hq)) { orderLine.Hq = !orderLine.Hq; Changed(); }
                Ui.Tooltip(orderLine.Hq ? "Crafted aiming for high quality." : "Crafted with the fastest rotation, normal quality.");
            }
            ImGui.SetCursorScreenPos(new Vector2(start.X + inner - qtyW - 40 * s, start.Y + (h - 30 * s) / 2));
            var qty = orderLine.Quantity;
            ImGui.SetNextItemWidth(qtyW);
            if (ImGui.InputInt("##tq" + orderLine.ItemId + "_" + orderLine.RecipeId, ref qty, 1, 10)) { orderLine.Quantity = Math.Clamp(qty, 1, 9999); Changed(); }
            Ui.Tooltip("How many to make");
            ImGui.SetCursorScreenPos(new Vector2(start.X + inner - 30 * s, start.Y + (h - 30 * s) / 2));
            if (Ui.IconButton("##rm" + orderLine.ItemId + "_" + orderLine.RecipeId, FontAwesomeIcon.TrashAlt, "Remove from the order", Ui.Kind.Danger))
            {
                Config.Order.Lines.Remove(orderLine);
                Changed();
            }
            ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + h + 4 * s));
        }
        Ui.EndCard();
    }

    /// <summary>"ready in about 12 min", counted from the start of the run in the order it works.</summary>
    private void ReadyChip(uint itemId)
    {
        if (!Plans.Estimate.ReadyAfter.TryGetValue(itemId, out var after) || after <= TimeSpan.Zero) return;
        ImGui.SameLine(0, 6 * Ui.S);
        Ui.Chip("ready in " + Estimator.Describe(after), Palette.TextMuted, FontAwesomeIcon.Clock);
        Ui.Tooltip("From the start of the run: gathering first, then the recipes in order, ingredients before the items that use them.");
    }

    // ------------------------------------------------------------------ material totals

    private static string? CraftSummary(Plan plan)
    {
        var crafts = plan.CraftSteps.Where(l => !l.IsTarget).Sum(l => l.Crafts);
        return crafts > 0 ? $"{crafts} {(crafts == 1 ? "craft" : "crafts")}" : null;
    }

    private static void Section(string title, FontAwesomeIcon icon, Vector4 color, IEnumerable<PlanLine> source, GameCatalog catalog, string? extra)
    {
        var lines = source
            .OrderByDescending(l => l.Remaining > 0)
            .ThenByDescending(l => l.Remaining)
            .ThenBy(l => catalog.ItemName(l.ItemId), StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (lines.Count == 0) return;

        var left = lines.Sum(l => l.Remaining);
        var summary = left == 0 ? "all in your bags" : $"{lines.Count} {(lines.Count == 1 ? "item" : "items")}, {left:N0} still to get";
        if (title == "To craft first") summary = left == 0 ? "all in your bags" : $"{lines.Count} {(lines.Count == 1 ? "item" : "items")}";
        if (extra != null) summary += ", " + extra;

        Ui.BeginCard();
        SectionHeader(title, icon, color, summary);
        foreach (var line in lines) MaterialRow(line, catalog);
        Ui.EndCard();
    }

    private static void SectionHeader(string title, FontAwesomeIcon icon, Vector4 color, string summary)
    {
        var s = Ui.S;
        Ui.Icon(icon, color);
        ImGui.SameLine(0, 10 * s);
        using (Ui.F.Strong.Push()) Ui.Text(title);
        ImGui.SameLine(0, 10 * s);
        Ui.Muted(summary);
        ImGui.Dummy(new Vector2(0, 2 * s));
    }

    private static void MaterialRow(PlanLine line, GameCatalog catalog)
    {
        var s = Ui.S;
        var width = Ui.CardInnerWidth;
        var h = RowHeight * s;
        var start = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        var hovered = ImGui.IsMouseHoveringRect(start, start + new Vector2(width, h)) && ImGui.IsWindowHovered();
        if (hovered) dl.AddRectFilled(start - new Vector2(6 * s, 0), start + new Vector2(width + 6 * s, h), Palette.U32(Palette.WithAlpha(Palette.Raised, 0.6f)), Ui.InputRadius * s);

        var icon = 26 * s;
        Ui.DrawItemIcon(dl, catalog.ItemIcon(line.ItemId), start + new Vector2(0, (h - icon) / 2), icon);
        var tx = start.X + icon + 12 * s;
        var textY = start.Y + (h - ImGui.GetTextLineHeight()) / 2;
        var name = catalog.ItemName(line.ItemId);
        dl.AddText(new Vector2(tx, textY), Palette.U32(line.Remaining == 0 ? Palette.TextMuted : Palette.Text), name);
        var chipX = tx + ImGui.CalcTextSize(name).X + 10 * s;

        // Detail chip: how it is obtained when that needs saying.
        (string Text, Vector4 Color)? chip = line.Supply switch
        {
            Supply.GatherTimed => (TimedChip(catalog.TimedMask(line.ItemId)), Palette.SupplyColor(Supply.GatherTimed)),
            Supply.Vendor => ($"{line.VendorUnitPrice:N0} gil each", Palette.SupplyColor(Supply.Vendor)),
            Supply.Craft when line.Recipe != null => ($"{line.Recipe.Job.Abbreviation()}, {line.Crafts} {(line.Crafts == 1 ? "craft" : "crafts")}" + CraftTime(line.Recipe), Palette.JobColor(line.Recipe.Job)),
            _ => null,
        };
        if (chip is { } c && !(line.Supply == Supply.Craft && line.Crafts == 0))
        {
            ImGui.SetCursorScreenPos(new Vector2(chipX, start.Y + (h - 20 * s) / 2));
            Ui.Chip(c.Text, c.Color);
        }

        // Right: still to get, then have/need with a bar.
        var have = line.FromStock;
        var need = line.Needed;
        var done = line.Remaining == 0;
        var count = $"{have}/{need}";
        var cw = ImGui.CalcTextSize(count).X;
        var right = start.X + width;
        dl.AddText(new Vector2(right - cw, textY), Palette.U32(done ? Palette.Accent : Palette.TextMuted), count);
        var barW = 110 * s;
        var barX = right - MathF.Max(cw, 52 * s) - 12 * s - barW;
        var color = line.Supply == Supply.Craft && line.Recipe != null ? Palette.JobColor(line.Recipe.Job) : Palette.SupplyColor(line.Supply);
        ImGui.SetCursorScreenPos(new Vector2(barX, start.Y + (h - 6 * s) / 2));
        Ui.Bar("##bar" + line.ItemId, need == 0 ? 1f : have / (float)need, done ? Palette.Accent : color, barW);

        using (Ui.F.Strong.Push())
        {
            var left = done ? "done" : $"{line.Remaining:N0} left";
            var lw = ImGui.CalcTextSize(left).X;
            dl.AddText(new Vector2(barX - 14 * s - lw, textY), Palette.U32(done ? Palette.Accent : Palette.Text), left);
        }

        if (hovered)
        {
            var tip = $"The whole order needs {need:N0}; you have {have:N0}.";
            if (line.Supply == Supply.Craft && line.Recipe is { } r && line.Crafts > 0)
                tip += $" {line.Crafts} {(line.Crafts == 1 ? "craft makes" : "crafts make")} {line.Produced}.";
            if (line.Supply == Supply.GatherTimed) tip += " Only on timed nodes; the run crafts while it waits for them.";
            Ui.ShowTooltip(tip);
        }

        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + h));
        ImGui.Dummy(new Vector2(width, 0));
    }

    /// <summary>"timed node, up now" / "timed node, opens in 12 min", from the node's Eorzea hours.</summary>
    private static string TimedChip(uint mask)
    {
        var now = DateTimeOffset.UtcNow;
        if (mask == 0) return "timed node";
        if (Core.Timing.Eorzea.IsUp(mask, now))
            return "timed node, up for " + Estimator.Describe(Core.Timing.Eorzea.UpFor(mask, now)).Replace("about ", "");
        return Core.Timing.Eorzea.UntilOpen(mask, now) is { } wait ? "timed node, opens in " + Estimator.Describe(wait).Replace("about ", "") : "timed node";
    }

    private static void DrawCrystals(Plan plan, GameCatalog catalog)
    {
        var crystals = plan.Lines.Where(l => Items.IsCrystal(l.ItemId) && l.Needed > 0).OrderBy(l => l.ItemId).ToList();
        if (crystals.Count == 0) return;
        var s = Ui.S;
        Ui.BeginCard();
        var left = crystals.Sum(c => c.Remaining);
        SectionHeader("Crystals", FontAwesomeIcon.Gem, Palette.SupplyColor(Supply.Gather), left == 0 ? "all in your bags" : $"{left:N0} still to get");
        var first = true;
        foreach (var c in crystals)
        {
            if (!first) ImGui.SameLine(0, 18 * s);
            first = false;
            Ui.ItemIcon(catalog.ItemIcon(c.ItemId), 24);
            Ui.Tooltip($"{catalog.ItemName(c.ItemId)}: need {c.Needed}, have {c.FromStock}");
            ImGui.SameLine(0, 6 * s);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 3 * s);
            Ui.Text($"{c.FromStock}/{c.Needed}", c.Remaining == 0 ? Palette.Accent : Palette.TextMuted);
        }
        Ui.EndCard();
    }
}
