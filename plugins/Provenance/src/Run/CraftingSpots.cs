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
            var homes = Teleporter.Homes();
            var home = MatchHome(homes, id);
            if (home == null)
            {
                Services.Log.Information("Crafting spot: no teleport entry matches the house here (ward {Ward}, plot {Plot}, room {Room}, apartment {Apartment}, district {District}, world {World}, id {Id:X}). Estates in the teleport list: {Homes}",
                    id.WardIndex, id.PlotIndex, id.RoomNumber, id.IsApartment, id.TerritoryTypeId, id.WorldId, id.Id,
                    string.Join("; ", homes.Select(d => $"{d.Estate} [{d.Kind}, ward {d.Ward}, plot {d.Plot}, district {d.TerritoryId}, id {d.House:X}]")));
                return (null, "This house isn't in your teleport list, so a run couldn't come back to it. Use your own house, your free company's, a shared house or your apartment. (What was compared is in /xllog.)");
            }
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
        var zone = row?.PlaceName.ValueNullable?.Name.ExtractText() is { Length: > 0 } place ? place : "Zone " + territory;

        // Out in a housing district: no aetheryte of its own, and each ward is a world apart. A run gets back through
        // the teleport to a house or apartment of yours in this ward.
        if (hm != null && hm->IsOutside())
        {
            var ward = hm->GetCurrentWard();
            var home = Teleporter.Homes().FirstOrDefault(d => d.TerritoryId == territory && WardOf(d) == ward);
            if (home == null)
                return (null, $"Out in {zone}, a run can only come back to a ward with a house or apartment of yours in the teleport list. Stand inside one, or in its ward.");
            spot.Kind = CraftingSpotKind.OpenWorld;
            spot.District = territory;
            spot.Ward = ward;
            spot.Aetheryte = home.AetheryteId;
            spot.SubIndex = home.SubIndex;
            spot.Label = $"{zone}, ward {ward + 1}";
            return (spot, null);
        }

        if (row?.TerritoryIntendedUse.RowId == 2)
        {
            var inn = catalog?.Inns.FirstOrDefault(i => i.RoomTerritory == territory);
            if (inn == null) return (null, "Provenance doesn't know the innkeeper for this inn room, so a run couldn't come back to it.");
            spot.Kind = CraftingSpotKind.InnRoom;
            spot.Label = "Inn room in " + inn.City;
            return (spot, null);
        }
        // Reachable from anywhere, not just from here: an aetheryte of the zone, or the aethernet.
        if (!Teleporter.Unlocked().Any(d => d.TerritoryId == territory && d.Estate == null) && Teleporter.AethernetRoute(territory) == null)
            return (null, "A run can't travel back here by itself (a duty, an instance, or somewhere without an aetheryte).");
        spot.Kind = CraftingSpotKind.OpenWorld;
        spot.Label = zone;
        return (spot, null);
    }

    /// <summary>The ward of a house or apartment in the teleport list (0-based): from the game's house id, else the list's own ward.</summary>
    private static int WardOf(Teleporter.Destination d)
    {
        if (d.House != 0 && d.House != ulong.MaxValue) return ((HouseId)d.House).WardIndex;
        return d.Ward;
    }

    /// <summary>Out in a housing ward (saved there, reached through an estate's teleport), not inside a house.</summary>
    public static bool InHousingWard(CraftingSpotSettings s) => s.Kind == CraftingSpotKind.OpenWorld && s.Aetheryte != 0;

    /// <summary>
    /// The teleport list's entry for the house you're in. The game's own id of each entry first; then which of
    /// your estates (free company, personal, shared, apartment) this is; then ward and plot.
    /// </summary>
    private static Teleporter.Destination? MatchHome(List<Teleporter.Destination> homes, HouseId here)
    {
        if (homes.FirstOrDefault(d => d.House != 0 && SameHouse(d.House, here)) is { } byId) return byId;

        var kinds = new (EstateType Kind, int Index)[]
        {
            (EstateType.FreeCompanyEstate, 0), (EstateType.PersonalEstate, 0), (EstateType.SharedEstate, 0),
            (EstateType.SharedEstate, 1), (EstateType.ApartmentRoom, 0),
        };
        foreach (var (kind, index) in kinds)
        {
            HouseId owned;
            try { owned = HousingManager.GetOwnedHouseId(kind, index); }
            catch (Exception) { continue; }
            if (owned.Id == 0 || owned.Id == ulong.MaxValue || !SameHouse(owned, here)) continue;
            var ofKind = homes.Where(d => d.Kind == kind || (kind == EstateType.ApartmentRoom && d.Apartment)).ToList();
            if (ofKind.Count == 1 || (ofKind.Count > 1 && kind != EstateType.SharedEstate)) return ofKind[0];
            if (ofKind.Count > index) return ofKind[index];
        }

        var byPlot = homes.Where(d => d.Apartment == here.IsApartment && d.Ward == here.WardIndex && (here.IsApartment || d.Plot == here.PlotIndex)).ToList();
        return byPlot.FirstOrDefault(d => d.TerritoryId == here.TerritoryTypeId) ?? (byPlot.Count == 1 ? byPlot[0] : null);
    }

    /// <summary>The same house (or apartment): ward, plot or room, district and world, where both ids say.</summary>
    private static bool SameHouse(HouseId a, HouseId b)
    {
        if (a.IsApartment != b.IsApartment || a.WardIndex != b.WardIndex) return false;
        if (a.IsApartment ? a.ApartmentDivision != b.ApartmentDivision || (a.RoomNumber != b.RoomNumber && a.RoomNumber > 0 && b.RoomNumber > 0)
                          : a.PlotIndex != b.PlotIndex) return false;
        if (a.TerritoryTypeId != 0 && b.TerritoryTypeId != 0 && a.TerritoryTypeId != b.TerritoryTypeId) return false;
        return a.WorldId == 0 || b.WorldId == 0 || a.WorldId == b.WorldId;
    }

    /// <summary>In the spot's zone, and for a house or apartment inside the right one: what's left is a walk.</summary>
    public static bool InPlace(CraftingSpotSettings s)
    {
        if (Services.ClientState.TerritoryType != s.Territory) return false;
        if (InHousingWard(s)) return OutsideInWard(s);
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
