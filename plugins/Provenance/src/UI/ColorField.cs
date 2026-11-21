using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Provenance.Core.Appearance;
using Provenance.UI.Theme;

namespace Provenance.UI;

/// <summary>A colour offered under a colour field's wheel, named in its tooltip.</summary>
internal sealed record ColorSwatch(uint Rgb, string Name);

/// <summary>
/// The colour field: a pill with a swatch and the colour's hex code. It opens a panel under it (above when only
/// that has room) with a colour wheel, a field for the hex code and swatches. The colour changes while picking, so
/// the window around it is the preview; Escape puts back the colour it had when the panel opened.
/// </summary>
internal static partial class Ui
{
    private sealed class ColorFieldState
    {
        public string PopupId = "";
        public Vector2 Min, Max;
        public double OpenedAt;
        public uint OpenedWith;
        /// <summary>The wheel's own value, so rounding to whole steps never feeds back into it while dragging.</summary>
        public Vector3 Wheel;
        /// <summary>The colour <see cref="Wheel"/> stands for; anything else (a code, a swatch) moves the wheel to it.</summary>
        public uint WheelRgb;
        public string Hex = "";
        public bool Typing;
        public bool WasOpen;
    }

    private static readonly Dictionary<string, ColorFieldState> ColorFields = new();

    private const ImGuiColorEditFlags WheelFlags = ImGuiColorEditFlags.PickerHueWheel | ImGuiColorEditFlags.NoSidePreview
        | ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoSmallPreview
        | ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoOptions | ImGuiColorEditFlags.NoDragDrop;

    /// <param name="rgb">The colour as 0xRRGGBB; changes live while picking.</param>
    /// <param name="fallback">The colour "Default" puts back.</param>
    /// <returns>True when a change is finished and worth saving: the wheel let go, a code entered, a swatch or the
    /// default picked, the panel closed.</returns>
    public static bool ColorField(string id, ref uint rgb, uint fallback, float width, IReadOnlyList<ColorSwatch>? swatches = null)
    {
        var s = S;
        var h = 34 * s;
        if (!ColorFields.TryGetValue(id, out var st)) ColorFields[id] = st = new ColorFieldState { PopupId = id + "_colors" };
        var open = ImGui.IsPopupOpen(st.PopupId);
        var w = MathF.Min(width, RemainingWidth());

        var pos = ImGui.GetCursorScreenPos();
        // While the panel is open, a click here only closes it (the panel blocks the rest of the window).
        if (ImGui.InvisibleButton(id, new Vector2(w, h)))
        {
            st.OpenedAt = ImGui.GetTime();
            st.OpenedWith = rgb;
            st.Typing = false;
            ImGui.OpenPopup(st.PopupId);
        }
        var hovered = ImGui.IsItemHovered();
        st.Min = pos;
        st.Max = pos + new Vector2(w, h);

        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, st.Max, Palette.U32(open || hovered ? Palette.RaisedHover : Palette.Raised), h / 2);
        if (open)
            dl.AddRect(pos, st.Max, Palette.U32(Palette.WithAlpha(Palette.Accent, 0.55f)), h / 2, ImDrawFlags.None, 1.2f * s);
        else if (ImGui.IsItemFocused() && ImGui.GetIO().NavVisible)
            dl.AddRect(pos - new Vector2(2 * s), st.Max + new Vector2(2 * s), Palette.U32(Palette.Accent), h / 2 + 2 * s, ImDrawFlags.None, 1.5f * s);

        // The swatch sits in the pill's round end, so its curve follows the pill's.
        var mid = pos.Y + h / 2;
        var r = 10 * s;
        var dot = new Vector2(pos.X + h / 2, mid);
        Swatch(dl, dot, r, rgb);
        using (F.Strong.Push())
            dl.AddText(new Vector2(dot.X + r + 10 * s, mid - ImGui.GetTextLineHeight() / 2), Palette.U32(Palette.Text), ThemeColors.ToHex(rgb));
        using (F.IconSmall.Push())
        {
            var chevron = (open ? FontAwesomeIcon.ChevronUp : FontAwesomeIcon.ChevronDown).ToIconString();
            var cs = ImGui.CalcTextSize(chevron);
            dl.AddText(new Vector2(st.Max.X - 14 * s - cs.X, mid - cs.Y / 2), Palette.U32(Palette.TextMuted), chevron);
        }

