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
public sealed record MountFact(uint Mount, uint Action, float Range, float EffectRange, bool Line, bool CanFly)
{
    /// <summary>
    /// Every action the quest names for the mount (<c>ACTION0</c>, <c>ACTION1</c> ...), the first being
    /// <see cref="Action"/>. The Arkasodara's hippo cart has three (a colour of gulal per target): the next one is
    /// tried on a target the one before did nothing to. Empty means just <see cref="Action"/>.
    /// </summary>
    public IReadOnlyList<uint> Others { get; init; } = Array.Empty<uint>();
    /// <summary>The action is thrown at a place on the ground (the target's), not used on the target itself.</summary>
    public bool Ground { get; init; }

    /// <summary>The action for the nth try at a target: the first, then the others in turn.</summary>
    public uint ActionFor(int tries)
    {
        if (Action == 0 || Others.Count == 0 || tries <= 0) return Action;
        var all = new List<uint> { Action };
        foreach (var o in Others)
            if (o != 0 && !all.Contains(o)) all.Add(o);
        return all[tries % all.Count];
    }
}

/// <summary>A row of the quest's listeners (<c>Quest.QuestListenerParams</c>), as the engine reads it.</summary>
/// <param name="Listener">
/// What the quest's script listens to: below 1,000,000 a BNpcBase (an enemy), 1,0xx,xxx an ENpc, 2,0xx,xxx an EObj,
/// 5,000,000 an area to walk into.
/// </param>
/// <param name="ConditionType">
/// For an enemy: 1 when <paramref name="ConditionValue"/> is the BNpcName of a field mob whose defeat counts, 3 when
/// it's the <c>Level</c> row a mob the quest puts there appears at.
/// </param>
/// <param name="Kind"><c>QuestUInt8A</c>: 1 talk, 5 defeat, 8 use a key item on it, 9 defeat for a key item, 10 walk into the area.</param>
/// <param name="Sequence"><c>ActorSpawnSeq</c>: the sequence it's active in.</param>
public readonly record struct ListenerRow(uint Listener, byte ConditionType, uint ConditionValue, byte Kind, byte Sequence);

/// <summary>What a quest wants done with an enemy (the listener's <c>QuestUInt8A</c>).</summary>
public enum EnemyTask
{
    /// <summary>Defeat it (5).</summary>
    Defeat,
    /// <summary>Defeat it for the key item it leaves behind (9).</summary>
    DefeatForItem,
    /// <summary>Use a key item on it (8): once it's subdued, or before the fight for an item thrown from afar.</summary>
    UseItem,
}

/// <summary>An enemy of a quest, from its listeners and its <c>ENEMY</c> script arguments.</summary>
/// <param name="Base">Its BNpcBase (the game object's base id).</param>
/// <param name="Name">The BNpcName a field mob must have (0: any of that base).</param>
/// <param name="Sequence">The sequence it's active in (0 when the data doesn't say: any but the report).</param>
/// <param name="Field">
/// A mob of the open world whose defeat counts (anyone may get to it first); otherwise the quest puts it there after a
/// trigger (an object used, an area walked into, a key item).
/// </param>
/// <param name="Spawn">Where a mob the quest puts there appears (null for a field mob, or when the data has no place).</param>
public sealed record QuestEnemy(uint Base, uint Name, byte Sequence, bool Field, EnemyTask Task, ScriptSpot? Spawn = null);

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
    /// <summary>
    /// Only high-quality copies of the product count (the quest's script counts with the "high quality only" filter):
    /// normal-quality ones serve no purpose, as the game itself says at the hand-in.
    /// </summary>
    public bool ProductHq { get; init; }
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
    /// <summary>The quest's enemies (empty for most crafter and gatherer dailies): see <see cref="EnemyRules"/>.</summary>
    public IReadOnlyList<QuestEnemy> Enemies { get; init; } = Array.Empty<QuestEnemy>();
    /// <summary>
    /// The NPCs and objects (not enemies) the quest's listeners say a key item is used on, with the sequence. When a
    /// sequence names some, a key item is used on those only; when it names only enemies, on no object at all.
    /// </summary>
    public IReadOnlyList<(byte Sequence, uint Object)> ItemObjects { get; init; } = Array.Empty<(byte, uint)>();

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

/// <summary>Who a mob has as its target.</summary>
public enum EnemyTarget { None, You, YourChocobo, OtherPlayer, Other }

