using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Lumina.Excel.Sheets;

namespace Provenance.Data;

/// <summary>A spot a quest's to-do line points at (a <c>Level</c> row): where, how wide, and what stands there.</summary>
/// <param name="Object">The ENpc, EObj or BNpc base the row places (0 when none).</param>
internal sealed record QuestSpot(uint Level, uint Territory, Vector3 Position, float Radius, uint Object);

/// <summary>One to-do line of a quest: done once the quest reaches <paramref name="Sequence"/>.</summary>
/// <param name="Count">The number of targets on the line (not a count of items; 0 on many lines).</param>
internal sealed record QuestTodo(int Index, byte Sequence, byte Count, IReadOnlyList<QuestSpot> Spots);

/// <summary>
/// What the game data says about a quest beyond its sheet row: the script's named arguments
/// (<c>Quest.QuestParams</c>: ACTOR0, RITEM1, EMOTE0, ...), its to-do lines with their spots, and its own text sheet
/// (journal lines, the "/say" phrases) in the client's language. Shared by the Tribes page, the run and the step
/// engine; read once per quest and kept.
/// </summary>
/// <remarks>
/// Facts from the allied-society research (scratchpad reports): script args with values of 1,000,000+ are ENpc ids,
/// 2,000,000+ EObj or EventItem ids, 4,000,000+ Level rows; quest text lives in <c>quest/&lt;3 digits&gt;/&lt;Id&gt;</c>,
/// keyed like <c>TEXT_BANDWA002_03897_TODO_00</c>.
/// </remarks>
internal sealed class QuestFacts
{
    private static readonly Dictionary<uint, QuestFacts?> cache = new();

    public uint RowId { get; private init; }
    /// <summary>The quest's id as the game names it, such as <c>BanDwa002_03897</c>.</summary>
    public string Key { get; private init; } = "";
    public string Name { get; private init; } = "";
    /// <summary>The script's named arguments in sheet order (empty names left out).</summary>
    public IReadOnlyList<(string Name, uint Value)> Args { get; private init; } = Array.Empty<(string, uint)>();
    public IReadOnlyList<QuestTodo> Todos { get; private init; } = Array.Empty<QuestTodo>();

    private Dictionary<string, string>? text;

    /// <summary>The quest's facts; null when the row doesn't exist. Row ids are the sheet's (65536 + the quest number).</summary>
    public static QuestFacts? Of(uint rowId)
    {
        if (rowId is > 0 and < 65536) rowId += 65536;
        lock (cache)
        {
            if (cache.TryGetValue(rowId, out var known)) return known;
            QuestFacts? facts = null;
            try { facts = Read(rowId); }
            catch (Exception e) { Services.Log.Debug("Quest {Row} couldn't be read: {Message}", rowId, e.Message); }
            cache[rowId] = facts;
            return facts;
        }
    }

    private static QuestFacts? Read(uint rowId)
    {
        if (Services.Data.GetExcelSheet<Quest>().GetRowOrDefault(rowId) is not { } q) return null;
        var args = new List<(string, uint)>();
        foreach (var p in q.QuestParams)
        {
            var name = p.ScriptInstruction.ExtractText();
            if (name.Length > 0) args.Add((name, p.ScriptArg));
        }
        var todos = new List<QuestTodo>();
        var levels = Services.Data.GetExcelSheet<Level>();
        var i = 0;
        foreach (var t in q.TodoParams)
        {
            var spots = new List<QuestSpot>();
            foreach (var loc in t.ToDoLocation)
            {
                if (loc.RowId == 0 || levels.GetRowOrDefault(loc.RowId) is not { } l) continue;
                spots.Add(new QuestSpot(l.RowId, l.Territory.RowId, new Vector3(l.X, l.Y, l.Z), l.Radius, l.Object.RowId));
            }
            if (t.ToDoCompleteSeq != 0 || spots.Count > 0) todos.Add(new QuestTodo(i, t.ToDoCompleteSeq, t.ToDoQty, spots));
            i++;
        }
        return new QuestFacts
        {
            RowId = rowId,
            Key = q.Id.ExtractText(),
            Name = q.Name.ExtractText(),
            Args = args,
            Todos = todos,
        };
    }

    /// <summary>The value of a named argument (<c>"RITEM1"</c>), or null.</summary>
    public uint? Arg(string name)
    {
        foreach (var (n, v) in Args)
            if (n == name) return v;
        return null;
    }

    /// <summary>Every argument whose name starts with <paramref name="prefix"/> followed only by digits (<c>"ACTOR"</c> → ACTOR0, ACTOR1, ...).</summary>
    public IEnumerable<(string Name, uint Value)> ArgsOf(string prefix)
        => Args.Where(a => a.Name.StartsWith(prefix, StringComparison.Ordinal) && a.Name.Length > prefix.Length
                           && a.Name.AsSpan(prefix.Length).IndexOfAnyExceptInRange('0', '9') < 0);

    /// <summary>
    /// The quest's own text in the client's language, keyed by row key (<c>TEXT_BANDWA002_03897_TODO_00</c>). Empty
    /// when the sheet can't be read.
    /// </summary>
    public IReadOnlyDictionary<string, string> Text
    {
        get
        {
            if (text != null) return text;
            text = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var underscore = Key.LastIndexOf('_');
                if (underscore < 0 || Key.Length < underscore + 4) return text;
                var path = $"quest/{Key.Substring(underscore + 1, 3)}/{Key}";
                var sheet = Services.Data.Excel.GetSheet<Lumina.Excel.RawRow>(Dalamud.Utility.ClientLanguageExtensions.ToLumina(Services.Data.Language), path);
                foreach (var row in sheet)
                {
                    var k = row.ReadStringColumn(0).ExtractText();
                    if (k.Length > 0) text[k] = row.ReadStringColumn(1).ExtractText();
                }
            }
            catch (Exception e)
            {
                Services.Log.Debug("Quest text {Key} couldn't be read: {Message}", Key, e.Message);
            }
            return text;
        }
    }

    /// <summary>The journal's to-do lines in order (<c>..._TODO_00</c>, <c>_01</c>, ...), as the game shows them.</summary>
    public IReadOnlyList<string> TodoText
        => Text.Where(kv => kv.Key.Contains("_TODO_", StringComparison.Ordinal) && kv.Value.Length > 0)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value).ToList();

    /// <summary>Text rows whose key contains <paramref name="marker"/> (<c>"_SAYTODO_"</c> for the "/say" phrases).</summary>
    public IEnumerable<string> TextWith(string marker)
        => Text.Where(kv => kv.Key.Contains(marker, StringComparison.Ordinal) && kv.Value.Length > 0).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value);
}
