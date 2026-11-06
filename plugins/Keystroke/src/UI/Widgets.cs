using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Keystroke.UI.Theme;

namespace Keystroke.UI;

/// <summary>
/// macOS-flavoured ImGui primitives: grouped cards, settings rows, toggles, segmented
/// controls, pill buttons. Everything is drawn through the draw list so it looks the same
/// regardless of the host style; only the Keystroke theme is assumed to be pushed.
/// </summary>
internal static class Widgets
{
    private static readonly Dictionary<uint, float> ToggleAnim = new();
    private static Fonts? fonts;

    public static float Scale => ImGuiHelpers.GlobalScale;

    public static void Init(Fonts f) => fonts = f;

    public static Fonts F => fonts ?? throw new InvalidOperationException("Widgets.Init not called");

    // ---------------------------------------------------------------- text

    public static void Text(string text, Vector4? color = null)
    {
        if (color is { } c) ImGui.TextColored(c, text);
        else ImGui.TextUnformatted(text);
    }

    public static void Secondary(string text) => ImGui.TextColored(Theme.Theme.SecondaryLabel, text);

    public static void Tertiary(string text) => ImGui.TextColored(Theme.Theme.TertiaryLabel, text);

    public static void Caption(string text, Vector4? color = null)
    {
        using var _ = F.Caption.Push();
        Text(text, color ?? Theme.Theme.SecondaryLabel);
    }

    public static void Icon(char icon, Vector4? color = null, bool large = false)
    {
        using var _ = (large ? F.IconsLarge : F.Icons).Push();
        Text(icon.ToString(), color);
    }

    /// <summary>Icon followed by text on the same baseline.</summary>
    public static void IconText(char icon, string text, Vector4? iconColor = null, Vector4? textColor = null)
    {
        Icon(icon, iconColor);
        ImGui.SameLine(0, 6 * Scale);
        Text(text, textColor);
    }

    public static Vector2 IconSize(char icon, bool large = false)
    {
        using var _ = (large ? F.IconsLarge : F.Icons).Push();
        return ImGui.CalcTextSize(icon.ToString());
    }

    /// <summary>Page title (Title font) with optional subtitle in secondary colour.</summary>
    public static void PageHeader(string title, string? subtitle = null)
    {
        using (F.Title.Push()) ImGui.TextUnformatted(title);
        if (subtitle != null)
        {
            ImGui.Dummy(new Vector2(0, 2 * Scale));
            Secondary(subtitle);
        }
        ImGui.Dummy(new Vector2(0, 10 * Scale));
    }

