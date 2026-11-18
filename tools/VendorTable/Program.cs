// Builds the vendor position table Provenance ships (Resources/vendors.json).
//
// NPC positions aren't in the game's Excel sheets for most vendors, and reading the zone layout
// files in game is unsafe (some crash Lumina's parser). Teamcraft (MIT) publishes NPC map positions;
// this tool keeps only NPCs that sell something (gil shops, and the scrip/token exchanges that sell
// master books and folklore tomes) and converts their map coordinates to world X/Z with each map's
// scale and offset.
//
// Usage: dotnet run -- <game sqpack folder> <teamcraft npcs.json> <output vendors.json>
// npcs.json: https://raw.githubusercontent.com/ffxiv-teamcraft/ffxiv-teamcraft/staging/libs/data/src/lib/json/npcs.json
using System.Globalization;
using System.Text.Json;
using Lumina;
using Lumina.Excel.Sheets;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: VendorTable <sqpack> <npcs.json> <vendors.json>");
    return 1;
}

var game = new GameData(args[0], new LuminaOptions { DefaultExcelLanguage = Lumina.Data.Language.English });
var gilShops = game.GetSubrowExcelSheet<GilShopItem>()!.Where(r => r.Count > 0).Select(r => r.RowId).ToHashSet();
var exchanges = game.GetExcelSheet<SpecialShop>()!.Select(r => r.RowId)
    .Concat(game.GetExcelSheet<InclusionShop>()!.Select(r => r.RowId)).ToHashSet();
var topics = game.GetExcelSheet<TopicSelect>()!;
var pre = game.GetExcelSheet<PreHandler>()!;
var maps = game.GetExcelSheet<Map>()!;

bool SellsForGil(uint data, int depth)
{
    if (gilShops.Contains(data) || exchanges.Contains(data)) return true;
    if (depth > 2) return false;
    if (topics.GetRowOrDefault(data) is { } t) return t.Shop.Any(s => s.RowId != 0 && SellsForGil(s.RowId, depth + 1));
    if (pre.GetRowOrDefault(data) is { } p) return SellsForGil(p.Target.RowId, depth + 1);
    return false;
}

var vendors = game.GetExcelSheet<ENpcBase>()!
    .Where(n => n.ENpcData.Any(d => d.RowId != 0 && SellsForGil(d.RowId, 0)))
    .Select(n => n.RowId)
    .ToHashSet();

using var doc = JsonDocument.Parse(File.ReadAllBytes(args[1]));
var table = new SortedDictionary<uint, float[]>();
foreach (var npc in doc.RootElement.EnumerateObject())
{
    if (!uint.TryParse(npc.Name, out var id) || !vendors.Contains(id)) continue;
    if (!npc.Value.TryGetProperty("position", out var pos) || pos.ValueKind != JsonValueKind.Object) continue;
    if (!pos.TryGetProperty("map", out var mapEl) || !mapEl.TryGetUInt32(out var mapId)) continue;
    if (maps.GetRowOrDefault(mapId) is not { } map || map.TerritoryType.RowId == 0) continue;
    var x = pos.GetProperty("x").GetSingle();
    var y = pos.GetProperty("y").GetSingle();
    table[id] = new[] { map.TerritoryType.RowId, World(x, map.SizeFactor, map.OffsetX), World(y, map.SizeFactor, map.OffsetY) };
}

// Map coordinates (1..42 at scale 100) back to world units.
static float World(float mapCoord, ushort sizeFactor, short offset)
{
    var c = sizeFactor / 100f;
    return MathF.Round(((mapCoord - 1f) * c / 41f * 2048f - 1024f) / c - offset, 1);
}

var output = new
{
    source = "Teamcraft npcs.json (MIT, https://github.com/ffxiv-teamcraft/ffxiv-teamcraft), converted to world X/Z",
    generated = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    // npc id -> [territory id, world X, world Z]
    vendors = table.ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value),
};
File.WriteAllText(args[2], JsonSerializer.Serialize(output));
Console.WriteLine($"{vendors.Count} vendors and exchanges in the game data, {table.Count} with a position, written to {args[2]}");
if (table.TryGetValue(1000999, out var check)) Console.WriteLine($"check: Soemrwyb (1000999) -> territory {check[0]}, X {check[1]}, Z {check[2]} (layout file: 128, -29, 198)");
return 0;
