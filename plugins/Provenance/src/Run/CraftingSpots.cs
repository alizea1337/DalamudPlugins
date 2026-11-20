using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using Provenance.Data;
using Provenance.Integrations;

namespace Provenance.Run;

/// <summary>
/// The crafting spot: saving where you stand as one, telling whether you're there, and finding the door into a
/// house or apartment building on the way.
/// </summary>
internal static unsafe class CraftingSpots
{
    /// <summary>EObj of every apartment building's entrance.</summary>
    private const uint ApartmentEntrance = 2007402;
    /// <summary>EObjName row of a house's door ("entrance"); every EObj with this name counts as a door.</summary>
    private const uint HouseEntranceName = 2002737;
    /// <summary>Where "Go to your apartment" lives in the game's text (row 0, column 1), in the client language.</summary>
    private const string ApartmentMenuSheet = "custom/003/HouFixMansionEntrance_00359";
    /// <summary>Close enough to the saved spot to count as there.</summary>
    public const float Near = 3f;

    private static HashSet<uint>? houseDoors;

    /// <summary>
    /// Where you stand, as a crafting spot, or why it can't be one: a run has to be able to come back here by
    /// itself (a teleport and a walk, an inn, or a house or apartment in the teleport list).
    /// </summary>
    public static (CraftingSpotSettings? Spot, string? Why) Capture(GameCatalog? catalog)
    {
        var me = Services.Objects.LocalPlayer;
        if (me == null || Services.Condition[ConditionFlag.BetweenAreas]) return (null, "Stand where the run should craft first.");
        var territory = Services.ClientState.TerritoryType;
        var spot = new CraftingSpotSettings { Enabled = true, Territory = territory, X = me.Position.X, Y = me.Position.Y, Z = me.Position.Z };

        var hm = HousingManager.Instance();
        if (hm != null && hm->IsInside())
        {
            if (hm->IsInWorkshop()) return (null, "That's the workshop. Stand in the house itself.");
            var id = hm->GetCurrentIndoorHouseId();
            if (!id.IsApartment && id.RoomNumber > 0)
                return (null, "Private chambers can't be a crafting spot yet. Stand in the house's main rooms.");
            var homes = Teleporter.Homes()
                .Where(d => d.Apartment == id.IsApartment && d.Ward == id.WardIndex && (id.IsApartment || d.Plot == id.PlotIndex)).ToList();
            var home = homes.FirstOrDefault(d => d.TerritoryId == id.TerritoryTypeId) ?? (homes.Count == 1 ? homes[0] : null);
            if (home == null)
                return (null, "This house isn't in your teleport list, so a run couldn't come back to it. Use your own house, your free company's, a shared house or your apartment.");
            spot.Kind = id.IsApartment ? CraftingSpotKind.Apartment : CraftingSpotKind.House;
            spot.District = home.TerritoryId;
            spot.Ward = id.WardIndex;
            spot.Plot = id.PlotIndex;
            spot.Room = id.RoomNumber;
            spot.World = id.WorldId;
            spot.Aetheryte = home.AetheryteId;
            spot.SubIndex = home.SubIndex;
            spot.Label = "Inside " + (home.Estate ?? home.Name);
            return (spot, null);
        }

        var row = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().GetRowOrDefault(territory);
        if (row?.TerritoryIntendedUse.RowId == 2)
        {
            var inn = catalog?.Inns.FirstOrDefault(i => i.RoomTerritory == territory);
            if (inn == null) return (null, "Provenance doesn't know the innkeeper for this inn room, so a run couldn't come back to it.");
            spot.Kind = CraftingSpotKind.InnRoom;
            spot.Label = "Inn room in " + inn.City;
            return (spot, null);
        }
        if (!Teleporter.CanReach(territory))
            return (null, "A run can't travel back here by itself (a duty, an instance, or somewhere without an aetheryte).");
        spot.Kind = CraftingSpotKind.OpenWorld;
        spot.Label = row?.PlaceName.ValueNullable?.Name.ExtractText() is { Length: > 0 } place ? place : "Zone " + territory;
        return (spot, null);
    }

    /// <summary>In the spot's zone, and for a house or apartment inside the right one: what's left is a walk.</summary>
    public static bool InPlace(CraftingSpotSettings s)
    {
        if (Services.ClientState.TerritoryType != s.Territory) return false;
        if (s.Kind is not (CraftingSpotKind.House or CraftingSpotKind.Apartment)) return true;
        var hm = HousingManager.Instance();
        if (hm == null || !hm->IsInside()) return false;
        var id = hm->GetCurrentIndoorHouseId();
        var apartment = s.Kind == CraftingSpotKind.Apartment;
        return id.IsApartment == apartment && id.WardIndex == s.Ward
               && (apartment ? id.RoomNumber == s.Room : id.PlotIndex == s.Plot)
               && (s.World == 0 || id.WorldId == s.World);
    }

    /// <summary>Standing at the spot (within <see cref="Near"/> yalms).</summary>
    public static bool IsAt(CraftingSpotSettings s)
        => s.IsSet && InPlace(s) && Services.Objects.LocalPlayer is { } me
           && Vector2.Distance(new Vector2(me.Position.X, me.Position.Z), new Vector2(s.X, s.Z)) <= Near;

    /// <summary>Outside in the spot's housing district, in its ward.</summary>
    public static bool OutsideInWard(CraftingSpotSettings s)
    {
        if (Services.ClientState.TerritoryType != s.District) return false;
        var hm = HousingManager.Instance();
        return hm != null && hm->IsOutside() && hm->GetCurrentWard() == s.Ward;
    }

    /// <summary>
    /// The door to go through: the apartment building's entrance, or the nearest house door. A teleport to an
    /// estate lands at its gate, so the nearest door is the house's own.
    /// </summary>
    public static IGameObject? Door(CraftingSpotSettings s)
    {
        IGameObject? door;
        if (s.Kind == CraftingSpotKind.Apartment) door = Interact.Nearest(ObjectKind.EventObj, id => id == ApartmentEntrance);
        else
        {
            houseDoors ??= HouseDoors();
            door = Interact.Nearest(ObjectKind.EventObj, houseDoors.Contains);
        }
        return door != null && Interact.Distance(door) < 60f ? door : null;
    }

    /// <summary>Every EObj named like a house's door, in the client language.</summary>
    private static HashSet<uint> HouseDoors()
    {
        var names = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.EObjName>();
        var door = names.GetRowOrDefault(HouseEntranceName)?.Singular.ExtractText() ?? "";
        if (door.Length == 0) return new HashSet<uint> { HouseEntranceName };
        return names.Where(r => string.Equals(r.Singular.ExtractText(), door, StringComparison.OrdinalIgnoreCase)).Select(r => r.RowId).ToHashSet();
    }

    /// <summary>"Go to your apartment" in the client language (and in English as a fallback).</summary>
    public static string[] ApartmentEntry()
    {
        var labels = new List<string>();
        try
        {
            var sheet = Services.Data.GetExcelSheet<Lumina.Excel.RawRow>(name: ApartmentMenuSheet);
            if (sheet.GetRowOrDefault(0) is { } row && row.ReadStringColumn(1).ExtractText() is { Length: > 0 } text) labels.Add(text);
        }
        catch (Exception e)
        {
            Services.Log.Warning(e, "Couldn't read the apartment entrance menu text");
        }
        labels.Add("Go to your apartment");
        return labels.ToArray();
    }
}
