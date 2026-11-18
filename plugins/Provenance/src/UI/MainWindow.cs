using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Provenance.UI.Pages;
using Provenance.UI.Theme;

namespace Provenance.UI;

/// <summary>
/// Window shell: wordmark and pill tabs on top, the selected page in a scrolling area, and a sticky
/// action bar at the bottom holding the page's status and its main buttons.
/// </summary>
internal sealed class MainWindow : Window
{
    private const float HeaderHeight = 68f;
    private const float FooterHeight = 66f;

    private readonly Plugin plugin;
    private readonly List<Page> pages;
    private int selected;

    public MainWindow(Plugin plugin, IEnumerable<Page> pages) : base("Provenance###ProvenanceMain")
    {
        this.plugin = plugin;
        this.pages = pages.ToList();
        selected = Math.Clamp(plugin.Config.Window.LastTab, 0, this.pages.Count - 1);
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse;
        RespectCloseHotkey = true;
        ApplyMode();
    }

    private static readonly Vector2 CompactSize = new(460, 176);
    private static readonly Vector2 FullMinimum = new(900, 620);
    private bool applySize;
    private Vector2 lastFullSize;

    public bool Compact => plugin.Config.Window.Compact;

    /// <summary>Switches between the full window and the small run strip, remembering the full size.</summary>
    public void SetCompact(bool compact)
    {
        if (compact == Compact) return;
        var w = plugin.Config.Window;
        if (compact)
        {
            // Measured while drawing: commands run outside the window, where ImGui can't be asked.
            var size = lastFullSize;
            if (size.X >= FullMinimum.X && size.Y >= FullMinimum.Y) { w.FullWidth = size.X; w.FullHeight = size.Y; }
        }
        w.Compact = compact;
        plugin.Config.Save();
        ApplyMode();
    }

    private void ApplyMode()
    {
        var w = plugin.Config.Window;
        if (Compact)
        {
            Size = CompactSize;
            SizeConstraints = new WindowSizeConstraints { MinimumSize = CompactSize, MaximumSize = CompactSize };
            Flags |= ImGuiWindowFlags.NoResize;
        }
        else
        {
            Size = new Vector2(Math.Max(w.FullWidth, FullMinimum.X), Math.Max(w.FullHeight, FullMinimum.Y));
            SizeConstraints = new WindowSizeConstraints { MinimumSize = FullMinimum, MaximumSize = new Vector2(4000, 4000) };
            Flags &= ~ImGuiWindowFlags.NoResize;
        }
        SizeCondition = ImGuiCond.Always;
        applySize = true;
    }

    public void Select<T>() where T : Page
    {
        var i = pages.FindIndex(p => p is T);
        if (i >= 0) selected = i;
    }

