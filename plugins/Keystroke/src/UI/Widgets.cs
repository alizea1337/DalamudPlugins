using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Keystroke.UI.Theme;

namespace Keystroke.UI;

/// <summary>
/// Keystroke's control vocabulary: group boxes with cut-in titles, checkbox rows, thin sliders with
/// the value riding the fill, inset combos, bevel buttons, the icon rail, and the keycap strip.
/// Everything is drawn through the draw list so the look is identical regardless of host style.
/// </summary>
internal static class Widgets
{
    private static Fonts? fonts;
    private static readonly Dictionary<uint, float> Anim = new();

    public static float S => ImGuiHelpers.GlobalScale;
    public static Fonts F => fonts ?? throw new InvalidOperationException("Widgets.Init not called");
    public static void Init(Fonts f) => fonts = f;

    /// <summary>Frame-rate independent approach toward a target; instant when motion is reduced.</summary>
    public static float Ease(uint id, float target, float speed = 10f)
    {
        if (Theme.Theme.ReduceMotion || !Theme.Theme.Effects) { Anim[id] = target; return target; }
        Anim.TryGetValue(id, out var v);
        var k = 1f - MathF.Exp(-speed * ImGui.GetIO().DeltaTime);
        v += (target - v) * k;
        if (MathF.Abs(target - v) < 0.002f) v = target;
        Anim[id] = v;
        return v;
    }

    /// <summary>0..1 breathing value for glows; constant when motion is reduced.</summary>
    public static float Breathe(float period = 2.4f)
        => Theme.Theme.ReduceMotion || !Theme.Theme.Effects ? 0.6f : 0.5f + 0.5f * MathF.Sin((float)(ImGui.GetTime() * Math.PI * 2 / period));

    // ---------------------------------------------------------------- text

    public static void Text(string text, Vector4? color = null)
    {
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var size = ImGui.CalcTextSize(text);
        Theme.Theme.ShadowText(dl, pos, color ?? Theme.Theme.Text, text, S);
        ImGui.Dummy(size);
    }

    public static void Dim(string text) => Text(text, Theme.Theme.TextDim);

    public static void Small(string text, Vector4? color = null)
    {
        using var _ = F.Small.Push();
        Text(text, color ?? Theme.Theme.TextDim);
    }

    public static void Wrapped(string text, Vector4? color = null, float width = 0f)
    {
        var w = width > 0 ? width : ContentWidth;
        ImGui.PushStyleColor(ImGuiCol.Text, color ?? Theme.Theme.TextDim);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + w);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    public static void Icon(char icon, Vector4? color = null, bool large = false)
    {
        using var _ = (large ? F.IconsLarge : F.Icons).Push();
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var size = ImGui.CalcTextSize(icon.ToString());
        Theme.Theme.ShadowText(dl, pos, color ?? Theme.Theme.TextDim, icon.ToString(), S);
        ImGui.Dummy(size);
    }

    public static Vector2 IconSize(char icon, bool large = false)
    {
        using var _ = (large ? F.IconsLarge : F.Icons).Push();
        return ImGui.CalcTextSize(icon.ToString());
    }

