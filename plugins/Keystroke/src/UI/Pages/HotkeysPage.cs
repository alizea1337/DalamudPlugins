using System.Numerics;
using Dalamud.Bindings.ImGui;
using Keystroke.UI.Theme;

namespace Keystroke.UI.Pages;

internal sealed class HotkeysPage : Page
{
    private string? recording;

    public HotkeysPage(Plugin plugin) : base(plugin) { }

    public override string Title => "hotkeys";
    public override char Icon => Theme.Icon.KeyRound;

    public override void Draw()
    {
        var h = Config.Hotkeys;
        var changed = false;

        Widgets.BeginColumns(2);
        Widgets.BeginGroup("bindings");
        Widgets.Wrapped("Work while the game is unfocused, so pick combinations no other app uses.");
        ImGui.Dummy(new Vector2(0, 4 * Widgets.S));
        Binding("start / stop", h.StartStop, ref changed);
        Binding("pause / resume", h.PauseResume, ref changed);
        Binding("show / hide overlay", h.ToggleOverlay, ref changed);
        Binding("open keystroke", h.OpenWindow, ref changed);
        Widgets.EndGroup();

        Widgets.Column(1);
        Widgets.BeginGroup("how to");
        Widgets.Wrapped("Click a binding, then press the new combination. Esc clears it.");
        Widgets.Wrapped("Right-click a binding to toggle \"only while the game is focused\".");
        Widgets.EndGroup();
        Widgets.EndColumns();

        if (changed) Save();
    }

    private void Binding(string label, HotkeyBinding b, ref bool changed)
    {
        var s = Widgets.S;
        var isRecording = recording == label;
        var text = isRecording ? "press keys…" : Widgets.HotkeyLabel(b);
        var enabled = b.Enabled && b.Key != 0;
        var localChanged = false;

        Widgets.Row(label + (b.OnlyWhenGameFocused ? "  (focused only)" : ""), 120 * s, () =>
        {
            if (Widgets.Button(text + "##bind" + label, isRecording ? Widgets.ButtonKind.Accent : Widgets.ButtonKind.Normal, new Vector2(120 * s, Theme.Theme.ControlHeight * s + 2 * s)))
                recording = isRecording ? null : label;
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) { b.OnlyWhenGameFocused = !b.OnlyWhenGameFocused; localChanged = true; }
        }, enabled ? "Click to rebind. Esc while recording turns the binding off. Right-click toggles focused-only." : "Off. Click and press a combination to enable.");

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
