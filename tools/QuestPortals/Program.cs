// Builds the portal table Provenance ships (Resources/portals.json): the NPCs and objects in the zones of the crafter
// and gatherer society dailies that warp the character, within the zone (A-4 Research, Hoper's Hold) or into a
// sub-zone (Zirgorteh the Open-armed, Elysion), with where each stands and where its warp lands. Only free warps open
// to everyone (PortalRoute.Usable): no ferries or airships that cost gil, no warps that show only during a quest.
//
// The game places these with its zone layout files (bg/.../level/*.lgb). The plugin mustn't read those in game
// (Lumina runs out of memory on the large ones), and some holders have no Level row (the A-4 Research portal), so
// this reads them offline, from the game's own files only.
//
// What it reads:
// - Warp rows with a question ("Proceed to Hoper's Hold?") and a destination territory;
// - who holds them: ENpcBase.ENpcData[], EObj.Data, directly or through ArrayEventHandler.Data[];
// - the territories of the crafter and gatherer society dailies (Quest.ClassJobCategory0 32, 33 or 35: giver, to-do
//   spots, TERRITORYTYPE script args), and the warps' destinations from there;
// - each warp's WarpCondition (its gil cost, the quests it needs);
// - in those zones' layout files, the event NPCs and objects that are holders, and the PopRange a warp lands on
//   (Warp.PopRange is that instance's id).
//
// Usage: dotnet run -- <game sqpack folder> <output portals.json>
using System.Globalization;
using System.Numerics;
using System.Text;
using Lumina;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel.Sheets;
using Provenance.Core.Tribes;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: QuestPortals <sqpack> <portals.json>");
    return 1;
}

var game = new GameData(args[0], new LuminaOptions { DefaultExcelLanguage = Lumina.Data.Language.English, PanicOnSheetChecksumMismatch = false });
var warps = game.GetExcelSheet<Warp>()!;
var eobjs = game.GetExcelSheet<EObj>()!;
var arrays = game.GetExcelSheet<ArrayEventHandler>()!;
var npcs = game.GetExcelSheet<ENpcBase>()!;
var territories = game.GetExcelSheet<TerritoryType>()!;
var levels = game.GetExcelSheet<Level>()!;
var quests = game.GetExcelSheet<Quest>()!;
var conditions = game.GetExcelSheet<WarpCondition>()!;

bool IsWarp(uint id) => id >> 16 == 2 && warps.GetRowOrDefault(id) is { } w && w.TerritoryType.RowId != 0 && !w.Question.IsEmpty;

List<uint> WarpsOf(uint data)
{
    var found = new List<uint>();
    if (IsWarp(data)) found.Add(data);
    else if (data >> 16 == 0xD && arrays.GetRowOrDefault(data) is { } a)
        found.AddRange(a.Data.Select(d => d.RowId).Where(IsWarp));
    return found;
}

var npcWarps = new Dictionary<uint, List<uint>>();
foreach (var n in npcs)
{
    var w = n.ENpcData.SelectMany(d => WarpsOf(d.RowId)).Distinct().ToList();
    if (w.Count > 0) npcWarps[n.RowId] = w;
}
var objWarps = new Dictionary<uint, List<uint>>();
foreach (var o in eobjs)
{
    var w = WarpsOf(o.Data.RowId);
    if (w.Count > 0) objWarps[o.RowId] = w;
}
Console.WriteLine($"{npcWarps.Count} NPCs and {objWarps.Count} objects hold a warp with a question");

// The zones of the crafter and gatherer society dailies (the run's quests): where they start and where their to-do
// lines point. Then the zones their portals lead to (sub-zones), for the way back out only.
var societyZones = new HashSet<uint>();
foreach (var q in quests.Where(q => q.BeastTribe.RowId != 0 && q.IsRepeatable && q.ClassJobCategory0.RowId is 32 or 33 or 35))
{
    societyZones.Add(q.IssuerLocation.ValueNullable?.Territory.RowId ?? 0);
    foreach (var todo in q.TodoParams)
    foreach (var loc in todo.ToDoLocation)
        if (loc.RowId != 0 && levels.GetRowOrDefault(loc.RowId) is { } l) societyZones.Add(l.Territory.RowId);
    foreach (var p in q.QuestParams)
        if (p.ScriptInstruction.ExtractText().StartsWith("TERRITORYTYPE", StringComparison.Ordinal)) societyZones.Add(p.ScriptArg);
}
societyZones.Remove(0);
var queue = new Queue<uint>(societyZones);
var seen = new HashSet<uint>(societyZones);
Console.WriteLine($"society daily zones: {string.Join(",", societyZones.OrderBy(z => z))}");

