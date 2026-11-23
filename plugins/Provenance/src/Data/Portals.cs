using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Provenance.Core.Tribes;

namespace Provenance.Data;

/// <summary>
/// The NPCs and objects that warp the character after a question, in the zones of the crafter and gatherer society
/// dailies: into sub-zones (Zirgorteh the Open-armed, Elysion) and back, and across a zone to parts only a portal
/// reaches (A-4 Research, Hoper's Hold). Only free warps open to everyone (<see cref="PortalRoute.Usable"/>): no ferry
/// or airship that costs gil, no warp that shows only during a quest. From the shipped portals.json, built offline by
/// <c>tools/QuestPortals</c> from the game's zone layout files, which the plugin doesn't read in game.
/// </summary>
internal static class Portals
{
    private const string Resource = "Provenance.Resources.portals.json";
    private static readonly Lazy<IReadOnlyList<Portal>> All = new(Load);

    public static IReadOnlyList<Portal> List => All.Value;

    /// <summary>The question the game asks before a warp (<c>Warp.Question</c>, client language), or "".</summary>
    public static string Question(uint warp)
        => Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Warp>().GetRowOrDefault(warp)?.Question.ExtractText() ?? "";

    /// <summary>The menu entry that may stand for a warp on an NPC with several things to say (<c>Warp.Name</c>), or "".</summary>
    public static string MenuName(uint warp)
        => Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Warp>().GetRowOrDefault(warp)?.Name.ExtractText() ?? "";

    private static IReadOnlyList<Portal> Load()
    {
        var result = new List<Portal>();
        try
        {
            using var stream = typeof(Portals).Assembly.GetManifestResourceStream(Resource);
            if (stream == null)
            {
                Services.Log.Warning("portals.json is missing from the plugin; quest steps behind portals won't be reached");
                return result;
            }
            using var doc = JsonDocument.Parse(stream);
            foreach (var p in doc.RootElement.GetProperty("portals").EnumerateArray())
            {
                static Vector3 V(JsonElement a) => new(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle());
                var arrival = p.GetProperty("arrival");
                result.Add(new Portal(p.GetProperty("territory").GetUInt32(), p.GetProperty("holder").GetUInt32(),
                    p.GetProperty("kind").GetString() == "enpc", V(p.GetProperty("at")), p.GetProperty("warp").GetUInt32(),
                    p.GetProperty("to").GetUInt32(), arrival.ValueKind == JsonValueKind.Array ? V(arrival) : null));
            }
        }
        catch (Exception e)
        {
            Services.Log.Warning(e, "Couldn't read portals.json");
        }
        return result;
    }

    /// <summary>Portals in a zone.</summary>
    public static IEnumerable<Portal> In(uint territory) => List.Where(p => p.Territory == territory);
}
