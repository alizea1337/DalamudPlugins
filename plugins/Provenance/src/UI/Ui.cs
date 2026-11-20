using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Provenance.UI.Theme;

namespace Provenance.UI;

/// <summary>
/// The widget kit. Shape rule: actions and chips are pills, cards 14, inputs and rows 8, item icons 8.
/// Motion: only easing on values that change because of the user or the run (the sidebar pill, switches,
/// bars, a page coming in). With "Reduce motion" on, movement snaps into place and only fades remain.
/// </summary>
internal static partial class Ui
{
    public const float CardRadius = 14f;
    public const float InputRadius = 8f;
    public const float IconRadius = 8f;

    public static Fonts F { get; private set; } = null!;
    /// <summary>
    /// Pixels per design unit: Dalamud's global scale times the interface size (Settings, General). It follows the
    /// fonts in use, so a new size switches text and layout in the same frame.
    /// </summary>
    public static float S => ImGuiHelpers.GlobalScale * (F?.Scale ?? 1f);

    private static readonly Dictionary<uint, float> Eased = new();
    private static readonly Stack<(Vector2 Start, float Width, Vector4 Bg, float Pad)> Cards = new();

    public static void Init(Fonts fonts) => F = fonts;

    /// <summary>The "Reduce motion" setting, set by the window each frame: movement snaps, fades stay.</summary>
    public static bool ReduceMotion { get; set; }

    /// <summary>
    /// Moves a stored value toward its target; framerate independent, settles in ~150 ms. For movement: with
    /// "Reduce motion" it jumps straight to the target.
    /// </summary>
    public static float Ease(uint id, float target, float speed = 18f)
    {
        if (!ReduceMotion) return Fade(id, target, speed);
        Eased[id] = target;
        return target;
    }

    /// <summary>Like <see cref="Ease"/>, for opacity and colour: it eases even with "Reduce motion".</summary>
    public static float Fade(uint id, float target, float speed = 18f)
    {
        if (!Eased.TryGetValue(id, out var v)) v = target;
        v += (target - v) * (1f - MathF.Exp(-speed * ImGui.GetIO().DeltaTime));
        if (MathF.Abs(target - v) < 0.001f) v = target;
        Eased[id] = v;
        return v;
    }

    // ------------------------------------------------------------------ style

