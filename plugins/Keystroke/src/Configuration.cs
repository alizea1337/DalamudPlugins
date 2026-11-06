using System;
using System.Numerics;
using Dalamud.Configuration;

namespace Keystroke;

/// <summary>Persisted user settings. Saved through Dalamud's plugin config; one class per feature area.</summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public AutomationSettings Automation { get; set; } = new();
    public TypingSettings Typing { get; set; } = new();
    public AlertSettings Alerts { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public OverlaySettings Overlay { get; set; } = new();
    public AdvancedSettings Advanced { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public WindowSettings Window { get; set; } = new();

    public void Save() => Services.PluginInterface.SavePluginConfig(this);

    public static Configuration Load()
    {
        var config = Services.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        // Nested objects can be null when a newer section is added to an older saved file.
        config.Automation ??= new();
        config.Typing ??= new();
        config.Alerts ??= new();
        config.Hotkeys ??= new();
        config.Overlay ??= new();
        config.Advanced ??= new();
        config.Appearance ??= new();
        config.Window ??= new();
        return config;
    }
}

[Serializable]
public sealed class AppearanceSettings
{
    /// <summary>Let the game show through the window and sidebar (macOS vibrancy look).</summary>
    public bool Glass { get; set; } = true;
    /// <summary>0 = fully opaque, 1 = as see-through as still readable.</summary>
    public float Transparency { get; set; } = 0.25f;
    /// <summary>Lift cards slightly so they stay readable on busy backgrounds.</summary>
    public bool ContrastCards { get; set; } = true;
}

[Serializable]
public sealed class AutomationSettings
{
    /// <summary>Start typing automatically when the Keybound Brawler duty begins.</summary>
    public bool AutoPlayOnEntry { get; set; } = true;
    public bool AutoQueueOnLobbyEntry { get; set; } = false;
    public bool AutoLeaveAfterResult { get; set; } = true;
    public bool AutoCommenceParty { get; set; } = false;

    public StopConditionSettings StopConditions { get; set; } = new();
}

[Serializable]
public sealed class StopConditionSettings
{
    public bool StopAfterGamesEnabled { get; set; } = false;
    public int StopAfterGames { get; set; } = 10;

    public bool StopAtMgpEnabled { get; set; } = false;
    public int StopAtMgp { get; set; } = 1_000_000;

    public bool StopAtTimeEnabled { get; set; } = false;
    public int StopAtHour { get; set; } = 23;
    public int StopAtMinute { get; set; } = 0;
}

public enum TypingProfile
{
    /// <summary>Everyday typist: the default.</summary>
    Normal = 0,
    /// <summary>Slow and uneven, like someone looking at the keyboard.</summary>
    Relaxed = 1,
    /// <summary>Fast, consistent touch typist.</summary>
    Skilled = 2,
    Custom = 3,
}

[Serializable]
public sealed class TypingSettings
{
    public TypingProfile Profile { get; set; } = TypingProfile.Normal;

    /// <summary>Median per-key delay in milliseconds (log-normal distribution around this value).</summary>
    public int MedianKeyDelayMs { get; set; } = 90;
    /// <summary>Spread of the per-key delay distribution; 0 = constant.</summary>
    public float KeyDelayJitter { get; set; } = 0.35f;
    /// <summary>Chance (0-1) that a key is preceded by a longer hesitation.</summary>
    public float HesitationChance { get; set; } = 0.02f;
    public int HesitationMinMs { get; set; } = 300;
    public int HesitationMaxMs { get; set; } = 700;
    public int BetweenWordsMs { get; set; } = 150;

    /// <summary>Copies the preset values for the current profile into the timing fields (no-op for Custom).</summary>
    public void ApplyPreset()
    {
        switch (Profile)
        {
            case TypingProfile.Relaxed:
                MedianKeyDelayMs = 150; KeyDelayJitter = 0.40f; HesitationChance = 0.05f;
                HesitationMinMs = 400; HesitationMaxMs = 900; BetweenWordsMs = 350;
                break;
            case TypingProfile.Normal:
                MedianKeyDelayMs = 90; KeyDelayJitter = 0.35f; HesitationChance = 0.02f;
                HesitationMinMs = 300; HesitationMaxMs = 700; BetweenWordsMs = 150;
                break;
            case TypingProfile.Skilled:
                MedianKeyDelayMs = 50; KeyDelayJitter = 0.25f; HesitationChance = 0.005f;
                HesitationMinMs = 200; HesitationMaxMs = 400; BetweenWordsMs = 60;
                break;
        }
    }

    public bool SkipOptionalPunctuation { get; set; } = false;
    public bool BackgroundInput { get; set; } = true;

    /// <summary>Insert the whole answer at once and submit it. Opt-in; detectable as non-human.</summary>
    public bool InstantMode { get; set; } = false;
    public bool InstantModeAcknowledged { get; set; } = false;
}

[Serializable]
public sealed class AlertSettings
{
    public bool MgcCapWarning { get; set; } = true;
    public int MgcCapWarningPercent { get; set; } = 90;

    public bool ToastEnabled { get; set; } = true;
    public bool SoundEnabled { get; set; } = true;
    public bool ChatEnabled { get; set; } = false;
    public bool WindowsToastEnabled { get; set; } = false;

    /// <summary>In-game sound effect id (1-16) played for alerts.</summary>
    public int SoundEffect { get; set; } = 9;

    public bool NotifyLoopFinished { get; set; } = true;
    public bool NotifyMgcWarning { get; set; } = true;
    public bool NotifyErrors { get; set; } = true;
    public bool NotifyGameResult { get; set; } = false;
}

[Serializable]
public sealed class HotkeyBinding
{
    public bool Enabled { get; set; } = true;
    public bool Ctrl { get; set; } = true;
    public bool Shift { get; set; } = true;
    public bool Alt { get; set; } = false;
    /// <summary>Win32 virtual-key code.</summary>
    public int Key { get; set; }
    public bool OnlyWhenGameFocused { get; set; } = false;

    public HotkeyBinding() { }

    public HotkeyBinding(int key) => Key = key;
}

[Serializable]
public sealed class HotkeySettings
{
    public HotkeyBinding StartStop { get; set; } = new(0x4B); // K
    public HotkeyBinding PauseResume { get; set; } = new(0x50); // P
    public HotkeyBinding ToggleOverlay { get; set; } = new(0x4F); // O
    public HotkeyBinding OpenWindow { get; set; } = new(0x4C); // L
}

[Serializable]
public sealed class OverlaySettings
{
    public bool Enabled { get; set; } = true;
    public bool Locked { get; set; } = false;
    public bool ClickThroughWhenLocked { get; set; } = true;
    public float Opacity { get; set; } = 0.92f;
    /// <summary>Show only inside the Keybound duty and lobby; otherwise always while logged in.</summary>
    public bool OnlyInKeyboundAreas { get; set; } = true;
    public Vector2 Position { get; set; } = new(40, 120);
    public bool ShowCurrentAnswer { get; set; } = true;
    public bool ShowSessionStats { get; set; } = true;
}

[Serializable]
public sealed class AdvancedSettings
{
    public bool InspectorEnabled { get; set; } = false;
    public bool AutomaticRecovery { get; set; } = true;
    public bool KeepActivityTimersAlive { get; set; } = true;
    public bool VerboseLogging { get; set; } = false;
}

[Serializable]
public sealed class WindowSettings
{
    public Vector2 Size { get; set; } = new(860, 580);
    public int LastPage { get; set; } = 0;
    /// <summary>Collapsed mini window with just the transport controls and typing mode.</summary>
    public bool Compact { get; set; } = false;
}
