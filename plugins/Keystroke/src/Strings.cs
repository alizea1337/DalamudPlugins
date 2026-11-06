namespace Keystroke;

/// <summary>
/// All user-facing UI strings. English only for now; keeping them here makes a later
/// localisation pass mechanical.
/// </summary>
internal static class Strings
{
    public const string PluginName = "Keystroke";
    public const string Command = "/keystroke";
    public const string CommandHelp = "Open the Keystroke window. /keystroke compact toggles the compact view, /keystroke inspector opens the Inspector.";

    // Sidebar
    public const string Dashboard = "Dashboard";
    public const string Automation = "Automation";
    public const string Typing = "Typing";
    public const string RewardsAlerts = "Rewards & Alerts";
    public const string Hotkeys = "Hotkeys";
    public const string Overlay = "Overlay";
    public const string Appearance = "Appearance";
    public const string Advanced = "Advanced";
    public const string About = "About";

    // Dashboard
    public const string StateIdle = "Idle";
    public const string StateWaitingForDuty = "Waiting for duty";
    public const string StateQueued = "Queued";
    public const string StatePlaying = "Playing";
    public const string StatePaused = "Paused";
    public const string StateStopping = "Finishing current game";
    public const string StateError = "Stopped on error";
    public const string NotInKeybound = "Not in the Keybound Brawler lobby or duty.";
    public const string NotLoggedIn = "Log in to a character to get started.";

    // Instant mode confirmation
    public const string InstantModeTitle = "Enable Instant mode?";
    public const string InstantModeBody =
        "Instant mode inserts the whole answer into the input field and submits it immediately instead of typing. " +
        "It is far faster than any human and is easy to notice for other players. Keystroke recommends leaving it off " +
        "unless you play in a premade group that doesn't mind.";
    public const string InstantModeConfirm = "Enable Instant mode";
    public const string Cancel = "Cancel";
}
