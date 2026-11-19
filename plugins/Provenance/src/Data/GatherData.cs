using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Lumina.Excel.Sheets;
using Provenance.Core.Fishing;

namespace Provenance.Data;

/// <summary>
/// Gathering and fishing knowledge that isn't in the game sheets, shipped with the plugin (built by
/// tools/GbrExport from GatherBuddy Reborn's data, Apache-2.0, and AutoHook's bite times, Teamcraft
/// data): exact node positions, stand spots for awkward nodes, the fish database, and fishing spots
/// with recorded places to stand. Positions seen during the player's own runs are added locally
/// (never uploaded) through <see cref="Learn"/>.
/// </summary>
internal sealed class GatherData
{
    private readonly Dictionary<uint, List<Vector3>> points = new();
    private readonly List<(Vector3 Node, Vector3 Stand)> offsets = new();
    private readonly Dictionary<uint, FishInfo> fish = new();
    private readonly Dictionary<uint, FishingSpotInfo> spots = new();
    private readonly Dictionary<uint, List<(uint Weather, int Rate)>> weatherTables = new();

    private readonly Dictionary<int, (int Gathering, int Perception)> baseStats = new();

    /// <summary>Base Gathering and Perception for a gathering item level (Bountiful's bonus and boon chance scale against these).</summary>
    public (int Gathering, int Perception) BaseStats(int gatheringItemLevel) => baseStats.GetValueOrDefault(gatheringItemLevel);

    public int PointCount => points.Count;
    public int FishCount => fish.Count;

    /// <summary>Known world positions of a gathering point (GatheringPoint row).</summary>
    public IReadOnlyList<Vector3> Positions(uint gatheringPoint) => points.TryGetValue(gatheringPoint, out var l) ? l : Array.Empty<Vector3>();

    /// <summary>Where to stand for a node at this position: a recorded spot for awkward nodes, else the node itself.</summary>
    public Vector3 StandFor(Vector3 node)
    {
        foreach (var (n, stand) in offsets)
            if (Vector3.DistanceSquared(n, node) < 1f) return stand;
        return node;
    }

    public FishInfo? Fish(uint itemId) => fish.GetValueOrDefault(itemId);
    public IEnumerable<FishInfo> AllFish => fish.Values;
    public FishingSpotInfo? Spot(uint id) => spots.GetValueOrDefault(id);

