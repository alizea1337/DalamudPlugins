// Builds Provenance's gathering and fishing data from GatherBuddy Reborn (Apache-2.0) and AutoHook's
// bite times (BSD-3; Teamcraft data, MIT). Only data is written; no code from either is shipped.
//
//   nodes.json      GatheringPoint row -> exact world positions seen by GatherBuddy Reborn's users,
//                   plus hand-recorded stand spots for awkward nodes (original -> offset)
//   fish.json       per fish: bait, mooch chain, predators and intuition, uptime window, weather,
//                   hookset and tug, snagging, lures, spearfishing size/speed, spots, bite times
//   fishspots.json  per fishing spot: territory, map position, and recorded places to stand (x,y,z,facing)
//
// Usage: dotnet run -p:GbrRoot=<GBR clone> -- <sqpack> <GBR clone> <AutoHook clone> <out dir>
using System.Globalization;
using System.Numerics;
using GatherBuddy;
using Lumina;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

if (args.Length < 4)
{
    Console.Error.WriteLine("usage: GbrExport <sqpack> <GatherBuddyReborn clone> <AutoHook clone> <out dir>");
    return 1;
}
var (sqpack, gbr, autoHook, outDir) = (args[0], args[1], args[2], args[3]);
Directory.CreateDirectory(outDir);
var custom = Path.Combine(gbr, "GatherBuddy", "CustomInfo");
var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

var lumina = new Lumina.GameData(sqpack, new LuminaOptions { DefaultExcelLanguage = Lumina.Data.Language.English });
var world = JsonConvert.DeserializeObject<Dictionary<uint, List<Vector3>>>(File.ReadAllText(Path.Combine(custom, "world_locations.json")))!;
var data = new GatherBuddy.GameData(new GbrExport.LuminaDataManager(lumina), new Logger("GbrExport"), world,
    Path.Combine(Path.GetTempPath(), "gbrexport_no_overrides.json"));
Console.WriteLine($"GatherBuddy Reborn data: {data.Fishes.Count} fish, {data.FishingSpots.Count} fishing spots, {world.Count} node points");

// ---- nodes.json
var offsets = JArray.Parse(File.ReadAllText(Path.Combine(custom, "node_offsets.json")))
    .Select(o => new[]
    {
        R((float)o["Original"]!["X"]!), R((float)o["Original"]!["Y"]!), R((float)o["Original"]!["Z"]!),
        R((float)o["Offset"]!["X"]!), R((float)o["Offset"]!["Y"]!), R((float)o["Offset"]!["Z"]!),
    }).ToList();
Write("nodes.json", new
{
    source = "GatherBuddy Reborn world_locations.json and node_offsets.json (Apache-2.0, https://github.com/FFXIV-CombatReborn/GatherBuddyReborn)",
    generated = today,
    // GatheringPoint row -> [[x,y,z], ...]
    points = world.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture),
        kv => kv.Value.Select(p => new[] { R(p.X), R(p.Y), R(p.Z) }).ToList()),
    // [ox,oy,oz, sx,sy,sz]: for a node at o, stand at s
    offsets,
});

// ---- bite times from AutoHook's fish list (Teamcraft)
var bites = new Dictionary<uint, (float Min, float Max)>();
foreach (var f in JArray.Parse(File.ReadAllText(Path.Combine(autoHook, "AutoHook", "Data", "FishData", "fish_list.json"))))
{
    var min = (float?)f["BiteTimeMin"];
    var max = (float?)f["BiteTimeMax"];
    if (min is > 0 && max is > 0) bites[(uint)f["ItemId"]!] = (min.Value, max.Value);
}