/// <summary>
/// A mob around the character, from the game's object table: every hostile one in view, and the quest's own kinds
/// even when they've stopped being hostile (subdued, waiting for the quest's item). Its own fields are read whatever
/// the game is doing; what the quest's handler says of it (<see cref="Icon"/>, <see cref="Held"/>) comes from the
/// last calm look, since the handler is asked nothing in combat.
/// </summary>
/// <param name="Id">The game object id (to target it).</param>
/// <param name="BaseId">Its BNpcBase.</param>
/// <param name="NameId">Its BNpcName.</param>
public sealed record StepEnemy(ulong Id, uint BaseId, uint NameId, string Name, Vector3 Position)
{
    /// <summary>Its hitbox radius: attacks and items reach that much further.</summary>
    public float Radius { get; init; }
    public bool Hostile { get; init; }
    public bool Targetable { get; init; } = true;
    /// <summary>The mob itself is in combat (with anyone).</summary>
    public bool InCombat { get; init; }
    public EnemyTarget Target { get; init; }
    /// <summary>Subdued: it takes no more damage and waits for the quest's item (<c>Character.ActorControlFlags &amp; 0x40</c>).</summary>
    public bool Weakened { get; init; }
    /// <summary>The FATE it belongs to (0: none).</summary>
    public uint Fate { get; init; }
    /// <summary>The icon this quest's handler gives it (0: none, or the handler doesn't hold it), as of the last calm look.</summary>
    public uint Icon { get; init; }
    /// <summary>The quest's handler holds it among its objects, as of the last calm look.</summary>
    public bool Held { get; init; }
    /// <summary>The icon the game draws over its nameplate now, from whichever handler (0: none).</summary>
    public uint Drawn { get; init; }
    /// <summary>Its health in percent.</summary>
    public int Hp { get; init; } = 100;
    /// <summary>The player who first hit it, as the game keeps it (0: nobody): for the log only, its meaning isn't confirmed.</summary>
    public ulong Tagger { get; init; }

    /// <summary>It fights the character or the character's chocobo.</summary>
    public bool AttacksMe => InCombat && Target is EnemyTarget.You or EnemyTarget.YourChocobo;
    /// <summary>
    /// It fights someone else (its target says so): theirs, not the run's. One in combat without a target is judged by
    /// whether the run began that fight (<see cref="EnemyRules.Own"/>).
    /// </summary>
    public bool FightsOther => InCombat && Target is EnemyTarget.OtherPlayer or EnemyTarget.Other;
}

/// <summary>A to-do line of the current sequence, as the game reports it.</summary>
/// <param name="Have">What the game counts so far (items in the bags or targets done).</param>
/// <param name="Needed">How many it wants.</param>
/// <param name="Item">The item the line is about (0 when it isn't about an item; can carry +1,000,000 for HQ).</param>
public sealed record TodoProgress(int Index, bool Checked, uint Have, uint Needed, uint Item)
{
    public uint ItemId => Item % 1_000_000;
    /// <summary>The line counts only high-quality copies of its item (the id carries the high-quality mark).</summary>
    public bool WantsHq => Item is >= 1_000_000 and < 2_000_000;
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
    /// <summary>
    /// Casting, in an event or cutscene, crafting, gathering, between areas: nothing is started now. Being in combat
    /// isn't in it (<see cref="InCombat"/>): attacked, the run fights back or carries on.
    /// </summary>
    public bool Busy { get; init; }
    /// <summary>In combat, as the game counts it. The quest's script is asked nothing then.</summary>
    public bool InCombat { get; init; }
    /// <summary>The class on now fights: a combat job the basic combat has actions for.</summary>
    public bool Fighter { get; init; }
    /// <summary>The character's health in percent: no fight is picked on low health (what attacks is fought whatever it is).</summary>
    public int Hp { get; init; } = 100;
    /// <summary>
    /// The quest's script was asked (its to-do lines and objects are read), or they're the last calm look's
    /// (<see cref="Remembered"/>). False while the game is busy, in combat, just after a zone change or while the
    /// script isn't loaded: the lines and objects are then empty.
    /// </summary>
    public bool ScriptRead { get; init; } = true;
    /// <summary>
    /// The to-do lines and objects are those of the last calm look: the character is in combat (the script isn't
    /// asked) and the quest hasn't moved since. A class that doesn't fight carries on by them.
    /// </summary>
    public bool Remembered { get; init; }

    public IReadOnlyList<TodoProgress> Todos { get; init; } = Array.Empty<TodoProgress>();
    public IReadOnlyList<StepObject> Objects { get; init; } = Array.Empty<StepObject>();
    public IReadOnlyList<StepMarker> Markers { get; init; } = Array.Empty<StepMarker>();
    /// <summary>The mobs around (hostile ones, and the quest's own kinds), read whatever the game is doing.</summary>
    public IReadOnlyList<StepEnemy> Hostiles { get; init; } = Array.Empty<StepEnemy>();
    /// <summary>Counts of the quest's items in the bags (normal and high quality together; key items too).</summary>
    public IReadOnlyDictionary<uint, int> Items { get; init; } = new Dictionary<uint, int>();
    public IReadOnlySet<uint> Statuses { get; init; } = new HashSet<uint>();
    /// <summary>Statuses that run out soon (within <see cref="StepRules.StatusRenewBelow"/>): the Ixal facility access is renewed first.</summary>
    public IReadOnlySet<uint> Expiring { get; init; } = new HashSet<uint>();

    public int Count(uint item) => item == 0 ? 0 : Items.GetValueOrDefault(item);

    /// <summary>How many high-quality copies of an item the bags hold (kept under the id with the high-quality mark).</summary>
    public int CountHq(uint item) => item == 0 ? 0 : Items.GetValueOrDefault(item % 1_000_000 + 1_000_000);

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