    public override void PreDraw()
    {
        // Size is forced once after a mode switch, then the user may resize the full window again.
        if (!applySize) SizeCondition = ImGuiCond.FirstUseEver;
        applySize = false;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 16 * Ui.S);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Palette.Window);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);
    }

    public override void OnClose()
    {
        plugin.Config.Window.LastTab = selected;
        plugin.Config.Save();
    }

    public override void Draw()
    {
        using var style = Ui.PushStyle();
        using var body = Ui.F.Body.Push();
        var s = Ui.S;
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();

        if (Compact)
        {
            DrawCompact(pos, size, s);
            return;
        }
        lastFullSize = size / s;

        DrawHeader(pos, size, s);

        var top = HeaderHeight * s;
        var footer = FooterHeight * s;
        ImGui.SetCursorScreenPos(pos + new Vector2(0, top));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(28, 6) * s);
        if (ImGui.BeginChild("##page", new Vector2(size.X, size.Y - top - footer), false, ImGuiWindowFlags.AlwaysUseWindowPadding))
        {
            try { pages[selected].Draw(); }
            catch (Exception e)
            {
                Services.Log.Error(e, "Page {Page} failed to draw", pages[selected].Title);
                Ui.Wrapped("This page hit an error and couldn't be drawn: " + e.Message, Palette.Danger);
            }
            ImGui.Dummy(new Vector2(0, 12 * s));
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();

        DrawFooter(pos, size, s);
    }

    private void DrawHeader(Vector2 pos, Vector2 size, float s)
    {
        var dl = ImGui.GetWindowDrawList();
        var h = HeaderHeight * s;

        // Wordmark: a small source -> product mark, the same curve the plan tree uses.
        var mark = pos + new Vector2(28 * s, h / 2);
        dl.AddBezierCubic(mark + new Vector2(0, -6 * s), mark + new Vector2(0, 4 * s), mark + new Vector2(4 * s, 6 * s), mark + new Vector2(12 * s, 6 * s), Palette.U32(Palette.WithAlpha(Palette.Accent, 0.7f)), 2f * s);
        dl.AddCircleFilled(mark + new Vector2(0, -7 * s), 3.5f * s, Palette.U32(Palette.SupplyColor(Core.Planning.Supply.Gather)));
        dl.AddCircleFilled(mark + new Vector2(14 * s, 6 * s), 4.5f * s, Palette.U32(Palette.Accent));
        using (Ui.F.Title.Push())
            dl.AddText(pos + new Vector2(50 * s, (h - ImGui.GetTextLineHeight()) / 2), Palette.U32(Palette.Text), "Provenance");

        // Pill tabs, centred; the marker eases to the selected tab.
        var tabH = 36 * s;
        var widths = new float[pages.Count];
        using (Ui.F.Strong.Push())
            for (var i = 0; i < pages.Count; i++)
                widths[i] = ImGui.CalcTextSize(pages[i].Title).X + 32 * s + (pages[i].Badge is { Count: > 0 } ? 24 * s : 0);
        var total = widths.Sum() + 8 * s;
        var trackMin = new Vector2(pos.X + (size.X - total) / 2, pos.Y + (h - tabH) / 2);
        dl.AddRectFilled(trackMin, trackMin + new Vector2(total, tabH), Palette.U32(Palette.Surface), tabH / 2);

        var x = 4 * s;
        var selX = 0f;
        for (var i = 0; i < selected; i++) selX += widths[i];
        var marker = Ui.Ease(ImGui.GetID("##tabmarker"), x + selX);
        var markerW = Ui.Ease(ImGui.GetID("##tabmarkerw"), widths[selected]);
        dl.AddRectFilled(trackMin + new Vector2(marker, 4 * s), trackMin + new Vector2(marker + markerW, tabH - 4 * s), Palette.U32(Palette.WithAlpha(Palette.Accent, 0.18f)), (tabH - 8 * s) / 2);

        for (var i = 0; i < pages.Count; i++)
        {
            var tabPos = trackMin + new Vector2(x, 0);
            ImGui.SetCursorScreenPos(tabPos);
            if (ImGui.InvisibleButton("##tab" + i, new Vector2(widths[i], tabH))) selected = i;
            var hovered = ImGui.IsItemHovered();
            var col = i == selected ? Palette.Accent : hovered ? Palette.Text : Palette.TextMuted;
            using (Ui.F.Strong.Push())
            {
                var tw = ImGui.CalcTextSize(pages[i].Title).X;
                var badge = pages[i].Badge;
                var contentW = tw + (badge is { Count: > 0 } ? 24 * s : 0);
                var tx = tabPos.X + (widths[i] - contentW) / 2;
                dl.AddText(new Vector2(tx, tabPos.Y + (tabH - ImGui.GetTextLineHeight()) / 2), Palette.U32(col), pages[i].Title);
                if (badge is { Count: > 0 } b)
                {
                    var c = new Vector2(tx + tw + 14 * s, tabPos.Y + tabH / 2);
                    dl.AddCircleFilled(c, 9 * s, Palette.U32(Palette.WithAlpha(b.Color, 0.25f)));
                    using (Ui.F.Small.Push())
                    {
                        var label = b.Count > 99 ? "99" : b.Count.ToString();
                        var ls = ImGui.CalcTextSize(label);
                        dl.AddText(c - ls / 2, Palette.U32(b.Color), label);
                    }
                }
            }
            x += widths[i];
        }

        ImGui.SetCursorScreenPos(pos + new Vector2(size.X - 28 * s - 64 * s, (h - 30 * s) / 2));
        if (Ui.IconButton("##compact", FontAwesomeIcon.CompressAlt, "Compact view")) SetCompact(true);
        ImGui.SameLine(0, 4 * s);
        if (Ui.IconButton("##close", FontAwesomeIcon.Times, "Close")) IsOpen = false;
    }

    /// <summary>
    /// The small strip: what the run is doing, how far along it is, and its buttons. Everything else
    /// is one click away behind the expand button.
    /// </summary>
    private void DrawCompact(Vector2 pos, Vector2 size, float s)
    {
        var dl = ImGui.GetWindowDrawList();
        var runs = plugin.Runs;
        var pad = 18 * s;
        var x0 = pos.X + pad;
        var inner = size.X - pad * 2;

        // Top row: mark, name, expand and close.
        var mark = pos + new Vector2(pad + 2 * s, 24 * s);
        dl.AddBezierCubic(mark + new Vector2(0, -5 * s), mark + new Vector2(0, 3 * s), mark + new Vector2(3 * s, 5 * s), mark + new Vector2(10 * s, 5 * s), Palette.U32(Palette.WithAlpha(Palette.Accent, 0.7f)), 1.8f * s);
        dl.AddCircleFilled(mark + new Vector2(0, -6 * s), 3 * s, Palette.U32(Palette.SupplyColor(Core.Planning.Supply.Gather)));
        dl.AddCircleFilled(mark + new Vector2(12 * s, 5 * s), 3.8f * s, Palette.U32(Palette.Accent));
        using (Ui.F.Strong.Push())
            dl.AddText(new Vector2(x0 + 24 * s, pos.Y + 24 * s - ImGui.GetTextLineHeight() / 2), Palette.U32(Palette.Text), "Provenance");
        ImGui.SetCursorScreenPos(new Vector2(pos.X + size.X - pad - 58 * s, pos.Y + 10 * s));
        if (Ui.IconButton("##expand", FontAwesomeIcon.ExpandAlt, "Full view", size: 28)) SetCompact(false);
        ImGui.SameLine(0, 2 * s);
        if (Ui.IconButton("##closec", FontAwesomeIcon.Times, "Close", size: 28)) IsOpen = false;

        // What's happening
        var (dot, headline, detail, fraction, progress) = CompactState();
        var y = pos.Y + 50 * s;
        dl.AddCircleFilled(new Vector2(x0 + 4 * s, y + ImGui.GetTextLineHeight() / 2), 4 * s, Palette.U32(dot));
        float hw;
        using (Ui.F.Strong.Push())
        {
            dl.AddText(new Vector2(x0 + 16 * s, y), Palette.U32(Palette.Text), headline);
            hw = ImGui.CalcTextSize(headline).X;
        }
        if (detail.Length > 0)
        {
            var room = inner - 16 * s - hw - 12 * s;
            var text = detail;
            while (text.Length > 4 && ImGui.CalcTextSize(text).X > room) text = text[..^2].TrimEnd() + "…";
            dl.AddText(new Vector2(x0 + 16 * s + hw + 12 * s, y), Palette.U32(Palette.TextMuted), text);
        }

        // Progress
        y += 28 * s;
        if (fraction is { } f)
        {
            ImGui.SetCursorScreenPos(new Vector2(x0, y + 4 * s));
            Ui.Bar("##compactbar", f, dot, inner - 120 * s, 6);
            using (Ui.F.Small.Push())
            {
                var pw = ImGui.CalcTextSize(progress).X;
                dl.AddText(new Vector2(x0 + inner - pw, y), Palette.U32(Palette.TextMuted), progress);
            }
        }
        else if (progress.Length > 0)
        {
            using (Ui.F.Small.Push()) dl.AddText(new Vector2(x0, y), Palette.U32(Palette.TextMuted), progress);
        }

        // Buttons: the same ones the Run page offers.
        var run = pages.OfType<Pages.RunPage>().FirstOrDefault();
        if (run == null) return;
        var actions = run.Actions.ToList();
        var bx = pos.X + size.X - pad;
        var by = pos.Y + size.Y - pad - 34 * s;
        for (var i = actions.Count - 1; i >= 0; i--)
        {
            var a = actions[i];
            bx -= Ui.ButtonWidth(a.Label, a.Icon);
            ImGui.SetCursorScreenPos(new Vector2(bx, by));
            if (Ui.Button(a.Label, a.Kind, a.Icon, enabled: a.Enabled, tooltip: a.Tooltip, id: "##compactaction" + i)) a.OnClick();
            bx -= 8 * s;
        }
    }

    private (Vector4 Dot, string Headline, string Detail, float? Fraction, string Progress) CompactState()
    {
        var runs = plugin.Runs;
        var catalog = plugin.Plans.Catalog;
        var plan = plugin.Plans.Plan;
        if (runs.Phase == Run.RunPhase.Idle)
        {
            var order = plugin.Config.Order;
            return (Palette.TextFaint, "Not running", order.IsEmpty ? "the order is empty" : order.Name,
                null, order.IsEmpty ? "" : plan.Missing.Count > 0 ? $"{plan.Missing.Count} items to get yourself first"
                    : $"{plan.CraftSteps.Sum(l => l.Crafts)} crafts planned, " + Core.Estimating.Estimator.Describe(plugin.Plans.Estimate.Total));
        }

        var dot = runs.Phase switch
        {
            Run.RunPhase.Running => Palette.Accent,
            Run.RunPhase.Pausing or Run.RunPhase.Paused or Run.RunPhase.Stopping => Palette.Warn,
            Run.RunPhase.Finished => Palette.Accent,
            _ => Palette.Danger,
        };
        if (runs.Stage == Run.RunStage.Craft && runs.Crafter.CraftsTotal > 0)
        {
            var c = runs.Crafter;
            var detail = c.Current is { } cur && catalog != null
                ? $"{catalog.ItemName(cur.Recipe.ItemId)}, craft {Math.Min(c.DoneThisRecipe + 1, cur.Crafts)} of {cur.Crafts}" + (c.CurrentAction.Length > 0 ? ", " + c.CurrentAction : "")
                : runs.Detail;
            return (dot, runs.Headline, detail, c.CraftsDone / (float)c.CraftsTotal, $"{c.CraftsDone} of {c.CraftsTotal} crafts" + TimeLeftText());
        }
        var targets = runs.Targets;
        if (targets.Count > 0)
        {
            var got = targets.Count(t => Run.RunEngine.Left(t) == 0);
            return (dot, runs.Headline, runs.Detail, got / (float)targets.Count, $"{got} of {targets.Count} gathered" + TimeLeftText());
        }
        return (dot, runs.Headline, runs.Detail, null, "");
    }

    private string TimeLeftText()
        => plugin.Runs.TimeLeft() is { } left && left > TimeSpan.Zero ? ", " + Core.Estimating.Estimator.Describe(left).Replace("about ", "") + " left" : "";

    private void DrawFooter(Vector2 pos, Vector2 size, float s)
    {
        var dl = ImGui.GetWindowDrawList();
        var h = FooterHeight * s;
        var top = pos.Y + size.Y - h;
        dl.AddRectFilled(new Vector2(pos.X, top), pos + size, Palette.U32(Palette.Surface), 16 * s, ImDrawFlags.RoundCornersBottom);
        dl.AddLine(new Vector2(pos.X, top), new Vector2(pos.X + size.X, top), Palette.U32(Palette.Line), 1 * s);

        var page = pages[selected];
        if (page.Status is { } status)
        {
            var c = new Vector2(pos.X + 32 * s, top + h / 2);
            dl.AddCircleFilled(c, 4 * s, Palette.U32(status.Color));
            dl.AddText(new Vector2(c.X + 12 * s, top + (h - ImGui.GetTextLineHeight()) / 2), Palette.U32(Palette.Text), status.Text);
        }

        var actions = page.Actions.ToList();
        var x = pos.X + size.X - 28 * s;
        var by = top + (h - 34 * s) / 2;
        for (var i = actions.Count - 1; i >= 0; i--)
        {
            var a = actions[i];
            x -= Ui.ButtonWidth(a.Label, a.Icon);
            ImGui.SetCursorScreenPos(new Vector2(x, by));
            if (Ui.Button(a.Label, a.Kind, a.Icon, enabled: a.Enabled, tooltip: a.Tooltip, id: "##footer" + i)) a.OnClick();
            x -= 10 * s;
        }
    }
}