    /// <summary>Small uppercase-ish section label above a card, like macOS grouped headers.</summary>
    public static void SectionLabel(string text)
    {
        ImGui.Dummy(new Vector2(0, 4 * Scale));
        using var _ = F.Caption.Push();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 6 * Scale);
        ImGui.TextColored(Theme.Theme.SecondaryLabel, text);
        ImGui.Dummy(new Vector2(0, 2 * Scale));
    }

    // ---------------------------------------------------------------- cards

    private readonly struct CardFrame
    {
        public readonly Vector2 Outer;
        public readonly float Width;
        public readonly Vector4 Fill;
        public CardFrame(Vector2 outer, float width, Vector4 fill) { Outer = outer; Width = width; Fill = fill; }
    }

    private static readonly Stack<CardFrame> Cards = new();

    /// <summary>
    /// Begins a grouped inset card that sizes to its content. Content is drawn on draw-list channel 1
    /// and the rounded background on channel 0 once the height is known. Always pair with <see cref="EndCard"/>.
    /// </summary>
    public static bool BeginCard(string id, float width = 0f, Vector4? fill = null)
    {
        var padX = Theme.Theme.CardPadding * Scale;
        var padY = 8 * Scale;
        var outer = ImGui.GetCursorScreenPos();
        var w = width > 0 ? width : ImGui.GetContentRegionAvail().X;

        ImGui.GetWindowDrawList().ChannelsSplit(2);
        ImGui.GetWindowDrawList().ChannelsSetCurrent(1);
        ImGui.PushID(id);
        ImGui.SetCursorScreenPos(outer + new Vector2(padX, padY));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8 * Scale, 0));
        ImGui.BeginGroup();
        // Pin the group width so right-aligned rows know the card's inner width.
        ImGui.Dummy(new Vector2(w - padX * 2, 0));
        Cards.Push(new CardFrame(outer, w, fill ?? Theme.Theme.CardBg));
        return true;
    }

    public static void EndCard()
    {
        var frame = Cards.Pop();
        var padY = 8 * Scale;
        ImGui.EndGroup();
        ImGui.PopStyleVar();
        ImGui.PopID();
        var inner = ImGui.GetItemRectMax();
        var max = new Vector2(frame.Outer.X + frame.Width, inner.Y + padY);
        var dl = ImGui.GetWindowDrawList();
        dl.ChannelsSetCurrent(0);
        dl.AddRectFilled(frame.Outer, max, Theme.Theme.U32(frame.Fill), Theme.Theme.CardRounding * Scale);
        dl.ChannelsMerge();
        ImGui.SetCursorScreenPos(new Vector2(frame.Outer.X, max.Y));
        ImGui.Dummy(new Vector2(frame.Width, 6 * Scale));
    }

    /// <summary>Hairline between rows, inset from the left like a macOS grouped list.</summary>
    public static void RowSeparator()
    {
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var w = ImGui.GetContentRegionAvail().X;
        dl.AddLine(pos, pos + new Vector2(w, 0), Theme.Theme.U32(Theme.Theme.Separator), 1f);
        ImGui.Dummy(new Vector2(0, 1));
    }

    /// <summary>Height of a standard single-line framed control (combo, slider, input) in the current style.</summary>
    public static float FrameHeight => ImGui.GetFrameHeight();

    /// <summary>Usable width: the card's inner width while inside a card, otherwise the window's.</summary>
    public static float ContentWidth => Cards.Count > 0
        ? Cards.Peek().Width - 2 * Theme.Theme.CardPadding * Scale
        : ImGui.GetContentRegionAvail().X;

    /// <summary>
    /// A settings row: label (+ optional description) on the left, a control drawn by <paramref name="control"/>
    /// right-aligned and vertically centred. The control must be <paramref name="controlWidth"/> ×
    /// <paramref name="controlHeight"/> (unscaled values are not expected; pass screen sizes).
    /// </summary>
    public static void Row(string label, string? description, float controlWidth, float controlHeight, Action control, char? icon = null, Vector4? iconColor = null)
    {
        var rowH = (description == null ? Theme.Theme.RowHeight : Theme.Theme.RowHeight + 14) * Scale;
        var start = ImGui.GetCursorPos();
        // Inside a card the usable width is the card's inner width, not the window's.
        var avail = Cards.Count > 0
            ? Cards.Peek().Width - 2 * Theme.Theme.CardPadding * Scale
            : ImGui.GetContentRegionAvail().X;

        // Label block
        var textX = start.X;
        if (icon is { } ic)
        {
            ImGui.SetCursorPos(new Vector2(start.X, start.Y + (rowH - IconSize(ic).Y) / 2));
            Icon(ic, iconColor ?? Theme.Theme.SecondaryLabel);
            textX += 26 * Scale;
        }

        var labelH = ImGui.GetTextLineHeight();
        var captionH = labelH * (Fonts.CaptionSize / Fonts.BodySize);
        var blockH = description == null ? labelH : labelH + 2 * Scale + captionH;
        ImGui.SetCursorPos(new Vector2(textX, start.Y + (rowH - blockH) / 2));
        ImGui.TextUnformatted(label);
        if (description != null)
        {
            ImGui.SetCursorPos(new Vector2(textX, start.Y + (rowH - blockH) / 2 + labelH + 2 * Scale));
            using var _ = F.Caption.Push();
            ImGui.PushTextWrapPos(start.X + avail - controlWidth - 12 * Scale);
            ImGui.TextColored(Theme.Theme.SecondaryLabel, description);
            ImGui.PopTextWrapPos();
        }

        // Control, right-aligned and centred on the row
        ImGui.SetCursorPos(new Vector2(start.X + avail - controlWidth, start.Y + (rowH - controlHeight) / 2));
        ImGui.PushID(label);
        control();
        ImGui.PopID();

        ImGui.SetCursorPos(new Vector2(start.X, start.Y + rowH));
    }

    /// <summary>Row whose control is a toggle bound to <paramref name="value"/>. Returns true when changed.</summary>
    public static bool ToggleRow(string label, ref bool value, string? description = null, char? icon = null, Vector4? iconColor = null)
    {
        var changed = false;
        var v = value;
        var dims = ToggleDims();
        Row(label, description, dims.X, dims.Y, () => { if (Toggle("##toggle", ref v)) changed = true; }, icon, iconColor);
        value = v;
        return changed;
    }

    /// <summary>Row with a right-aligned value text (read-only display).</summary>
    public static void ValueRow(string label, string value, Vector4? valueColor = null, string? description = null, char? icon = null)
    {
        var w = ImGui.CalcTextSize(value).X;
        Row(label, description, w, ImGui.GetTextLineHeight(), () => Text(value, valueColor ?? Theme.Theme.SecondaryLabel), icon);
    }

    // ---------------------------------------------------------------- toggle

    public static Vector2 ToggleDims() => new(40 * Scale, 24 * Scale);

    /// <summary>macOS-style switch. Animates between states.</summary>
    public static bool Toggle(string id, ref bool value)
    {
        var size = ToggleDims();
        var pos = ImGui.GetCursorScreenPos();
        var imguiId = ImGui.GetID(id);
        var clicked = ImGui.InvisibleButton(id, size);
        if (clicked) value = !value;
        var hovered = ImGui.IsItemHovered();

        ToggleAnim.TryGetValue(imguiId, out var t);
        var target = value ? 1f : 0f;
        t = MoveToward(t, target, ImGui.GetIO().DeltaTime * 8f);
        ToggleAnim[imguiId] = t;

        var dl = ImGui.GetWindowDrawList();
        var radius = size.Y / 2;
        var off = Theme.Theme.ControlBgHover;
        var on = Theme.Theme.Green;
        var track = Theme.Theme.Lerp(off, on, t);
        if (hovered) track = Theme.Theme.Lerp(track, Vector4.One, 0.06f);
        dl.AddRectFilled(pos, pos + size, Theme.Theme.U32(track), radius);

        var knobR = radius - 2 * Scale;
        var knobX = pos.X + radius + (size.X - size.Y) * t;
        dl.AddCircleFilled(new Vector2(knobX, pos.Y + radius), knobR + 0.5f, Theme.Theme.U32(new Vector4(0, 0, 0, 0.18f)));
        dl.AddCircleFilled(new Vector2(knobX, pos.Y + radius), knobR, Theme.Theme.U32(Vector4.One));
        return clicked;
    }

    private static float MoveToward(float v, float target, float step)
        => Math.Abs(target - v) <= step ? target : v + MathF.Sign(target - v) * step;

    // ---------------------------------------------------------------- buttons

    public enum ButtonStyle { Default, Primary, Destructive, Quiet }

    /// <summary>Rounded button; Primary is filled with the accent colour.</summary>
    public static bool Button(string label, ButtonStyle style = ButtonStyle.Default, Vector2? size = null, char? icon = null)
    {
        var colors = 0;
        switch (style)
        {
            case ButtonStyle.Primary:
                ImGui.PushStyleColor(ImGuiCol.Button, Theme.Theme.Accent);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.Theme.AccentHover);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, Theme.Theme.AccentActive);
                ImGui.PushStyleColor(ImGuiCol.Text, Vector4.One);
                colors = 4;
                break;
            case ButtonStyle.Destructive:
                ImGui.PushStyleColor(ImGuiCol.Button, Theme.Theme.WithAlpha(Theme.Theme.Red, 0.18f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.Theme.WithAlpha(Theme.Theme.Red, 0.28f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, Theme.Theme.WithAlpha(Theme.Theme.Red, 0.40f));
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.Theme.Red);
                colors = 4;
                break;
            case ButtonStyle.Quiet:
                ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.Theme.ControlBg);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, Theme.Theme.ControlBgHover);
                colors = 3;
                break;
        }

        bool pressed;
        if (icon is { } ic)
        {
            var text = label.Split("##")[0];
            var iconSz = IconSize(ic);
            var textSz = ImGui.CalcTextSize(text);
            var pad = ImGui.GetStyle().FramePadding;
            var w = iconSz.X + (text.Length > 0 ? 6 * Scale + textSz.X : 0) + pad.X * 2;
            var h = MathF.Max(iconSz.Y, textSz.Y) + pad.Y * 2;
            var sz = size ?? new Vector2(w, h);
            var pos = ImGui.GetCursorScreenPos();
            pressed = ImGui.Button("##" + label, sz);
            var dl = ImGui.GetWindowDrawList();
            var textCol = Theme.Theme.U32(style == ButtonStyle.Primary ? Vector4.One : style == ButtonStyle.Destructive ? Theme.Theme.Red : Theme.Theme.Label);
            var x = pos.X + (sz.X - (w - pad.X * 2)) / 2;
            using (F.Icons.Push())
                dl.AddText(new Vector2(x, pos.Y + (sz.Y - iconSz.Y) / 2), textCol, ic.ToString());
            if (text.Length > 0)
                dl.AddText(new Vector2(x + iconSz.X + 6 * Scale, pos.Y + (sz.Y - textSz.Y) / 2), textCol, text);
        }
        else
        {
            pressed = size is { } s ? ImGui.Button(label, s) : ImGui.Button(label);
        }

        if (colors > 0) ImGui.PopStyleColor(colors);
        return pressed;
    }

    /// <summary>Circular icon button (used for transport controls).</summary>
    public static bool CircleButton(string id, char icon, float diameter, Vector4 fill, Vector4 iconColor, string? tooltip = null)
    {
        var size = new Vector2(diameter * Scale);
        var pos = ImGui.GetCursorScreenPos();
        var pressed = ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var dl = ImGui.GetWindowDrawList();
        var col = fill;
        if (active) col = Theme.Theme.Lerp(col, Vector4.Zero, 0.15f);
        else if (hovered) col = Theme.Theme.Lerp(col, Vector4.One, 0.08f);
        dl.AddCircleFilled(pos + size / 2, size.X / 2, Theme.Theme.U32(col), 48);
        var sz = IconSize(icon, true);
        using (F.IconsLarge.Push())
            dl.AddText(pos + (size - sz) / 2, Theme.Theme.U32(iconColor), icon.ToString());
        if (tooltip != null && hovered) Tooltip(tooltip);
        return pressed;
    }

    public static void Tooltip(string text)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8) * Scale);
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(text);
        ImGui.EndTooltip();
        ImGui.PopStyleVar();
    }

    // ---------------------------------------------------------------- segmented control

    /// <summary>Segmented control. Returns true when the selection changed.</summary>
    public static bool Segmented(string id, string[] labels, ref int selected, float width = 0f)
    {
        var changed = false;
        var h = 26 * Scale;
        var totalW = width > 0 ? width : ImGui.GetContentRegionAvail().X;
        var segW = totalW / labels.Length;
        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + new Vector2(totalW, h), Theme.Theme.U32(Theme.Theme.ControlBg), 7 * Scale);

        ImGui.PushID(id);
        for (var i = 0; i < labels.Length; i++)
        {
            var p = pos + new Vector2(segW * i, 0);
            ImGui.SetCursorScreenPos(p);
            if (ImGui.InvisibleButton(i.ToString(), new Vector2(segW, h)) && selected != i)
            {
                selected = i;
                changed = true;
            }
            var hovered = ImGui.IsItemHovered();
            if (i == selected)
                dl.AddRectFilled(p + new Vector2(2, 2) * Scale, p + new Vector2(segW, h) - new Vector2(2, 2) * Scale,
                    Theme.Theme.U32(Theme.Theme.ControlBgActive), 6 * Scale);
            else if (hovered)
                dl.AddRectFilled(p + new Vector2(2, 2) * Scale, p + new Vector2(segW, h) - new Vector2(2, 2) * Scale,
                    Theme.Theme.U32(Theme.Theme.ControlBgHover), 6 * Scale);
            var ts = ImGui.CalcTextSize(labels[i]);
            dl.AddText(p + (new Vector2(segW, h) - ts) / 2, Theme.Theme.U32(i == selected ? Theme.Theme.Label : Theme.Theme.SecondaryLabel), labels[i]);
        }
        ImGui.PopID();
        ImGui.SetCursorScreenPos(pos + new Vector2(0, h));
        ImGui.Dummy(new Vector2(totalW, 0));
        return changed;
    }

    // ---------------------------------------------------------------- misc controls

    public static bool SliderInt(string id, ref int value, int min, int max, string format = "%d", float width = 200f)
    {
        ImGui.SetNextItemWidth(width * Scale);
        return ImGui.SliderInt(id, ref value, min, max, format, ImGuiSliderFlags.AlwaysClamp);
    }

    public static bool SliderFloat(string id, ref float value, float min, float max, string format = "%.2f", float width = 200f)
    {
        ImGui.SetNextItemWidth(width * Scale);
        return ImGui.SliderFloat(id, ref value, min, max, format, ImGuiSliderFlags.AlwaysClamp);
    }

    public static bool InputInt(string id, ref int value, int step = 1, float width = 120f)
    {
        ImGui.SetNextItemWidth(width * Scale);
        return ImGui.InputInt(id, ref value, step, step * 10);
    }

    public static bool Combo(string id, ref int selected, string[] items, float width = 180f)
    {
        ImGui.SetNextItemWidth(width * Scale);
        var changed = false;
        if (ImGui.BeginCombo(id, items[Math.Clamp(selected, 0, items.Length - 1)]))
        {
            for (var i = 0; i < items.Length; i++)
            {
                if (ImGui.Selectable(items[i], i == selected))
                {
                    selected = i;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        return changed;
    }

    /// <summary>Coloured status dot with label.</summary>
    public static void StatusDot(Vector4 color, string label)
    {
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var r = 5 * Scale;
        var lh = ImGui.GetTextLineHeight();
        dl.AddCircleFilled(pos + new Vector2(r, lh / 2), r, Theme.Theme.U32(color));
        ImGui.Dummy(new Vector2(r * 2, lh));
        ImGui.SameLine(0, 6 * Scale);
        ImGui.TextUnformatted(label);
    }

    /// <summary>Key-value stat tile used on the dashboard.</summary>
    public static void StatTile(string label, string value, Vector4? valueColor = null, float width = 0f)
    {
        var w = width > 0 ? width : ContentWidth;
        ImGui.BeginGroup();
        using (F.Caption.Push()) ImGui.TextColored(Theme.Theme.SecondaryLabel, label);
        using (F.Title.Push()) Text(value, valueColor);
        ImGui.EndGroup();
        ImGui.SameLine();
        ImGui.Dummy(new Vector2(MathF.Max(0, w - ImGui.GetItemRectSize().X), 0));
    }

    /// <summary>Inline banner (info/warning/error).</summary>
    public static void Banner(char icon, Vector4 color, string text)
    {
        if (BeginCard("##banner" + text.GetHashCode(), 0, Theme.Theme.WithAlpha(color, 0.12f)))
        {
            ImGui.Dummy(new Vector2(0, 4 * Scale));
            Icon(icon, color);
            ImGui.SameLine(0, 8 * Scale);
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - Theme.Theme.CardPadding * Scale);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            ImGui.Dummy(new Vector2(0, 4 * Scale));
            EndCard();
        }
    }

    /// <summary>Returns a display string like "⌃⇧K" for a binding.</summary>
    public static string HotkeyLabel(HotkeyBinding b)
    {
        if (!b.Enabled || b.Key == 0) return "Off";
        var s = "";
        if (b.Ctrl) s += "Ctrl+";
        if (b.Alt) s += "Alt+";
        if (b.Shift) s += "Shift+";
        return s + KeyName(b.Key);
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
