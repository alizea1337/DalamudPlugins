using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Keystroke.Automation;
using Keystroke.UI.Pages;
using Keystroke.UI.Theme;

namespace Keystroke.UI;

/// <summary>
/// The main panel: bevelled frame, signal stripe, icon rail on the left, the selected page on the
/// right. Collapses to a compact strip with transport, keycaps and the mode switch.
/// </summary>
internal sealed class MainWindow : Window
{
    private static readonly Vector2 FullSize = new(780, 560);
    private static readonly Vector2 CompactSize = new(400, 150);
    private static readonly string[] ModeLabels = { "human", "instant" };

    private readonly Plugin plugin;
    private readonly List<Page> pages;
    private int selected;
    private int shownPage = -1;
    private float pageFade = 1f;
    private bool applySize;
    private (bool open, bool compact)? layoutBeforeDuty;

    public MainWindow(Plugin plugin, IEnumerable<Page> pages) : base("Keystroke###KeystrokeMain")
    {
        this.plugin = plugin;
        this.pages = new List<Page>(pages);
        selected = Math.Clamp(plugin.Config.Window.LastPage, 0, this.pages.Count - 1);
        shownPage = selected;
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBackground;
        RespectCloseHotkey = true;
        ApplyMode();
    }

    private bool Compact => plugin.Config.Window.Compact;

    public void Select<T>() where T : Page
    {
        var i = pages.FindIndex(p => p is T);
        if (i >= 0) selected = i;
    }

    public void SetCompact(bool compact)
    {
        plugin.Config.Window.Compact = compact;
        plugin.Config.Save();
        ApplyMode();
    }

    public void EnterDutyLayout()
    {
        if (layoutBeforeDuty != null) return;
        layoutBeforeDuty = (IsOpen, Compact);
        if (!Compact) SetCompact(true);
        IsOpen = true;
    }

    public void RestoreLayout()
    {
        if (layoutBeforeDuty is not { } prev) return;
        layoutBeforeDuty = null;
        if (prev.compact != Compact) SetCompact(prev.compact);
        IsOpen = prev.open;
    }