        var nowOpen = ImGui.IsPopupOpen(st.PopupId);
        // Closed since the last frame (a click outside, Escape): whatever it shows now stays, and is saved.
        var save = nowOpen ? ColorPanel(st, ref rgb, fallback, swatches) : st.WasOpen;
        st.WasOpen = nowOpen;
        return save;
    }

    /// <summary>A round swatch with a faint ring, so a colour close to what's behind it still shows its edge.</summary>
    private static void Swatch(ImDrawListPtr dl, Vector2 center, float radius, uint rgb)
    {
        dl.AddCircleFilled(center, radius, Palette.U32(Palette.Hex(rgb)));
        dl.AddCircle(center, radius, Palette.U32(Palette.WithAlpha(Palette.Text, 0.16f)), 0, 1f * S);
    }

    private static bool ColorPanel(ColorFieldState st, ref uint rgb, uint fallback, IReadOnlyList<ColorSwatch>? swatches)
    {
        var s = S;
        var pad = 14 * s;
        var gap = 12 * s;
        var wheel = 200 * s;
        var panelW = wheel + pad * 2 + 24 * s;
        var inner = panelW - pad * 2;
        var rowH = 34 * s;
        var dot = 22 * s;
        var dotGap = 8 * s;
        var perRow = swatches is { Count: > 0 } ? Math.Max(1, (int)((inner + dotGap) / (dot + dotGap))) : 0;
        var dotRows = perRow > 0 ? (swatches!.Count + perRow - 1) / perRow : 0;
        // A ring of 3 around the picked swatch reaches out past its row.
        var dotsH = dotRows > 0 ? dotRows * dot + (dotRows - 1) * dotGap + 6 * s : 0;
        var size = new Vector2(panelW, pad * 2 + wheel + gap + rowH + (dotsH > 0 ? gap + dotsH : 0));

        // Below the field; above it when only that has room. Same entrance as the dropdown lists.
        var vp = ImGui.GetMainViewport();
        var below = vp.WorkPos.Y + vp.WorkSize.Y - st.Max.Y - 8 * s;
        var above = st.Min.Y - vp.WorkPos.Y - 8 * s;
        var down = size.Y <= below || below >= above;
        var y = down ? st.Max.Y + 6 * s : st.Min.Y - 6 * s - size.Y;
        var x = MathF.Max(vp.WorkPos.X, MathF.Min(st.Min.X, vp.WorkPos.X + vp.WorkSize.X - size.X - 4 * s));
        var t = (float)Math.Clamp((ImGui.GetTime() - st.OpenedAt) / DropdownOpenSeconds, 0, 1);
        var eased = 1f - MathF.Pow(1f - t, 3f);
        if (!ReduceMotion) y += (down ? -1 : 1) * (1f - eased) * DropdownSlide * s;
        ImGui.SetNextWindowPos(new Vector2(x, y));
        ImGui.SetNextWindowSize(size);

        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, MathF.Max(0.01f, eased));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(pad));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 12 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Palette.Raised);
        ImGui.PushStyleColor(ImGuiCol.Border, Palette.WithAlpha(Palette.Text, 0.08f));
        var save = false;
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar
                                       | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNavInputs;
        if (ImGui.BeginPopup(st.PopupId, flags))
        {
            // The panel's text at the text size too, while its fonts are built (see MainWindow.Draw).
            ImGui.SetWindowFontScale(TextScale / F.Scale);
            var dl = ImGui.GetWindowDrawList();
            var origin = ImGui.GetWindowPos();
            PanelShadow(dl, origin, origin + ImGui.GetWindowSize(), 12 * s);
            var top = origin + new Vector2(pad);

            if (st.WheelRgb != rgb || ImGui.IsWindowAppearing())
            {
                var c = Palette.Hex(rgb);
                st.Wheel = new Vector3(c.X, c.Y, c.Z);
                st.WheelRgb = rgb;
            }
            if (!st.Typing) st.Hex = ThemeColors.ToHex(rgb)[1..];

            // The wheel. ImGui keeps room for a hue bar right of it even with the wheel, so the item is wider than
            // the wheel by that much, and the wheel is centred on its own.
            ImGui.SetCursorScreenPos(new Vector2(top.X + (inner - wheel) / 2, top.Y));
            ImGui.SetNextItemWidth(wheel + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X);
            var col = st.Wheel;
            if (ImGui.ColorPicker3("##wheel", ref col, WheelFlags))
            {
                st.Wheel = col;
                rgb = st.WheelRgb = Palette.Rgb(new Vector4(col, 1f));
                st.Typing = false;
            }
            if (ImGui.IsItemDeactivatedAfterEdit()) save = true;

            // The hex code: typed or pasted ("#" and the short form are fine), taken as soon as it's a colour.
            var rowY = top.Y + wheel + gap;
            ImGui.SetCursorScreenPos(new Vector2(top.X, rowY));
            var valid = ThemeColors.TryParseHex(st.Hex, out var typed);
            ImGui.PushStyleColor(ImGuiCol.FrameBg, Palette.Surface);
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Palette.Surface);
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Palette.Surface);
            float lineH;
            using (F.Body.Push()) lineH = ImGui.GetTextLineHeight();
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(26 * s, (rowH - lineH) / 2));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, rowH / 2);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, st.Typing && !valid ? 1f * s : 0f);
            ImGui.PushStyleColor(ImGuiCol.Border, Palette.WithAlpha(Palette.Warn, 0.7f));
            ImGui.SetNextItemWidth(118 * s);
            var wasTyping = st.Typing;
            bool edited;
            using (F.Body.Push())
                edited = ImGui.InputText("##hex", ref st.Hex, 7,
                    ImGuiInputTextFlags.CharsHexadecimal | ImGuiInputTextFlags.CharsUppercase | ImGuiInputTextFlags.AutoSelectAll);
            var boxMin = ImGui.GetItemRectMin();
            var typing = ImGui.IsItemActive();
            var left = ImGui.IsItemDeactivated();
            ImGui.PopStyleColor(4);
            ImGui.PopStyleVar(3);
            using (F.Body.Push())
                dl.AddText(new Vector2(boxMin.X + 13 * s, boxMin.Y + (rowH - lineH) / 2),
                    Palette.U32(st.Typing && !valid ? Palette.Warn : Palette.TextFaint), "#");
            if (edited)
            {
                st.Typing = true;
                if (ThemeColors.TryParseHex(st.Hex, out typed) && typed != rgb) rgb = typed;
            }
            if (left)
            {
                if (st.Typing) save = true;
                st.Typing = false;
            }
            else if (typing) st.Typing = true;

            // Back to the first colour, while it isn't that.
            if (rgb != fallback)
            {
                var label = "Default";
                var resetSize = LinkButtonSize(label, FontAwesomeIcon.Undo);
                ImGui.SetCursorScreenPos(new Vector2(top.X + inner - resetSize.X, rowY + (rowH - resetSize.Y) / 2));
                if (LinkButton(label, Palette.TextMuted, FontAwesomeIcon.Undo, "Back to " + ThemeColors.ToHex(fallback) + ", the first colour.", "##default"))
                {
                    rgb = fallback;
                    st.Typing = false;
                    save = true;
                }
            }

            // Swatches to pick at once; the picked one wears a ring.
            if (perRow > 0)
            {
                var dotsY = rowY + rowH + gap + 3 * s;
                for (var i = 0; i < swatches!.Count; i++)
                {
                    var sw = swatches[i];
                    var at = new Vector2(top.X + 3 * s + (i % perRow) * (dot + dotGap), dotsY + (i / perRow) * (dot + dotGap));
                    ImGui.SetCursorScreenPos(at);
                    if (ImGui.InvisibleButton("##swatch" + i, new Vector2(dot)))
                    {
                        rgb = sw.Rgb;
                        st.Typing = false;
                        save = true;
                    }
                    var over = ImGui.IsItemHovered();
                    if (over) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    Tooltip(sw.Name + "  " + ThemeColors.ToHex(sw.Rgb));
                    var c = at + new Vector2(dot / 2);
                    var picked = (rgb & 0xFFFFFF) == (sw.Rgb & 0xFFFFFF);
                    if (picked || over)
                        dl.AddCircle(c, dot / 2 + 3 * s, Palette.U32(picked ? Palette.Text : Palette.WithAlpha(Palette.Text, 0.35f)), 0, 1.5f * s);
                    Swatch(dl, c, dot / 2, sw.Rgb);
                }
            }

            // Escape in the code field only leaves the field; anywhere else it puts the colour back and closes.
            if (ImGui.IsKeyPressed(ImGuiKey.Escape) && !typing && !wasTyping)
            {
                rgb = st.OpenedWith;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(4);
        return save;
    }
}
