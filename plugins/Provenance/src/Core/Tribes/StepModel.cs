using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Provenance.Core.Tribes;

// The step engine's picture of a quest: what the game data says about it (QuestScript, read once) and what the game
// shows right now (StepSnapshot, read every decision). Plain data, so the decisions in StepRules can be tested
// without the game.

/// <summary>A spot a to-do line points at (a <c>Level</c> row).</summary>
/// <param name="Area">A circle on the map (Level type 51) rather than an NPC or object; its radius in the sheet is a
/// code, not a distance, so the live map marker's radius is used instead when there is one.</param>
/// <param name="Object">The NPC or object the row places (0 for an area).</param>
public sealed record ScriptSpot(uint Level, uint Territory, Vector3 Position, uint Object, bool Area);

/// <summary>A to-do line of the quest: shown while the quest is at <paramref name="Sequence"/>.</summary>
/// <param name="Count">How many targets the line wants (not an item count).</param>
/// <param name="Text">The journal line in the client's language (item names can be blank: the game fills them in).</param>
public sealed record ScriptTodo(int Index, byte Sequence, byte Count, IReadOnlyList<ScriptSpot> Spots, string Text);

/// <summary>A key item of the quest and how it's used.</summary>
/// <param name="Action">The item's action (<c>EventItem.Action</c>); 0 means it's handed over by talking, not used.</param>
/// <param name="Ground">Used on a spot on the ground (the action is ground-targeted).</param>
/// <param name="Self">Used on yourself near the target (the action's range is 0).</param>
/// <param name="Range">How far from the target it can be used, in yalms.</param>
/// <param name="CastSeconds">Its cast: stand still for it.</param>
/// <param name="Words">Its names as a to-do line may spell them (name, singular, plural), in the client's language.</param>
public sealed record KeyItemFact(uint Id, string Name, uint Action, bool Ground, bool Self, float Range, float CastSeconds, IReadOnlyList<string> Words)
{
    public bool Usable => Action != 0;
}

/// <summary>The quest mount and its action (<c>MOUNT0</c>, <c>ACTION0</c>).</summary>
/// <param name="Action">0 for a mount without an action (ridden into an area).</param>
/// <param name="Range">Range of the action on a target; 0 for an action centred on the rider.</param>
/// <param name="EffectRange">How far the action reaches around (or ahead of) the rider.</param>
/// <param name="Line">The action goes out in a line ahead of the rider: face the target first.</param>
/// <param name="CanFly">The mount may fly (the Mount sheet gives it a flying condition).</param>
public sealed record MountFact(uint Mount, uint Action, float Range, float EffectRange, bool Line, bool CanFly);

/// <summary>An emote a to-do line asks for ("/psych"), with the command as the client spells it.</summary>
public sealed record EmoteFact(ushort Emote, string Command);

/// <summary>
/// What the game data says about a quest, for the step engine: its to-do lines with their spots and text, and what
/// the script's arguments and the quest's own text sheet say each step needs.
/// </summary>
public sealed class QuestScript
{
    /// <summary>The Quest sheet row (65536 + the quest number).</summary>
    public uint Quest { get; init; }
    public string Name { get; init; } = "";
    /// <summary>A daily (repeatable): done means completed today, not ever.</summary>
    public bool Repeatable { get; init; }
    /// <summary>The giver (<c>ACTOR0</c> / <c>IssuerStart</c>) and where they stand.</summary>
    public uint Giver { get; init; }
    public ScriptSpot? GiverSpot { get; init; }
    /// <summary>Who the quest is reported to at the end (<c>TargetEnd</c>; usually the giver).</summary>
    public uint TargetEnd { get; init; }
    public IReadOnlyList<ScriptTodo> Todos { get; init; } = Array.Empty<ScriptTodo>();

    /// <summary>The crafted item the quest asks for (0 when it crafts nothing), and the materials it's made from.</summary>
    public uint Product { get; init; }
    public uint Materials { get; init; }
    /// <summary>The status the crafting needs (the Ixal facility access), 0 when none.</summary>
    public uint CraftStatus { get; init; }
    /// <summary>An item to wear while crafting (the Ixal's Ehcatl wristgloves), 0 when none.</summary>
    public uint CraftEquip { get; init; }

    /// <summary>Items to gather, by the class the quest was accepted as (16 miner, 17 botanist, 18 fisher).</summary>
    public IReadOnlyDictionary<uint, uint> GatherItems { get; init; } = new Dictionary<uint, uint>();

