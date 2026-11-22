using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Provenance.UI.Theme;

namespace Provenance.UI;

/// <summary>One row of a dropdown list.</summary>
/// <param name="Detail">Short muted text on the right (an innkeeper, a level).</param>
/// <param name="Sub">A second line under the label (what a food adds).</param>
/// <param name="Badge">Accent text on the right instead of the detail ("3 in your bags").</param>
/// <param name="Dot">A colour dot before the label (a job's colour).</param>
/// <param name="GameIcon">A game icon before the label (an item).</param>
/// <param name="Keywords">More text the search matches but nobody sees (a job's abbreviation).</param>
/// <param name="Job">A ClassJob row: its glyph, in the job's colour, leads the label.</param>
/// <param name="Disabled">Shown faded and can't be picked (the tooltip says why).</param>
/// <param name="Tooltip">Shown while the row is hovered.</param>
internal sealed record DropdownItem(string Label, string? Detail = null, string? Sub = null, string? Badge = null,
    Vector4? Dot = null, uint GameIcon = 0, bool Hq = false, string? Keywords = null, uint Job = 0, bool Disabled = false, string? Tooltip = null);

/// <summary>
/// The select field and its list. The field is a pill with the current choice and a chevron; the list
/// opens right below it (above when there's no room), never at the mouse.
/// </summary>
internal static partial class Ui
{
    /// <summary>Lists this long get a search box.</summary>
    private const int DropdownSearchFrom = 9;
    private const float DropdownMaxRows = 8.5f;

    /// <summary>The list fades in and slides this far from the field, over this many seconds.</summary>
    private const float DropdownSlide = 8f;
    private const double DropdownOpenSeconds = 0.16;

    private sealed class DropdownState
    {
        public string PopupId = "";
        public Vector2 Min, Max;
        public string Search = "";
        public int Active;
        public bool Scroll;
        public double OpenedAt;
    }

    private static readonly Dictionary<string, DropdownState> Dropdowns = new();

    private static DropdownState DropdownOf(string id)
    {
        if (!Dropdowns.TryGetValue(id, out var st)) Dropdowns[id] = st = new DropdownState { PopupId = id + "_list" };
        return st;
    }

    /// <summary>
    /// A select field: a pill with the current choice (or the placeholder) and a chevron. Clicking it
    /// opens the list <see cref="DropdownList"/> draws; clicking it again closes it.
    /// </summary>
    /// <param name="width">0 fits the content.</param>
    /// <param name="prefix">A muted label inside the pill, before the choice ("Food").</param>
    /// <param name="detail">Muted text after the choice ("level 100").</param>
    /// <param name="job">A ClassJob row: its glyph, in the job's colour, leads the choice.</param>
    /// <returns>True on the frame the list opens: build what it shows then.</returns>
    public static bool DropdownField(string id, string? value, string placeholder, float width = 0, FontAwesomeIcon? icon = null,
        Vector4? dot = null, uint gameIcon = 0, bool hq = false, string? prefix = null, string? detail = null, uint job = 0)
    {
        var s = S;
        var h = 34 * s;
        var st = DropdownOf(id);
        var open = ImGui.IsPopupOpen(st.PopupId);
        var text = value ?? placeholder;
        if (value == null) detail = null;

        var lineH = ImGui.GetTextLineHeight();
        var prefixW = prefix != null ? ImGui.CalcTextSize(prefix).X + 10 * s : 0;
        float textW, detailW = 0;
        using (F.Strong.Push()) textW = ImGui.CalcTextSize(text).X;
        if (detail != null) using (F.Small.Push()) detailW = ImGui.CalcTextSize(detail).X + 8 * s;
        var leadW = gameIcon != 0 ? 26 * s : job != 0 ? 26 * s : dot != null ? 16 * s : icon is { } ic ? IconSize(ic, true).X + 9 * s : 0;
        var chevW = IconSize(FontAwesomeIcon.ChevronDown, true).X;
        var natural = 14 * s + prefixW + leadW + textW + detailW + 14 * s + chevW + 14 * s;
        var w = MathF.Min(width > 0 ? width : MathF.Max(natural, 180 * s), RemainingWidth());

        var pos = ImGui.GetCursorScreenPos();
        // While the list is open, a click here only closes it (the list blocks the rest of the window).
        var opening = ImGui.InvisibleButton(id, new Vector2(w, h));
        var hovered = ImGui.IsItemHovered();
        st.Min = pos;
        st.Max = pos + new Vector2(w, h);
        if (opening)
        {
            st.Search = "";
            st.OpenedAt = ImGui.GetTime();
            ImGui.OpenPopup(st.PopupId);
        }

        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, st.Max, Palette.U32(open || hovered ? Palette.RaisedHover : Palette.Raised), h / 2);
        if (open)
            dl.AddRect(pos, st.Max, Palette.U32(Palette.WithAlpha(Palette.Accent, 0.55f)), h / 2, ImDrawFlags.None, 1.2f * s);
        else if (ImGui.IsItemFocused() && ImGui.GetIO().NavVisible)
            dl.AddRect(pos - new Vector2(2 * s), st.Max + new Vector2(2 * s), Palette.U32(Palette.Accent), h / 2 + 2 * s, ImDrawFlags.None, 1.5f * s);