    public static IDisposable PushStyle()
    {
        var s = S;
        var colors = new (ImGuiCol, Vector4)[]
        {
            (ImGuiCol.WindowBg, Palette.Window),
            (ImGuiCol.ChildBg, Vector4.Zero),
            (ImGuiCol.PopupBg, Palette.Surface),
            (ImGuiCol.Border, Palette.Line),
            (ImGuiCol.Text, Palette.Text),
            (ImGuiCol.TextDisabled, Palette.TextFaint),
            (ImGuiCol.FrameBg, Palette.Raised),
            (ImGuiCol.FrameBgHovered, Palette.RaisedHover),
            (ImGuiCol.FrameBgActive, Palette.RaisedHover),
            (ImGuiCol.Button, Palette.Raised),
            (ImGuiCol.ButtonHovered, Palette.RaisedHover),
            (ImGuiCol.ButtonActive, Palette.RaisedHover),
            (ImGuiCol.Header, Palette.Raised),
            (ImGuiCol.HeaderHovered, Palette.RaisedHover),
            (ImGuiCol.HeaderActive, Palette.RaisedHover),
            (ImGuiCol.CheckMark, Palette.Accent),
            (ImGuiCol.SliderGrab, Palette.Accent),
            (ImGuiCol.SliderGrabActive, Palette.AccentHover),
            (ImGuiCol.ScrollbarBg, Vector4.Zero),
            (ImGuiCol.ScrollbarGrab, Palette.Line),
            (ImGuiCol.ScrollbarGrabHovered, Palette.TextFaint),
            (ImGuiCol.ScrollbarGrabActive, Palette.TextMuted),
            (ImGuiCol.TextSelectedBg, Palette.WithAlpha(Palette.Accent, 0.30f)),
            (ImGuiCol.NavHighlight, Palette.Accent),
        };
        foreach (var (col, value) in colors) ImGui.PushStyleColor(col, value);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, InputRadius * s);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 10 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 8 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 10 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 8 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10, 7) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(10, 10) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
        return new Popper(colors.Length, 9);
    }

    private sealed class Popper(int colors, int vars) : IDisposable
    {
        public void Dispose()
        {
            ImGui.PopStyleVar(vars);
            ImGui.PopStyleColor(colors);
        }
    }

    // ------------------------------------------------------------------ text

    public static void Text(string text, Vector4? color = null) => ImGui.TextColored(color ?? Palette.Text, text);

    public static void Muted(string text) => ImGui.TextColored(Palette.TextMuted, text);

    /// <summary>Width left on the current line: up to the card's inner edge inside a card, else the window's.</summary>
    public static float RemainingWidth()
    {
        if (!Cards.TryPeek(out var c)) return ImGui.GetContentRegionAvail().X;
        return MathF.Max(1, c.Start.X + c.Width - c.Pad - ImGui.GetCursorScreenPos().X);
    }

    public static void Wrapped(string text, Vector4? color = null, float wrapWidth = 0)
    {
        var w = MathF.Min(wrapWidth > 0 ? wrapWidth : float.MaxValue, RemainingWidth());
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + w);
        ImGui.TextColored(color ?? Palette.TextMuted, text);
        ImGui.PopTextWrapPos();
    }

    public static void Heading(string text, string? sub = null)
    {
        using (F.Title.Push()) Text(text);
        if (sub == null) return;
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - 6 * S);
        Wrapped(sub, Palette.TextMuted, MathF.Min(ImGui.GetContentRegionAvail().X, 560 * S));
    }

    public static void Icon(FontAwesomeIcon icon, Vector4 color, bool small = false)
    {
        using (small ? F.IconSmall.Push() : F.Icon.Push()) ImGui.TextColored(color, icon.ToIconString());
    }

    public static Vector2 IconSize(FontAwesomeIcon icon, bool small = false)
    {
        using (small ? F.IconSmall.Push() : F.Icon.Push()) return ImGui.CalcTextSize(icon.ToIconString());
    }

    public static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ShowTooltip(text);
    }

    /// <summary>The "Tooltip size" setting, set by the window each frame.</summary>
    public static TooltipSize TooltipSize { get; set; } = TooltipSize.Normal;

    /// <summary>Pixel size of the tooltip text for a setting.</summary>
    public static int TooltipPixels(TooltipSize size) => size switch
    {
        TooltipSize.Small => 12,
        TooltipSize.Large => 16,
        TooltipSize.Larger => 18,
        TooltipSize.Largest => 20,
        _ => (int)Fonts.BodySize,
    };

    public static void ShowTooltip(string text)
    {
        var px = TooltipPixels(TooltipSize);
        var font = F.Regular(px);
        // A size picked a moment ago may still be building: the normal size until it's there.
        if (!font.Available) { font = F.Body; px = (int)Fonts.BodySize; }
        using (font.Push())
        {
            ImGui.BeginTooltip();
            // The line grows with the text, so a line holds about as many words at every size.
            ImGui.PushTextWrapPos(320 * S * px / Fonts.BodySize);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    // ------------------------------------------------------------------ buttons

    public enum Kind { Primary, Secondary, Ghost, Danger }

    public static float ButtonWidth(string label, FontAwesomeIcon? icon = null)
    {
        float textW;
        using (F.Strong.Push()) textW = ImGui.CalcTextSize(label).X;
        return textW + 36 * S + (icon != null ? IconSize(icon.Value).X + 8 * S : 0);
    }

    /// <summary>A pill button. Disabled buttons still show their tooltip so they can say why.</summary>
    public static bool Button(string label, Kind kind, FontAwesomeIcon? icon = null, float width = 0, bool enabled = true, string? tooltip = null, string? id = null)
    {
        var s = S;
        var h = 34 * s;
        var w = width > 0 ? width : ButtonWidth(label, icon);
        var pos = ImGui.GetCursorScreenPos();
        var pressed = ImGui.InvisibleButton(id ?? ("##btn" + label), new Vector2(w, h)) && enabled;
        var hovered = ImGui.IsItemHovered() && enabled;
        var held = ImGui.IsItemActive() && enabled;
        if (tooltip != null) Tooltip(tooltip);

        var (bg, fg) = kind switch
        {
            Kind.Primary => (hovered ? Palette.AccentHover : Palette.Accent, Palette.OnAccent),
            Kind.Danger => (Palette.WithAlpha(Palette.Danger, hovered ? 0.28f : 0.18f), Palette.Danger),
            Kind.Ghost => (hovered ? Palette.Raised : Vector4.Zero, Palette.Text),
            _ => (hovered ? Palette.RaisedHover : Palette.Raised, Palette.Text),
        };
        if (!enabled)
        {
            bg = kind == Kind.Primary ? Palette.WithAlpha(Palette.Accent, 0.22f) : Palette.WithAlpha(bg, bg.W * 0.5f);
            fg = kind == Kind.Primary ? Palette.WithAlpha(Palette.OnAccent, 0.9f) : Palette.TextFaint;
        }

        // Press feedback: shrink 3% while held.
        var inset = held ? new Vector2(w, h) * 0.015f : Vector2.Zero;
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos + inset, pos + new Vector2(w, h) - inset, Palette.U32(bg), h / 2);
        if (ImGui.IsItemFocused() && ImGui.GetIO().NavVisible)
            dl.AddRect(pos - new Vector2(2 * s), pos + new Vector2(w, h) + new Vector2(2 * s), Palette.U32(Palette.Accent), h / 2 + 2 * s, ImDrawFlags.None, 1.5f * s);

        float textW;
        using (F.Strong.Push()) textW = ImGui.CalcTextSize(label).X;
        var iconW = icon != null ? IconSize(icon.Value).X + 8 * s : 0;
        var x = pos.X + (w - textW - iconW) / 2;
        if (icon != null)
        {
            var isz = IconSize(icon.Value);
            using (F.Icon.Push()) dl.AddText(new Vector2(x, pos.Y + (h - isz.Y) / 2), Palette.U32(fg), icon.Value.ToIconString());
            x += iconW;
        }
        using (F.Strong.Push())
            dl.AddText(new Vector2(x, pos.Y + (h - ImGui.GetTextLineHeight()) / 2), Palette.U32(fg), label);
        return pressed;
    }

    /// <summary>A round icon-only button. Always carries a tooltip naming the action.</summary>
    /// <summary>
    /// A quiet text action for card headers and inline lines: small coloured text with an optional
    /// small icon, no fill until hovered (then a soft tinted pill). For secondary or destructive
    /// actions that shouldn't compete with the content.
    /// </summary>
    public static bool LinkButton(string label, Vector4 color, FontAwesomeIcon? icon = null, string? tooltip = null, string? id = null)
    {
        var size = LinkButtonSize(label, icon);
        var s = S;
        var pos = ImGui.GetCursorScreenPos();
        var pressed = ImGui.InvisibleButton(id ?? "##link" + label, size);
        var hovered = ImGui.IsItemHovered();
        if (tooltip != null) Tooltip(tooltip);
        var dl = ImGui.GetWindowDrawList();
        if (hovered)
        {
            dl.AddRectFilled(pos, pos + size, Palette.U32(Palette.WithAlpha(color, ImGui.IsItemActive() ? 0.2f : 0.12f)), size.Y / 2);
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        var fg = Palette.U32(hovered ? color : Palette.WithAlpha(color, 0.82f));
        var x = pos.X + 10 * s;
        if (icon is { } ic)
        {
            var isz = IconSize(ic, small: true);
            using (F.IconSmall.Push()) dl.AddText(new Vector2(x, pos.Y + (size.Y - isz.Y) / 2), fg, ic.ToIconString());
            x += isz.X + 6 * s;
        }
        using (F.Small.Push())
        {
            var th = ImGui.GetTextLineHeight();
            dl.AddText(new Vector2(x, pos.Y + (size.Y - th) / 2), fg, label);
        }
        return pressed;
    }

    public static Vector2 LinkButtonSize(string label, FontAwesomeIcon? icon = null)
    {
        var s = S;
        float tw, th;
        using (F.Small.Push()) { tw = ImGui.CalcTextSize(label).X; th = ImGui.GetTextLineHeight(); }
        var iw = icon is { } ic ? IconSize(ic, small: true).X + 6 * s : 0;
        return new Vector2(tw + iw + 20 * s, th + 10 * s);
    }

    public static bool IconButton(string id, FontAwesomeIcon icon, string tooltip, Kind kind = Kind.Ghost, float size = 30, bool enabled = true)
    {
        var s = S;
        var d = size * s;
        var pos = ImGui.GetCursorScreenPos();
        var pressed = ImGui.InvisibleButton(id, new Vector2(d)) && enabled;
        var hovered = ImGui.IsItemHovered() && enabled;
        Tooltip(tooltip);
        var dl = ImGui.GetWindowDrawList();
        var bg = kind switch
        {
            Kind.Danger => hovered ? Palette.WithAlpha(Palette.Danger, 0.2f) : Vector4.Zero,
            Kind.Secondary => hovered ? Palette.RaisedHover : Palette.Raised,
            _ => hovered ? Palette.Raised : Vector4.Zero,
        };
        if (bg.W > 0) dl.AddCircleFilled(pos + new Vector2(d / 2), d / 2, Palette.U32(bg));
        var fg = !enabled ? Palette.TextFaint : kind == Kind.Danger && hovered ? Palette.Danger : hovered ? Palette.Text : Palette.TextMuted;
        var isz = IconSize(icon);
        using (F.Icon.Push()) dl.AddText(pos + (new Vector2(d) - isz) / 2, Palette.U32(fg), icon.ToIconString());
        return pressed;
    }

    // ------------------------------------------------------------------ inputs

    /// <summary>A rounded checkbox with an optional muted description under the label.</summary>
    public static bool Checkbox(string label, ref bool value, string? description = null, bool enabled = true)
    {
        var s = S;
        var box = 18 * s;
        var start = ImGui.GetCursorScreenPos();
        var avail = RemainingWidth();
        float labelH;
        using (F.Body.Push()) labelH = ImGui.GetTextLineHeight();
        var descH = 0f;
        if (description != null)
            using (F.Small.Push()) descH = ImGui.CalcTextSize(description, false, avail - box - 12 * s).Y + 2 * s;
        var rowH = MathF.Max(box, labelH) + descH;

        var clicked = ImGui.InvisibleButton("##cb" + label, new Vector2(avail, rowH)) && enabled;
        var hovered = ImGui.IsItemHovered() && enabled;
        if (clicked) value = !value;

        var dl = ImGui.GetWindowDrawList();
        var boxMin = start + new Vector2(0, (labelH - box) / 2 + 1 * s);
        var on = Ease(ImGui.GetID("##cbease" + label), value ? 1f : 0f, 24f);
        var fill = Palette.Mix(hovered ? Palette.RaisedHover : Palette.Raised, Palette.Accent, on);
        if (!enabled) fill = Palette.WithAlpha(fill, 0.5f);
        dl.AddRectFilled(boxMin, boxMin + new Vector2(box), Palette.U32(fill), 6 * s);
        if (on > 0.5f)
        {
            // Check mark drawn as two strokes, so it scales cleanly.
            var c = Palette.U32(Palette.WithAlpha(Palette.OnAccent, (on - 0.5f) * 2f));
            dl.AddLine(boxMin + new Vector2(4.5f, 9.5f) * s, boxMin + new Vector2(7.8f, 12.8f) * s, c, 2.2f * s);
            dl.AddLine(boxMin + new Vector2(7.8f, 12.8f) * s, boxMin + new Vector2(13.5f, 5.5f) * s, c, 2.2f * s);
        }

        var tx = start.X + box + 12 * s;
        using (F.Body.Push())
            dl.AddText(new Vector2(tx, start.Y), Palette.U32(enabled ? Palette.Text : Palette.TextFaint), label);
        if (description != null)
            using (F.Small.Push())
                dl.AddText(ImGui.GetFont(), ImGui.GetFontSize(), new Vector2(tx, start.Y + labelH + 2 * s), Palette.U32(Palette.TextMuted), description, avail - box - 12 * s);
        return clicked;
    }

    /// <summary>Choose one of a few options. The selected segment is a pill that eases between positions.</summary>
    public static bool Segmented(string id, string[] labels, ref int index, float width = 0)
    {
        var s = S;
        var h = 32 * s;
        float[] widths = new float[labels.Length];
        using (F.Strong.Push())
            for (var i = 0; i < labels.Length; i++) widths[i] = ImGui.CalcTextSize(labels[i]).X + 28 * s;
        var total = 0f;
        foreach (var w in widths) total += w;
        if (width > total)
            for (var i = 0; i < widths.Length; i++) widths[i] = width / labels.Length;

        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        var full = MathF.Max(total, width) + 8 * s;
        dl.AddRectFilled(pos, pos + new Vector2(full, h), Palette.U32(Palette.Raised), h / 2);

        var changed = false;
        var x = 4 * s;
        var selX = 0f;
        for (var i = 0; i < labels.Length; i++)
        {
            if (i == index) selX = x;
            x += widths[i];
        }
        var eased = Ease(ImGui.GetID(id + "##seg"), selX);
        dl.AddRectFilled(pos + new Vector2(eased, 4 * s), pos + new Vector2(eased + widths[Math.Clamp(index, 0, widths.Length - 1)], h - 4 * s), Palette.U32(Palette.RaisedHover), (h - 8 * s) / 2);

        x = 4 * s;
        for (var i = 0; i < labels.Length; i++)
        {
            ImGui.SetCursorScreenPos(pos + new Vector2(x, 0));
            if (ImGui.InvisibleButton(id + "##" + i, new Vector2(widths[i], h)) && index != i) { index = i; changed = true; }
            var hovered = ImGui.IsItemHovered();
            using (F.Strong.Push())
            {
                var tw = ImGui.CalcTextSize(labels[i]).X;
                var col = i == index ? Palette.Text : hovered ? Palette.Text : Palette.TextMuted;
                dl.AddText(pos + new Vector2(x + (widths[i] - tw) / 2, (h - ImGui.GetTextLineHeight()) / 2), Palette.U32(col), labels[i]);
            }
            x += widths[i];
        }
        ImGui.SetCursorScreenPos(pos);
        ImGui.Dummy(new Vector2(full, h));
        return changed;
    }

    /// <summary>Two handles on one track for a min–max range.</summary>
    public static bool RangeSlider(string id, ref int lo, ref int hi, int min, int max, float width)
    {
        var s = S;
        var h = 28 * s;
        var pos = ImGui.GetCursorScreenPos();
        var trackY = pos.Y + h / 2;
        var r = 8 * s;
        var x0 = pos.X + r;
        var x1 = pos.X + width - r;
        float ToX(int v) => x0 + (x1 - x0) * (v - min) / (float)(max - min);
        int FromX(float x) => Math.Clamp((int)MathF.Round(min + (x - x0) / (x1 - x0) * (max - min)), min, max);

        ImGui.InvisibleButton(id, new Vector2(width, h));
        var active = ImGui.IsItemActive();
        var hovered = ImGui.IsItemHovered();
        var changed = false;
        var stateId = ImGui.GetID(id + "##handle");
        var storage = ImGui.GetStateStorage();
        if (ImGui.IsItemActivated())
        {
            // Grab whichever handle is closer to the click.
            var mx = ImGui.GetIO().MousePos.X;
            storage.SetInt(stateId, MathF.Abs(mx - ToX(lo)) <= MathF.Abs(mx - ToX(hi)) && !(lo == hi && mx > ToX(hi)) ? 0 : 1);
        }
        if (active)
        {
            var v = FromX(ImGui.GetIO().MousePos.X);
            if (storage.GetInt(stateId) == 0) { v = Math.Min(v, hi); if (v != lo) { lo = v; changed = true; } }
            else { v = Math.Max(v, lo); if (v != hi) { hi = v; changed = true; } }
        }

        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(new Vector2(x0, trackY - 2 * s), new Vector2(x1, trackY + 2 * s), Palette.U32(Palette.Raised), 2 * s);
        dl.AddRectFilled(new Vector2(ToX(lo), trackY - 2 * s), new Vector2(ToX(hi), trackY + 2 * s), Palette.U32(Palette.Accent), 2 * s);
        foreach (var v in new[] { lo, hi })
        {
            var c = new Vector2(ToX(v), trackY);
            dl.AddCircleFilled(c, r, Palette.U32(hovered || active ? Palette.AccentHover : Palette.Accent));
            dl.AddCircleFilled(c, r - 3 * s, Palette.U32(Palette.Surface));
        }
        return changed;
    }

    // ------------------------------------------------------------------ chips, icons, bars

    /// <param name="job">A ClassJob row: its glyph leads the chip (instead of <paramref name="icon"/>).</param>
    public static float ChipWidth(string text, FontAwesomeIcon? icon = null, uint job = 0)
    {
        float tw;
        using (F.Small.Push()) tw = ImGui.CalcTextSize(text).X;
        var lead = job != 0 ? 14 * S + 4 * S : icon != null ? IconSize(icon.Value, true).X + 5 * S : 0;
        return tw + 16 * S + lead;
    }

    /// <summary>A small tinted pill label. Colour is never the only signal: chips always carry text.</summary>
    /// <param name="job">A ClassJob row: its glyph, in the chip's colour, leads the text.</param>
    public static void Chip(string text, Vector4 color, FontAwesomeIcon? icon = null, uint job = 0)
    {
        var s = S;
        var h = 20 * s;
        var w = ChipWidth(text, icon, job);
        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + new Vector2(w, h), Palette.U32(Palette.WithAlpha(color, 0.16f)), h / 2);
        var x = pos.X + 8 * s;
        if (job != 0)
        {
            JobIcons.Draw(dl, job, new Vector2(x + 7 * s, pos.Y + h / 2), 14 * s);
            x += 18 * s;
        }
        else if (icon != null)
        {
            var isz = IconSize(icon.Value, true);
            using (F.IconSmall.Push()) dl.AddText(new Vector2(x, pos.Y + (h - isz.Y) / 2), Palette.U32(color), icon.Value.ToIconString());
            x += isz.X + 5 * s;
        }
        using (F.Small.Push())
            dl.AddText(new Vector2(x, pos.Y + (h - ImGui.GetTextLineHeight()) / 2), Palette.U32(color), text);
        ImGui.Dummy(new Vector2(w, h));
    }

    public static float ToggleChipWidth(string text, uint job = 0)
    {
        using (F.Strong.Push()) return ImGui.CalcTextSize(text).X + (job != 0 ? 40 : 34) * S;
    }

    /// <summary>A chip that toggles; used for the browser's filters.</summary>
    /// <param name="job">A ClassJob row: its glyph takes the place of the colour dot.</param>
    public static bool ToggleChip(string id, string text, Vector4 color, bool on, uint job = 0)
    {
        var s = S;
        var h = 30 * s;
        var w = ToggleChipWidth(text, job);
        var pos = ImGui.GetCursorScreenPos();
        var pressed = ImGui.InvisibleButton(id, new Vector2(w, h));
        var hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        var bg = on ? Palette.WithAlpha(color, 0.22f) : hovered ? Palette.RaisedHover : Palette.Raised;
        dl.AddRectFilled(pos, pos + new Vector2(w, h), Palette.U32(bg), h / 2);
        float textX;
        if (job != 0)
        {
            JobIcons.Draw(dl, job, pos + new Vector2(18 * s, h / 2), 18 * s, on || hovered ? 1f : 0.7f);
            textX = 31 * s;
        }
        else
        {
            dl.AddCircleFilled(pos + new Vector2(14 * s, h / 2), 4 * s, Palette.U32(on ? color : Palette.WithAlpha(color, 0.55f)));
            textX = 24 * s;
        }
        using (F.Strong.Push())
            dl.AddText(pos + new Vector2(textX, (h - ImGui.GetTextLineHeight()) / 2), Palette.U32(on ? color : Palette.TextMuted), text);
        return pressed;
    }

    /// <summary>A game item icon with rounded corners; a quiet placeholder while it loads.</summary>
    public static void ItemIcon(uint iconId, float size, bool hq = false)
    {
        var d = size * S;
        var pos = ImGui.GetCursorScreenPos();
        DrawItemIcon(ImGui.GetWindowDrawList(), iconId, pos, d, hq);
        ImGui.Dummy(new Vector2(d));
    }

    public static void DrawItemIcon(ImDrawListPtr dl, uint iconId, Vector2 pos, float d, bool hq = false)
    {
        var radius = MathF.Min(IconRadius * S, d * 0.25f);
        if (iconId != 0 && Services.Textures.TryGetFromGameIcon(new GameIconLookup(iconId, hq), out var tex)
            && tex.TryGetWrap(out var wrap, out _) && wrap != null)
        {
            dl.AddImageRounded(wrap.Handle, pos, pos + new Vector2(d), Vector2.Zero, Vector2.One, Palette.U32(Vector4.One), radius);
            return;
        }
        dl.AddRectFilled(pos, pos + new Vector2(d), Palette.U32(Palette.Raised), radius);
    }

    /// <summary>A thin rounded progress bar whose fill eases to its new value.</summary>
    public static void Bar(string id, float fraction, Vector4 color, float width, float height = 6)
    {
        var s = S;
        var h = height * s;
        var pos = ImGui.GetCursorScreenPos();
        var f = Ease(ImGui.GetID(id), Math.Clamp(fraction, 0f, 1f), 10f);
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + new Vector2(width, h), Palette.U32(Palette.Raised), h / 2);
        if (f > 0.001f) dl.AddRectFilled(pos, pos + new Vector2(MathF.Max(h, width * f), h), Palette.U32(color), h / 2);
        ImGui.Dummy(new Vector2(width, h));
    }

    // ------------------------------------------------------------------ cards

    /// <summary>
    /// Starts a card: content is drawn first on a foreground channel, the background is filled in
    /// <see cref="EndCard"/> once the height is known. Cards do not nest.
    /// </summary>
    public static void BeginCard(Vector4? background = null, float padding = 18, float width = 0)
    {
        var s = S;
        var dl = ImGui.GetWindowDrawList();
        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);
        var start = ImGui.GetCursorScreenPos();
        var w = width > 0 ? width : ImGui.GetContentRegionAvail().X;
        Cards.Push((start, w, background ?? Palette.Surface, padding * s));
        ImGui.SetCursorScreenPos(start + new Vector2(padding * s));
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + w - padding * s * 2);
    }

    /// <summary>Usable width inside the current card.</summary>
    public static float CardInnerWidth => Cards.TryPeek(out var c) ? c.Width - c.Pad * 2 : ImGui.GetContentRegionAvail().X;

    public static void EndCard(float gapAfter = 14)
    {
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();
        var (start, width, bg, pad) = Cards.Pop();
        var end = new Vector2(start.X + width, ImGui.GetItemRectMax().Y + pad);
        var dl = ImGui.GetWindowDrawList();
        dl.ChannelsSetCurrent(0);
        dl.AddRectFilled(start, end, Palette.U32(bg), CardRadius * S);
        dl.ChannelsMerge();
        ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y));
        ImGui.Dummy(new Vector2(width, gapAfter * S));
    }
}