// ---- fish.json
var fish = new SortedDictionary<uint, object>();
foreach (var f in data.Fishes.Values)
{
    fish[f.ItemId] = new
    {
        spear = f.IsSpearFish,
        bait = f.InitialBait.Id,
        mooch = f.Mooches.Select(m => m.ItemId).ToArray(),
        predators = f.Predators.Select(p => new[] { p.Item1.ItemId, (uint)p.Item2 }).ToArray(),
        intuition = f.IntuitionLength,
        // Uptime in real milliseconds: up for `on`, then down for `off`, aligned at `shift`.
        interval = f.Interval.AlwaysUp() ? null : new long[] { f.Interval.OnTime, f.Interval.OffTime, f.Interval.ShiftTime },
        weather = f.CurrentWeather.Select(w => w.Id).ToArray(),
        previousWeather = f.PreviousWeather.Select(w => w.Id).ToArray(),
        hookset = f.HookSet.ToString(),
        tug = f.BiteType.ToString(),
        snag = f.Snagging.ToString(),
        lure = f.Lure.ToString(),
        size = f.Size.ToString(),
        speed = f.Speed.ToString(),
        multiHook = f.MultiHookLower > 0 ? new[] { f.MultiHookLower, f.MultiHookUpper } : null,
        surfaceSlap = f.SurfaceSlap?.ItemId ?? 0,
        ocean = f.OceanArea.ToString() != "None",
        big = f.IsBigFish,
        spots = f.FishingSpots.Select(s => s.Id).ToArray(),
        bite = bites.TryGetValue(f.ItemId, out var b) ? new[] { b.Min, b.Max } : null,
    };
}
Write("fish.json", new
{
    source = "GatherBuddy Reborn fish data (Apache-2.0); bite times from AutoHook's fish_list.json (BSD-3, Teamcraft data, MIT)",
    generated = today,
    fish = fish.ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value),
});

// ---- fishspots.json: spot data plus up to 8 recorded places to stand per spot
var stands = new Dictionary<uint, List<float[]>>();
foreach (var r in JArray.Parse(File.ReadAllText(Path.Combine(custom, "fish_records.json"))))
{
    var spot = (uint)r["FishingSpot"]!;
    float x = (float)r["PositionX"]!, y = (float)r["PositionY"]!, z = (float)r["PositionZ"]!, rot = (float)r["Rotation"]!;
    if (spot == 0 || x == 0 || y == 0 || z == 0 || rot == 0) continue;
    if (!stands.TryGetValue(spot, out var list)) stands[spot] = list = new();
    if (list.Count >= 8 || list.Any(p => Vector3.Distance(new(p[0], p[1], p[2]), new(x, y, z)) < 3)) continue;
    list.Add(new[] { R(x), R(y), R(z), MathF.Round(rot, 3) });
}
var spots = new SortedDictionary<uint, object>();
foreach (var s in data.FishingSpots.Values)
{
    spots[s.Id] = new
    {
        territory = s.Territory.Id,
        spear = s.Spearfishing,
        map = new[] { s.IntegralXCoord, s.IntegralYCoord, (int)s.Radius },
        stand = stands.TryGetValue(s.Id, out var st) ? st : new List<float[]>(),
        // Spearfishing: the GatheringPointBase whose points (positions in nodes.json) are the spot's nodes.
        pointBase = s.Spearfishing ? s.SpearfishingSpotData?.GatheringPointBase.RowId : null,
    };
}
Write("fishspots.json", new
{
    source = "GatherBuddy Reborn fishing spots and fish_records.json stand positions (Apache-2.0)",
    generated = today,
    spots = spots.ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value),
});
Console.WriteLine($"fish {fish.Count} ({bites.Count} with bite times), spots {spots.Count} ({stands.Count} with stand positions), node points {world.Count}");
return 0;

static float R(float v) => MathF.Round(v, 1);

void Write(string name, object value)
{
    var path = Path.Combine(outDir, name);
    File.WriteAllText(path, JsonConvert.SerializeObject(value, Formatting.None, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
    Console.WriteLine($"{name}: {new FileInfo(path).Length / 1024} KB");
}
