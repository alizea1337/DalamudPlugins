using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Keystroke.Input;

/// <summary>
/// Global hotkeys polled on the framework tick via GetAsyncKeyState, so they work whether or
/// not the game window is focused. Each binding fires once per press with a repeat guard.
/// </summary>
internal sealed class HotkeyService
{
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private static readonly TimeSpan RepeatGuard = TimeSpan.FromMilliseconds(300);

    private readonly Configuration config;
    private readonly Dictionary<HotkeyBinding, DateTime> lastFired = new();
    private readonly HashSet<HotkeyBinding> held = new();

    public event Action? StartStop;
    public event Action? PauseResume;
    public event Action? ToggleOverlay;
    public event Action? OpenWindow;

    /// <summary>While true, Tick does not fire bindings (the Hotkeys page is capturing a new one).</summary>
    public bool Suspended { get; set; }

    public HotkeyService(Configuration config) => this.config = config;

    public void Tick()
    {
        if (Suspended) return;
        var focused = GameIsForeground();
        var h = config.Hotkeys;
        Check(h.StartStop, focused, StartStop);
        Check(h.PauseResume, focused, PauseResume);
        Check(h.ToggleOverlay, focused, ToggleOverlay);
        Check(h.OpenWindow, focused, OpenWindow);
    }

    private void Check(HotkeyBinding b, bool focused, Action? handler)
    {
        if (!b.Enabled || b.Key == 0) { held.Remove(b); return; }
        if (b.OnlyWhenGameFocused && !focused) { held.Remove(b); return; }

        var down = IsDown(b.Key) && IsDown(VkControl) == b.Ctrl && IsDown(VkShift) == b.Shift && IsDown(VkMenu) == b.Alt;
        if (!down) { held.Remove(b); return; }
        if (held.Contains(b)) return;
        held.Add(b);

        var now = DateTime.UtcNow;
        if (lastFired.TryGetValue(b, out var last) && now - last < RepeatGuard) return;
        lastFired[b] = now;
        try { handler?.Invoke(); }
        catch (Exception e) { Services.Log.Error(e, "Hotkey handler failed"); }
    }

    /// <summary>Returns a binding when a non-modifier key is currently pressed (used by the capture UI).</summary>
    public bool TryCaptureBinding(out HotkeyBinding binding)
    {
        Suspended = true;
        binding = new HotkeyBinding();
        for (var vk = 0x08; vk <= 0xFE; vk++)
        {
            if (vk is VkControl or VkShift or VkMenu or VkLWin or VkRWin or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5) continue;
            if (vk is 0x01 or 0x02 or 0x04 or 0x05 or 0x06) continue; // mouse buttons
            if (!IsDown(vk)) continue;
            binding = new HotkeyBinding(vk) { Ctrl = IsDown(VkControl), Shift = IsDown(VkShift), Alt = IsDown(VkMenu) };
            Suspended = false;
            return true;
        }
        return false;
    }

    public static bool GameIsForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0) return false;
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        return pid == Environment.ProcessId;
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
}
