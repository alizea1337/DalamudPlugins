using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace Provenance.Integrations;

/// <summary>
/// Provenance's own teleport: the game's aetheryte list, picking a destination, and the checks the
/// game makes before it lets you cast Teleport. No other plugin involved.
/// </summary>
internal static unsafe class Teleporter
{
    /// <param name="Estate">For houses and apartments: "Mist, ward 5, plot 12 (free company)"; null for aetherytes.</param>
    /// <param name="Ward">For houses and apartments: the ward (0-based).</param>
    /// <param name="Plot">For houses: the plot (0-based).</param>
    /// <param name="House">For houses and apartments: the game's id of the house (a HouseId), 0 for aetherytes.</param>
    /// <param name="Kind">For houses and apartments: what kind of estate it is.</param>
    public sealed record Destination(uint AetheryteId, byte SubIndex, string Name, uint TerritoryId, uint GilCost, string? Estate = null,
        int Ward = -1, int Plot = -1, bool Apartment = false, ulong House = 0,
        FFXIVClientStructs.FFXIV.Client.Game.EstateType Kind = FFXIVClientStructs.FFXIV.Client.Game.EstateType.Unknown3);

    /// <summary>Aetherytes the character can teleport to right now, with their current gil cost.</summary>
    public static List<Destination> Unlocked()
    {
        var result = new List<Destination>();
        var telepo = Telepo.Instance();
        if (telepo == null || !Services.ClientState.IsLoggedIn) return result;
        telepo->UpdateAetheryteList();
        var sheet = Services.Data.GetExcelSheet<Aetheryte>();
        foreach (var info in telepo->TeleportList)
        {
            if (info.AetheryteId == 0) continue;
            var row = sheet.GetRowOrDefault(info.AetheryteId);
            var name = row?.PlaceName.ValueNullable?.Name.ExtractText() ?? "Aetheryte " + info.AetheryteId;
            result.Add(new Destination(info.AetheryteId, info.SubIndex, name, info.TerritoryId, info.GilCost, EstateLabel(info),
                info.Ward, info.Plot, info.IsApartment, info.HouseId.Id, info.EstateType));
        }
        return result;
    }

    /// <summary>Houses and apartments in the teleport list (their zone is a housing area).</summary>
    public static List<Destination> Homes() => Unlocked().Where(d => d.Estate != null).ToList();

    private static string? EstateLabel(TeleportInfo info)
    {
        var territory = Services.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(info.TerritoryId);
        if (territory?.TerritoryIntendedUse.RowId != 13) return null;
        var area = territory.Value.PlaceName.ValueNullable?.Name.ExtractText() ?? "Housing";
        var kind = info.EstateType switch
        {
            FFXIVClientStructs.FFXIV.Client.Game.EstateType.FreeCompanyEstate => "free company house",
            FFXIVClientStructs.FFXIV.Client.Game.EstateType.PersonalChambers => "private chambers",
            FFXIVClientStructs.FFXIV.Client.Game.EstateType.SharedEstate => "shared house",
            FFXIVClientStructs.FFXIV.Client.Game.EstateType.ApartmentBuilding or FFXIVClientStructs.FFXIV.Client.Game.EstateType.ApartmentRoom => "apartment",
            _ => "house",
        };
        return info.IsApartment ? $"{area}, ward {info.Ward + 1} ({kind})" : $"{area}, ward {info.Ward + 1}, plot {info.Plot + 1} ({kind})";
    }

