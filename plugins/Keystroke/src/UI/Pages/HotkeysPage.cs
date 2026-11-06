using System.Numerics;
using Dalamud.Bindings.ImGui;
using Keystroke.UI.Theme;

namespace Keystroke.UI.Pages;

internal sealed class HotkeysPage : Page
{
    private string? recording;

    public HotkeysPage(Plugin plugin) : base(plugin) { }

    public override string Title => Strings.Hotkeys;
    public override char Icon => Theme.Icon.KeyRound;

    public override void Draw()
    {
        var h = Config.Hotkeys;
        var changed = false;

        Widgets.PageHeader(Strings.Hotkeys, "Work even when the game isn't focused, so pick combinations no other app uses.");

        if (Widgets.BeginCard("keys"))
        {
            Binding("Start / Stop", h.StartStop, ref changed, Theme.Icon.Power);
            Widgets.RowSeparator();
            Binding("Pause / Resume", h.PauseResume, ref changed, Theme.Icon.Pause);
            Widgets.RowSeparator();
            Binding("Show / hide overlay", h.ToggleOverlay, ref changed, Theme.Icon.Eye);
            Widgets.RowSeparator();
            Binding("Open Keystroke", h.OpenWindow, ref changed, Theme.Icon.AppWindow);
            Widgets.EndCard();
        }

        Widgets.Caption("Click a shortcut, then press the new combination. Esc clears it.");

        if (changed) Save();
    }

    private void Binding(string label, HotkeyBinding b, ref bool changed, char icon)
    {
        var s = Widgets.Scale;
        var isRecording = recording == label;
        var text = isRecording ? "Press keys…" : Widgets.HotkeyLabel(b);
        var w = 150 * s;
        var enabled = b.Enabled;
        var focusOnly = b.OnlyWhenGameFocused;
        var localChanged = false;

        Widgets.Row(label, b.OnlyWhenGameFocused ? "Only while the game is focused" : "Global", w + 60 * s, Widgets.FrameHeight, () =>
        {
            if (Widgets.Toggle("##en", ref enabled)) { b.Enabled = enabled; localChanged = true; }
            ImGui.SameLine(0, 10 * s);
            if (isRecording) ImGui.PushStyleColor(ImGuiCol.Button, Theme.Theme.WithAlpha(Theme.Theme.Accent, 0.35f));
            if (ImGui.Button(text + "##bind", new Vector2(w, Widgets.FrameHeight)))
                recording = isRecording ? null : label;
            if (isRecording) ImGui.PopStyleColor();
            if (ImGui.IsItemHovered() && !isRecording) Widgets.Tooltip("Right-click to toggle 'only while focused'.");
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) { b.OnlyWhenGameFocused = !focusOnly; localChanged = true; }
        }, icon);

        if (isRecording && Plugin.Hotkeys.TryCaptureBinding(out var captured))
        {
            if (captured.Key == 0x1B) { b.Key = 0; b.Enabled = false; }
            else { b.Key = captured.Key; b.Ctrl = captured.Ctrl; b.Shift = captured.Shift; b.Alt = captured.Alt; b.Enabled = true; }
            recording = null;
            localChanged = true;
        }

        if (localChanged) changed = true;
    }
}