    /// <summary>A zone's weather table: weathers with their chances in percent.</summary>
    public IReadOnlyList<(uint Weather, int Rate)> WeatherTable(uint territoryId)
    {
        if (weatherTables.TryGetValue(territoryId, out var t)) return t;
        var table = new List<(uint, int)>();
        var territory = Services.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryId);
        if (territory?.WeatherRate.ValueNullable is { } rate)
            for (var i = 0; i < rate.Weather.Count && i < rate.Rate.Count; i++)
                if (rate.Rate[i] > 0) table.Add((rate.Weather[i].RowId, rate.Rate[i]));
        weatherTables[territoryId] = table;
        return table;
    }

    /// <summary>Positions learned during the player's own runs (saved next to the plugin's config, never uploaded).</summary>
    private readonly Dictionary<uint, List<Vector3>> learned = new();

    private static string LearnedPath => Path.Combine(Services.PluginInterface.GetPluginConfigDirectory(), "learned_nodes.json");

    /// <summary>Adds a node position seen during a run. True when it was new.</summary>
    public bool Learn(uint gatheringPoint, Vector3 position)
    {
        if (!points.TryGetValue(gatheringPoint, out var list)) points[gatheringPoint] = list = new List<Vector3>();
        if (list.Any(p => Vector3.DistanceSquared(p, position) < 4f)) return false;
        list.Add(position);
        if (!learned.TryGetValue(gatheringPoint, out var mine)) learned[gatheringPoint] = mine = new List<Vector3>();
        mine.Add(position);
        return true;
    }

    /// <summary>Writes the learned positions to the plugin's config folder (this PC only).</summary>
    public void SaveLearned()
    {
        try
        {
            var data = learned.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.Select(p => new[] { MathF.Round(p.X, 1), MathF.Round(p.Y, 1), MathF.Round(p.Z, 1) }).ToList());
            File.WriteAllText(LearnedPath, JsonSerializer.Serialize(data));
        }
        catch (Exception e)
        {
            Services.Log.Warning(e, "Couldn't save learned node positions");
        }
    }

    private void LoadLearned()
    {
        try
        {
            if (!File.Exists(LearnedPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(LearnedPath));
            foreach (var p in doc.RootElement.EnumerateObject())
                if (uint.TryParse(p.Name, out var id))
                    foreach (var pos in p.Value.EnumerateArray().Select(V3))
                        Learn(id, pos);
        }
        catch (Exception e)
        {
            Services.Log.Warning(e, "Couldn't read learned node positions");
        }
    }

    public static GatherData Load()
    {
        var d = new GatherData();
        try
        {
            using (var nodes = Open("nodes.json"))
            {
                foreach (var p in nodes.RootElement.GetProperty("points").EnumerateObject())
                    if (uint.TryParse(p.Name, out var id))
                        d.points[id] = p.Value.EnumerateArray().Select(V3).ToList();
                foreach (var o in nodes.RootElement.GetProperty("offsets").EnumerateArray())
                {
                    var a = o.EnumerateArray().Select(x => x.GetSingle()).ToArray();
                    d.offsets.Add((new Vector3(a[0], a[1], a[2]), new Vector3(a[3], a[4], a[5])));
                }
            }
            using (var fishDoc = Open("fish.json"))
                foreach (var f in fishDoc.RootElement.GetProperty("fish").EnumerateObject())
                    if (uint.TryParse(f.Name, out var id)) d.fish[id] = ReadFish(id, f.Value);
            using (var stream = typeof(GatherData).Assembly.GetManifestResourceStream("Provenance.Resources.ilvconvert.csv"))
            using (var reader = new StreamReader(stream ?? throw new FileNotFoundException("ilvconvert.csv is missing from the plugin")))
            {
                reader.ReadLine();
                while (reader.ReadLine() is { } line)
                {
                    var parts = line.Split(',');
                    if (parts.Length >= 3 && int.TryParse(parts[0], out var glvl) && int.TryParse(parts[1], out var gat) && int.TryParse(parts[2], out var per))
                        d.baseStats[glvl] = (gat, per);
                }
            }
            using (var spotDoc = Open("fishspots.json"))
                foreach (var s in spotDoc.RootElement.GetProperty("spots").EnumerateObject())
                    if (uint.TryParse(s.Name, out var id)) d.spots[id] = ReadSpot(id, s.Value);
        }
        catch (Exception e)
        {
            Services.Log.Error(e, "Couldn't read the gathering and fishing data");
        }
        d.LoadLearned();
        return d;
    }

    private static JsonDocument Open(string name)
    {
        var stream = typeof(GatherData).Assembly.GetManifestResourceStream("Provenance.Resources." + name)
                     ?? throw new FileNotFoundException(name + " is missing from the plugin");
        using (stream) return JsonDocument.Parse(stream);
    }

    private static Vector3 V3(JsonElement e)
    {
        var a = e.EnumerateArray().Select(x => x.GetSingle()).ToArray();
        return new Vector3(a[0], a[1], a[2]);
    }

    private static FishInfo ReadFish(uint id, JsonElement f)
    {
        uint[] Ids(string name) => f.TryGetProperty(name, out var a) ? a.EnumerateArray().Select(x => x.GetUInt32()).ToArray() : Array.Empty<uint>();
        string Str(string name) => f.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
        Uptime? window = null;
        if (f.TryGetProperty("interval", out var iv))
        {
            var a = iv.EnumerateArray().Select(x => x.GetInt64()).ToArray();
            window = new Uptime(a[0], a[1], a[2]);
        }
        (float, float)? bite = null;
        if (f.TryGetProperty("bite", out var b))
        {
            var a = b.EnumerateArray().Select(x => x.GetSingle()).ToArray();
            bite = (a[0], a[1]);
        }
        var predators = f.TryGetProperty("predators", out var pr)
            ? pr.EnumerateArray().Select(p => { var a = p.EnumerateArray().Select(x => x.GetUInt32()).ToArray(); return (a[0], (int)a[1]); }).ToArray()
            : Array.Empty<(uint, int)>();
        return new FishInfo(
            id,
            f.GetProperty("spear").GetBoolean(),
            f.TryGetProperty("bait", out var bait) ? bait.GetUInt32() : 0,
            Ids("mooch"),
            predators,
            f.TryGetProperty("intuition", out var it) ? it.GetInt32() : 0,
            window,
            Ids("weather"),
            Ids("previousWeather"),
            Str("hookset") switch
            {
                "Precise" => HookSet.Precise, "Powerful" => HookSet.Powerful, "Hook" => HookSet.Hook,
                "DoubleHook" => HookSet.DoubleHook, "TripleHook" => HookSet.TripleHook, "Stellar" => HookSet.Stellar,
                _ => HookSet.Unknown,
            },
            Str("tug") switch { "Weak" => Tug.Weak, "Strong" => Tug.Strong, "Legendary" => Tug.Legendary, _ => Tug.Unknown },
            Str("snag") == "Required",
            Str("lure"),
            Enum.TryParse<SpearSize>(Str("size"), out var size) ? size : SpearSize.Unknown,
            Enum.TryParse<SpearSpeed>(Str("speed"), out var speed) ? speed : SpearSpeed.Unknown,
            f.TryGetProperty("surfaceSlap", out var slap) ? slap.GetUInt32() : 0,
            f.TryGetProperty("ocean", out var ocean) && ocean.GetBoolean(),
            f.TryGetProperty("big", out var big) && big.GetBoolean(),
            Ids("spots"),
            bite);
    }

    private static FishingSpotInfo ReadSpot(uint id, JsonElement s)
    {
        var map = s.GetProperty("map").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        var stands = s.GetProperty("stand").EnumerateArray()
            .Select(p => { var a = p.EnumerateArray().Select(x => x.GetSingle()).ToArray(); return (a[0], a[1], a[2], a[3]); })
            .ToArray();
        return new FishingSpotInfo(id, s.GetProperty("territory").GetUInt32(), s.GetProperty("spear").GetBoolean(),
            map[0], map[1], map[2], stands, s.TryGetProperty("pointBase", out var pb) ? pb.GetUInt32() : 0);
    }
}
