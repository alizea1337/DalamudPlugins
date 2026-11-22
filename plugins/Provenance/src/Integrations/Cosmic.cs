using System;
using System.Collections.Generic;
using System.Linq;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.WKS;
using Provenance.Core.Cosmic;

namespace Provenance.Integrations;

/// <summary>
/// Cosmic Exploration in the game: which planet you're on and which are open, the mission under way and its score,
/// the mission board, and taking, reporting and abandoning missions. Board handling follows what ECommons' window
/// wrappers expose; taking and reporting call the game's own mission module (FFXIVClientStructs).
/// </summary>
internal static unsafe class Cosmic
{
    /// <summary>The quest that opens each planet (Questionable's quest paths name them).</summary>
    private static readonly uint[] UnlockQuests = { 5253, 5409, 5448, 5483 };

    public static Planet? CurrentPlanet => Planets.OfTerritory(Services.ClientState.TerritoryType);

    public static bool Unlocked(Planet p) => QuestManager.IsQuestComplete(65536 + UnlockQuests[(int)p]);

    /// <summary>The newest planet you can go to, or null when Cosmic Exploration isn't open yet.</summary>
    public static Planet? Newest => Planets.All.Reverse().Cast<Planet?>().FirstOrDefault(p => Unlocked(p!.Value));

    /// <summary>The unlock quest's name, for the planet picker: "Go Forth, Brave Explorers".</summary>
    public static string UnlockQuestName(Planet p)
        => Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Quest>().GetRowOrDefault(65536 + UnlockQuests[(int)p])?.Name.ExtractText() ?? "its quest";

    private static WKSManager* Manager => WKSManager.Instance();

    /// <summary>The game's Cosmic Exploration state is there (it's loaded on the planets): rank unlocks and missions done can be read.</summary>
    public static bool Loaded => Manager != null;

    /// <summary>The mission under way (WKSMissionUnit row), or 0.</summary>
    public static uint ActiveMission => Manager == null ? 0u : Manager->State.CurrentMission.MissionUnitRowId;
    public static uint Score => Manager == null ? 0u : Manager->State.CurrentMission.ScoreUInt;

    /// <summary>The tier the mission under way has reached so far.</summary>
    public static Tier Rank => Manager == null ? Tier.None : (int)Manager->State.CurrentMission.Rank switch
    {
        1 => Tier.Bronze,
        2 => Tier.Silver,
        3 => Tier.Gold,
        _ => Tier.None,
    };

    public static bool Completed(uint mission) => Manager != null && Manager->IsMissionCompleted(mission);
    public static bool Golded(uint mission) => Manager != null && Manager->IsMissionGolded(mission);

    public static int Credits => GameState.CurrencyCount(Planets.Cosmocredit);

    public static bool Accept(uint mission)
    {
        if (Manager == null || Manager->MissionModule == null) return false;
        Manager->MissionModule->InitiateMission((ushort)mission);
        return true;
    }

    public static bool Report()
    {
        if (Manager == null || Manager->MissionModule == null) return false;
        Manager->MissionModule->ReportMission();
        return true;
    }

    public static bool Abandon()
    {
        if (Manager == null || Manager->MissionModule == null) return false;
        Manager->MissionModule->AbandonMission();
        return true;
    }

    public static bool BoardOpen => Interact.Ready("WKSMission") != null;

    /// <summary>Opens the mission board from the planet's HUD. False when the HUD isn't there.</summary>
    public static bool OpenBoard()
    {
        var hud = Interact.Ready("WKSHud");
        if (hud == null) return false;
        new AddonMaster.WKSHud((nint)hud).Mission();
        return true;
    }

    /// <summary>Which board tab is showing: 0 basic, 1 provisional, 2 critical (as the window counts them).</summary>
    public static uint BoardTab
    {
        get
        {
            var board = Interact.Ready("WKSMission");
            return board == null ? uint.MaxValue : new AddonMaster.WKSMission((nint)board).CurrentTab;
        }
    }

    public static void ShowTab(bool provisional)
    {
        var board = Interact.Ready("WKSMission");
        if (board == null) return;
        var m = new AddonMaster.WKSMission((nint)board);
        if (provisional) m.ProvisionalMissions();
        else m.BasicMissions();
    }

    /// <summary>The missions the board lists on the tab showing (for the class it shows), or null when it isn't open.</summary>
    public static List<uint>? Board()
    {
        var board = Interact.Ready("WKSMission");
        if (board == null) return null;
        try
        {
            return new AddonMaster.WKSMission((nint)board).StellerMissions.Select(e => e.MissionId).Where(id => id != 0).ToList();
        }
        catch (Exception e)
        {
            Services.Log.Debug("Couldn't read the mission board: {Message}", e.Message);
            return new List<uint>();
        }
    }

    public static void CloseBoard() => Interact.Close("WKSMission");
}