        var x = pos.X + 14 * s;
        var mid = pos.Y + h / 2;
        if (prefix != null)
        {
            dl.AddText(new Vector2(x, mid - lineH / 2), Palette.U32(Palette.TextMuted), prefix);
            x += prefixW;
        }
        if (gameIcon != 0) DrawItemIcon(dl, gameIcon, new Vector2(x, mid - 10 * s), 20 * s, hq);
        else if (job != 0) JobIcons.Draw(dl, job, new Vector2(x + 9 * s, mid), 20 * s);
        else if (dot is { } d) dl.AddCircleFilled(new Vector2(x + 4 * s, mid), 4 * s, Palette.U32(d));
        else if (icon is { } i)
        {
            var isz = IconSize(i, true);
            using (F.IconSmall.Push()) dl.AddText(new Vector2(x, mid - isz.Y / 2), Palette.U32(Palette.TextMuted), i.ToIconString());
        }
        x += leadW;

        var right = st.Max.X - 14 * s - chevW - 14 * s;
        using (F.Strong.Push())
        {
            var shown = Fit(text, right - x - detailW);
            dl.AddText(new Vector2(x, mid - ImGui.GetTextLineHeight() / 2), Palette.U32(value != null ? Palette.Text : Palette.TextMuted), shown);
            x += ImGui.CalcTextSize(shown).X;
        }
        if (detail != null)
            using (F.Small.Push())
                dl.AddText(new Vector2(x + 8 * s, mid - ImGui.GetTextLineHeight() / 2), Palette.U32(Palette.TextMuted), Fit(detail, right - x - 8 * s));