    public static void Tooltip(string text)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 5) * S);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Theme.Theme.Rail);
        ImGui.PushStyleColor(ImGuiCol.Border, Theme.Theme.BevelLight);
        ImGui.BeginTooltip();
        using (F.Small.Push()) ImGui.TextUnformatted(text);
        ImGui.EndTooltip();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar();
    }

    // ---------------------------------------------------------------- columns & groups

    private sealed class GroupFrame
    {
        public Vector2 Outer;
        public float Width;
        public string Title = "";
    }

    private static readonly Stack<GroupFrame> Groups = new();
    private static float columnWidth;
    private static Vector2 columnStart;
    private static float[] columnBottoms = Array.Empty<float>();
    private static int column;

    /// <summary>Usable width inside the current group (or column, or window).</summary>
    public static float ContentWidth => Groups.Count > 0
        ? Groups.Peek().Width - 2 * Theme.Theme.GroupPadding * S
        : columnWidth > 0 ? columnWidth : ImGui.GetContentRegionAvail().X;

    /// <summary>Starts an N-column layout for group boxes. Call <see cref="Column"/> to switch, <see cref="EndColumns"/> to finish.</summary>
    public static void BeginColumns(int count)
    {
        columnStart = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail().X;
        var gap = Theme.Theme.GroupGap * S;
        columnWidth = (avail - gap * (count - 1)) / count;
        columnBottoms = new float[count];
        for (var i = 0; i < count; i++) columnBottoms[i] = columnStart.Y;
        column = 0;
    }

    public static void Column(int index)
    {
        // Remember where the previous column ended.
        columnBottoms[column] = MathF.Max(columnBottoms[column], ImGui.GetCursorScreenPos().Y);
        column = index;
        var gap = Theme.Theme.GroupGap * S;
        ImGui.SetCursorScreenPos(new Vector2(columnStart.X + index * (columnWidth + gap), columnBottoms[index]));
    }

    public static void EndColumns()
    {
        columnBottoms[column] = MathF.Max(columnBottoms[column], ImGui.GetCursorScreenPos().Y);
        var maxY = columnStart.Y;
        foreach (var b in columnBottoms) maxY = MathF.Max(maxY, b);
        ImGui.SetCursorScreenPos(new Vector2(columnStart.X, maxY));
        ImGui.Dummy(new Vector2(columnWidth, 0));
        columnWidth = 0;
    }

    /// <summary>
    /// Group box: a 1 px outline with the title cut into the top edge, a slightly lifted panel fill,
    /// and padded content. Height follows content. Pair with <see cref="EndGroup"/>.
    /// </summary>
    public static void BeginGroup(string title, float width = 0f)
    {
        var w = width > 0 ? width : (columnWidth > 0 ? columnWidth : ImGui.GetContentRegionAvail().X);
        var outer = ImGui.GetCursorScreenPos();
        // After a previous group ImGui's next line starts at the window edge; inside a column layout
        // the group must stay on its column's X.
        if (columnWidth > 0) outer.X = columnStart.X + column * (columnWidth + Theme.Theme.GroupGap * S);
        var pad = Theme.Theme.GroupPadding * S;
        var titleH = ImGui.GetTextLineHeight();

        var dl = ImGui.GetWindowDrawList();
        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);
        ImGui.PushID(title);
        ImGui.SetCursorScreenPos(outer + new Vector2(pad, titleH * 0.5f + pad));
        ImGui.BeginGroup();
        ImGui.Dummy(new Vector2(w - pad * 2, 0));
        Groups.Push(new GroupFrame { Outer = outer, Width = w, Title = title });
    }

    public static void EndGroup()
    {
        var g = Groups.Pop();
        var pad = Theme.Theme.GroupPadding * S;
        ImGui.EndGroup();
        ImGui.PopID();
        var innerMax = ImGui.GetItemRectMax();
        var titleH = ImGui.GetTextLineHeight();
        var top = g.Outer.Y + titleH * 0.5f;
        var max = new Vector2(g.Outer.X + g.Width, innerMax.Y + pad);
        var min = new Vector2(g.Outer.X, top);

        var dl = ImGui.GetWindowDrawList();
        dl.ChannelsSetCurrent(0);
        // Body and outline
        dl.AddRectFilled(min, max, Theme.Theme.U32(Theme.Theme.Panel));
        dl.AddRect(min, max, Theme.Theme.U32(Theme.Theme.BevelMid));
        dl.AddRect(min + new Vector2(1 * S), max - new Vector2(1 * S), Theme.Theme.U32(Theme.Theme.WithAlpha(Theme.Theme.BevelLight, 0.35f)));
        // Title cut into the top edge
        float titleW;
        using (F.Bold.Push()) titleW = ImGui.CalcTextSize(g.Title).X;
        var tx = min.X + 10 * S;
        dl.AddRectFilled(new Vector2(tx - 4 * S, g.Outer.Y), new Vector2(tx + titleW + 4 * S, top + 2 * S), Theme.Theme.U32(Theme.Theme.Surface));
        using (F.Bold.Push()) Theme.Theme.ShadowText(dl, new Vector2(tx, g.Outer.Y), Theme.Theme.TextBright, g.Title, S);
        dl.ChannelsMerge();

        ImGui.SetCursorScreenPos(new Vector2(g.Outer.X, max.Y));
        ImGui.Dummy(new Vector2(g.Width, Theme.Theme.GroupGap * S));
    }

    // ---------------------------------------------------------------- rows

    /// <summary>Checkbox row: small bevelled square that fills with the accent, label to the right, optional hint on hover.</summary>
    public static bool Check(string label, ref bool value, string? hint = null)
    {
        var size = Theme.Theme.CheckboxSize * S;
        var rowH = Theme.Theme.RowHeight * S;
        var pos = ImGui.GetCursorScreenPos();
        var width = ContentWidth;
        var id = ImGui.GetID(label);
        var clicked = ImGui.InvisibleButton(label, new Vector2(width, rowH));
        if (clicked) value = !value;
        var hovered = ImGui.IsItemHovered();
        if (hovered && hint != null) Tooltip(hint);

        var dl = ImGui.GetWindowDrawList();
        var t = Ease(id, value ? 1f : 0f, 14f);
        var boxMin = pos + new Vector2(0, (rowH - size) / 2);
        var boxMax = boxMin + new Vector2(size);
        // Well
        dl.AddRectFilled(boxMin, boxMax, Theme.Theme.U32(Theme.Theme.InsetShadow));
        dl.AddRect(boxMin + new Vector2(1 * S), boxMax - new Vector2(1 * S), Theme.Theme.U32(Theme.Theme.InsetLine));
        // Fill (vertical gradient accent → dark accent)
        if (t > 0.01f)
        {
            var a = boxMin + new Vector2(2 * S);
            var b = boxMax - new Vector2(2 * S);
            var top = Theme.Theme.WithAlpha(Theme.Theme.AccentColor, t);
            var bottom = Theme.Theme.WithAlpha(Theme.Theme.AccentDark, t);
            dl.AddRectFilledMultiColor(a, b, Theme.Theme.U32(top), Theme.Theme.U32(top), Theme.Theme.U32(bottom), Theme.Theme.U32(bottom));
            if (Theme.Theme.Effects) Theme.Theme.DrawGlow(dl, a, b, Theme.Theme.AccentColor, 5 * S, t * 0.6f);
        }
        var textCol = value ? Theme.Theme.TextBright : hovered ? Theme.Theme.TextBright : Theme.Theme.Text;
        var ty = pos.Y + (rowH - ImGui.GetTextLineHeight()) / 2;
        Theme.Theme.ShadowText(dl, new Vector2(boxMax.X + 8 * S, ty), textCol, label.Split("##")[0], S);
        return clicked;
    }

    /// <summary>Label on the left, a control drawn by <paramref name="control"/> right-aligned at <paramref name="controlWidth"/>.</summary>
    public static void Row(string label, float controlWidth, Action control, string? hint = null)
    {
        var rowH = Theme.Theme.RowHeight * S;
        var pos = ImGui.GetCursorScreenPos();
        var width = ContentWidth;
        var dl = ImGui.GetWindowDrawList();
        var ty = pos.Y + (rowH - ImGui.GetTextLineHeight()) / 2;
        Theme.Theme.ShadowText(dl, new Vector2(pos.X, ty), Theme.Theme.Text, label, S);
        if (hint != null)
        {
            ImGui.SetCursorScreenPos(pos);
            ImGui.InvisibleButton("##hint" + label, new Vector2(MathF.Max(1, width - controlWidth - 8 * S), rowH));
            if (ImGui.IsItemHovered()) Tooltip(hint);
        }
        ImGui.SetCursorScreenPos(new Vector2(pos.X + width - controlWidth, pos.Y + (rowH - Theme.Theme.ControlHeight * S) / 2));
        ImGui.PushID(label);
        control();
        ImGui.PopID();
        ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + rowH));
    }

    /// <summary>Read-only value row.</summary>
    public static void ValueRow(string label, string value, Vector4? color = null, string? hint = null)
    {
        var w = ImGui.CalcTextSize(value).X;
        Row(label, w, () =>
        {
            var dl = ImGui.GetWindowDrawList();
            var p = ImGui.GetCursorScreenPos();
            Theme.Theme.ShadowText(dl, new Vector2(p.X, p.Y + (Theme.Theme.ControlHeight * S - ImGui.GetTextLineHeight()) / 2), color ?? Theme.Theme.TextDim, value, S);
            ImGui.Dummy(new Vector2(w, Theme.Theme.ControlHeight * S));
        }, hint);
    }

    /// <summary>
    /// Slider: label above-left, thin inset track, accent fill with the value label riding the fill end.
    /// Returns true when the value changed.
    /// </summary>
    public static bool Slider(string label, ref int value, int min, int max, string format = "{0}", string? hint = null, float width = 0f)
    {
        var f = (float)value;
        var changed = SliderCore(label, ref f, min, max, 1f, v => string.Format(format, (int)MathF.Round(v)), hint, width);
        if (changed) value = (int)MathF.Round(f);
        return changed;
    }

    public static bool SliderF(string label, ref float value, float min, float max, string format = "{0:0.00}", string? hint = null, float width = 0f, float step = 0f)
        => SliderCore(label, ref value, min, max, step, v => string.Format(format, v), hint, width);

    private static bool SliderCore(string label, ref float value, float min, float max, float step, Func<float, string> fmt, string? hint, float width)
    {
        var w = width > 0 ? width : ContentWidth;
        var trackH = 4 * S;
        var labelH = ImGui.GetTextLineHeight();
        var totalH = labelH + 4 * S + trackH + 6 * S + labelH * 0.9f;
        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();

        Theme.Theme.ShadowText(dl, pos, Theme.Theme.Text, label.Split("##")[0], S);

        var trackMin = pos + new Vector2(0, labelH + 4 * S);
        var trackMax = trackMin + new Vector2(w, trackH);
        ImGui.SetCursorScreenPos(trackMin - new Vector2(0, 4 * S));
        ImGui.InvisibleButton(label, new Vector2(w, trackH + 8 * S + labelH * 0.9f));
        var active = ImGui.IsItemActive();
        var hovered = ImGui.IsItemHovered();
        if (hovered && hint != null && !active) Tooltip(hint);

        var changed = false;
        if (active)
        {
            var mx = ImGui.GetIO().MousePos.X;
            var t = Math.Clamp((mx - trackMin.X) / w, 0f, 1f);
            var nv = min + t * (max - min);
            if (step > 0) nv = MathF.Round(nv / step) * step;
            if (MathF.Abs(nv - value) > 1e-4f) { value = nv; changed = true; }
        }

        var frac = max > min ? Math.Clamp((value - min) / (max - min), 0f, 1f) : 0f;
        var id = ImGui.GetID(label + "##fill");
        var shown = Ease(id, frac, 16f);

        Theme.Theme.DrawInset(dl, trackMin - new Vector2(1 * S), trackMax + new Vector2(1 * S), S);
        var fillMax = new Vector2(trackMin.X + w * shown, trackMax.Y);
        if (shown > 0.001f)
        {
            dl.AddRectFilledMultiColor(trackMin, fillMax,
                Theme.Theme.U32(Theme.Theme.AccentColor), Theme.Theme.U32(Theme.Theme.AccentColor),
                Theme.Theme.U32(Theme.Theme.AccentDark), Theme.Theme.U32(Theme.Theme.AccentDark));
            if (Theme.Theme.Effects && (active || hovered)) Theme.Theme.DrawGlow(dl, trackMin, fillMax, Theme.Theme.AccentColor, 4 * S, 0.7f);
        }
        // Value label centred on the fill end, clamped to the track
        var text = fmt(value);
        using (F.Small.Push())
        {
            var ts = ImGui.CalcTextSize(text);
            var cx = Math.Clamp(fillMax.X - ts.X / 2, trackMin.X, trackMax.X - ts.X);
            Theme.Theme.ShadowText(dl, new Vector2(cx, trackMax.Y + 4 * S), active ? Theme.Theme.TextBright : Theme.Theme.TextDim, text, S);
        }
        ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + totalH));
        ImGui.Dummy(new Vector2(w, 2 * S));
        return changed;
    }

    /// <summary>Inset combo box. Returns true when the selection changed.</summary>
    public static bool Combo(string id, ref int selected, string[] items, float width = 0f)
    {
        var w = width > 0 ? width : 120 * S;
        var h = Theme.Theme.ControlHeight * S;
        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6 * S, (h - ImGui.GetTextLineHeight()) / 2));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Theme.TextBright);
        ImGui.SetNextItemWidth(w);
        Theme.Theme.DrawInset(dl, pos, pos + new Vector2(w, h), S);
        var changed = false;
        if (ImGui.BeginCombo(id, items[Math.Clamp(selected, 0, items.Length - 1)], ImGuiComboFlags.NoArrowButton))
        {
            for (var i = 0; i < items.Length; i++)
            {
                var sel = i == selected;
                if (sel) ImGui.PushStyleColor(ImGuiCol.Text, Theme.Theme.AccentText);
                if (ImGui.Selectable(items[i], sel)) { selected = i; changed = true; }
                if (sel) ImGui.PopStyleColor();
            }
            ImGui.EndCombo();
        }
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar();
        // Chevron
        using (F.Icons.Push())
        {
            var ch = Theme.Icon.ChevronDown.ToString();
            var cs = ImGui.CalcTextSize(ch);
            dl.AddText(new Vector2(pos.X + w - cs.X - 4 * S, pos.Y + (h - cs.Y) / 2), Theme.Theme.U32(Theme.Theme.TextDim), ch);
        }
        return changed;
    }

    /// <summary>Inset number field.</summary>
    public static bool IntField(string id, ref int value, float width = 0f, int step = 0)
    {
        var w = width > 0 ? width : 70 * S;
        var h = Theme.Theme.ControlHeight * S;
        var pos = ImGui.GetCursorScreenPos();
        Theme.Theme.DrawInset(ImGui.GetWindowDrawList(), pos, pos + new Vector2(w, h), S);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6 * S, (h - ImGui.GetTextLineHeight()) / 2));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Theme.TextBright);
        ImGui.SetNextItemWidth(w);
        var changed = ImGui.InputInt(id, ref value, step, step * 10);
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar();
        return changed;
    }

    // ---------------------------------------------------------------- buttons

    public enum ButtonKind { Normal, Accent, Danger }

    /// <summary>Bevelled button with optional leading icon. Presses in while held.</summary>
    public static bool Button(string label, ButtonKind kind = ButtonKind.Normal, Vector2? size = null, char? icon = null, string? hint = null)
    {
        var text = label.Split("##")[0];
        var pad = new Vector2(10, 0) * S;
        var textSz = ImGui.CalcTextSize(text);
        var iconSz = icon is { } ic ? IconSize(ic) : Vector2.Zero;
        var w = textSz.X + (icon != null ? iconSz.X + (text.Length > 0 ? 5 * S : 0) : 0) + pad.X * 2;
        var sz = size ?? new Vector2(w, Theme.Theme.ControlHeight * S + 2 * S);
        var pos = ImGui.GetCursorScreenPos();
        var pressed = ImGui.InvisibleButton(label, sz);
        var hovered = ImGui.IsItemHovered();
        var held = ImGui.IsItemActive();
        var dl = ImGui.GetWindowDrawList();

        var fill = kind switch
        {
            ButtonKind.Accent => held ? Theme.Theme.AccentDark : hovered ? Theme.Theme.Lerp(Theme.Theme.AccentColor, Vector4.One, 0.08f) : Theme.Theme.AccentColor,
            ButtonKind.Danger => held ? Theme.Theme.Scale(Theme.Theme.Danger, 0.7f) : hovered ? Theme.Theme.Danger : Theme.Theme.Scale(Theme.Theme.Danger, 0.85f),
            _ => held ? Theme.Theme.Rgb(0x1A, 0x1A, 0x1D) : hovered ? Theme.Theme.Rgb(0x2A, 0x2A, 0x2E) : Theme.Theme.BevelMid,
        };
        Theme.Theme.DrawRaised(dl, pos, pos + sz, S, fill, held);
        if (kind == ButtonKind.Accent && Theme.Theme.Effects && hovered) Theme.Theme.DrawGlow(dl, pos, pos + sz, Theme.Theme.AccentColor, 6 * S, 0.8f);

        var textCol = kind == ButtonKind.Accent ? Theme.Theme.Rgb(0x0B, 0x12, 0x02) : Theme.Theme.TextBright;
        var contentW = w - pad.X * 2;
        var x = pos.X + (sz.X - contentW) / 2 + (held ? 1 * S : 0);
        var yOff = held ? 1 * S : 0;
        if (icon is { } ic2)
        {
            using (F.Icons.Push())
                dl.AddText(new Vector2(x, pos.Y + (sz.Y - iconSz.Y) / 2 + yOff), Theme.Theme.U32(textCol), ic2.ToString());
            x += iconSz.X + (text.Length > 0 ? 5 * S : 0);
        }
        if (text.Length > 0)
        {
            if (kind == ButtonKind.Accent) dl.AddText(new Vector2(x, pos.Y + (sz.Y - textSz.Y) / 2 + yOff), Theme.Theme.U32(textCol), text);
            else Theme.Theme.ShadowText(dl, new Vector2(x, pos.Y + (sz.Y - textSz.Y) / 2 + yOff), textCol, text, S);
        }
        if (hovered && hint != null) Tooltip(hint);
        return pressed;
    }

    /// <summary>Square icon button for transport controls; accent variant lights up.</summary>
    public static bool IconButton(string id, char icon, float size, ButtonKind kind = ButtonKind.Normal, string? hint = null)
        => Button(id, kind, new Vector2(size * S), icon, hint);

    /// <summary>Segmented selector: a row of bevelled cells, the selected one filled with the accent.</summary>
    public static bool Segmented(string id, string[] labels, ref int selected, float width = 0f)
    {
        var w = width > 0 ? width : ContentWidth;
        var h = Theme.Theme.ControlHeight * S + 2 * S;
        var cellW = w / labels.Length;
        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        var changed = false;
        ImGui.PushID(id);
        for (var i = 0; i < labels.Length; i++)
        {
            var p = pos + new Vector2(cellW * i, 0);
            ImGui.SetCursorScreenPos(p);
            if (ImGui.InvisibleButton(i.ToString(), new Vector2(cellW, h)) && selected != i) { selected = i; changed = true; }
            var hovered = ImGui.IsItemHovered();
            var sel = i == selected;
            var fill = sel ? Theme.Theme.AccentDeep : hovered ? Theme.Theme.Rgb(0x24, 0x24, 0x28) : Theme.Theme.Rgb(0x1B, 0x1B, 0x1E);
            Theme.Theme.DrawRaised(dl, p, p + new Vector2(cellW + 1 * S, h), S, fill, sel);
            if (sel)
            {
                dl.AddRectFilled(new Vector2(p.X + 1 * S, p.Y + h - 3 * S), new Vector2(p.X + cellW, p.Y + h - 1 * S), Theme.Theme.U32(Theme.Theme.AccentColor));
                if (Theme.Theme.Effects) Theme.Theme.DrawGlow(dl, new Vector2(p.X + 1 * S, p.Y + h - 3 * S), new Vector2(p.X + cellW, p.Y + h - 1 * S), Theme.Theme.AccentColor, 4 * S, 0.5f);
            }
            var ts = ImGui.CalcTextSize(labels[i]);
            Theme.Theme.ShadowText(dl, p + (new Vector2(cellW, h) - ts) / 2, sel ? Theme.Theme.AccentText : hovered ? Theme.Theme.TextBright : Theme.Theme.Text, labels[i], S);
        }
        ImGui.PopID();
        ImGui.SetCursorScreenPos(pos + new Vector2(0, h));
        ImGui.Dummy(new Vector2(w, 2 * S));
        return changed;
    }

    // ---------------------------------------------------------------- rail

    /// <summary>One icon-rail tab. Selected: accent icon, glow, 2 px bar on the left.</summary>
    public static bool RailTab(string id, char icon, string label, bool selected, float width)
    {
        var h = 44 * S;
        var pos = ImGui.GetCursorScreenPos();
        var pressed = ImGui.InvisibleButton(id, new Vector2(width, h));
        var hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        var t = Ease(ImGui.GetID(id), selected ? 1f : hovered ? 0.35f : 0f, 12f);

        if (t > 0.01f)
        {
            dl.AddRectFilled(pos, pos + new Vector2(width, h), Theme.Theme.U32(Theme.Theme.WithAlpha(Theme.Theme.AccentColor, 0.06f * t)));
            dl.AddRectFilled(pos, pos + new Vector2(2 * S, h), Theme.Theme.U32(Theme.Theme.WithAlpha(Theme.Theme.AccentColor, t)));
        }
        var iconSz = IconSize(icon, true);
        var iconPos = pos + new Vector2((width - iconSz.X) / 2, 7 * S);
        var col = Theme.Theme.Lerp(Theme.Theme.TextDim, Theme.Theme.AccentColor, t);
        if (selected && Theme.Theme.Effects)
            Theme.Theme.DrawGlow(dl, iconPos + new Vector2(2, 2) * S, iconPos + iconSz - new Vector2(2, 2) * S, Theme.Theme.AccentColor, 8 * S, 0.5f + 0.5f * Breathe());
        using (F.IconsLarge.Push()) Theme.Theme.ShadowText(dl, iconPos, col, icon.ToString(), S);
        using (F.Tiny.Push())
        {
            var ts = ImGui.CalcTextSize(label);
            Theme.Theme.ShadowText(dl, pos + new Vector2((width - ts.X) / 2, h - ts.Y - 4 * S), Theme.Theme.Lerp(Theme.Theme.TextFaint, Theme.Theme.TextBright, t), label, S);
        }
        return pressed;
    }

    // ---------------------------------------------------------------- keycaps

    /// <summary>
    /// The word in progress as keycaps. Sent keys are pressed in and lit with the accent; the next key
    /// has a faint glow; the rest sit raised and dim. Wraps to the available width.
    /// </summary>
    public static void Keycaps(string word, int sent, float capSize = 22f, float width = 0f)
    {
        if (word.Length == 0) return;
        var w = width > 0 ? width : ContentWidth;
        var cap = capSize * S;
        var gap = 3 * S;
        var perRow = Math.Max(1, (int)((w + gap) / (cap + gap)));
        var rows = (word.Length + perRow - 1) / perRow;
        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();

        for (var i = 0; i < word.Length; i++)
        {
            var r = i / perRow;
            var c = i % perRow;
            var p = pos + new Vector2(c * (cap + gap), r * (cap + gap));
            var id = ImGui.GetID("cap" + i + word);
            var down = i < sent;
            var next = i == sent;
            var press = Ease(id, down ? 1f : 0f, 18f);
            var ch = word[i] == ' ' ? "␣" : word[i].ToString().ToUpperInvariant();

            var fill = Theme.Theme.Lerp(Theme.Theme.Rgb(0x2A, 0x2A, 0x2E), Theme.Theme.AccentDeep, press);
            var offset = new Vector2(0, 2 * S * press);
            if (press < 0.99f)
                dl.AddRectFilled(p + new Vector2(0, 2 * S), p + new Vector2(cap, cap + 2 * S), Theme.Theme.U32(Theme.Theme.Frame)); // cap side
            Theme.Theme.DrawRaised(dl, p + offset, p + new Vector2(cap, cap) + offset, S, fill, press > 0.5f);
            if (down && Theme.Theme.Effects)
                Theme.Theme.DrawGlow(dl, p + offset + new Vector2(2 * S), p + new Vector2(cap, cap) + offset - new Vector2(2 * S), Theme.Theme.AccentColor, 5 * S, 0.6f * press);
            if (next && Theme.Theme.Effects)
                dl.AddRect(p + new Vector2(1 * S), p + new Vector2(cap, cap) - new Vector2(1 * S), Theme.Theme.U32(Theme.Theme.WithAlpha(Theme.Theme.AccentColor, 0.35f + 0.35f * Breathe(1.2f))));
            var col = down ? Theme.Theme.AccentText : next ? Theme.Theme.TextBright : Theme.Theme.TextDim;
            using (F.Bold.Push())
            {
                var ts = ImGui.CalcTextSize(ch);
                Theme.Theme.ShadowText(dl, p + offset + (new Vector2(cap, cap) - ts) / 2, col, ch, S);
            }
        }
        ImGui.Dummy(new Vector2(w, rows * (cap + gap) + 2 * S));
    }

    // ---------------------------------------------------------------- misc

    public static void StatusDot(Vector4 color, string label, bool pulse = false)
    {
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var r = 3.5f * S;
        var lh = ImGui.GetTextLineHeight();
        var c = pos + new Vector2(r + 1 * S, lh / 2);
        if (pulse && Theme.Theme.Effects) dl.AddCircleFilled(c, r + 3 * S * Breathe(1.6f), Theme.Theme.U32(Theme.Theme.WithAlpha(color, 0.25f)));
        dl.AddCircleFilled(c, r, Theme.Theme.U32(color));
        ImGui.Dummy(new Vector2(r * 2 + 2 * S, lh));
        ImGui.SameLine(0, 6 * S);
        Text(label);
    }

    /// <summary>Big number with a small label under it.</summary>
    public static void Stat(string label, string value, Vector4? color = null, float width = 0f)
    {
        var w = width > 0 ? width : ContentWidth;
        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        float vh;
        using (F.Display.Push())
        {
            vh = ImGui.GetTextLineHeight();
            Theme.Theme.ShadowText(dl, pos, color ?? Theme.Theme.TextBright, value, S);
        }
        using (F.Small.Push())
            Theme.Theme.ShadowText(dl, pos + new Vector2(0, vh + 1 * S), Theme.Theme.TextDim, label, S);
        ImGui.Dummy(new Vector2(w, vh + ImGui.GetTextLineHeight() + 4 * S));
    }

    /// <summary>Inline notice strip with a coloured edge.</summary>
    public static void Notice(char icon, Vector4 color, string text)
    {
        var w = ContentWidth;
        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        ImGui.SetCursorScreenPos(pos + new Vector2(10 * S, 6 * S));
        ImGui.BeginGroup();
        Icon(icon, color);
        ImGui.SameLine(0, 6 * S);
        Wrapped(text, Theme.Theme.Text, w - 30 * S);
        ImGui.EndGroup();
        var max = new Vector2(pos.X + w, ImGui.GetItemRectMax().Y + 6 * S);
        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(0);
        dl.AddRectFilled(pos, max, Theme.Theme.U32(Theme.Theme.WithAlpha(color, 0.08f)));
        dl.AddRectFilled(pos, new Vector2(pos.X + 2 * S, max.Y), Theme.Theme.U32(color));
        dl.ChannelsMerge();
        ImGui.SetCursorScreenPos(new Vector2(pos.X, max.Y));
        ImGui.Dummy(new Vector2(w, 6 * S));
    }

    public static string HotkeyLabel(HotkeyBinding b)
    {
        if (!b.Enabled || b.Key == 0) return "off";
        var s = "";
        if (b.Ctrl) s += "ctrl+";
        if (b.Alt) s += "alt+";
        if (b.Shift) s += "shift+";
        return s + KeyName(b.Key).ToLowerInvariant();
    }

    public static string KeyName(int vk)
    {
        if (vk >= 0x30 && vk <= 0x5A) return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
        if (vk >= 0x60 && vk <= 0x69) return "Num" + (vk - 0x60);
        return vk switch
        {
            0x20 => "Space", 0x0D => "Enter", 0x1B => "Esc", 0x09 => "Tab", 0x08 => "Backspace",
            0x2D => "Insert", 0x2E => "Delete", 0x24 => "Home", 0x23 => "End", 0x21 => "PgUp", 0x22 => "PgDn",
            0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
            0xBC => ",", 0xBE => ".", 0xBF => "/", 0xC0 => "`", 0xBD => "-", 0xBB => "=",
            0xDB => "[", 0xDD => "]", 0xDC => "\\", 0xBA => ";", 0xDE => "'",
            _ => $"VK{vk:X2}",
        };
    }
}