    /// <summary>Best match by name: exact first, then starts-with, then contains.</summary>
    public static Destination? Find(string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return null;
        var all = Unlocked();
        return all.FirstOrDefault(d => d.Name.Equals(q, StringComparison.CurrentCultureIgnoreCase))
               ?? all.FirstOrDefault(d => d.Name.StartsWith(q, StringComparison.CurrentCultureIgnoreCase))
               ?? all.FirstOrDefault(d => d.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>The unlocked aetheryte in a zone closest to a point (or the cheapest one when the point is unknown).</summary>
    public static Destination? NearestTo(uint territoryId, Vector3? point)
    {
        var candidates = Unlocked().Where(d => d.TerritoryId == territoryId).ToList();
        if (candidates.Count == 0) return null;
        if (point is not { } p) return candidates.OrderBy(d => d.GilCost).First();
        return candidates.OrderBy(d => Position(d.AetheryteId) is { } a ? Vector3.DistanceSquared(a, p) : float.MaxValue).First();
    }

    /// <summary>
    /// For an area without its own aetheryte (Limsa Lominsa Upper Decks, Old Gridania, …): the city's main
    /// aetheryte to teleport to and the attuned aethernet shard in that area to take from there.
    /// </summary>
    public static (Destination Main, uint Shard)? AethernetRoute(uint territoryId)
    {
        var sheet = Services.Data.GetExcelSheet<Aetheryte>();
        var unlocked = Unlocked();
        var ui = UIState.Instance();
        foreach (var shard in sheet.Where(a => !a.IsAetheryte && a.Territory.RowId == territoryId && a.AethernetName.RowId != 0))
        {
            if (ui == null || !ui->IsAetheryteUnlocked(shard.RowId)) continue;
            var main = sheet.FirstOrDefault(a => a.IsAetheryte && a.AethernetGroup == shard.AethernetGroup && a.AethernetGroup != 0);
            if (main.RowId == 0) continue;
            var dest = unlocked.FirstOrDefault(d => d.AetheryteId == main.RowId);
            if (dest != null) return (dest, shard.RowId);
        }
        return null;
    }

    /// <summary>
    /// Within a city: the attuned aethernet crystal nearest to the player and the one nearest to the
    /// target, when walking to the first and from the second saves a good part of the walk (a hop
    /// costs about half a minute of talking and loading). Null when walking is as quick.
    /// </summary>
    public static (uint From, uint To)? AethernetHop(uint territoryId, Vector3 me, Vector2 target)
    {
        var ui = UIState.Instance();
        if (ui == null) return null;
        var here = new Vector2(me.X, me.Z);
        var nodes = Services.Data.GetExcelSheet<Aetheryte>()
            .Where(a => a.Territory.RowId == territoryId && a.AethernetGroup != 0 && (a.IsAetheryte || a.AethernetName.RowId != 0))
            .Where(a => ui->IsAetheryteUnlocked(a.RowId))
            .Select(a => (a.RowId, a.AethernetGroup, At: Position(a.RowId)))
            .Where(n => n.At != null)
            .Select(n => (n.RowId, n.AethernetGroup, At: new Vector2(n.At!.Value.X, n.At.Value.Z)))
            .ToList();
        var walk = Vector2.Distance(here, target);
        (uint, uint)? best = null;
        var bestCost = walk * 0.7f;
        foreach (var group in nodes.GroupBy(n => n.AethernetGroup))
        {
            var entry = group.OrderBy(n => Vector2.Distance(here, n.At)).First();
            var exit = group.OrderBy(n => Vector2.Distance(n.At, target)).First();
            if (entry.RowId == exit.RowId) continue;
            var cost = Vector2.Distance(here, entry.At) + Vector2.Distance(exit.At, target) + 60f;
            if (cost < bestCost) { best = (entry.RowId, exit.RowId); bestCost = cost; }
        }
        return best;
    }

    /// <summary>Counted on top of the teleport for an area only the city's aethernet reaches (the hop takes a while).</summary>
    private const long AethernetExtra = 30;

    /// <summary>
    /// What getting to an area costs right now, in gil: nothing when already there or elsewhere in the same city
    /// (the aethernet is free), else the cheapest aetheryte in it, or the city's aetheryte for an area only its
    /// aethernet reaches. Null when the run can't get there. Used to pick the cheapest of several places.
    /// </summary>
    /// <param name="unlocked">The teleport list, when the caller has it already (see <see cref="Unlocked"/>).</param>
    public static long? TripCost(uint territoryId, List<Destination>? unlocked = null)
    {
        var here = Services.ClientState.TerritoryType;
        if (here == territoryId) return 0;
        unlocked ??= Unlocked();
        var direct = unlocked.Where(d => d.TerritoryId == territoryId && d.Estate == null).Select(d => (long?)d.GilCost).Min();
        if (direct != null) return direct;
        if (AethernetRoute(territoryId) is not { } route) return null;
        return route.Main.TerritoryId == here ? 0 : route.Main.GilCost + AethernetExtra;
    }

    /// <summary>Whether the run can get to an area: already there, an aetheryte in it, or an aethernet shard.</summary>
    public static bool CanReach(uint territoryId)
        => Services.ClientState.TerritoryType == territoryId
           || Unlocked().Any(d => d.TerritoryId == territoryId)
           || AethernetRoute(territoryId) != null;

    /// <summary>What picking a destination in the aethernet window came to.</summary>
    public enum AethernetPick
    {
        /// <summary>The window has no list yet.</summary>
        NotReady,
        Taken,
        /// <summary>The list doesn't have the destination.</summary>
        Missing,
        /// <summary>The destination isn't attuned.</summary>
        Locked,
    }

    /// <summary>Picks an aethernet destination in the open aethernet window.</summary>
    public static AethernetPick TakeAethernet(uint shardId)
    {
        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentTelepotTown.Instance();
        if (agent == null || agent->Data == null) return AethernetPick.NotReady;
        var data = agent->Data;
        if (data->AetheryteCount == 0) return AethernetPick.NotReady;
        for (byte i = 0; i < data->AetheryteCount && i < 64; i++)
        {
            if (data->Entries[i].AetheryteId != shardId) continue;
            if (data->Entries[i].IsLocked) return AethernetPick.Locked;
            MoveIntent.Expect(30, "aethernet to " + AethernetName(shardId));
            agent->TeleportToAetheryte(i);
            DebugLog.Write("travel", $"aethernet to {AethernetName(shardId)} (shard {shardId}) in zone {Services.ClientState.TerritoryType}");
            return AethernetPick.Taken;
        }
        return AethernetPick.Missing;
    }

    /// <summary>An aetheryte row is a city's main aetheryte (not an aethernet shard).</summary>
    public static bool IsMainAetheryte(uint aetheryteId)
        => Services.Data.GetExcelSheet<Aetheryte>().GetRowOrDefault(aetheryteId) is { IsAetheryte: true };

    /// <summary>What the aethernet calls a stop ("Gladiators' Guild").</summary>
    public static string AethernetName(uint aetheryteId)
        => Services.Data.GetExcelSheet<Aetheryte>().GetRowOrDefault(aetheryteId)?.AethernetName.ValueNullable?.Name.ExtractText() is { Length: > 0 } name ? name : "that stop";

    private static string? aethernetMenuText;

    /// <summary>
    /// The aetheryte menu's entry for the city's aethernet, in the client's language, from the game's own text
    /// ("Aethernet", "Ätherytennetz", "Réseau de transport urbain éthéré", "都市転送網"). Empty when it can't be read.
    /// </summary>
    public static string AethernetMenuText
    {
        get
        {
            if (aethernetMenuText != null) return aethernetMenuText;
            try
            {
                var sheet = Services.Data.Excel.GetSheet<Lumina.Excel.RawRow>(Dalamud.Utility.ClientLanguageExtensions.ToLumina(Services.Data.Language), "transport/Aetheryte");
                aethernetMenuText = sheet.GetRowOrDefault(1)?.ReadStringColumn(1).ExtractText().Trim().TrimEnd('.') ?? "";
            }
            catch (Exception e)
            {
                Services.Log.Debug("The aetheryte menu's text couldn't be read: {Message}", e.Message);
                aethernetMenuText = "";
            }
            return aethernetMenuText;
        }
    }

    private static readonly Dictionary<uint, Vector3?> positions = new();

    /// <summary>
    /// World position of an aetheryte crystal or aethernet shard: from its Level row when it has one,
    /// otherwise from the zone map's marker for it (height unknown, 0). Cached.
    /// </summary>
    public static Vector3? Position(uint aetheryteId)
    {
        if (positions.TryGetValue(aetheryteId, out var known)) return known;
        var row = Services.Data.GetExcelSheet<Aetheryte>().GetRowOrDefault(aetheryteId);
        Vector3? result = null;
        if (row is { } a)
        {
            if (a.Level.Count > 0 && a.Level[0].ValueNullable is { } level) result = new Vector3(level.X, level.Y, level.Z);
            else result = FromMapMarker(a);
        }
        positions[aetheryteId] = result;
        return result;
    }

    /// <summary>Map markers: type 3 is an aetheryte (keyed by its id), type 4 an aethernet shard (keyed by its name).</summary>
    private static Vector3? FromMapMarker(Aetheryte a)
    {
        if (a.Territory.ValueNullable?.Map.ValueNullable is not { } map) return null;
        var markers = Services.Data.GetSubrowExcelSheet<MapMarker>();
        if (!markers.HasRow(map.MapMarkerRange)) return null;
        foreach (var m in markers.GetRow(map.MapMarkerRange))
        {
            var match = (m.DataType == 3 && m.DataKey.RowId == a.RowId) || (m.DataType == 4 && a.AethernetName.RowId != 0 && m.DataKey.RowId == a.AethernetName.RowId);
            if (!match) continue;
            var c = map.SizeFactor / 100f;
            return new Vector3((m.X - 1024f) / c - map.OffsetX, 0, (m.Y - 1024f) / c - map.OffsetY);
        }
        return null;
    }

    /// <summary>Why a teleport can't start right now, or null when it can.</summary>
    public static string? Blocked()
    {
        var c = Services.Condition;
        if (!Services.ClientState.IsLoggedIn || Services.Objects.LocalPlayer == null) return "not logged in";
        if (Services.Objects.LocalPlayer.IsDead) return "you are knocked out";
        if (c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51]) return "already changing areas";
        if (c[ConditionFlag.InCombat]) return "you are in combat";
        if (c[ConditionFlag.Casting]) return "you are casting";
        if (c[ConditionFlag.BoundByDuty] || c[ConditionFlag.BoundByDuty56]) return "you are in a duty";
        if (c[ConditionFlag.Crafting] || c[ConditionFlag.Gathering] || c[ConditionFlag.Fishing]) return "you are crafting or gathering";
        if (c[ConditionFlag.OccupiedInQuestEvent] || c[ConditionFlag.Occupied]) return "you are busy with something else";
        if (c[ConditionFlag.Jumping]) return "you are jumping";
        return null;
    }

    /// <summary>Starts the Teleport cast. Returns why it couldn't, or null when the cast started.</summary>
    public static string? Teleport(Destination d)
    {
        if (Blocked() is { } why) { NoteRefusal(d, why); return why; }
        var telepo = Telepo.Instance();
        if (telepo == null) { NoteRefusal(d, "the teleport menu isn't available"); return "the teleport menu isn't available"; }
        MoveIntent.Expect(40, "teleport to " + (d.Estate ?? d.Name));
        if (telepo->Teleport(d.AetheryteId, d.SubIndex))
        {
            DebugLog.Write("travel", $"teleport to {d.Estate ?? d.Name} (aetheryte {d.AetheryteId}, zone {d.TerritoryId}, {d.GilCost} gil) from zone {Services.ClientState.TerritoryType}");
            return null;
        }
        NoteRefusal(d, "the game refused the teleport");
        return "the game refused the teleport";
    }

    private static (uint Aetheryte, string Why) lastRefusal;
    private static DateTime lastRefusalAt;

    /// <summary>A teleport that can't happen yet goes into the debug log once, not on every retry.</summary>
    private static void NoteRefusal(Destination d, string why)
    {
        if (lastRefusal == (d.AetheryteId, why) && DateTime.UtcNow - lastRefusalAt < TimeSpan.FromSeconds(15)) return;
        lastRefusal = (d.AetheryteId, why);
        lastRefusalAt = DateTime.UtcNow;
        DebugLog.Write("travel", $"teleport to {d.Estate ?? d.Name} not possible yet: {why}");
    }
}
