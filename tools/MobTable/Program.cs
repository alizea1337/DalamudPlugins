// Builds the monster-drop table Provenance ships (Resources/mobs.json).
//
// Which monsters drop which items, and where they spawn, comes from Teamcraft (MIT,
// https://github.com/ffxiv-teamcraft/ffxiv-teamcraft): drop-sources.json (item -> BNpcName ids) and
// monsters.json (BNpcName id -> crowd-sourced positions in map coordinates). This tool keeps open-world
// spawns that aren't tied to a FATE, groups each monster's positions per zone into a few spawn areas,
// and converts them to world X/Z with each map's scale and offset.
//
// Usage: dotnet run -- <game sqpack folder> <drop-sources.json> <monsters.json> <output mobs.json>
// https://raw.githubusercontent.com/ffxiv-teamcraft/ffxiv-teamcraft/staging/libs/data/src/lib/json/drop-sources.json
// https://raw.githubusercontent.com/ffxiv-teamcraft/ffxiv-teamcraft/staging/libs/data/src/lib/json/monsters.json
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Lumina;
using Lumina.Excel.Sheets;

if (args.Length < 4)
{
    Console.Error.WriteLine("usage: MobTable <sqpack> <drop-sources.json> <monsters.json> <mobs.json>");
    return 1;
}

var game = new GameData(args[0], new LuminaOptions { DefaultExcelLanguage = Lumina.Data.Language.English });
var maps = game.GetExcelSheet<Map>()!;
var territories = game.GetExcelSheet<TerritoryType>()!;
using var drops = JsonDocument.Parse(File.ReadAllBytes(args[1]));
using var monsters = JsonDocument.Parse(File.ReadAllBytes(args[2]));

const float ClusterYalms = 80f;
var items = new SortedDictionary<uint, List<uint>>();
var mobs = new SortedDictionary<uint, List<float[]>>();

foreach (var item in drops.RootElement.EnumerateObject())
{
    if (!uint.TryParse(item.Name, out var itemId)) continue;
    foreach (var m in item.Value.EnumerateArray())
    {
        var mobId = m.GetUInt32();
        if (!mobs.ContainsKey(mobId))
        {
            var areas = Areas(mobId);
            if (areas.Count == 0) continue;
            mobs[mobId] = areas;
        }
        if (!items.TryGetValue(itemId, out var list)) items[itemId] = list = new List<uint>();
        if (!list.Contains(mobId)) list.Add(mobId);
    }
}

// Spawn areas of one monster: [territory, world X, world Z, radius, level], open world only, no FATEs.
List<float[]> Areas(uint mobId)
{
    var result = new List<float[]>();
    if (!monsters.RootElement.TryGetProperty(mobId.ToString(CultureInfo.InvariantCulture), out var mob)) return result;
    if (!mob.TryGetProperty("positions", out var positions) || positions.ValueKind != JsonValueKind.Array) return result;
    var points = new List<(uint Territory, Vector2 At, int Level)>();
    foreach (var p in positions.EnumerateArray())
    {
        if (p.TryGetProperty("fate", out var fate) && fate.ValueKind == JsonValueKind.Number && fate.GetInt32() != 0) continue;
        var level = p.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.Number ? lv.GetInt32() : 0;
        if (level is <= 0 or > 200) continue;
        if (maps.GetRowOrDefault(p.GetProperty("map").GetUInt32()) is not { } map) continue;
        var territory = map.TerritoryType.RowId;
        if (territories.GetRowOrDefault(territory) is not { } t || t.TerritoryIntendedUse.RowId != 1) continue; // open world
        var x = World(p.GetProperty("x").GetSingle(), map.SizeFactor, map.OffsetX);
        var z = World(p.GetProperty("y").GetSingle(), map.SizeFactor, map.OffsetY);
        points.Add((territory, new Vector2(x, z), level));
    }
    // Greedy grouping: a point within ClusterYalms of a group's centre joins it.
    var groups = new List<(uint Territory, List<Vector2> Points, int Level)>();
    foreach (var (territory, at, level) in points)
    {
        var g = groups.FindIndex(g => g.Territory == territory && Vector2.Distance(Centre(g.Points), at) <= ClusterYalms);
        if (g < 0) groups.Add((territory, new List<Vector2> { at }, level));
        else { groups[g].Points.Add(at); groups[g] = (territory, groups[g].Points, Math.Min(groups[g].Level, level)); }
    }
    foreach (var (territory, pts, level) in groups.OrderByDescending(g => g.Points.Count).Take(4))
    {
        var c = Centre(pts);
        var radius = Math.Clamp(pts.Max(p => Vector2.Distance(p, c)), 15f, 120f);
        result.Add(new[] { territory, MathF.Round(c.X, 1), MathF.Round(c.Y, 1), MathF.Round(radius), level });
    }
    return result;
}

static Vector2 Centre(List<Vector2> pts) => new(pts.Average(p => p.X), pts.Average(p => p.Y));

// Map coordinates (1..42 at scale 100) back to world units.
static float World(float mapCoord, ushort sizeFactor, short offset)
{
    var c = sizeFactor / 100f;
    return ((mapCoord - 1f) * c / 41f * 2048f - 1024f) / c - offset;
}

var output = new
{
    source = "Teamcraft drop-sources.json and monsters.json (MIT, https://github.com/ffxiv-teamcraft/ffxiv-teamcraft), open-world non-FATE spawns, world X/Z",
    generated = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    // item id -> BNpcName ids that drop it
    items = items.ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value),
    // BNpcName id -> spawn areas [territory id, world X, world Z, radius, level]
    mobs = mobs.ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value),
};
File.WriteAllText(args[3], JsonSerializer.Serialize(output));
Console.WriteLine($"{items.Count} items, {mobs.Count} monsters with open-world spawns, written to {args[3]}");
return 0;