    private void ApplyMode()
    {
        var size = Compact ? CompactSize : FullSize;
        Size = size;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = size, MaximumSize = size };
        Flags |= ImGuiWindowFlags.NoResize;
        SizeCondition = ImGuiCond.Always;
        applySize = true;
    }

    public override void PreDraw()
    {
        if (!applySize) SizeCondition = ImGuiCond.FirstUseEver;
        applySize = false;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
    }

    public override void PostDraw() => ImGui.PopStyleVar(3);

    public override void OnClose()
    {
        plugin.Config.Window.LastPage = selected;
        plugin.Config.Save();
    }

    public override void Draw()
    {
        using var theme = Theme.Theme.Push();
        using var body = Widgets.F.Body.Push();
        var s = Widgets.S;
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var dl = ImGui.GetWindowDrawList();

        Theme.Theme.DrawFrame(dl, pos, pos + size, s);
        var inset = Theme.Theme.FrameInset * s;
        Theme.Theme.DrawStripe(dl, pos + new Vector2(inset, inset), size.X - inset * 2, Theme.Theme.StripeHeight * s);

        if (Compact) DrawCompact(pos, size, s);
        else DrawFull(pos, size, s);

        InstantModeSheet.Draw(plugin.Config);
    }

    // ------------------------------------------------------------------ full

    private void DrawFull(Vector2 pos, Vector2 size, float s)
    {
        var inset = Theme.Theme.FrameInset * s;
        var top = inset + Theme.Theme.StripeHeight * s;
        var railW = Theme.Theme.RailWidth * s;
        var dl = ImGui.GetWindowDrawList();

        // Rail
        var railMin = pos + new Vector2(inset, top);
        var railMax = new Vector2(railMin.X + railW, pos.Y + size.Y - inset);
        dl.AddRectFilled(railMin, railMax, Theme.Theme.U32(Theme.Theme.Rail));
        dl.AddLine(new Vector2(railMax.X, railMin.Y), railMax, Theme.Theme.U32(Theme.Theme.Frame), 1 * s);
        dl.AddLine(new Vector2(railMax.X + 1 * s, railMin.Y), new Vector2(railMax.X + 1 * s, railMax.Y), Theme.Theme.U32(Theme.Theme.BevelMid), 1 * s);

        ImGui.SetCursorScreenPos(railMin + new Vector2(0, 8 * s));
        for (var i = 0; i < pages.Count; i++)
        {
            if (Widgets.RailTab("##tab" + i, pages[i].Icon, pages[i].Title, i == selected, railW)) selected = i;
        }

        // Rail footer: state dot + compact toggle
        var footerY = railMax.Y - 54 * s;
        var (col, label) = StateBadge(plugin.Controller.State);
        ImGui.SetCursorScreenPos(new Vector2(railMin.X + 6 * s, footerY));
        var pulse = plugin.Controller.State is AutomationState.Playing or AutomationState.Queued;
        dl.AddCircleFilled(new Vector2(railMin.X + railW / 2, footerY + 4 * s), 3.5f * s, Theme.Theme.U32(col));
        if (pulse && Theme.Theme.Effects) dl.AddCircleFilled(new Vector2(railMin.X + railW / 2, footerY + 4 * s), 3.5f * s + 3 * s * Widgets.Breathe(1.6f), Theme.Theme.U32(Theme.Theme.WithAlpha(col, 0.25f)));
        using (Widgets.F.Tiny.Push())
        {
            var ts = ImGui.CalcTextSize(label);
            Theme.Theme.ShadowText(dl, new Vector2(railMin.X + (railW - ts.X) / 2, footerY + 11 * s), Theme.Theme.TextDim, label, s);
        }
        ImGui.SetCursorScreenPos(new Vector2(railMin.X + (railW - 18 * s) / 2, railMax.Y - 24 * s));
        if (Widgets.Button("##compact", Widgets.ButtonKind.Normal, new Vector2(18 * s), Theme.Icon.Minus, "compact view")) SetCompact(true);

        // Header line: page title left, close right
        var contentX = railMax.X + 2 * s;
        var headerH = 34 * s;
        var contentW = pos.X + size.X - inset - contentX;
        using (Widgets.F.Title.Push())
            Theme.Theme.ShadowText(dl, new Vector2(contentX + 14 * s, railMin.Y + 8 * s), Theme.Theme.TextBright, pages[selected].Title.ToLowerInvariant(), s);
        dl.AddLine(new Vector2(contentX, railMin.Y + headerH), new Vector2(pos.X + size.X - inset, railMin.Y + headerH), Theme.Theme.U32(Theme.Theme.BevelMid), 1 * s);
        ImGui.SetCursorScreenPos(new Vector2(pos.X + size.X - inset - 26 * s, railMin.Y + 8 * s));
        if (Widgets.Button("##close", Widgets.ButtonKind.Normal, new Vector2(18 * s), Theme.Icon.X, "close")) IsOpen = false;

        // Page cross-fade
        if (shownPage != selected)
        {
            pageFade = Theme.Theme.ReduceMotion || !Theme.Theme.Effects ? 1f : 0f;
            shownPage = selected;
        }
        pageFade = Math.Min(1f, pageFade + ImGui.GetIO().DeltaTime * 7f);

        ImGui.SetCursorScreenPos(new Vector2(contentX, railMin.Y + headerH + 1 * s));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14 * s, 12 * s));
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, pageFade);
        if (ImGui.BeginChild("##content", new Vector2(contentW, pos.Y + size.Y - inset - railMin.Y - headerH - 1 * s), false, ImGuiWindowFlags.AlwaysUseWindowPadding))
        {
            try { pages[selected].Draw(); }
            catch (Exception e)
            {
                Services.Log.Error(e, "Page draw failed: {Page}", pages[selected].Title);
                Widgets.Notice(Theme.Icon.TriangleAlert, Theme.Theme.Danger, "This page failed to draw: " + e.Message);
            }
            ImGui.Dummy(new Vector2(0, 8 * s));
        }
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
    }

    // ------------------------------------------------------------------ compact

    private void DrawCompact(Vector2 pos, Vector2 size, float s)
    {
        var inset = Theme.Theme.FrameInset * s;
        var top = inset + Theme.Theme.StripeHeight * s;
        var dl = ImGui.GetWindowDrawList();
        var c = plugin.Controller;
        var state = c.State;
        var (col, label) = StateBadge(state);
        var t = plugin.Config.Typing;
        var x0 = pos.X + inset + 12 * s;
        var innerW = size.X - inset * 2 - 24 * s;

        // Header: state + status, expand/close on the right
        ImGui.SetCursorScreenPos(new Vector2(x0, pos.Y + top + 8 * s));
        Widgets.StatusDot(col, label, state is AutomationState.Playing or AutomationState.Queued);
        ImGui.SameLine(0, 8 * s);
        using (Widgets.F.Small.Push())
        {
            var detail = c.StatusDetail.Length > 40 ? c.StatusDetail[..40] + "…" : c.StatusDetail;
            Widgets.Text(detail, Theme.Theme.TextDim);
        }
        ImGui.SetCursorScreenPos(new Vector2(pos.X + size.X - inset - 48 * s, pos.Y + top + 6 * s));
        if (Widgets.Button("##expand", Widgets.ButtonKind.Normal, new Vector2(18 * s), Theme.Icon.AppWindow, "full view")) SetCompact(false);
        ImGui.SameLine(0, 4 * s);
        if (Widgets.Button("##close", Widgets.ButtonKind.Normal, new Vector2(18 * s), Theme.Icon.X, "close")) IsOpen = false;

        // Keycaps for the current word, or session line when idle
        ImGui.SetCursorScreenPos(new Vector2(x0, pos.Y + top + 34 * s));
        if (plugin.Config.Appearance.Keycaps && c.CurrentPrompt.Length > 0)
            Widgets.Keycaps(c.CurrentPrompt, c.CurrentAnswer.Length, 20f, innerW);
        else
        {
            var st = c.Session;
            var ms = c.MeasuredMsPerKey;
            Widgets.Small($"{st.GamesCompleted} games · {st.MgpGained:N0} mgp · {st.MgcGained:N0} mgc" + (ms > 0 ? $" · {60000 / ms / 5:0} wpm" : ""));
        }

        // Transport + mode
        var running = state is AutomationState.Playing or AutomationState.WaitingForDuty or AutomationState.Queued or AutomationState.Finishing;
        var paused = state == AutomationState.Paused;
        var y = pos.Y + size.Y - inset - 36 * s;
        ImGui.SetCursorScreenPos(new Vector2(x0, y));
        if (!running && !paused)
        {
            if (Widgets.IconButton("##c_start", Theme.Icon.Play, 26, Widgets.ButtonKind.Accent, "start")) c.Start();
        }
        else
        {
            if (paused) { if (Widgets.IconButton("##c_resume", Theme.Icon.Play, 26, Widgets.ButtonKind.Accent, "resume")) c.Resume(); }
            else { if (Widgets.IconButton("##c_pause", Theme.Icon.Pause, 26, Widgets.ButtonKind.Normal, "pause")) c.Pause(); }
        }
        ImGui.SameLine(0, 6 * s);
        if (!running && !paused) ImGui.BeginDisabled();
        if (Widgets.IconButton("##c_stop", Theme.Icon.Square, 26, Widgets.ButtonKind.Danger, "stop")) c.Stop();
        if (!running && !paused) ImGui.EndDisabled();

        ImGui.SameLine(0, 14 * s);
        var mode = t.InstantMode ? 1 : 0;
        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, y + 3 * s));
        if (Widgets.Segmented("##c_mode", ModeLabels, ref mode, pos.X + size.X - inset - 12 * s - ImGui.GetCursorScreenPos().X))
        {
            if (mode == 1) InstantModeSheet.Request(t, plugin.Config);
            else { t.InstantMode = false; plugin.Config.Save(); }
        }
    }

    public static (Vector4, string) StateBadge(AutomationState state) => state switch
    {
        AutomationState.Idle => (Theme.Theme.TextDim, "idle"),
        AutomationState.WaitingForDuty => (Theme.Theme.Warn, "waiting"),
        AutomationState.Queued => (Theme.Theme.Info, "queued"),
        AutomationState.Playing => (Theme.Theme.AccentColor, "playing"),
        AutomationState.Paused => (Theme.Theme.Warn, "paused"),
        AutomationState.Finishing => (Theme.Theme.Warn, "finishing"),
        AutomationState.Error => (Theme.Theme.Danger, "error"),
        _ => (Theme.Theme.TextDim, state.ToString().ToLowerInvariant()),
    };
}