    public IReadOnlyList<KeyItemFact> KeyItems { get; init; } = Array.Empty<KeyItemFact>();
    public MountFact? Mount { get; init; }
    /// <summary>The phrase to /say, by to-do line (the line's text names the phrase).</summary>
    public IReadOnlyDictionary<int, string> Say { get; init; } = new Dictionary<int, string>();
    /// <summary>The emote, by to-do line (the line's text names its command).</summary>
    public IReadOnlyDictionary<int, EmoteFact> Emotes { get; init; } = new Dictionary<int, EmoteFact>();
    /// <summary>The status a carried object gives (404 "Transporting"), 0 when the quest carries nothing.</summary>
    public uint CarryStatus { get; init; }
    /// <summary>Zones the quest's script sends you to (<c>TERRITORYTYPE</c> arguments: Elysion, Zirgorteh ...).</summary>
    public IReadOnlyList<uint> Territories { get; init; } = Array.Empty<uint>();
    /// <summary>Rewards to pick one of when the quest is completed (empty for most).</summary>
    public IReadOnlyList<uint> OptionalRewards { get; init; } = Array.Empty<uint>();
    /// <summary>The quest's own menu questions and answers (its Q and A text rows), in the client's language.</summary>
    public IReadOnlyCollection<string> MenuPrompts { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<string> MenuAnswers { get; init; } = Array.Empty<string>();

    public IEnumerable<ScriptTodo> TodosAt(byte sequence) => Todos.Where(t => t.Sequence == sequence);
}

public enum StepObjectKind { Npc, Object, Other }

/// <summary>One of the quest's own objects the game has loaded (from its event handler).</summary>
/// <param name="Id">The game object id (to target it).</param>
/// <param name="Icon">The icon this quest gives it (its own handler's answer; 0: none). Not the icon the game draws,
/// which comes from every handler: a giver showing "dailies available" would count as this quest's target.</param>
/// <param name="Active">The quest's script counts it as active now.</param>
public sealed record StepObject(ulong Id, uint BaseId, StepObjectKind Kind, string Name, Vector3 Position, uint Icon, bool Active, bool Targetable)
{
    /// <summary>The icon the game draws over it, from all its handlers (for the log only).</summary>
    public uint Drawn { get; init; }
    /// <summary>Its place in the zone's layout (0 when it has none): tells two of the same NPC or object apart.</summary>
    public uint Instance { get; init; }

    /// <summary>Something the step wants done with it: targetable and active.</summary>
    public bool Live => Targetable && Active;

    /// <summary>What it's remembered by: its place in the layout when it has one (kept when it's loaded again), else its game object id.</summary>
    public ulong Key => Instance != 0 ? ((ulong)BaseId << 32) | Instance : Id;
}

/// <summary>One of the quest's map markers.</summary>
/// <param name="Radius">The circle's radius for an area marker (0 or 1 for a point).</param>
/// <param name="DataId">The marked object's id as the map keeps it (the low 16 bits of a base id; 0 for an area).</param>
public sealed record StepMarker(uint Territory, Vector3 Position, float Radius, uint Icon, uint Level, uint DataId)
{
    public bool IsArea => Radius > 1.5f;
}

/// <summary>A to-do line of the current sequence, as the game reports it.</summary>
/// <param name="Have">What the game counts so far (items in the bags or targets done).</param>
/// <param name="Needed">How many it wants.</param>
/// <param name="Item">The item the line is about (0 when it isn't about an item; can carry +1,000,000 for HQ).</param>
public sealed record TodoProgress(int Index, bool Checked, uint Have, uint Needed, uint Item)
{
    public uint ItemId => Item % 1_000_000;
}

/// <summary>What the game shows about the quest and the character right now.</summary>
public sealed class StepSnapshot
{
    public uint Quest { get; init; }
    public bool Accepted { get; init; }
    /// <summary>The quest is done (today's daily completed, or the quest complete).</summary>
    public bool Completed { get; init; }
    public byte Sequence { get; init; }
    public byte[] Variables { get; init; } = new byte[6];
    /// <summary>The class the quest was accepted as (it only goes on as that class).</summary>
    public uint AcceptJob { get; init; }
    public uint Job { get; init; }

    public uint Territory { get; init; }
    public Vector3 Player { get; init; }
    public bool Mounted { get; init; }
    /// <summary>The mount ridden (0 when not mounted or unknown).</summary>
    public uint Mount { get; init; }
    public bool Flying { get; init; }
    /// <summary>Casting, in an event or cutscene, crafting, gathering, in combat, between areas: nothing is started now.</summary>
    public bool Busy { get; init; }
    /// <summary>
    /// The quest's script was asked (its to-do lines and objects are read). False while the game is busy, just after a
    /// zone change or while the script isn't loaded: the lines and objects are then empty and nothing is decided.
    /// </summary>
    public bool ScriptRead { get; init; } = true;

    public IReadOnlyList<TodoProgress> Todos { get; init; } = Array.Empty<TodoProgress>();
    public IReadOnlyList<StepObject> Objects { get; init; } = Array.Empty<StepObject>();
    public IReadOnlyList<StepMarker> Markers { get; init; } = Array.Empty<StepMarker>();
    /// <summary>Counts of the quest's items in the bags (normal and high quality together; key items too).</summary>
    public IReadOnlyDictionary<uint, int> Items { get; init; } = new Dictionary<uint, int>();
    public IReadOnlySet<uint> Statuses { get; init; } = new HashSet<uint>();
    /// <summary>Statuses that run out soon (within <see cref="StepRules.StatusRenewBelow"/>): the Ixal facility access is renewed first.</summary>
    public IReadOnlySet<uint> Expiring { get; init; } = new HashSet<uint>();

    public int Count(uint item) => item == 0 ? 0 : Items.GetValueOrDefault(item);

    /// <summary>
    /// The quest's progress in one line: sequence, variables, what each to-do line counts and the quest's items. When
    /// it changes, the last action did something. Riding the quest mount, carrying and the facility access aren't in
    /// it: losing them isn't progress (it would clear what was tried and start the no-progress clock again, so a lost
    /// mount could be got back, lost and got back without end).
    /// </summary>
    public string Signature()
    {
        var items = string.Join(",", Items.Where(kv => kv.Value != 0).OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}x{kv.Value}"));
        var todos = string.Join(",", Todos.Select(t => $"{t.Index}:{(t.Checked ? 1 : 0)}:{t.Have}/{t.Needed}"));
        return $"{Accepted}|{Sequence}|{Convert.ToHexString(Variables)}|{todos}|{items}";
    }
}
