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
    public sealed record Destination(uint AetheryteId, byte SubIndex, string Name, uint TerritoryId, uint GilCost);

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
            result.Add(new Destination(info.AetheryteId, info.SubIndex, name, info.TerritoryId, info.GilCost));
        }
        return result;
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

    /// <summary>World position of an aetheryte crystal, when the game data has one.</summary>
    public static Vector3? Position(uint aetheryteId)
    {
        var row = Services.Data.GetExcelSheet<Aetheryte>().GetRowOrDefault(aetheryteId);
        if (row is not { } a || a.Level.Count == 0 || a.Level[0].ValueNullable is not { } level) return null;
        return new Vector3(level.X, level.Y, level.Z);
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
        if (Blocked() is { } why) return why;
        var telepo = Telepo.Instance();
        if (telepo == null) return "the teleport menu isn't available";
        return telepo->Teleport(d.AetheryteId, d.SubIndex) ? null : "the game refused the teleport";
    }
}