        using (F.IconSmall.Push())
        {
            var chevron = (open ? FontAwesomeIcon.ChevronUp : FontAwesomeIcon.ChevronDown).ToIconString();
            var cs = ImGui.CalcTextSize(chevron);
            dl.AddText(new Vector2(st.Max.X - 14 * s - cs.X, mid - cs.Y / 2), Palette.U32(Palette.TextMuted), chevron);
        }
        return opening;
    }

    /// <summary>
    /// The list of the <see cref="DropdownField"/> with the same id. Lists of 9 or more get a search
    /// box; arrow keys move, Enter picks, Escape closes.
    /// </summary>
    /// <param name="selected">Index of the current choice in <paramref name="items"/>, or -1.</param>
    /// <param name="multi">Several can be on at once: which are; a pick then toggles one and the list stays open.</param>
    /// <returns>The index of the item picked this frame, or -1.</returns>
    public static int DropdownList(string id, IReadOnlyList<DropdownItem>? items, int selected, string empty, float minWidth = 0, Func<int, bool>? multi = null)
    {
        var st = DropdownOf(id);
        if (!ImGui.IsPopupOpen(st.PopupId)) return -1;
        items ??= Array.Empty<DropdownItem>();
        var s = S;

        var searchable = items.Count >= DropdownSearchFrom;
        var query = searchable ? st.Search.Trim() : "";
        var rows = new List<int>(items.Count);
        for (var i = 0; i < items.Count; i++)
            if (query.Length == 0 || Matches(items[i], query)) rows.Add(i);

        float bodyH, smallH;
        using (F.Body.Push()) bodyH = ImGui.GetTextLineHeight();
        using (F.Small.Push()) smallH = ImGui.GetTextLineHeight();
        var twoLine = items.Any(i => i.Sub != null);
        var rowH = twoLine ? bodyH + smallH + 18 * s : 36 * s;
        var gap = 2 * s;
        var pad = 6 * s;
        var inputH = bodyH + 16 * s;
        var searchH = searchable ? inputH + gap + 6 * s : 0;
        var listH = rows.Count == 0 ? 40 * s : MathF.Min(rows.Count, DropdownMaxRows) * (rowH + gap) - gap;
        var size = new Vector2(MathF.Max(MathF.Max(st.Max.X - st.Min.X, minWidth), 240 * s), pad * 2 + searchH + listH);

        // Below the field; above it when only that has room.
        var vp = ImGui.GetMainViewport();
        var below = vp.WorkPos.Y + vp.WorkSize.Y - st.Max.Y - 8 * s;
        var above = st.Min.Y - vp.WorkPos.Y - 8 * s;
        var down = size.Y <= below || below >= above;
        var y = down ? st.Max.Y + 6 * s : st.Min.Y - 6 * s - size.Y;
        var x = MathF.Max(vp.WorkPos.X, MathF.Min(st.Min.X, vp.WorkPos.X + vp.WorkSize.X - size.X - 4 * s));

        // Opening: fades in and slides out of the field (ease-out), in a sixth of a second.
        var t = (float)Math.Clamp((ImGui.GetTime() - st.OpenedAt) / DropdownOpenSeconds, 0, 1);
        var eased = 1f - MathF.Pow(1f - t, 3f);
        if (!Ui.ReduceMotion) y += (down ? -1 : 1) * (1f - eased) * DropdownSlide * s;
        ImGui.SetNextWindowPos(new Vector2(x, y));
        ImGui.SetNextWindowSize(size);

        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, MathF.Max(0.01f, eased));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(pad));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 12 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, gap));
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Palette.Raised);
        ImGui.PushStyleColor(ImGuiCol.Border, Palette.WithAlpha(Palette.Text, 0.08f));
        var picked = -1;
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar
                                       | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNavInputs;
        if (ImGui.BeginPopup(st.PopupId, flags))
        {
            // The list's text at the text size too, while its fonts are built (see MainWindow.Draw).
            ImGui.SetWindowFontScale(TextScale / F.Scale);
            var appearing = ImGui.IsWindowAppearing();
            var dl = ImGui.GetWindowDrawList();
            var winMin = ImGui.GetWindowPos();
            PanelShadow(dl, winMin, winMin + ImGui.GetWindowSize(), 12 * s);
            if (appearing)
            {
                st.Active = Math.Max(0, rows.IndexOf(selected));
                st.Scroll = true;
            }

            if (searchable)
            {
                ImGui.PushStyleColor(ImGuiCol.FrameBg, Palette.Surface);
                ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Palette.Surface);
                ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Palette.Surface);
                ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(32 * s, 8 * s));
                ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8 * s);
                if (appearing) ImGui.SetKeyboardFocusHere();
                ImGui.SetNextItemWidth(-1);
                var before = st.Search;
                using (F.Body.Push()) ImGui.InputTextWithHint("##search", "Search", ref st.Search, 64);
                var boxMin = ImGui.GetItemRectMin();
                var boxH = ImGui.GetItemRectSize().Y;
                ImGui.PopStyleVar(2);
                ImGui.PopStyleColor(3);
                using (F.IconSmall.Push())
                {
                    var glyph = FontAwesomeIcon.Search.ToIconString();
                    var gs = ImGui.CalcTextSize(glyph);
                    dl.AddText(boxMin + new Vector2(12 * s, (boxH - gs.Y) / 2), Palette.U32(Palette.TextFaint), glyph);
                }
                if (st.Search != before)
                {
                    st.Active = 0;
                    st.Scroll = true;
                }
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 6 * s);
            }

            if (rows.Count > 0)
            {
                st.Active = Math.Clamp(st.Active, 0, rows.Count - 1);
                if (ImGui.IsKeyPressed(ImGuiKey.DownArrow)) { st.Active = Math.Min(rows.Count - 1, st.Active + 1); st.Scroll = true; }
                if (ImGui.IsKeyPressed(ImGuiKey.UpArrow)) { st.Active = Math.Max(0, st.Active - 1); st.Scroll = true; }
                if ((ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter)) && !items[rows[st.Active]].Disabled) picked = rows[st.Active];
            }
            if (ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();

            var scrolls = rows.Count > DropdownMaxRows;
            if (ImGui.BeginChild("##rows", new Vector2(0, listH), false, (scrolls ? ImGuiWindowFlags.None : ImGuiWindowFlags.NoScrollbar) | ImGuiWindowFlags.NoNavInputs))
            {
                if (st.Scroll && rows.Count > 0)
                {
                    // The active row in view; on opening, the current choice in the middle.
                    var top = st.Active * (rowH + gap);
                    var scrollY = ImGui.GetScrollY();
                    if (appearing) ImGui.SetScrollY(MathF.Max(0, top - (listH - rowH) / 2));
                    else if (top < scrollY) ImGui.SetScrollY(top);
                    else if (top + rowH > scrollY + listH) ImGui.SetScrollY(top + rowH - listH);
                    st.Scroll = false;
                }
                if (rows.Count == 0)
                {
                    var at = ImGui.GetCursorScreenPos();
                    using (F.Body.Push())
                        ImGui.GetWindowDrawList().AddText(at + new Vector2(10 * s, (listH - bodyH) / 2), Palette.U32(Palette.TextMuted),
                            query.Length > 0 ? "Nothing matches." : empty);
                    ImGui.Dummy(new Vector2(1, listH));
                }
                for (var k = 0; k < rows.Count; k++)
                    if (DropdownRow(items[rows[k]], rows[k], multi?.Invoke(rows[k]) ?? rows[k] == selected, k == st.Active, rowH, bodyH, smallH)
                        && !items[rows[k]].Disabled)
                        picked = rows[k];
                // The mouse takes over the highlight once it moves over a row.
                if (ImGui.GetIO().MouseDelta != Vector2.Zero && hoveredRow >= 0) st.Active = rows.IndexOf(hoveredRow);
                hoveredRow = -1;
            }
            ImGui.EndChild();
            if (picked >= 0 && multi == null) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(5);
        return picked;
    }

    private static int hoveredRow = -1;

    private static bool Matches(DropdownItem item, string query)
        => item.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase)
           || (item.Detail?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false)
           || (item.Sub?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false)
           || (item.Keywords?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false);

    private static bool DropdownRow(DropdownItem item, int index, bool selected, bool active, float rowH, float bodyH, float smallH)
    {
        var s = S;
        var pos = ImGui.GetCursorScreenPos();
        var w = ImGui.GetContentRegionAvail().X;
        var clicked = ImGui.InvisibleButton("##row" + index, new Vector2(w, rowH));
        if (ImGui.IsItemHovered()) hoveredRow = index;
        if (item.Tooltip != null) Tooltip(item.Tooltip);

        var dl = ImGui.GetWindowDrawList();
        if (selected || active)
        {
            var bg = selected ? Palette.WithAlpha(Palette.Accent, active ? 0.22f : 0.14f) : Palette.RaisedHover;
            dl.AddRectFilled(pos, pos + new Vector2(w, rowH), Palette.U32(bg), 8 * s);
        }

        var x = pos.X + 10 * s;
        var right = pos.X + w - 10 * s;
        // The check's column is kept on every row so the details line up.
        using (F.IconSmall.Push())
        {
            var check = FontAwesomeIcon.Check.ToIconString();
            var cs = ImGui.CalcTextSize(check);
            if (selected) dl.AddText(new Vector2(right - cs.X, pos.Y + (rowH - cs.Y) / 2), Palette.U32(Palette.Accent), check);
            right -= cs.X + 10 * s;
        }

        var twoLine = item.Sub != null;
        var top = twoLine ? pos.Y + (rowH - bodyH - smallH - 2 * s) / 2 : pos.Y + (rowH - bodyH) / 2;
        if (item.GameIcon != 0)
        {
            var d = twoLine ? 30 * s : 22 * s;
            DrawItemIcon(dl, item.GameIcon, new Vector2(x, pos.Y + (rowH - d) / 2), d, item.Hq);
            x += d + 10 * s;
        }
        else if (item.Job != 0)
        {
            JobIcons.Draw(dl, item.Job, new Vector2(x + 10 * s, top + bodyH / 2), 20 * s);
            x += 30 * s;
        }
        else if (item.Dot is { } c)
        {
            dl.AddCircleFilled(new Vector2(x + 4 * s, top + bodyH / 2), 4 * s, Palette.U32(c));
            x += 18 * s;
        }

        var tail = item.Badge ?? item.Detail;
        var tailW = 0f;
        if (tail != null)
            using (F.Small.Push())
            {
                tailW = ImGui.CalcTextSize(tail).X;
                dl.AddText(new Vector2(right - tailW, top + (bodyH - smallH) / 2), Palette.U32(item.Badge != null ? Palette.Accent : Palette.TextMuted), tail);
            }
        using (selected ? F.Strong.Push() : F.Body.Push())
            dl.AddText(new Vector2(x, top), Palette.U32(item.Disabled ? Palette.TextFaint : Palette.Text), Fit(item.Label, right - x - (tailW > 0 ? tailW + 12 * s : 0)));
        if (item.Sub != null)
            using (F.Small.Push())
                dl.AddText(new Vector2(x, top + bodyH + 2 * s), Palette.U32(Palette.TextMuted), Fit(item.Sub, right - x));
        return clicked;
    }

    /// <summary>A soft shadow around a floating panel, drawn outside its edges only.</summary>
    internal static void PanelShadow(ImDrawListPtr dl, Vector2 min, Vector2 max, float rounding)
    {
        var s = S;
        var vp = ImGui.GetMainViewport();
        dl.PushClipRect(vp.Pos, vp.Pos + vp.Size, false);
        const int layers = 7;
        for (var i = 1; i <= layers; i++)
        {
            var spread = (i - 0.5f) * 2f * s;
            var alpha = 0.11f * (1f - (i - 1f) / layers);
            dl.AddRect(min - new Vector2(spread), max + new Vector2(spread), Palette.U32(new Vector4(0, 0, 0, alpha)), rounding + spread, ImDrawFlags.None, 2f * s);
        }
        dl.PopClipRect();
    }

    /// <summary>The text, cut with an ellipsis to fit the width in the current font.</summary>
    public static string Fit(string text, float width)
    {
        if (width <= 0) return "";
        if (ImGui.CalcTextSize(text).X <= width) return text;
        const string dots = "…";
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (ImGui.CalcTextSize(text[..mid] + dots).X <= width) lo = mid;
            else hi = mid - 1;
        }
        return text[..lo].TrimEnd() + dots;
    }
}
