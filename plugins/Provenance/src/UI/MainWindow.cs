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

/// <summary>A group in the sidebar: an optional caption over its pages.</summary>
internal sealed record NavGroup(string? Caption, Page[] Pages);

/// <summary>
/// Window shell: a sidebar on the left (wordmark, then the pages in groups), the selected page in a
/// scrolling area on the right, and a sticky action bar under it with the page's status and main buttons.
/// </summary>
/// <remarks>
/// Motion, all answering a click: the selection pill slides to the picked row and stretches along the way
/// (its leading edge runs ahead of the trailing one), rows fade their hover in and out, and a page enters
/// with a short fade while rising from below (picked further down the list) or settling from above.
/// "Reduce motion" keeps the fades and drops the movement.
/// </remarks>
internal sealed class MainWindow : Window
{
    private const float SidebarWidth = 204f;
    private const float TopBarHeight = 56f;
    private const float FooterHeight = 66f;
    private const float RowHeight = 32f;
    private const float RowGap = 2f;
    private const double EnterSeconds = 0.26;
    private const double FadeSeconds = 0.14;
    private const float EnterRise = 14f;

    private readonly Plugin plugin;
    private readonly List<NavGroup> groups;
    private readonly List<Page> pages;
    private int selected;

    // The page entrance: which page it ran for, when it started, and from which side (+1 below, -1 above, 0 opening).
    private int shown = -1;
    private double enteredAt = double.NegativeInfinity;
    private int enterFrom;

    // The selection pill, relative to the top of the list; null until the list has been laid out once.
    private float? pillTop;
    private float pillBottom;

    public MainWindow(Plugin plugin, IEnumerable<NavGroup> groups) : base("Provenance###ProvenanceMain")
    {
        this.plugin = plugin;
        this.groups = groups.ToList();
        pages = this.groups.SelectMany(g => g.Pages).ToList();
        selected = Math.Max(0, pages.FindIndex(p => p.Title == plugin.Config.Window.LastPage));
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse;
        RespectCloseHotkey = true;
        ApplyMode();
    }

    private static readonly Vector2 CompactSize = new(460, 176);
    private static readonly Vector2 FullMinimum = new(960, 620);
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
        if (!compact) shown = -1; // back in the full window: the page comes in again
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

    /// <summary>The page of this type (pages are created once with the window).</summary>
    public T? Page<T>() where T : Page => pages.OfType<T>().FirstOrDefault();

    /// <summary>Shows the first page of this type (for the settings, General).</summary>
    public void Select<T>() where T : Page
    {
        var i = pages.FindIndex(p => p is T);
        if (i >= 0) selected = i;
    }

