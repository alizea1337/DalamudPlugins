using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace Keystroke.Game;

/// <summary>
/// Keybound Brawler identifiers. Values marked "hint" in docs/game-facts.md are not yet confirmed
/// in-game; keep them here so a correction is a one-line change.
/// </summary>
internal static class Keybound
{
    public const ushort DutyTerritory = 1359;
    public const ushort LobbyTerritory = 1360;
    public const uint ContentFinderCondition = 1095;

    public const uint MgpItemId = 29;
    public const uint MgcItemId = 52322;
    public const uint CofferItemId = 52450;

    public const uint CursedGazeActionId = 49529;
    public static readonly uint[] SensoryJammerActionIds = { 49532, 49702 };

    /// <summary>English cast names that block input; fallback when the action id is unknown.</summary>
    public static bool IsBlockingCastName(string name)
        => name.Contains("Sensory Jammer", System.StringComparison.OrdinalIgnoreCase)
        || name.Contains("Cursed Gaze", System.StringComparison.OrdinalIgnoreCase);

    // Button component node ids (verified 2026-11-06)
    public const uint RegisterDialogRegisterButton = 9;
    public const uint ContentsFinderCommenceButton = 63;
    public const uint ResultExitButton = 48;

    public const int NumberArrayIndex = 161;
    public const int StringArrayIndex = 128;

    public const string InputAddon = "KTGInput";
    public const string WordAddon = "KTGEWord";
    public const string ResultAddon = "KTGResult";
    public const string HudAddon = "KTGHud";
    public const string EnterDialogueAddon = "KTGEnterDialogue";
    public const string CastBarAddon = "KTGEnemyCastBar";
    public const string HpGaugeAddon = "KTGHudGaugeHp";

    public static readonly string[] KnownAddons =
    {
        InputAddon, WordAddon, ResultAddon, HudAddon, EnterDialogueAddon, CastBarAddon, HpGaugeAddon,
        "ContentsFinderConfirm", "SelectYesno",
    };

    /// <summary>Agent that owns input for Keybound Brawler; unnamed in FFXIVClientStructs (Unk398).</summary>
    public const AgentId InputAgent = (AgentId)398;

    public static readonly (string Name, AgentId Id)[] Agents =
    {
        ("KTGHud", AgentId.KTGHud),
        ("KTGInput (Unk398)", InputAgent),
        ("KTGEnterDialogue", AgentId.KTGEnterDialogue),
        ("Unk400", (AgentId)400),
        ("KTGResult", AgentId.KTGResult),
    };

    public static bool IsDuty(ushort territory) => territory == DutyTerritory;
    public static bool IsLobby(ushort territory) => territory == LobbyTerritory;
    public static bool IsKeyboundArea(ushort territory) => IsDuty(territory) || IsLobby(territory);
}
