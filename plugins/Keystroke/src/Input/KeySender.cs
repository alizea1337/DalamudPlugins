using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Keystroke.Input;

/// <summary>
/// Delivers keystrokes to the game. Background mode posts WM_KEYDOWN/WM_KEYUP to the game window
/// so the game's own message loop translates them into characters, which works whether or not the
/// window is focused. Foreground mode uses SendInput with scan codes and requires focus.
/// </summary>
internal sealed class KeySender
{
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmChar = 0x0102;
    private const int VkReturn = 0x0D;
    private const int VkBack = 0x08;
    private const int VkEscape = 0x1B;
    private const int VkShift = 0x10;

    private readonly nint hwnd = Process.GetCurrentProcess().MainWindowHandle;
    private readonly nint layout = GetKeyboardLayout(0);

    public bool WindowValid => hwnd != 0;

    public bool GameIsForeground() => GetForegroundWindow() == hwnd;

    /// <summary>Types one character. Returns false if it cannot be mapped to a key on the current layout.</summary>
    public bool SendChar(char c, bool background)
    {
        var scan = VkKeyScanEx(c, layout);
        if (scan == -1)
        {
            // Not on the layout: fall back to a direct character message (background only).
            if (!background) return false;
            PostMessageW(hwnd, WmChar, c, 1);
            return true;
        }
        var vk = scan & 0xFF;
        var shift = (scan & 0x100) != 0;
        var altGr = (scan & 0x600) == 0x600;
        if (altGr)
        {
            if (!background) return false;
            PostMessageW(hwnd, WmChar, c, 1);
            return true;
        }

        if (background)
        {
            // The game calls TranslateMessage on posted key messages, producing WM_CHAR with the
            // shift state read from the thread's key state; shifted chars are therefore posted as
            // WM_CHAR directly so case is preserved without touching global key state.
            if (shift) PostMessageW(hwnd, WmChar, c, 1);
            else PostKey(vk);
        }
        else
        {
            if (shift) SendInputKey(VkShift, false);
            SendInputKey(vk, false);
            SendInputKey(vk, true);
            if (shift) SendInputKey(VkShift, true);
        }
        return true;
    }

    public void SendEnter(bool background) => SendVk(VkReturn, background);

    public void SendBackspace(bool background) => SendVk(VkBack, background);

    public void SendEscape(bool background) => SendVk(VkEscape, background);

    private void SendVk(int vk, bool background)
    {
        if (background) PostKey(vk);
        else
        {
            SendInputKey(vk, false);
            SendInputKey(vk, true);
        }
    }

    private void PostKey(int vk)
    {
        var scan = MapVirtualKeyEx((uint)vk, 0, layout);
        var lDown = (nint)(1 | (scan << 16));
        var lUp = (nint)(1 | (scan << 16) | (1u << 30) | (1u << 31));
        PostMessageW(hwnd, WmKeyDown, vk, lDown);
        PostMessageW(hwnd, WmKeyUp, vk, lUp);
    }

    private void SendInputKey(int vk, bool up)
    {
        var scan = (ushort)MapVirtualKeyEx((uint)vk, 0, layout);
        var input = new INPUT
        {
            type = 1,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = scan,
                    dwFlags = (uint)(0x0008 | (up ? 0x0002 : 0)), // KEYEVENTF_SCANCODE | KEYUP
                },
            },
        };
        _ = SendInput(1, ref input, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public nint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public nint dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetKeyboardLayout(uint idThread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern short VkKeyScanEx(char ch, nint dwhkl);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, nint dwhkl);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, ref INPUT pInputs, int cbSize);
}