    public override void OnOpen() => shown = -1;

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
        plugin.Config.Window.LastPage = pages[selected].Title;
        plugin.Config.Save();
    }

    public override void Draw()
    {
        using var style = Ui.PushStyle();
        using var body = Ui.F.Body.Push();
        Ui.ReduceMotion = plugin.Config.Window.ReduceMotion;
        Ui.TooltipSize = plugin.Config.Window.TooltipSize;
        var s = Ui.S;
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();

        if (Compact)
        {
            DrawCompact(pos, size, s);
            return;
        }
        lastFullSize = size / s;

        // The sidebar first: a click there changes the page, and the page below should start its entrance this frame.
        var side = SidebarWidth * s;
        DrawSidebar(pos, size, s);

        // One page for the whole frame; a page switching to another while drawing takes effect next frame.
        var current = selected;
        if (shown != current)
        {
            enterFrom = shown < 0 ? 0 : Math.Sign(current - shown);
            shown = current;
            enteredAt = ImGui.GetTime();
        }
        var (alpha, rise) = Entrance(s);

        var left = pos.X + side;
        var width = size.X - side;
        ImGui.SetCursorScreenPos(new Vector2(pos.X + size.X - 24 * s - 64 * s, pos.Y + (TopBarHeight * s - 30 * s) / 2));
        if (Ui.IconButton("##compact", FontAwesomeIcon.CompressAlt, "Compact view")) SetCompact(true);
        ImGui.SameLine(0, 4 * s);
        if (Ui.IconButton("##close", FontAwesomeIcon.Times, "Close")) IsOpen = false;

        var top = TopBarHeight * s;
        var footer = FooterHeight * s;
        ImGui.SetCursorScreenPos(new Vector2(left, pos.Y + top));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(28, 4) * s);
        // One child per page, so each keeps its own scroll position.
        if (ImGui.BeginChild("##page" + current, new Vector2(width, size.Y - top - footer), false, ImGuiWindowFlags.AlwaysUseWindowPadding))
        {
            var dl = ImGui.GetWindowDrawList();
            var first = dl.VtxBuffer.Size;
            try { pages[current].Draw(); }
            catch (Exception e)
            {
                Services.Log.Error(e, "Page {Page} failed to draw", pages[current].Title);
                Ui.Wrapped("This page hit an error and couldn't be drawn: " + e.Message, Palette.Danger);
            }
            ImGui.Dummy(new Vector2(0, 12 * s));
            Shift(dl, first, rise, alpha);
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();

        DrawFooter(pages[current], new Vector2(left, pos.Y), new Vector2(width, size.Y), s, alpha);
    }

    /// <summary>How far into its entrance the page is: its opacity and how far it still sits from its place.</summary>
    private (float Alpha, float Rise) Entrance(float s)
    {
        var reduce = Ui.ReduceMotion;
        var t = (float)Math.Clamp((ImGui.GetTime() - enteredAt) / (reduce ? FadeSeconds : EnterSeconds), 0, 1);
        if (t >= 1f) return (1f, 0f);
        var eased = 1f - MathF.Pow(1f - t, 4f); // a strong ease-out: quick to arrive, soft to land
        if (reduce) return (t, 0f);
        var from = enterFrom == 0 ? 0.6f : enterFrom;
        return (eased, from * EnterRise * s * (1f - eased));
    }

    /// <summary>
    /// Moves what was drawn since <paramref name="first"/> down by <paramref name="dy"/> and fades it, after the fact:
    /// the layout and the mouse keep working on the real positions, only the picture moves.
    /// </summary>
    private static unsafe void Shift(ImDrawListPtr dl, int first, float dy, float alpha)
    {
        if (dy == 0f && alpha >= 1f) return;
        var a = Math.Clamp(alpha, 0f, 1f);
        var vtx = dl.VtxBuffer;
        var data = vtx.Data;
        for (var i = first; i < vtx.Size; i++)
        {
            ref var v = ref data[i];
            v.Pos.Y += dy;
            var va = (v.Col >> 24) & 0xFF;
            v.Col = (v.Col & 0x00FFFFFFu) | ((uint)(va * a) << 24);
        }
    }

    // ------------------------------------------------------------------ sidebar

    private void DrawSidebar(Vector2 pos, Vector2 size, float s)
    {
        var dl = ImGui.GetWindowDrawList();
        var w = SidebarWidth * s;
        dl.AddRectFilled(pos, pos + new Vector2(w, size.Y), Palette.U32(Palette.Surface), 16 * s, ImDrawFlags.RoundCornersLeft);

        // Wordmark: a small source -> product mark, the same curve the plan tree uses.
        var h = TopBarHeight * s;
        var mark = pos + new Vector2(26 * s, h / 2 + 1 * s);
        dl.AddBezierCubic(mark + new Vector2(0, -6 * s), mark + new Vector2(0, 4 * s), mark + new Vector2(4 * s, 6 * s), mark + new Vector2(12 * s, 6 * s), Palette.U32(Palette.WithAlpha(Palette.Accent, 0.7f)), 2f * s);
        dl.AddCircleFilled(mark + new Vector2(0, -7 * s), 3.5f * s, Palette.U32(Palette.SupplyColor(Core.Planning.Supply.Gather)));
        dl.AddCircleFilled(mark + new Vector2(14 * s, 6 * s), 4.5f * s, Palette.U32(Palette.Accent));
        using (Ui.F.Title.Push())
            dl.AddText(pos + new Vector2(48 * s, (h - ImGui.GetTextLineHeight()) / 2), Palette.U32(Palette.Text), "Provenance");

        // The list scrolls with the wheel when the window is too short for it; the version stays at the bottom.
        var listTop = pos.Y + h + 2 * s;
        var listBottom = pos.Y + size.Y - 40 * s;
        ImGui.SetCursorScreenPos(new Vector2(pos.X, listTop));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.BeginChild("##nav", new Vector2(w, listBottom - listTop), false, ImGuiWindowFlags.NoScrollbar))
            DrawNav(w, s);
        ImGui.EndChild();
        ImGui.PopStyleVar();

        using (Ui.F.Small.Push())
            dl.AddText(new Vector2(pos.X + 26 * s, pos.Y + size.Y - 20 * s - ImGui.GetTextLineHeight() / 2), Palette.U32(Palette.TextFaint), "Version " + Plugin.Version);
    }

    private void DrawNav(float w, float s)
    {
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var x0 = origin.X + 12 * s;
        var rowW = w - 24 * s;
        var rowH = RowHeight * s;

        // Layout first, so the pill can be drawn under the rows.
        var rowY = new float[pages.Count];
        var captions = new List<(string Text, float Y)>();
        var dividers = new List<float>();
        var y = 0f;
        var index = 0;
        for (var g = 0; g < groups.Count; g++)
        {
            if (g > 0)
            {
                y += 9 * s;
                dividers.Add(y);
                y += 10 * s;
            }
            if (groups[g].Caption is { } caption)
            {
                captions.Add((caption, y + 4 * s));
                y += 26 * s;
            }
            foreach (var _ in groups[g].Pages)
            {
                rowY[index++] = y;
                y += rowH + RowGap * s;
            }
        }

        foreach (var d in dividers)
            dl.AddLine(new Vector2(x0 + 4 * s, origin.Y + d), new Vector2(x0 + rowW - 4 * s, origin.Y + d), Palette.U32(Palette.Line), 1 * s);
        using (Ui.F.Small.Push())
            foreach (var (text, cy) in captions)
                dl.AddText(new Vector2(x0 + 12 * s, origin.Y + cy), Palette.U32(Palette.TextFaint), text);

        DrawPill(dl, origin, x0, rowW, rowY[selected], rowH, s);

        for (var i = 0; i < pages.Count; i++)
            DrawRow(dl, i, new Vector2(x0, origin.Y + rowY[i]), new Vector2(rowW, rowH), s);

        ImGui.SetCursorScreenPos(origin + new Vector2(0, y));
        ImGui.Dummy(new Vector2(1, 6 * s));
    }

    /// <summary>
    /// The raised pill behind the selected row. On its way to a new row the leading edge moves faster than the
    /// trailing one, so it stretches toward where it's going and settles back to a row's height on arrival.
    /// </summary>
    private void DrawPill(ImDrawListPtr dl, Vector2 origin, float x0, float rowW, float targetTop, float rowH, float s)
    {
        var targetBottom = targetTop + rowH;
        if (pillTop is not { } top || Ui.ReduceMotion)
        {
            top = targetTop;
            pillBottom = targetBottom;
        }
        else
        {
            var dt = ImGui.GetIO().DeltaTime;
            var lead = 1f - MathF.Exp(-26f * dt);
            var trail = 1f - MathF.Exp(-13f * dt);
            var down = targetTop > top;
            top += (targetTop - top) * (down ? trail : lead);
            pillBottom += (targetBottom - pillBottom) * (down ? lead : trail);
            if (MathF.Abs(targetTop - top) < 0.25f) top = targetTop;
            if (MathF.Abs(targetBottom - pillBottom) < 0.25f) pillBottom = targetBottom;
        }
        pillTop = top;
        var min = new Vector2(x0, origin.Y + top);
        var max = new Vector2(x0 + rowW, origin.Y + pillBottom);
        dl.AddRectFilled(min, max, Palette.U32(Palette.Raised), Ui.InputRadius * s);
        // A hairline of light along the top edge, so it reads as raised rather than painted on.
        dl.AddLine(min + new Vector2(Ui.InputRadius * s, 0.5f * s), new Vector2(max.X - Ui.InputRadius * s, min.Y + 0.5f * s), Palette.U32(Palette.WithAlpha(Palette.Text, 0.05f)), 1 * s);
    }

    private void DrawRow(ImDrawListPtr dl, int i, Vector2 min, Vector2 size, float s)
    {
        var page = pages[i];
        var max = min + size;
        ImGui.SetCursorScreenPos(min);
        if (ImGui.InvisibleButton("##navrow" + i, size)) selected = i;
        var hovered = ImGui.IsItemHovered();
        var held = ImGui.IsItemActive();
        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        var isSelected = i == selected;

        // Hover and press: a soft fill on the row (the selected row already sits on the pill).
        var hover = Ui.Fade(ImGui.GetID("##navhover" + i), hovered && !isSelected ? (held ? 1f : 0.6f) : 0f, 22f);
        if (hover > 0.01f)
            dl.AddRectFilled(min, max, Palette.U32(Palette.WithAlpha(Palette.Raised, hover)), Ui.InputRadius * s);
        var on = Ui.Fade(ImGui.GetID("##navon" + i), isSelected ? 1f : 0f, 16f);
        var lift = Math.Clamp(hover / 0.6f, 0f, 1f);

        var midY = min.Y + size.Y / 2;
        var iconColor = Palette.Mix(Palette.Mix(Palette.TextFaint, Palette.TextMuted, lift), Palette.Accent, on);
        using (Ui.F.IconSmall.Push())
        {
            var glyph = page.Icon.ToIconString();
            var gs = ImGui.CalcTextSize(glyph);
            dl.AddText(new Vector2(min.X + 20 * s - gs.X / 2, midY - gs.Y / 2), Palette.U32(iconColor), glyph);
        }
        var textColor = Palette.Mix(Palette.Mix(Palette.TextMuted, Palette.Text, lift), Palette.Text, on);
        dl.AddText(new Vector2(min.X + 38 * s, midY - ImGui.GetTextLineHeight() / 2), Palette.U32(textColor), page.Title);

        var right = max.X - 10 * s;
        if (page.Badge is { Count: > 0 } badge)
        {
            using (Ui.F.Small.Push())
            {
                var label = badge.Count > 99 ? "99+" : badge.Count.ToString();
                var ls = ImGui.CalcTextSize(label);
                var bw = MathF.Max(ls.X + 12 * s, 20 * s);
                var bh = 18 * s;
                var bmin = new Vector2(right - bw, midY - bh / 2);
                dl.AddRectFilled(bmin, bmin + new Vector2(bw, bh), Palette.U32(Palette.WithAlpha(badge.Color, 0.18f)), bh / 2);
                dl.AddText(bmin + new Vector2((bw - ls.X) / 2, (bh - ls.Y) / 2), Palette.U32(badge.Color), label);
            }
        }
        else if (page.Live is { } live)
        {
            // A live run: a dot in its colour, breathing a ring outwards while it works.
            var c = new Vector2(right - 6 * s, midY);
            if (live.Pulse && !Ui.ReduceMotion)
            {
                var p = (float)(ImGui.GetTime() % 1.8 / 1.8);
                var grow = 1f - MathF.Pow(1f - p, 3f);
                dl.AddCircleFilled(c, (3.5f + 6f * grow) * s, Palette.U32(Palette.WithAlpha(live.Color, 0.45f * (1f - p))));
            }
            dl.AddCircleFilled(c, 3.5f * s, Palette.U32(live.Color));
        }
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
            // The text keeps its width; the bar takes what's left (at least a third of the row).
            float pw;
            var text = progress;
            using (Ui.F.Small.Push())
            {
                var maxText = inner * 0.66f - 12 * s;
                while (text.Length > 4 && ImGui.CalcTextSize(text).X > maxText) text = text[..^2].TrimEnd() + "…";
                pw = ImGui.CalcTextSize(text).X;
            }
            ImGui.SetCursorScreenPos(new Vector2(x0, y + 4 * s));
            Ui.Bar("##compactbar", f, dot, Math.Max(40 * s, inner - pw - 14 * s), 6);
            using (Ui.F.Small.Push()) dl.AddText(new Vector2(x0 + inner - pw, y), Palette.U32(Palette.TextMuted), text);
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
        if (runs.Crafting && runs.Crafter.CraftsTotal > 0)
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
            // Kinds of materials done: "2 of 3 gathered", "1 of 2 caught", "3 of 5 gathered or caught".
            var got = targets.Count(t => runs.Left(t) == 0);
            return (dot, runs.Headline, runs.Detail, got / (float)targets.Count, $"{got} of {targets.Count} {Run.RunEngine.GotWord(targets)}" + TimeLeftText());
        }
        return (dot, runs.Headline, runs.Detail, null, "");
    }

    private string TimeLeftText()
        => plugin.Runs.TimeLeft() is { } left && left > TimeSpan.Zero ? ", " + Core.Estimating.Estimator.Describe(left).Replace("about ", "") + " left" : "";

    /// <summary>The action bar under the page: its status on the left, its buttons on the right. They fade in with the page.</summary>
    private static void DrawFooter(Page page, Vector2 pos, Vector2 size, float s, float alpha)
    {
        var dl = ImGui.GetWindowDrawList();
        var h = FooterHeight * s;
        var top = pos.Y + size.Y - h;
        dl.AddRectFilled(new Vector2(pos.X, top), pos + size, Palette.U32(Palette.Surface), 16 * s, ImDrawFlags.RoundCornersBottomRight);
        dl.AddLine(new Vector2(pos.X, top), new Vector2(pos.X + size.X, top), Palette.U32(Palette.Line), 1 * s);

        var first = dl.VtxBuffer.Size;
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
        Shift(dl, first, 0f, alpha);
    }
}
