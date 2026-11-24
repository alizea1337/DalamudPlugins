using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Lumina.Excel.Sheets;
using Provenance.Core.Tribes;

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
/// engine; read once per quest and kept. Safe from any thread: the catalog reads them on a worker thread while the
/// step engine reads them on the game's.
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
    /// <summary>
    /// What the quest's script listens to (<c>Quest.QuestListenerParams</c>, empty rows left out): its enemies with
    /// what's done with each, and the NPCs and objects its steps are about. See <see cref="EnemyRules"/>.
    /// </summary>
    public IReadOnlyList<ListenerRow> Listeners { get; private init; } = Array.Empty<ListenerRow>();

    private readonly Lazy<IReadOnlyDictionary<string, string>> text;
    private readonly Lazy<bool> wantsHq;

    private QuestFacts()
    {
        text = new Lazy<IReadOnlyDictionary<string, string>>(ReadText, LazyThreadSafetyMode.ExecutionAndPublication);
        wantsHq = new Lazy<bool>(ReadWantsHq, LazyThreadSafetyMode.ExecutionAndPublication);
    }

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
        var listeners = new List<ListenerRow>();
        foreach (var l in q.QuestListenerParams)
            if (l.Listener != 0) listeners.Add(new ListenerRow(l.Listener, l.ConditionType, l.ConditionValue, l.QuestUInt8A, l.ActorSpawnSeq));
        return new QuestFacts
        {
            RowId = rowId,
            Key = q.Id.ExtractText(),
            Name = q.Name.ExtractText(),
            Args = args,
            Todos = todos,
            Listeners = listeners,
        };
    }

    /// <summary>A <c>Level</c> row as a spot: where, in which zone, and what it places there (an enemy's BNpcBase for a spawn point). Null when there's no such row.</summary>
    public static ScriptSpot? LevelSpot(uint levelRow)
        => levelRow != 0 && Services.Data.GetExcelSheet<Level>().GetRowOrDefault(levelRow) is { } l
            ? new ScriptSpot(l.RowId, l.Territory.RowId, new Vector3(l.X, l.Y, l.Z), l.Object.RowId, l.Object.RowId == 0)
            : null;

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
    /// when the sheet can't be read. Read the first time it's asked for, once, whichever thread asks.
    /// </summary>
    public IReadOnlyDictionary<string, string> Text => text.Value;

    /// <summary>
    /// The quest counts only high-quality copies of the item it has made: its script
    /// (<c>game_script/quest/&lt;3 digits&gt;/&lt;Id&gt;.luab</c>) counts items with the game's "high quality only" filter
    /// (<c>NUM_OF_ITEMS_FILTER_HQ</c>; the others use <c>..._NQ_OR_HQ</c>). The sheets don't say it, and the journal
    /// shows it only as a small mark after the item's name. 59 of the societies' crafting dailies are so (most of the
    /// Ixal's, and some of the Moogles', Namazu's, Dwarves', Loporrits' and Yok Huy's), found by reading the scripts.
    /// Only the name of the filter is looked for in the file; nothing of the script is run or parsed.
    /// </summary>
    public bool WantsHq => wantsHq.Value;

    private static readonly byte[] HqFilter = System.Text.Encoding.ASCII.GetBytes("NUM_OF_ITEMS_FILTER_HQ\0");

    private bool ReadWantsHq()
    {
        try
        {
            var underscore = Key.LastIndexOf('_');
            if (underscore < 0 || Key.Length < underscore + 4) return false;
            var file = Services.Data.GetFile($"game_script/quest/{Key.Substring(underscore + 1, 3)}/{Key}.luab");
            return file != null && file.Data.AsSpan().IndexOf(HqFilter) >= 0;
        }
        catch (Exception e)
        {
            Services.Log.Debug("Quest script {Key} couldn't be read: {Message}", Key, e.Message);
            return false;
        }
    }

    /// <summary>Reads the text sheet into a dictionary of its own, which is only handed out once it's complete.</summary>
    private IReadOnlyDictionary<string, string> ReadText()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var underscore = Key.LastIndexOf('_');
            if (underscore < 0 || Key.Length < underscore + 4) return result;
            var path = $"quest/{Key.Substring(underscore + 1, 3)}/{Key}";
            var sheet = Services.Data.Excel.GetSheet<Lumina.Excel.RawRow>(Dalamud.Utility.ClientLanguageExtensions.ToLumina(Services.Data.Language), path);
            foreach (var row in sheet)
            {
                var k = row.ReadStringColumn(0).ExtractText();
                if (k.Length > 0) result[k] = row.ReadStringColumn(1).ExtractText();
            }
        }
        catch (Exception e)
        {
            Services.Log.Debug("Quest text {Key} couldn't be read: {Message}", Key, e.Message);
        }
        return result;
    }

    /// <summary>The journal's to-do lines in order (<c>..._TODO_00</c>, <c>_01</c>, ...), as the game shows them.</summary>
    public IReadOnlyList<string> TodoText
        => Text.Where(kv => kv.Key.Contains("_TODO_", StringComparison.Ordinal) && kv.Value.Length > 0)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value).ToList();

    /// <summary>Text rows whose key contains <paramref name="marker"/> (<c>"_SAYTODO_"</c> for the "/say" phrases).</summary>
    public IEnumerable<string> TextWith(string marker)
        => Text.Where(kv => kv.Key.Contains(marker, StringComparison.Ordinal) && kv.Value.Length > 0).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value);
}