// Free, open to everyone, and in or out of a society zone.
bool Usable(uint territory, uint warp)
{
    var w = warps.GetRow(warp);
    var c = conditions.GetRowOrDefault(w.WarpCondition.RowId);
    var needsQuest = c is { } wc && new[] { wc.RequiredQuest1, wc.RequiredQuest2, wc.RequiredQuest3, wc.RequiredQuest4 }.Any(r => r.RowId != 0);
    var usable = PortalRoute.Usable(territory, w.TerritoryType.RowId, c?.Gil ?? 0, needsQuest, societyZones);
    if (!usable && (societyZones.Contains(territory) || societyZones.Contains(w.TerritoryType.RowId)))
        Console.WriteLine($"left out: {territory} warp {warp} \"{w.Question.ExtractText()}\" -> {w.TerritoryType.RowId} (gil {c?.Gil ?? 0}, needs a quest {needsQuest})");
    return usable;
}

var holders = new List<(uint Territory, uint Holder, string Kind, Vector3 At, string Layer, List<uint> Warps)>();
var pops = new Dictionary<uint, (uint Territory, Vector3 At)>();
while (queue.Count > 0)
{
    var t = queue.Dequeue();
    var society = societyZones.Contains(t);
    if (territories.GetRowOrDefault(t) is not { } tt) continue;
    var bg = tt.Bg.ExtractText();
    if (bg.Length == 0 || bg.LastIndexOf('/') < 0) continue;
    var dir = bg[..bg.LastIndexOf('/')];
    foreach (var file in new[] { "planevent", "bg", "planmap", "planlive" })
    {
        LgbFile? lgb;
        try { lgb = game.GetFile<LgbFile>($"bg/{dir}/{file}.lgb"); }
        catch (Exception e) { Console.Error.WriteLine($"{t} {file}: {e.GetType().Name}"); continue; }
        if (lgb == null) continue;
        foreach (var layer in lgb.Layers)
        foreach (var inst in layer.InstanceObjects)
        {
            var at = new Vector3(inst.Transform.Translation.X, inst.Transform.Translation.Y, inst.Transform.Translation.Z);
            if (inst.AssetType == LayerEntryType.PopRange) { pops.TryAdd(inst.InstanceId, (t, at)); continue; }
            var (kind, id) = inst.Object switch
            {
                LayerCommon.ENPCInstanceObject en => ("enpc", en.ParentData.ParentData.BaseId),
                LayerCommon.EventInstanceObject eo => ("eobj", eo.ParentData.BaseId),
                _ => ("", 0u),
            };
            var held = kind == "enpc" ? npcWarps.GetValueOrDefault(id) : kind == "eobj" ? objWarps.GetValueOrDefault(id) : null;
            // Only the warps the trips may take: in a society zone or back into one, free, open to everyone.
            held = held?.Where(w => Usable(t, w)).ToList();
            if (held is not { Count: > 0 }) continue;
            holders.Add((t, id, kind, at, layer.Name, held));
            if (society)
                foreach (var w in held)
                    if (seen.Add(warps.GetRow(w).TerritoryType.RowId)) queue.Enqueue(warps.GetRow(w).TerritoryType.RowId);
        }
    }
}

static string F(float v) => Math.Round(v, 1).ToString(CultureInfo.InvariantCulture);
static string P(Vector3 v) => $"[{F(v.X)},{F(v.Y)},{F(v.Z)}]";

var sb = new StringBuilder();
sb.Append("{\"source\":\"the game's zone layout files (bg/.../level/*.lgb) and Warp, EObj, ENpcBase, ArrayEventHandler sheets, read by tools/QuestPortals\",");
sb.Append($"\"generated\":\"{DateTime.UtcNow:yyyy-MM-dd}\",\"portals\":[");
var first = true;
foreach (var h in holders.DistinctBy(h => (h.Territory, h.Holder, h.At)).OrderBy(h => h.Territory).ThenBy(h => h.Holder))
foreach (var w in h.Warps)
{
    var warp = warps.GetRow(w);
    var to = warp.TerritoryType.RowId;
    var arrival = pops.TryGetValue(warp.PopRange.RowId, out var pop) && pop.Territory == to ? P(pop.At) : "null";
    if (!first) sb.Append(',');
    first = false;
    sb.Append($"\n{{\"territory\":{h.Territory},\"holder\":{h.Holder},\"kind\":\"{h.Kind}\",\"at\":{P(h.At)},\"warp\":{w},\"to\":{to},\"arrival\":{arrival}}}");
    Console.WriteLine($"{h.Territory} {h.Kind} {h.Holder} at {P(h.At)} ({h.Layer}): warp {w} \"{warp.Question.ExtractText()}\" -> {to} arrival {arrival}");
}
sb.Append("\n]}\n");
File.WriteAllText(args[1], sb.ToString());
Console.WriteLine($"wrote {args[1]}");
return 0;
