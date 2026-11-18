using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Provenance.Core.Catalog;
using Provenance.Core.Planning;
using Provenance.Data;
using Provenance.UI.Theme;

namespace Provenance.UI.Pages;

/// <summary>
/// The order as a material tree: every target is a card, its materials hang below it on curves coloured
/// by where they come from, each with a have/need bar. Above it, the start check.
/// </summary>
internal sealed class PlanPage : Page
{
    private const float NodeRow = 34f;
    private const float Indent = 28f;

    private readonly HashSet<string> collapsed = new();
    private DateTime clearArmedUntil = DateTime.MinValue;

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
            return ($"Ready: {crafts} {(crafts == 1 ? "craft" : "crafts")} across {plan.CraftSteps.Count} {(plan.CraftSteps.Count == 1 ? "recipe" : "recipes")}", Palette.Accent);
        }
    }

    public override IEnumerable<FooterAction> Actions
    {
        get
        {
            var plan = Plans.Plan;
            var why = Config.Order.IsEmpty ? "Add items to the order first"
                : !plan.CanStart ? "Get the missing items first; they are listed at the top of the plan"
                : "Running an order arrives with the next milestones (gathering, crafting and the scheduler)";
            yield return new FooterAction("Start", Ui.Kind.Primary, FontAwesomeIcon.Play, () => { }, Enabled: false, Tooltip: why);
        }
    }

    public override void Draw()
    {
        var s = Ui.S;
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

        DrawOrderHeader();
        var plan = Plans.Plan;
        DrawReadiness(plan, catalog);
        foreach (var root in plan.Tree) DrawTarget(root, plan, catalog);
    }

    // ------------------------------------------------------------------ sections

    private void DrawEmpty()
    {
        var s = Ui.S;
        Ui.Heading("Nothing planned yet", "Import a Teamcraft list or pick recipes in Browse. The plan shows every material down to the crystals and where each one comes from.");
        ImGui.Dummy(new Vector2(0, 6 * s));
        if (Ui.Button("Import a list", Ui.Kind.Primary, FontAwesomeIcon.FileImport)) Go<ImportPage>();
        ImGui.SameLine(0, 10 * s);
        if (Ui.Button("Browse recipes", Ui.Kind.Secondary, FontAwesomeIcon.Search)) Go<BrowsePage>();
    }

    private void DrawOrderHeader()
    {
        var s = Ui.S;
        var name = Config.Order.Name;
        using (Ui.F.Title.Push())
        {
            ImGui.PushStyleColor(ImGuiCol.FrameBg, System.Numerics.Vector4.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0, 4) * s);
            ImGui.SetNextItemWidth(MathF.Min(ImGui.GetContentRegionAvail().X - 160 * s, 520 * s));
            if (ImGui.InputText("##ordername", ref name, 80)) Config.Order.Name = name;
            if (ImGui.IsItemDeactivatedAfterEdit()) Config.Save();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor();
        }
        Ui.Tooltip("Rename the order");

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

    private void DrawReadiness(Plan plan, GameCatalog catalog)
    {
        var s = Ui.S;
        if (plan.Missing.Count > 0)
        {
            Ui.BeginCard(Palette.WithAlpha(Palette.Danger, 0.08f));
            Ui.Icon(FontAwesomeIcon.HandPaper, Palette.Danger);
            ImGui.SameLine(0, 10 * s);
            using (Ui.F.Strong.Push()) Ui.Text("Get these yourself before starting", Palette.Danger);
            Ui.Wrapped("The run can't get these by itself. Put them in your inventory or saddlebag and this list updates on its own.");
            ImGui.Dummy(new Vector2(0, 2 * s));
            foreach (var line in plan.Missing)
            {
                Ui.ItemIcon(catalog.ItemIcon(line.ItemId), 28);
                ImGui.SameLine(0, 10 * s);
                ImGui.BeginGroup();
                Ui.Text(catalog.ItemName(line.ItemId));
                using (Ui.F.Small.Push()) Ui.Muted($"Need {line.Remaining} more. {line.ManualReason}.");
                ImGui.EndGroup();
            }
            Ui.EndCard();
            return;
        }

        Ui.BeginCard(Palette.WithAlpha(Palette.Accent, 0.07f));
        Ui.Icon(FontAwesomeIcon.CheckCircle, Palette.Accent);
        ImGui.SameLine(0, 10 * s);
        using (Ui.F.Strong.Push()) Ui.Text("Everything can be gathered, bought or crafted");
        var chips = new List<(string Text, Vector4 Color, FontAwesomeIcon Icon)>();
        void Add(Supply supply, string verb, FontAwesomeIcon icon)
        {
            var n = plan.Count(supply);
            if (n > 0) chips.Add(($"{n} {verb}", Palette.SupplyColor(supply), icon));
        }
        Add(Supply.Gather, "to gather", FontAwesomeIcon.Leaf);
        Add(Supply.GatherTimed, "on timed nodes", FontAwesomeIcon.Clock);
        Add(Supply.Fish, "to fish", FontAwesomeIcon.Fish);
        Add(Supply.Vendor, plan.VendorGil > 0 ? $"to buy for {plan.VendorGil:N0} gil" : "to buy", FontAwesomeIcon.Store);
        Add(Supply.Drop, "monster drops", FontAwesomeIcon.Paw);
        var crafts = plan.CraftSteps.Sum(l => l.Crafts);
        if (crafts > 0) chips.Add(($"{crafts} crafts", Palette.Accent, FontAwesomeIcon.Hammer));
        ChipRow(chips);

        // Crystals are shared by everything, so they get one row here instead of a branch per item.
        var crystals = plan.Lines.Where(l => Items.IsCrystal(l.ItemId) && l.Needed > 0).OrderBy(l => l.ItemId).ToList();
        if (crystals.Count > 0)
        {
            ImGui.Dummy(new Vector2(0, 2 * s));
            using (Ui.F.Small.Push()) Ui.Muted("Crystals");
            var first = true;
            foreach (var c in crystals)
            {
                if (!first) ImGui.SameLine(0, 14 * s);
                first = false;
                Ui.ItemIcon(catalog.ItemIcon(c.ItemId), 22);
                Ui.Tooltip($"{catalog.ItemName(c.ItemId)}: need {c.Needed}, have {c.FromStock}");
                ImGui.SameLine(0, 6 * s);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 2 * s);
                Ui.Text($"{c.FromStock}/{c.Needed}", c.Remaining == 0 ? Palette.Accent : Palette.TextMuted);
            }
        }
        Ui.EndCard();
    }

    private static void ChipRow(List<(string Text, Vector4 Color, FontAwesomeIcon Icon)> chips)
    {
        var s = Ui.S;
        var avail = Ui.CardInnerWidth;
        var x = 0f;
        for (var i = 0; i < chips.Count; i++)
        {
            var w = Ui.ChipWidth(chips[i].Text, chips[i].Icon);
            if (i > 0)
            {
                if (x + 8 * s + w <= avail) ImGui.SameLine(0, 8 * s);
                else x = 0;
            }
            Ui.Chip(chips[i].Text, chips[i].Color, chips[i].Icon);
            x += (i > 0 ? 8 * s : 0) + w;
        }
    }

    private void DrawTarget(PlanTreeNode root, Plan plan, GameCatalog catalog)
    {
        var s = Ui.S;
        var line = plan[root.ItemId];
        if (line == null) return;
        Ui.BeginCard();
        var inner = Ui.CardInnerWidth;
        var start = ImGui.GetCursorScreenPos();

        // Header: icon, name, recipe, amount, remove
        Ui.ItemIcon(catalog.ItemIcon(root.ItemId), 40);
        var iconAnchor = start + new Vector2(20 * s, 40 * s);
        ImGui.SameLine(0, 14 * s);
        ImGui.BeginGroup();
        using (Ui.F.Strong.Push()) Ui.Text(catalog.ItemName(root.ItemId));
        if (line.Recipe is { } r)
        {
            Ui.Chip($"{r.Job.Abbreviation()} level {r.Level}", Palette.JobColor(r.Job));
            if (line.Crafts > 0 && r.Yield > 1) { ImGui.SameLine(0, 6 * s); Ui.Chip($"{line.Crafts} crafts make {line.Produced}", Palette.TextMuted); }
        }
        else
        {
            Ui.Chip(Palette.SupplyLabel(line.Supply), Palette.SupplyColor(line.Supply));
        }
        ImGui.EndGroup();

        var orderLine = Config.Order.Lines.FirstOrDefault(l => l.ItemId == root.ItemId);
        if (orderLine != null)
        {
            var qtyW = 104 * s;
            ImGui.SetCursorScreenPos(new Vector2(start.X + inner - qtyW - 40 * s, start.Y + 4 * s));
            var qty = orderLine.Quantity;
            ImGui.SetNextItemWidth(qtyW);
            if (ImGui.InputInt("##tq" + root.ItemId, ref qty, 1, 10)) { orderLine.Quantity = Math.Clamp(qty, 1, 9999); Changed(); }
            Ui.Tooltip("How many to make");
            ImGui.SetCursorScreenPos(new Vector2(start.X + inner - 30 * s, start.Y + 3 * s));
            if (Ui.IconButton("##rm" + root.ItemId, FontAwesomeIcon.TrashAlt, "Remove from the order", Ui.Kind.Danger))
            {
                Config.Order.Lines.RemoveAll(l => l.ItemId == root.ItemId);
                Changed();
                Ui.EndCard();
                return;
            }
        }

        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + 50 * s));
        var children = root.Children.Where(c => !Items.IsCrystal(c.ItemId)).ToList();
        if (children.Count == 0 && line.Crafts == 0 && line.Recipe != null)
        {
            using (Ui.F.Small.Push()) Ui.Muted("Covered by what you already own.");
        }
        foreach (var child in children)
            DrawNode(child, plan, catalog, 1, iconAnchor, root.ItemId.ToString(), start.X, inner);
        Ui.EndCard();
    }

    // ------------------------------------------------------------------ tree

    private void DrawNode(PlanTreeNode node, Plan plan, GameCatalog catalog, int depth, Vector2 parentAnchor, string parentPath, float left, float width)
    {
        var line = plan[node.ItemId];
        if (line == null) return;
        var s = Ui.S;
        var path = parentPath + "/" + node.ItemId;
        var h = NodeRow * s;
        var y = ImGui.GetCursorScreenPos().Y;
        var x = left + (depth - 1) * Indent * s + 8 * s;
        var icon = 24 * s;
        var iconPos = new Vector2(x + 18 * s, y + (h - icon) / 2);
        var dl = ImGui.GetWindowDrawList();
        var color = line.Supply == Supply.Craft && line.Recipe != null ? Palette.JobColor(line.Recipe.Job) : Palette.SupplyColor(line.Supply);

        // Connector: down from the parent's icon, then a soft curve into this one.
        var end = new Vector2(iconPos.X - 3 * s, iconPos.Y + icon / 2);
        var cornerY = end.Y;
        dl.AddLine(parentAnchor, new Vector2(parentAnchor.X, cornerY - 10 * s), Palette.U32(Palette.WithAlpha(Palette.Line, 1f)), 1.5f * s);
        dl.AddBezierCubic(new Vector2(parentAnchor.X, cornerY - 10 * s), new Vector2(parentAnchor.X, cornerY - 2 * s), new Vector2(parentAnchor.X + 4 * s, end.Y), end,
            Palette.U32(Palette.WithAlpha(color, 0.55f)), 1.5f * s);

        var hasChildren = node.Children.Any(c => !Items.IsCrystal(c.ItemId));
        var isCollapsed = collapsed.Contains(path) || (depth >= 3 && !collapsed.Contains(path + "+"));
        var rowMin = new Vector2(x, y);
        var rowMax = new Vector2(left + width, y + h);
        ImGui.SetCursorScreenPos(rowMin);
        if (ImGui.InvisibleButton("##node" + path, rowMax - rowMin) && hasChildren)
        {
            if (isCollapsed) { collapsed.Remove(path); collapsed.Add(path + "+"); }
            else { collapsed.Add(path); collapsed.Remove(path + "+"); }
        }
        var hovered = ImGui.IsItemHovered();
        if (hovered) dl.AddRectFilled(rowMin + new Vector2(14 * s, 1 * s), rowMax - new Vector2(0, 1 * s), Palette.U32(Palette.WithAlpha(Palette.Raised, 0.6f)), Ui.InputRadius * s);
        if (hasChildren)
        {
            using (Ui.F.IconSmall.Push())
            {
                var chevron = (isCollapsed ? FontAwesomeIcon.ChevronRight : FontAwesomeIcon.ChevronDown).ToIconString();
                var cs = ImGui.CalcTextSize(chevron);
                dl.AddText(new Vector2(x + 2 * s, y + (h - cs.Y) / 2), Palette.U32(hovered ? Palette.Text : Palette.TextFaint), chevron);
            }
        }

        Ui.DrawItemIcon(dl, catalog.ItemIcon(node.ItemId), iconPos, icon);
        var tx = iconPos.X + icon + 10 * s;
        var textY = y + (h - ImGui.GetTextLineHeight()) / 2;
        var name = catalog.ItemName(node.ItemId);
        dl.AddText(new Vector2(tx, textY), Palette.U32(Palette.Text), name);
        var nameW = ImGui.CalcTextSize(name).X;

        // Chip: recipe job and crafts, or the source
        var chip = line.Supply == Supply.Craft && line.Recipe != null
            ? (line.Crafts > 0 ? $"{line.Recipe.Job.Abbreviation()} ×{line.Crafts}" : $"{line.Recipe.Job.Abbreviation()}, owned")
            : Palette.SupplyLabel(line.Supply);
        ImGui.SetCursorScreenPos(new Vector2(tx + nameW + 10 * s, y + (h - 20 * s) / 2));
        Ui.Chip(chip, color);
        if (line.Supply == Supply.Vendor) Ui.Tooltip($"{line.VendorUnitPrice:N0} gil each");
        if (line.ManualReason != null) Ui.Tooltip(line.ManualReason);

        // Have / need across the whole order
        var barW = 110 * s;
        var have = line.FromStock;
        var need = line.Needed;
        var label = $"{have}/{need}";
        var lw = ImGui.CalcTextSize(label).X;
        var right = left + width - 4 * s;
        dl.AddText(new Vector2(right - lw, textY), Palette.U32(have >= need ? Palette.Accent : Palette.TextMuted), label);
        ImGui.SetCursorScreenPos(new Vector2(right - lw - 12 * s - barW, y + (h - 6 * s) / 2));
        Ui.Bar("##bar" + path, need == 0 ? 1f : have / (float)need, have >= need ? Palette.Accent : color, barW);
        if (hovered)
            Ui.ShowTooltip($"This branch uses {node.Quantity}. The whole order needs {need}, and you have {have}." + (hasChildren ? " Click to show or hide its materials." : ""));

        ImGui.SetCursorScreenPos(new Vector2(left, y + h));
        if (!hasChildren || isCollapsed) return;
        var anchor = new Vector2(iconPos.X + icon / 2, iconPos.Y + icon);
        foreach (var child in node.Children.Where(c => !Items.IsCrystal(c.ItemId)))
            DrawNode(child, plan, catalog, depth + 1, anchor, path, left, width);
    }
}
