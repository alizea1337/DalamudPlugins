using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Lumina.Excel.Sheets;

namespace Provenance.Data;

/// <summary>Where a monster spawns: its zone, the middle of a spawn area (world X/Z), the area's radius and the level seen there.</summary>
internal sealed record MobSpot(uint NameId, string Name, uint TerritoryId, Vector2 Position, float Radius, int Level)
{
    public string PlaceName => Services.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(TerritoryId)?.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
}

/// <summary>
/// Monster drops from the shipped mobs.json (built by tools/MobTable from Teamcraft's crowd-sourced
/// drop and spawn data): which monsters (BNpcName) drop an item and where they spawn in the open world.
/// </summary>
internal sealed class MobIndex
{
    private const string Resource = "Provenance.Resources.mobs.json";
    private readonly Dictionary<uint, List<MobSpot>> spots = new();

    public IEnumerable<uint> Items => spots.Keys;

    /// <summary>Spawn areas of monsters that drop the item, lowest level first.</summary>
    public IReadOnlyList<MobSpot> For(uint itemId) => spots.TryGetValue(itemId, out var l) ? l : Array.Empty<MobSpot>();

    public static MobIndex Load()
    {
        var index = new MobIndex();
        using var stream = typeof(MobIndex).Assembly.GetManifestResourceStream(Resource);
        if (stream == null)
        {
            Services.Log.Warning("mobs.json is missing from the plugin; monster drops can't be farmed");
            return index;
        }
        using var doc = JsonDocument.Parse(stream);
        var names = Services.Data.GetExcelSheet<BNpcName>();
        var areas = new Dictionary<uint, List<MobSpot>>();
        foreach (var mob in doc.RootElement.GetProperty("mobs").EnumerateObject())
        {
            if (!uint.TryParse(mob.Name, out var id)) continue;
            var name = names.GetRowOrDefault(id)?.Singular.ExtractText() ?? "monster";
            areas[id] = mob.Value.EnumerateArray().Select(a => new MobSpot(id, name, (uint)a[0].GetSingle(),
                new Vector2(a[1].GetSingle(), a[2].GetSingle()), a[3].GetSingle(), (int)a[4].GetSingle())).ToList();
        }
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateObject())
        {
            if (!uint.TryParse(item.Name, out var itemId)) continue;
            var list = item.Value.EnumerateArray().SelectMany(m => areas.GetValueOrDefault(m.GetUInt32()) ?? new List<MobSpot>())
                .OrderBy(s => s.Level).ToList();
            if (list.Count > 0) index.spots[itemId] = list;
        }
        return index;
    }
}
