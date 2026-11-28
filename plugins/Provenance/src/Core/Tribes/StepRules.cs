using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Provenance.Core.Tribes;

/// <summary>What the step engine does next.</summary>
public enum StepKind
{
    /// <summary>Nothing to start now (casting, in an event, loading).</summary>
    Wait,
    /// <summary>The quest is done.</summary>
    Done,
    /// <summary>The quest can't go on (not in the journal).</summary>
    Failed,
    /// <summary>The player has to do this one: the plan's text says what.</summary>
    AskUser,
    /// <summary>The run crafts the quest's item.</summary>
    Craft,
    /// <summary>The run gathers the quest's item.</summary>
    Gather,
    /// <summary>Back to the class the quest was accepted as.</summary>
    SwitchJob,
    /// <summary>Off the quest mount (nothing more for it to do; the quest can't be reported while riding it).</summary>
    Dismount,
    Talk,
    Say,
    Emote,
    UseItem,
    MountAction,
    /// <summary>Go where the game points (a marker or a to-do spot) and look there.</summary>
    Go,
    /// <summary>Fight an enemy: one of the quest's, or whatever attacks. The quest's item goes on it when it wants one.</summary>
    Fight,
}

/// <summary>How a key item is used.</summary>
public enum ItemUse { Target, Ground, Self }

/// <summary>A decision: what to do, on what or where, and how close to get first.</summary>
public sealed record StepPlan(StepKind Kind, string What)
{
    public StepObject? Target { get; init; }
    /// <summary>The enemy to fight, or to use the quest mount's action on.</summary>
    public StepEnemy? Enemy { get; init; }
    /// <summary>Where to go or act when there's no object (a marker, a to-do spot).</summary>
    public Vector3? Spot { get; init; }
    /// <summary>The zone of the target or spot.</summary>
    public uint Territory { get; init; }
    /// <summary>How close to get before acting, in yalms.</summary>
    public float Within { get; init; } = 3f;
    /// <summary>The job, emote, item or action to use.</summary>
    public uint Id { get; init; }
    /// <summary>The /say phrase or the emote's command.</summary>
    public string? Text { get; init; }
    public ItemUse Use { get; init; }
    /// <summary>Turn to the target before acting (an action that goes out in a line).</summary>
    public bool Face { get; init; }
    /// <summary>Carrying something: walk (no mount, no teleport).</summary>
    public bool OnFoot { get; init; }
    /// <summary>Riding a quest mount that may not fly.</summary>
    public bool NoFly { get; init; }
    /// <summary>Crafting and gathering: how many are still wanted (the materials, for crafting).</summary>
    public int Count { get; init; }
    /// <summary>Crafting: only high-quality copies count, so the craft goes for full quality.</summary>
    public bool Hq { get; init; }
    /// <summary>Crafting: the materials item; gathering: the to-do line's item.</summary>
    public uint Other { get; init; }
    /// <summary>The Level row of the spot gone to (to note it was looked at), 0 for none.</summary>
    public uint Level { get; init; }
    /// <summary>What an attempt is counted under (public actions: once per step).</summary>
    public string Key { get; init; } = "";
    /// <summary>
    /// The class the step is done as (0: whichever is on). A trip can be made on another one (an Ixal hand-over is
    /// travelled to on the combat job, through where the quest's enemies appear): at the target the step is decided
    /// afresh, which puts the class on first.
    /// </summary>
    public uint Job { get; init; }
    /// <summary>A /say or an emote: seen by everyone around, so sent once per step (an emote once more).</summary>
    public bool Public => Kind is StepKind.Say or StepKind.Emote;

    public override string ToString()
    {
        var on = Target != null ? $" -> {Target.Name} ({Target.BaseId}) at {V(Target.Position)}"
            : Enemy != null ? $" -> {Enemy.Name} ({Enemy.BaseId}/{Enemy.NameId}) at {V(Enemy.Position)}"
            : Spot is { } p ? $" -> {V(p)} in {Territory}" : "";
        var extra = Kind switch
        {
            StepKind.Say => $" \"{Text}\"",
            StepKind.Emote => $" {Text}",
            StepKind.UseItem => $" item {Id} ({Use})",
            StepKind.MountAction => $" action {Id}{(Face ? " facing" : "")}",
            StepKind.SwitchJob => $" job {Id}",
            StepKind.Craft => $" {Count}x item {Id} from {Other}",
            StepKind.Gather => $" {Count}x item {Id}",
            _ => "",
        };
        return $"{Kind}{extra}{on}{(Kind is StepKind.Talk or StepKind.Say or StepKind.Emote or StepKind.UseItem or StepKind.MountAction or StepKind.Go or StepKind.Fight ? $" within {Within:0.#}" : "")}"
               + (OnFoot ? " on foot" : "") + $": {What}";
    }

    private static string V(Vector3 v) => $"{v.X:0},{v.Y:0},{v.Z:0}";
}

/// <summary>A counted attempt at something.</summary>
public sealed class Attempt
{
    public int Count;
    public DateTime Last;
}

/// <summary>
/// What the engine remembers while it works on one quest: the last progress (the snapshot's signature) and when it
/// came; objects tried without effect and spots looked at since then; objects that played a "not this one" scene on
/// the current to-do lines (kept while those lines are open); the quest mount's giver and how often the character got
/// on it; who handed over what's carried and how often; the teleports taken to drop the quest mount. The /say and
/// emotes sent are in <see cref="Public"/>, which outlives the quest.
/// </summary>
public sealed class StepMemory
{
    public string Signature { get; private set; } = "";
    public DateTime ProgressAt { get; private set; }
    /// <summary>Objects (<see cref="StepObject.Key"/>) talked to or acted on since the last progress without anything changing.</summary>
    public HashSet<ulong> Tried { get; } = new();
    /// <summary>To-do spots and markers gone to and looked at since the last progress.</summary>
    public HashSet<uint> Looked { get; } = new();
    /// <summary>Attempts at the current step's interactions, cleared by progress.</summary>
    public Dictionary<string, Attempt> Attempts { get; } = new();
    /// <summary>
    /// The /say and emotes sent. The stepper hands every quest the day's shared store (<see cref="PublicAttemptStore.Shared"/>),
    /// so stopping and starting again never sends one twice; a dry run (the watch, the tests) gets its own.
    /// </summary>
    public PublicAttemptStore Public { get; init; } = new();
    /// <summary>Answers given in menus: prompt and entries → (times answered, last index).</summary>
    public Dictionary<string, (int Times, int Index, string Signature)> Answers { get; } = new();
    /// <summary>The NPC that put the character on the quest mount (not a target of the mount's action).</summary>
    public uint MountGiver { get; set; }
    /// <summary>Times the character got on the quest mount: the first time, and each time it was lost and got back.</summary>
    public int Mounts { get; set; }
    /// <summary>The NPC that handed over what's carried, and times something was picked up.</summary>
    public uint CarryGiver { get; set; }
    public int Pickups { get; set; }
    /// <summary>Teleports taken to drop a quest mount that wouldn't let go (each costs gil).</summary>
    public int DropTeleports { get; set; }
    /// <summary>Rounds of crafting that brought no high-quality copy of an item only those count for.</summary>
    public int HqMisses { get; set; }
    /// <summary>
    /// Enemies given up on (by game object id): a fight that took too long, one that couldn't be reached or hurt, one
    /// the quest's item or the quest mount's action did nothing to, one the game says isn't the character's to
    /// attack. Not fought again while this quest is worked on, unless it attacks: what attacks is always fought.
    /// </summary>
    public HashSet<ulong> GivenUp { get; } = new();
    /// <summary>
    /// Enemies that attack but couldn't be reached or hurt (on a ledge, behind something, invulnerable), left for now:
    /// the step goes on meanwhile. The stepper puts them here, and takes them out again after a while for another try.
    /// </summary>
    public HashSet<ulong> CantHit { get; } = new();
    /// <summary>
    /// The enemy the run's attack has just gone out on (0: none). In combat without a target yet, it's the run's all
    /// the same: the game takes a moment to show whom a mob turned on.
    /// </summary>
    public ulong Striking { get; set; }
    /// <summary>Since when the character has been getting its health back before the next fight (default: it isn't).</summary>
    public DateTime RestSince { get; set; }
    /// <summary>How many were given up on since the player last took over: after <see cref="StepRules.MaxGiveUps"/> the player is asked.</summary>
    public int GiveUps { get; set; }
    /// <summary>Enemies defeated since the quest last moved on (a defeat that counts moves it on).</summary>
    public int Kills { get; set; }
    private readonly HashSet<string> notForItem = new();

    /// <summary>
    /// The game said this object isn't a target for this key item at this sequence (563 "Invalid target"): the item
    /// isn't aimed at it again while the quest is at that sequence, and the object stays one to talk to (it isn't
    /// counted as tried).
    /// </summary>
    public void RefusedItem(StepSnapshot s, uint item, ulong objectKey) => notForItem.Add($"{s.Sequence}:{item}:{objectKey}");

    public bool NotForItem(StepSnapshot s, uint item, ulong objectKey) => notForItem.Contains($"{s.Sequence}:{item}:{objectKey}");

    /// <summary>Times the quest's item was used on an enemy, by game object id.</summary>
    public Dictionary<ulong, int> ItemUses { get; } = new();
    /// <summary>
    /// Enemies the run fought (by game object id): those that turned on the character or the chocobo, or lost health
    /// to the run's attack (not one it only walked up to). One of them that was subdued is the run's to use the
    /// quest's item on, whatever else marks it: it stops attacking then, and may stop counting as hostile.
    /// </summary>
    public HashSet<ulong> Fought { get; } = new();
    /// <summary>
    /// Defeats in a row of a kind (BNpcBase) that didn't move the quest on. A quest with two kinds of field mob to slay
    /// goes on counting one after the other is done: after <see cref="EnemyRules.IdleDefeats"/> of these the kind is
    /// left alone and the other is looked for. Kept by the stepper; a defeat that counts takes the kind out again.
    /// </summary>
    public Dictionary<uint, int> IdleDefeats { get; } = new();
    /// <summary>
    /// A defeat was seen to move this quest on (its sequence or variables): only then does a defeat that doesn't say
    /// anything about its kind. Until then no kind is left alone for it.
    /// </summary>
    public bool DefeatsCount { get; set; }

    private string decoyLines = "";
    private readonly HashSet<ulong> decoys = new();

    /// <summary>Takes the snapshot's signature: a change is progress, which clears what was tried.</summary>
    /// <returns>True when it changed.</returns>
    public bool Observe(string signature, DateTime now)
    {
        if (signature == Signature) return false;
        var first = Signature.Length == 0;
        Signature = signature;
        ProgressAt = now;
        Kills = 0;
        if (!first)
        {
            Tried.Clear();
            Looked.Clear();
            Attempts.Clear();
        }
        return true;
    }

    /// <summary>
    /// Time spent elsewhere (crafting, gathering, paused) doesn't count toward "no progress": restarts the clock
    /// without forgetting what was tried.
    /// </summary>
    public void Touch(DateTime now)
    {
        if (Signature.Length > 0) ProgressAt = now;
    }

    public void Note(string key, DateTime now)
    {
        if (!Attempts.TryGetValue(key, out var a)) Attempts[key] = a = new Attempt();
        a.Count++;
        a.Last = now;
    }

    public int Count(string key) => Attempts.TryGetValue(key, out var a) ? a.Count : 0;

    /// <summary>
    /// An object that played its scene on these lines (<see cref="StepRules.LineKey"/>) without anything changing: a
    /// decoy ("not this one"). It isn't tried again while the same lines are open, even after progress elsewhere on them.
    /// </summary>
    public void Decoy(string lines, ulong key)
    {
        if (lines != decoyLines)
        {
            decoys.Clear();
            decoyLines = lines;
        }
        decoys.Add(key);
    }

    public bool IsDecoy(string lines, ulong key) => lines == decoyLines && decoys.Contains(key);
}

/// <summary>
/// The /say and emotes sent, per quest, to-do line and quest state, for the game day (the daily reset at 15:00 UTC
/// starts a new one). They're public and can't be taken back, so neither a new run nor a new stepper sends one again.
/// </summary>
public sealed class PublicAttemptStore
{
    /// <summary>The store the stepper uses (one for the plugin).</summary>
    public static PublicAttemptStore Shared { get; } = new();

    private static readonly TimeSpan ResetHour = TimeSpan.FromHours(15);
    private readonly Dictionary<string, Attempt> book = new();
    private DateTime day;

    public Attempt? Get(string key, DateTime now)
    {
        lock (book)
        {
            Roll(now);
            return book.GetValueOrDefault(key);
        }
    }

    public Attempt Note(string key, DateTime now)
    {
        lock (book)
        {
            Roll(now);
            if (!book.TryGetValue(key, out var a)) book[key] = a = new Attempt();
            a.Count++;
            a.Last = now;
            return a;
        }
    }

    /// <summary>The game day a moment (UTC) belongs to: it starts at the daily reset, 15:00 UTC.</summary>
    public static DateTime GameDay(DateTime utc) => (utc - ResetHour).Date;

    private void Roll(DateTime now)
    {
        var d = GameDay(now);
        if (d == day) return;
        book.Clear();
        day = d;
    }
}

/// <summary>
/// The game staying busy (an event, a cutscene, a cast, scene tasks pending) with nothing about the quest changing.
/// Every wait for the game is bounded by it: after <see cref="Limit"/> the step is the player's.
/// </summary>
public sealed class BlockWatch
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(75);

    private DateTime since;
    private string state = "";

    /// <param name="blocked">The game is busy now.</param>
    /// <param name="questState">The quest's state as far as it can be read while busy (sequence, variables).</param>
    /// <returns>True once blocked for longer than <see cref="Limit"/> with the same state (then it starts over).</returns>
    public bool Feed(bool blocked, string questState, DateTime now)
    {
        if (!blocked)
        {
            since = default;
            return false;
        }
        if (since == default || questState != state)
        {
            since = now;
            state = questState;
            return false;
        }
        if (now - since <= Limit) return false;
        since = default;
        return true;
    }

    public void Reset() => since = default;
}

/// <summary>
/// The step engine's decisions, from the quest's data and what the game shows: which kind of step this is, which
/// object or spot it's about, and when to stop trying and hand the step to the player. No game calls: the same rules
/// drive the run and the watch's dry run.
/// </summary>
/// <remarks>
/// The order and the signals follow the quest-step research (queststeps_engine.md §4–§8, from the game's quest
/// scripts and sheets): the game evaluates each quest's script and marks what's left to do, so the engine follows the
/// quest's own objects that are targetable and active (or show an icon), and its map markers; crafting and gathering
/// are recognised by the to-do line's item; /say, emotes and key items by what the to-do line's text names.
/// </remarks>
public static class StepRules
{
    /// <summary>
    /// After a /say or an emote, this long with nothing changing before it's sent again (emotes) or the step goes to
    /// the player: the game takes a moment to count it.
    /// </summary>
    public static readonly TimeSpan PublicRetryAfter = TimeSpan.FromSeconds(15);
    /// <summary>Interactions with the same object, without anything changing, before it counts as tried.</summary>
    public const int InteractTries = 3;
    /// <summary>No progress at all for this long while working: hand the step to the player.</summary>
    public static readonly TimeSpan NoProgressLimit = TimeSpan.FromMinutes(5);
    /// <summary>How close to get to say a phrase or emote at someone.</summary>
    public const float PublicWithin = 4f;
    /// <summary>How close to get to talk to an NPC or an object.</summary>
    public const float TalkWithin = 3f;
    /// <summary>Gathering when the game doesn't say how many: the society quests want three.</summary>
    public const int DefaultGatherCount = 3;
    /// <summary>Times the quest mount, or what's carried, is got back after it was lost, before the player takes over.</summary>
    public const int MaxRegains = 2;
    /// <summary>Teleports to drop a quest mount that won't let go (each costs gil), per quest.</summary>
    public const int MaxDropTeleports = 1;
    /// <summary>
    /// Rounds of crafting an item that only counts high quality which may come out normal quality before the player is
    /// asked (each takes new materials and crystals; gear that can't fill the quality bar won't do better next time).
    /// </summary>
    public const int MaxHqMisses = 2;
    /// <summary>A status the crafting needs (the Ixal facility access) is renewed before crafting when it has less than this left.</summary>
    public static readonly TimeSpan StatusRenewBelow = TimeSpan.FromSeconds(60);

    /// <summary>How often a public action is sent for one step at most: a /say once (the player was told so), an emote once more.</summary>
    public static int PublicTries(StepKind kind) => kind == StepKind.Say ? 1 : 2;

    /// <summary>How close the trip brings the character to an enemy before the fight takes over (it closes the rest to its own reach).</summary>
    public const float EngageWithin = 20f;
    /// <summary>Enemies given up on (a fight over its time, one out of reach) before the player is asked.</summary>
    public const int MaxGiveUps = 2;
    /// <summary>Enemies defeated without the quest moving on before the player is asked (a defeat that counts moves it on).</summary>
    public const int MaxIdleKills = 15;
    /// <summary>"Weakness" and "Brink of Death": the statuses a knock-out leaves behind. No fight is started with them.</summary>
    public static readonly IReadOnlySet<uint> KnockOutStatuses = new HashSet<uint> { 43, 44 };
    /// <summary>No fight is picked, and nothing that calls the quest's enemies is done, below this much health (percent).</summary>
    public const int FightFromHp = 70;
    /// <summary>The wait for health is this long at most; then the step goes on as it is.</summary>
    public static readonly TimeSpan RestFor = TimeSpan.FromSeconds(40);
    /// <summary>The wait for health is for a step this close by (further off, the health comes back on the way).</summary>
    public const float RestNear = 60f;
    /// <summary>
    /// A quest accepted as a crafter with a fight at this sequence (the Ixal's): this close to whoever is talked to
    /// or handed something, the crafter goes back on. Wider than the talk's own reach, so standing at its edge doesn't
    /// change the class back and forth.
    /// </summary>
    public const float ClassChangeWithin = 10f;

    /// <summary>A class that doesn't fight: a crafter (8–15) or a gatherer (16–18).</summary>
    public static bool HandOrLand(uint job) => job is >= 8 and <= 18;

    /// <summary>The next step.</summary>
    /// <param name="combatJob">
    /// The job a quest accepted as a crafter or gatherer is fought on (the Ixal's: Settings, Monster drops); 0 when none
    /// is picked. A battle society's quest is fought on the job it was accepted as.
    /// </param>
    /// <remarks>
    /// The order around fights. What attacks the character or the chocobo is answered first, before any reason to ask
    /// the player (a run that asks stops, and a stopped run doesn't defend itself); on the quest mount that's its
    /// action, on the quest's own enemies only. Then the reasons to ask (nothing changed for long, fights given up,
    /// defeats that didn't count). Then the quest's own enemies in view, once the character has its health back. In
    /// combat with nothing the run can fight, a combat job waits it out, unless what attacks can't be reached or hurt:
    /// then, as for a class that doesn't fight, the step goes on by the last calm look. Whoever calls this while in
    /// combat holds a returned <see cref="StepKind.AskUser"/> back until the fight is over (the stepper does).
    /// </remarks>
    public static StepPlan Decide(QuestScript q, StepSnapshot s, StepMemory m, DateTime now, bool leaveOutSay = false, uint combatJob = 0)
    {
        if (!s.Accepted)
            return s.Completed ? new StepPlan(StepKind.Done, "The quest is complete.") : new StepPlan(StepKind.Failed, "The quest isn't in the journal any more.");
        var stale = m.ProgressAt != default && now - m.ProgressAt > NoProgressLimit;
        // Before the wait for a busy game: a game that stays busy doesn't stop the clock.
        if (s.Busy) return stale ? Ask(Stale()) : new StepPlan(StepKind.Wait, "busy");

        var riding = q.Mount is { } qm && s.Mounted && s.Mount == qm.Mount;

        // Enemies come before everything else, and before the wait while in combat (a run that is attacked mustn't
        // stand there). On a combat job: whatever attacks, and the quest's own enemies in view. On the quest mount its
        // action is the weapon, and only for the quest's own (the Ananta's marid tramples the ants; past anything else
        // that attacks it rides on).
        StepEnemy? enemy = null;
        EnemyJudgement why = default;
        if (s.Fighter && riding)
        {
            enemy = q.Mount!.Action != 0 ? EnemyRules.NextOwn(q, s, m) : null;
            if (enemy != null) why = EnemyRules.Own(q, s, enemy, m);
        }
        else if (s.Fighter)
        {
            enemy = EnemyRules.Next(q, s, m);
            if (enemy != null) why = EnemyRules.Judge(q, s, enemy, m);
        }
        var attacked = enemy != null && (riding ? enemy.AttacksMe : why.Verdict == EnemyVerdict.Attacker);
        if (attacked) return riding ? MountAttack(q.Mount!, s, m, enemy!) : Fight(s, enemy!, why);

        if (stale) return Ask(Stale());
        if (m.GiveUps >= MaxGiveUps)
            return Ask($"{m.GiveUps} of the quest's fights were given up (too long, or the enemy couldn't be reached or hurt); do this step yourself, then resume.");
        if (m.Kills >= MaxIdleKills)
            return Ask($"{m.Kills} enemies were defeated without the quest moving on; do this step yourself, then resume.");

        if (enemy != null)
        {
            // One that isn't attacking is the run's choice: not while weak from a knock-out, nor on low health.
            if (riding) return MountAttack(q.Mount!, s, m, enemy);
            if (s.Statuses.Overlaps(KnockOutStatuses))
                return new StepPlan(StepKind.Wait, "weak from a knock-out: no fight is started until that wears off");
            return Rest(s, m, now) ?? Fight(s, enemy, why);
        }
        if (s.InCombat)
        {
            // Nothing of the run's to fight (the end of a fight, or someone else's): wait it out. The busy watch bounds it.
            // Not when what attacks couldn't be reached or hurt: standing there helps nothing, so the step goes on.
            if (s.Fighter && !riding && !EnemyRules.AttackedBeyondReach(s, m)) return new StepPlan(StepKind.Wait, "in combat");
            // A class that doesn't fight carries on: by the last calm look when the quest hasn't moved since (below),
            // else on to the next marked place, away from whatever attacks.
            if (!s.ScriptRead)
                return Where(q, s, m) is { } on
                    ? on with { What = "carry on while attacked: " + on.What, NoFly = riding && !q.Mount!.CanFly }
                    : new StepPlan(StepKind.Wait, "in combat");
        }
        if (!s.ScriptRead) return new StepPlan(StepKind.Wait, "busy");

        var fights = EnemyRules.Fights(q, s.Sequence);
        // A quest accepted as a crafter or gatherer with a fight at this sequence (the Ixal's): the fight is done on the
        // combat job, the talks and hand-overs as the class it was accepted as.
        if (fights && s.AcceptJob != 0 && HandOrLand(s.AcceptJob))
            return Detour(q, s, m, now, leaveOutSay, combatJob, riding);

        // The class first: as another class nobody (the facility supervisor neither) moves the quest on.
        if (s.AcceptJob != 0 && s.Job != s.AcceptJob)
        {
            if (!s.InCombat) return Change(s.AcceptJob, s);
            // The game changes no class in combat: the trip goes on meanwhile, the class once that's over.
            return OnTheWay(Step(q, s, m, now, leaveOutSay, riding, fights) with { Job = s.AcceptJob }, s);
        }
        // A fight ahead on a class the basic combat has no actions for (a quest already in the journal can have been
        // accepted as any combat class): the player's, before anything calls the enemy.
        if (fights && !s.Fighter) return Ask(NoActions);
        return Careful(q, s, m, now, Step(q, s, m, now, leaveOutSay, riding, fights), fights);
    }

    private const string NoActions = "This step has a fight, and Provenance's basic combat has no actions for the class you're on; do the fight yourself, then resume.";

    private static string Stale() => $"Nothing has changed for {NoProgressLimit.TotalMinutes:0} minutes; do this step yourself, then resume.";

    private static StepPlan Change(uint job, StepSnapshot s)
        => new(StepKind.SwitchJob, job == s.AcceptJob ? "the quest goes on only as the class it was accepted as" : "the quest's fight is done on your combat job") { Id = job };

    /// <summary>
    /// A sequence with a fight, of a quest accepted as a crafter or gatherer (13 Ixal dailies). Which class does what:
    /// talking to someone a to-do line names, handing over and crafting are done as the class the quest was accepted
    /// as (it only moves on as that one); everything else in the sequence is done on the combat job, since it calls
    /// the quest's enemies or leads through where they appear (the object that's used, the place walked into, the
    /// NPCs who turn on you, the field mobs' area), and the class can't be changed once something attacks. So every
    /// trip is made on the combat job, and the class the quest was accepted as goes on only at whoever is talked to
    /// (<see cref="ClassChangeWithin"/>), out of combat, with none of the quest's enemies left to fight.
    /// </summary>
    /// <remarks>
    /// By the game data and the quests' scripts (7.58): 1505 "Of Rodents and Rigging" hands its crate to Hildeyerd in
    /// sequence 2, with an ambush on the way (an area that calls a Qiqirn); 1516 "Hot Cross Buns" has "Speak with
    /// Duvicauroix" and the shearing knife's use on a feral croc both in sequence 1 (in either order, by its script: the
    /// knife is in the bags from the accept, Duvicauroix hands out the materials). Those two name the NPC in the to-do
    /// line's place. 1502 and 1519 call their enemies by talking to NPCs no line names; 1494, 1496, 1504 and 1513 by an
    /// object or an area; 1503, 1506, 1512, 1517 and 1566 fight in a sequence of its own. Whether the game would take a
    /// hand-over from the combat job isn't known (nothing here was run in game); as the crafter it does.
    /// </remarks>
    private static StepPlan Detour(QuestScript q, StepSnapshot s, StepMemory m, DateTime now, bool leaveOutSay, uint combatJob, bool riding)
    {
        if (combatJob == 0 || HandOrLand(combatJob))
            return Ask("This step has a fight, and no combat job is picked under Settings, Monster drops. Pick one (or do the fight yourself), then resume.");
        var step = Step(q, s, m, now, leaveOutSay, riding, true);
        // Nothing to do just now (an emote that may still count): no class is changed for a wait.
        if (step.Kind == StepKind.Wait) return step;
        var handOver = HandOver(q, s, step);
        var own = step.Kind is StepKind.Craft or StepKind.Gather || (handOver && Near(step, s, ClassChangeWithin));
        var wanted = own ? s.AcceptJob : combatJob;
        // The step is done as the class it needs: arriving as another (the trip to a hand-over), it's decided afresh.
        if (step.Kind is not (StepKind.AskUser or StepKind.Craft or StepKind.Gather)) step = step with { Job = handOver ? s.AcceptJob : combatJob };
        if (s.Job != wanted)
        {
            if (!s.InCombat) return Change(wanted, s);
            // Attacked as the wrong class (the crafter before the change, or a straggler that can't be hit after the
            // fight): the game changes no class in combat, so the trip goes on and the class once that's over.
            return OnTheWay(step, s);
        }
        if (own || handOver || step.Kind == StepKind.AskUser) return step;
        // On the combat job, with a fight ahead: one the basic combat has actions for.
        if (!s.Fighter) return Ask(NoActions);
        return Careful(q, s, m, now, step, true);
    }

    /// <summary>
    /// Talking to (or a phrase or emote at) an NPC the open to-do lines name, or whoever the quest is reported to: a
    /// talk or hand-over, not something that calls the quest's enemies. An NPC the quest's script holds without a line
    /// naming them (the Qiqirn rats of 1502, the suspicious Coerthans of 1519) is one who turns on you.
    /// </summary>
    public static bool HandOver(QuestScript q, StepSnapshot s, StepPlan step)
        => step.Kind is StepKind.Talk or StepKind.Say or StepKind.Emote && step.Target is { Kind: StepObjectKind.Npc } t
           && (t.BaseId == TurnIn(q) || t.BaseId == q.Giver || SpotObjects(OpenLines(q, s)).Contains(t.BaseId));

    /// <summary>
    /// The open to-do lines point somewhere besides this NPC (another NPC or object, or an area): a talk with them that
    /// played its scene and changed nothing isn't tried again before those (see the stepper).
    /// </summary>
    public static bool PointsElsewhere(QuestScript q, StepSnapshot s, uint npc)
        => OpenLines(q, s).SelectMany(t => t.Spots).Any(p => p.Object != npc && p.Object != q.Giver);

    /// <summary>The step's target or spot is in this zone, within this many yalms on the ground.</summary>
    private static bool Near(StepPlan step, StepSnapshot s, float within)
    {
        if ((step.Target?.Position ?? step.Enemy?.Position ?? step.Spot) is not { } at) return true;
        if (step.Territory != 0 && step.Territory != s.Territory) return false;
        return Vector2.Distance(new Vector2(at.X, at.Z), new Vector2(s.Player.X, s.Player.Z)) <= within;
    }

    /// <summary>
    /// In combat on a class the step can't be done as: the way to it is made all the same (standing where one is
    /// attacked helps nothing); there, the wait for the fight to end, and then the class.
    /// </summary>
    private static StepPlan OnTheWay(StepPlan step, StepSnapshot s)
    {
        var travels = step.Kind is StepKind.Talk or StepKind.Say or StepKind.Emote or StepKind.UseItem or StepKind.Go && (step.Target != null || step.Spot != null);
        if (step.Kind == StepKind.AskUser) return step;
        if (!travels || Near(step, s, step.Within)) return new StepPlan(StepKind.Wait, "in combat: the class can't be changed now");
        return step with { What = "on the way while attacked (the class is changed once that's over): " + step.What };
    }

    /// <summary>
    /// A step in a sequence with a fight that may call the quest's enemies (anything but a talk or hand-over: an object
    /// used, a key item, a place walked into, the mount's action) waits while the character is weak from a knock-out,
    /// and, when it's close by, until the health is back (<see cref="Rest"/>).
    /// </summary>
    private static StepPlan Careful(QuestScript q, StepSnapshot s, StepMemory m, DateTime now, StepPlan step, bool fights)
    {
        if (!fights || HandOver(q, s, step)) return step;
        if (step.Kind is not (StepKind.Talk or StepKind.Say or StepKind.Emote or StepKind.UseItem or StepKind.MountAction or StepKind.Go)) return step;
        // Weak from a knock-out, with a fight ahead: wait until it wears off (it lasts under two minutes).
        if (s.Statuses.Overlaps(KnockOutStatuses))
            return new StepPlan(StepKind.Wait, "weak from a knock-out: no fight is started until that wears off");
        return Near(step, s, RestNear) && Rest(s, m, now) is { } rest ? rest : step;
    }

    /// <summary>
    /// Before a fight the run picks (never before fighting back): the wait until the health is at
    /// <see cref="FightFromHp"/> or more, for <see cref="RestFor"/> at most; null when there's nothing to wait for. The
    /// clock is kept in the memory, and starts over once the health is back (or, by the stepper, with the next fight).
    /// </summary>
    public static StepPlan? Rest(StepSnapshot s, StepMemory m, DateTime now)
    {
        if (s.Hp >= FightFromHp)
        {
            m.RestSince = default;
            return null;
        }
        if (m.RestSince == default) m.RestSince = now;
        return now - m.RestSince > RestFor ? null : new StepPlan(StepKind.Wait, $"getting your health back before the next fight ({s.Hp}%)");
    }

    /// <summary>
    /// The quest's step once nothing is to be fought and the script could be read: crafting, gathering, the quest
    /// mount, a phrase or emote, a key item, the hand-over, the marked target, the marked place, where the quest's
    /// enemies appear; else the player.
    /// </summary>
    private static StepPlan Step(QuestScript q, StepSnapshot s, StepMemory m, DateTime now, bool leaveOutSay, bool riding, bool fights)
    {
        // Crafting: the to-do line is about the product, or (older quests) the materials are in the bags and products
        // are still short. Short of both: talk to whoever the step marks (a crate, the materials NPC hands more out).
        var craftShort = false;
        if (Craft(q, s) is { } craft && craft.Count > 0)
        {
            if (s.Count(q.Materials) > 0 || q.Materials == 0)
            {
                if (q.CraftStatus != 0 && (!s.Statuses.Contains(q.CraftStatus) || s.Expiring.Contains(q.CraftStatus)))
                {
                    if (StatusGiver(q, s, m) is { } giver)
                        return Talk(giver, s.Statuses.Contains(q.CraftStatus) ? "renew the facility access (it runs out soon) before crafting" : "get the facility access back before crafting");
                    return Ask("The crafting needs the facility access status; talk to the supervisor for it, then resume.");
                }
                if (craft.Hq && m.HqMisses >= MaxHqMisses)
                    return Ask($"The quest only counts its item made high quality, and {m.HqMisses} tries came out normal quality; craft it high quality yourself (food, medicine or better gear help), then resume.");
                if (s.InCombat) return new StepPlan(StepKind.Wait, "in combat: the crafting waits");
                return craft;
            }
            craftShort = true;
        }

        // Gathering: the to-do line is about the item for the class the quest was accepted as.
        if (Gather(q, s, m) is { } gather && gather.Count > 0)
            return s.InCombat ? new StepPlan(StepKind.Wait, "in combat: the gathering waits") : gather;

        if (riding) return Riding(q, s, m);

        var carrying = q.CarryStatus != 0 && s.Statuses.Contains(q.CarryStatus);
        var lines = OpenLines(q, s);

        foreach (var line in lines)
        {
            if (q.Say.TryGetValue(line.Index, out var phrase))
            {
                if (leaveOutSay) return Ask($"This step says “{phrase}” in public (/say), which is turned off; say it yourself, then resume.");
                return Public(q, s, m, now, line, StepKind.Say, phrase, 0, $"say “{phrase}”");
            }
            if (q.Emotes.TryGetValue(line.Index, out var emote))
                return Public(q, s, m, now, line, StepKind.Emote, emote.Command, emote.Emote, $"{emote.Command} at the target");
        }

        // A key item to use. The quest's listeners say at which sequences one is used at all: at any other, a key item
        // that's held was collected, or goes to someone by talking ("Deliver the limpet bomb core to Ba Go", "Obtain
        // sugary sap from deepwood saplings"), however the to-do line names it. Where they say one is used, or where the
        // data says nothing, the line names it ("Use the firecrackers ..."), or it's the only one held and the line
        // points at objects (not people) to use it on ("Salt the designated locations").
        var usable = q.KeyItems.Where(k => k.Usable && s.Count(k.Id) > 0).ToList();
        if (q.ItemSequences is { } useSequences && !useSequences.Contains(s.Sequence)) usable.Clear();
        // The listeners also say what the item goes on: the NPCs and objects they name at this sequence, and no object at
        // all when they name only enemies (the item is for the enemy then: the fight uses it).
        var itemObjects = fights || q.ItemSequences != null ? q.ItemObjects.Where(o => o.Sequence == s.Sequence).Select(o => o.Object).ToHashSet() : new HashSet<uint>();
        var itemForEnemies = fights && itemObjects.Count == 0 && EnemyRules.Active(q, s.Sequence).Any(e => e.Task == EnemyTask.UseItem);
        foreach (var item in itemForEnemies ? new List<KeyItemFact>() : usable)
        {
            // Any of its names whole; its title's last word too (not the singular's or plural's: "sacks" is too common).
            var naming = lines.Where(l => item.Words.Any(w => l.Text.Contains(w, StringComparison.CurrentCultureIgnoreCase)) || Names(l.Text, item.Name)).ToList();
            var named = naming.Count > 0;
            var spotObjects = SpotObjects(named ? naming : lines);
            var live = Live(q, s, m).ToList();
            var onObjects = usable.Count == 1 && lines.Count > 0 && live.Any(o => o.Kind == StepObjectKind.Object && spotObjects.Contains(o.BaseId));
            // The only item held, and the listeners name what it goes on ("Free the captives from their cages"): on those.
            var onListened = usable.Count == 1 && itemObjects.Count > 0 && live.Any(o => itemObjects.Contains(o.BaseId));
            if ((named || onObjects || onListened) && UseItem(q, s, m, item, carrying, named, spotObjects, itemObjects) is { } use) return use;
        }

        // Once the crafting or gathering is done: to whoever the items go to, and nobody else: whoever the quest is
        // reported to at its report, else the first place of the crafting line. The NPC who handed the materials out
        // keeps the quest's icon (he hands out spares), but talking to him again only says to go and deliver the work.
        if (Supplied(q, s))
        {
            if (s.Sequence == 255) return Report(q, s, m) with { OnFoot = carrying };
            if (Deliver(q, s, m) is { } deliver) return deliver with { OnFoot = carrying };
        }
        // Nothing made yet and nothing to make it from: whoever hands the materials out, before the one the work goes to.
        else if (Source(q, s, m, craftShort) is { } source) return source with { OnFoot = carrying };

        // Carrying something: not to whoever handed it over (talking to them again only says where it goes).
        if (Pick(q, s, m, carrying && m.CarryGiver != 0 ? o => o.BaseId != m.CarryGiver : null) is { } target)
        {
            // Getting the quest mount (or what's carried) back again and again: something takes it away; the player's.
            if (!riding && q.Mount != null && s.Sequence != 255 && m.MountGiver != 0 && target.BaseId == m.MountGiver && m.Mounts > MaxRegains)
                return Ask($"The quest mount was lost {m.Mounts} times; do this step yourself, then resume.");
            if (!carrying && m.CarryGiver != 0 && target.BaseId == m.CarryGiver && m.Pickups > MaxRegains)
                return Ask($"What you carry was dropped {m.Pickups} times; do this step yourself, then resume.");
            return Talk(target, carrying ? "deliver what you carry" : "the step's marked target") with { OnFoot = carrying };
        }

        // The quest wants only its item used on its enemies, and the item isn't in the bags (yet, or any more): there's
        // nothing to do in the enemies' area, so the line's other places come first (whoever hands the item out).
        if (fights && EnemyRules.Active(q, s.Sequence) is { Count: > 0 } wanted && wanted.All(e => e.Task == EnemyTask.UseItem) && EnemyRules.EnemyItem(q, s) == null)
        {
            var areas = lines.SelectMany(t => t.Spots).Where(p => p.Area).Select(p => p.Level).ToHashSet();
            if (areas.Count > 0 && Where(q, s, m, areas) is { } first) return first with { OnFoot = carrying };
        }
        // Carrying something: not back to where whoever handed it over stands.
        var fromGiver = carrying && m.CarryGiver != 0 ? lines.SelectMany(t => t.Spots).Where(p => p.Object == m.CarryGiver).Select(p => p.Level).ToHashSet() : null;
        if (Where(q, s, m, fromGiver) is { } go) return go with { OnFoot = carrying };

        // Nothing marked is left and none of the quest's enemies is in view: where the quest puts them, and around
        // the marked area for field mobs.
        if (fights)
        {
            if (EnemyRules.Search(q, s, m) is { } look) return look with { OnFoot = carrying };
            return Ask("Couldn't find the quest's enemies (none in view that the quest marks, and nowhere left to look); do this step yourself, then resume.");
        }
        if (craftShort)
            return Ask("Out of the materials for the quest's item, and nobody marked hands out more; get them from the quest's NPC, then resume.");
        return Ask("Can't tell what this step wants (nothing marked nearby, no map marker); do it yourself, then resume.");
    }

    /// <summary>A fight with an enemy the filter allows: the trip gets near it, the fight closes in and does the rest.</summary>
    private static StepPlan Fight(StepSnapshot s, StepEnemy enemy, EnemyJudgement why)
        => new(StepKind.Fight, why.Verdict switch
        {
            EnemyVerdict.Attacker => "fight back: " + why.Why,
            EnemyVerdict.Subdued => "use the quest's item on it: " + why.Why,
            _ => "the quest's enemy: " + why.Why,
        })
        {
            Enemy = enemy, Spot = enemy.Position, Territory = s.Territory, Within = EngageWithin, Key = $"fight:{enemy.Id}",
        };

    /// <summary>
    /// The quest mount's action on one of the quest's own enemies (<see cref="EnemyRules.NextOwn"/>; a mount without
    /// an action has no such target). An enemy it did nothing to in <see cref="InteractTries"/> uses the game took is
    /// left by the stepper, which goes by the enemy's health before and after.
    /// </summary>
    private static StepPlan MountAttack(MountFact mount, StepSnapshot s, StepMemory m, StepEnemy enemy)
    {
        var key = $"action:{enemy.Id}";
        return new StepPlan(StepKind.MountAction, "the quest mount's action on the quest's enemy")
        {
            Enemy = enemy, Spot = enemy.Position, Territory = s.Territory, Within = ActionWithin(mount), Id = mount.ActionFor(m.Count(key)),
            Face = mount.Line, NoFly = !mount.CanFly, Key = key, Use = mount.Ground ? ItemUse.Ground : ItemUse.Target,
        };
    }

    /// <summary>How close to ride for the mount's action: inside its line, inside its reach around the rider, or inside its range on a target.</summary>
    private static float ActionWithin(MountFact mount)
        => mount.Line ? Math.Clamp(mount.EffectRange * 0.6f, 4f, 12f)
            : mount.Range <= 0 ? Math.Max(2f, mount.EffectRange - 2f)
            : Math.Max(2f, mount.Range - 2f);

    /// <summary>
    /// On the quest mount: its action on the next target, riding into the area, or (at the report) getting off. Before
    /// the report, a mount with nothing to do in view and nowhere left to look is the player's: getting off would lose it.
    /// </summary>
    private static StepPlan Riding(QuestScript q, StepSnapshot s, StepMemory m)
    {
        var mount = q.Mount!;
        var noFly = !mount.CanFly;
        // Not targets of the action: whoever put you on the mount (talked to in an earlier sequence), and the giver.
        var earlier = q.Todos.Where(t => t.Sequence < s.Sequence).SelectMany(t => t.Spots).Select(p => p.Object).Where(o => o != 0).ToHashSet();
        var targets = Live(q, s, m).Where(o => o.BaseId != m.MountGiver && o.BaseId != q.Giver && !earlier.Contains(o.BaseId)).ToList();
        if (mount.Action != 0 && targets.Count > 0)
        {
            var t = Nearest(targets, s.Player);
            var key = $"action:{t.Key}";
            // A mount with several actions (a colour of gulal per target): the next one on a target the last did nothing to.
            return new StepPlan(StepKind.MountAction, "the quest mount's action on the next target")
            {
                Target = t, Territory = s.Territory, Within = ActionWithin(mount), Id = mount.ActionFor(m.Count(key)), Face = mount.Line, NoFly = noFly, Key = key,
                Use = mount.Ground ? ItemUse.Ground : ItemUse.Target,
            };
        }
        if (s.Sequence == 255)
            return new StepPlan(StepKind.Dismount, "the quest mount has nothing more to do (the quest can't be reported while riding it)");
        // Nothing to use the action on in view: the marked area (or the spot of the line). Not the places of earlier
        // lines while another is left: whoever put you on the mount stays marked (the line's second place: the mount
        // can be had again there), and riding back there only to look around loses a long way.
        var behind = q.Todos.Where(t => t.Sequence < s.Sequence).SelectMany(t => t.Spots).Select(p => p.Level).ToHashSet();
        if ((Where(q, s, m, behind) ?? Where(q, s, m)) is { } go)
            return go with { NoFly = noFly, What = mount.Action == 0 ? "ride into the marked area" : go.What };
        // The mount's action is for enemies the quest puts somewhere (the marid's ants): ride to where they appear.
        if (EnemyRules.Search(q, s, m) is { } look) return look with { NoFly = noFly };
        return Ask("On the quest mount with nothing left in view to use it on and nowhere marked; do this step yourself, then resume.");
    }

    /// <summary>The craft step, when the current to-do line is about the quest's product (or, for older quests, the materials wait).</summary>
    public static StepPlan? Craft(QuestScript q, StepSnapshot s)
    {
        // Accepted as a gatherer (the Namazu quests take either), the quest gathers instead.
        if (q.Product == 0 || s.AcceptJob is >= 16 and <= 18) return null;
        var hq = WantsHq(q, s);
        var have = Made(q, s);
        var line = s.Todos.FirstOrDefault(t => t.ItemId == q.Product && t.Needed > 0);
        int needed;
        if (line != null) needed = (int)line.Needed;
        else if (s.Count(q.Materials) > 0 && q.TodosAt(s.Sequence).Any()) needed = have + s.Count(q.Materials);
        else return null;
        return new StepPlan(StepKind.Craft, hq ? "craft the quest's item, high quality (only that counts)" : "craft the quest's item")
        {
            Id = q.Product, Other = q.Materials, Count = Math.Max(0, needed - have), Hq = hq,
        };
    }

    /// <summary>
    /// Only high-quality copies of the quest's product count: its script says so, or the to-do line's item carries the
    /// high-quality mark. Normal-quality ones "serve no purpose", as the game puts it at the hand-in.
    /// </summary>
    public static bool WantsHq(QuestScript q, StepSnapshot s)
        => q.Product != 0 && (q.ProductHq || s.Todos.Any(t => t.ItemId == q.Product && t.WantsHq));

    /// <summary>The quest's products in the bags that count: the high-quality ones when only those do.</summary>
    public static int Made(QuestScript q, StepSnapshot s) => WantsHq(q, s) ? s.CountHq(q.Product) : s.Count(q.Product);

    /// <summary>
    /// The game's own count of the crafting: the to-do line about the product, else (older quests name no item on it)
    /// the crafting line when it counts something.
    /// </summary>
    private static TodoProgress? CraftCount(QuestScript q, StepSnapshot s)
        => s.Todos.FirstOrDefault(t => t.ItemId == q.Product && t.Needed > 0)
           ?? (CraftLine(q, s) is { } line ? s.Todos.FirstOrDefault(t => t.Index == line.Index && t.Item == 0 && t.Needed > 0) : null);

    /// <summary>
    /// The gather step, when the current to-do line is about the item for the class the quest was accepted as; with
    /// the area to gather in when the game marks one (an area marker, else the line's area spot), so a sub-zone
    /// (Elysion) is entered first.
    /// </summary>
    public static StepPlan? Gather(QuestScript q, StepSnapshot s, StepMemory? m = null)
    {
        if (!q.GatherItems.TryGetValue(s.AcceptJob, out var item) || item == 0) return null;
        var line = s.Todos.FirstOrDefault(t => t.ItemId == item);
        if (line == null) return null;
        var needed = line.Needed > 0 ? (int)line.Needed : DefaultGatherCount;
        var have = s.Count(item);
        var plan = new StepPlan(StepKind.Gather, "gather the quest's item") { Id = item, Other = item, Count = Math.Max(0, needed - have) };
        var area = s.Markers.Where(k => k.IsArea).OrderBy(k => k.Territory == s.Territory ? 0 : 1).ThenBy(k => Vector3.DistanceSquared(k.Position, s.Player)).FirstOrDefault();
        if (area != null) return plan with { Spot = area.Position, Territory = area.Territory, Within = Math.Max(5f, area.Radius) };
        var spot = q.Todos.Where(t => t.Index == line.Index).SelectMany(t => t.Spots).Where(p => p.Area)
            .OrderBy(p => p.Territory == s.Territory ? 0 : 1).ThenBy(p => Vector3.DistanceSquared(p.Position, s.Player)).FirstOrDefault();
        if (spot != null) return plan with { Spot = spot.Position, Territory = spot.Territory, Within = 30f, Level = spot.Level };
        // No area to go by: a zone the script sends the character to (Elysion for the Omicrons) when it isn't the giver's,
        // else here. The run finds the place from the game's gathering data.
        var giverZone = q.GiverSpot?.Territory ?? s.Territory;
        var away = q.Territories.FirstOrDefault(t => t != giverZone);
        return plan with { Territory = away != 0 ? away : s.Territory };
    }

    /// <summary>
    /// A /say or an emote at the line's target: only someone the line's own spots name (never a bystander, never the
    /// giver unless the line names them), and once per step (a /say once, an emote once more after
    /// <see cref="PublicRetryAfter"/> with nothing changing); then the player does it.
    /// </summary>
    private static StepPlan Public(QuestScript q, StepSnapshot s, StepMemory m, DateTime now, ScriptTodo line, StepKind kind, string text, uint id, string what)
    {
        var spots = SpotObjects(new[] { line });
        var target = spots.Count == 0 ? null
            : Pick(q, s, m, o => spots.Contains(o.BaseId) && (kind != StepKind.Emote || o.Kind == StepObjectKind.Npc));
        StepPlan plan;
        string where;
        if (target != null)
        {
            plan = new StepPlan(kind, what) { Target = target, Territory = s.Territory, Within = PublicWithin, Text = text, Id = id, Key = PublicKey(q, s, line, kind) };
            where = $"near {target.Name}";
        }
        else if (kind == StepKind.Say && line.Spots.FirstOrDefault(p => p.Area || p.Object == 0) is { } area)
        {
            // A phrase said at a place, not to someone (the line's spot is an area nobody stands on): go into the area and
            // say it there, once. The live marker gives the area's real size when there is one.
            var marker = s.Markers.FirstOrDefault(k => k.Level == area.Level);
            plan = new StepPlan(kind, what)
            {
                Spot = marker?.Position ?? area.Position, Territory = marker?.Territory ?? area.Territory, Level = area.Level,
                Within = marker is { IsArea: true } ? Math.Clamp(marker.Radius * 0.5f, 3f, 15f) : AreaSayWithin,
                Text = text, Id = id, Key = PublicKey(q, s, line, kind),
            };
            where = "at the marked place";
        }
        else
        {
            // Nothing in view yet: go there first (the target comes into view on the way).
            if (Where(q, s, m) is { } go && go.Target == null && go.Spot != null) return go;
            return Ask($"Can't find where to {what}; do this step yourself, then resume.");
        }

        var verdict = PublicVerdict(kind, m.Public.Get(plan.Key, now), m.ProgressAt, now);
        return verdict switch
        {
            // Attacked (a class that doesn't fight carries on): the way there is made, but nothing public is sent in
            // combat. It's sent once only, and the game may not count it then.
            PublicTry.Yes when s.InCombat && Near(plan, s, plan.Within)
                => new StepPlan(StepKind.Wait, $"in combat: the {(kind == StepKind.Say ? "phrase" : "emote")} waits until that's over (it's sent only once)"),
            PublicTry.Yes => plan,
            PublicTry.NotYet => new StepPlan(StepKind.Wait, $"waiting to see whether the {(kind == StepKind.Say ? "phrase" : "emote")} counted"),
            _ => Ask(kind == StepKind.Say
                ? $"Saying “{text}” didn't move the quest on; do this step yourself (say it {where}), then resume."
                : $"{text} didn't move the quest on; do this step yourself ({text} {where.Replace("near", "at")}), then resume."),
        };
    }

    /// <summary>How close to an area's spot a phrase is said when the game shows no marker for the area.</summary>
    public const float AreaSayWithin = 6f;

    /// <summary>
    /// What a public action is counted under: the quest, the line and the quest's state (sequence and variables), not
    /// the target, so a phrase that didn't count isn't said again near someone else. The state is in it because a line
    /// can want the phrase said to two or three people: once one counts, the quest's variables move and the next is a
    /// step of its own.
    /// </summary>
    public static string PublicKey(QuestScript q, StepSnapshot s, ScriptTodo line, StepKind kind)
        => $"{q.Quest}:{kind}:{line.Index}:{s.Sequence}:{Convert.ToHexString(s.Variables)}";

    public enum PublicTry { Yes, NotYet, GiveUp }

    /// <summary>
    /// Whether a public action may be sent: never tried, yes. Tried: not before both the try and the last change of the
    /// quest are <see cref="PublicRetryAfter"/> old (it may still count); after that again only while under
    /// <see cref="PublicTries"/> (an emote once more, a /say never), otherwise it's the player's.
    /// </summary>
    public static PublicTry PublicVerdict(StepKind kind, Attempt? tried, DateTime progressAt, DateTime now)
    {
        if (tried == null || tried.Count == 0) return PublicTry.Yes;
        if (now - tried.Last < PublicRetryAfter || now - progressAt < PublicRetryAfter) return PublicTry.NotYet;
        return tried.Count >= PublicTries(kind) ? PublicTry.GiveUp : PublicTry.Yes;
    }

    /// <summary>
    /// Using a key item: on the target, on the ground at it, or on yourself next to it. Thrown on the ground (a wrong
    /// spot can use it up), or used without the line naming it, it's aimed only at an object the line's spots name.
    /// </summary>
    private static StepPlan? UseItem(QuestScript q, StepSnapshot s, StepMemory m, KeyItemFact item, bool carrying, bool named, IReadOnlySet<uint> spotObjects,
        IReadOnlySet<uint>? itemObjects = null)
    {
        var use = item.Ground ? ItemUse.Ground : item.Self ? ItemUse.Self : ItemUse.Target;
        var strict = use == ItemUse.Ground || !named;
        // What the quest's listeners say the item goes on, when they name NPCs or objects at this sequence: those only.
        // Otherwise, thrown on the ground or used without the line naming it, only an object the line's spots name.
        Func<StepObject, bool>? aim = itemObjects is { Count: > 0 } ? o => itemObjects.Contains(o.BaseId)
            : strict ? o => spotObjects.Contains(o.BaseId) && (named || o.Kind == StepObjectKind.Object)
            : null;
        // Never on an object the game said isn't a target for this item (563 "Invalid target"): that one is talked to.
        Func<StepObject, bool> only = o => (aim?.Invoke(o) ?? true) && !m.NotForItem(s, item.Id, o.Key);
        var target = Pick(q, s, m, only);
        var within = use == ItemUse.Self ? 2f : Math.Max(1.5f, item.Range - 1f);
        if (target != null)
            return new StepPlan(StepKind.UseItem, $"use the {item.Name}") { Target = target, Territory = s.Territory, Within = within, Id = item.Id, Use = use, OnFoot = carrying, Key = $"item:{item.Id}:{target.Key}" };
        // Only objects the game turned the item down for are in view: the step's talk (below) is about them.
        if (Live(q, s, m).Any(o => (aim?.Invoke(o) ?? true) && m.NotForItem(s, item.Id, o.Key))) return null;
        if (Where(q, s, m) is { } go)
        {
            // An area to use it in (no object to aim at): self-use there; otherwise go and look first.
            if (use == ItemUse.Self && go.Spot != null && go.Target == null)
                return new StepPlan(StepKind.UseItem, $"use the {item.Name} in the marked area") { Spot = go.Spot, Territory = go.Territory, Within = go.Within, Id = item.Id, Use = use, Level = go.Level, Key = $"item:{item.Id}:{go.Level}" };
            return go;
        }
        return null;
    }

    /// <summary>
    /// The quest's crafting or gathering is done: it makes or gathers an item (for the class it was accepted as), none
    /// of it is left to make or gather, and the to-do line about the item counts it all (or, with no line that counts
    /// it, the items are in the bags). Short of materials isn't done: the materials NPC is still wanted then.
    /// </summary>
    public static bool Supplied(QuestScript q, StepSnapshot s)
    {
        var crafts = q.Product != 0 && s.AcceptJob is not (>= 16 and <= 18);
        var item = crafts ? q.Product : q.GatherItems.GetValueOrDefault(s.AcceptJob);
        if (item == 0) return false;
        if ((crafts ? Craft(q, s) : Gather(q, s))?.Count > 0) return false;
        var line = crafts ? CraftCount(q, s) : s.Todos.FirstOrDefault(t => t.ItemId == item && t.Needed > 0);
        return line != null ? line.Have >= line.Needed : (crafts ? Made(q, s) : s.Count(item)) > 0;
    }

    /// <summary>
    /// The to-do line of the quest's crafting ("Synthesize ...") when the quest is at it: a line that places exactly
    /// two NPCs or objects. The first is whoever the made items go to, the second where the materials come from (a
    /// crate, a machine, the NPC handing them out). The game data has it so for every crafting daily of the societies;
    /// the line's text can't be gone by (it differs by language, and newer quests leave the item's name out of it).
    /// </summary>
    public static ScriptTodo? CraftLine(QuestScript q, StepSnapshot s)
        => q.Product == 0 ? null
            : q.TodosAt(s.Sequence).FirstOrDefault(t => t.Spots.Count == 2 && t.Spots.All(p => p.Object != 0 && !p.Area) && t.Spots[0].Object != t.Spots[1].Object);

    /// <summary>
    /// Before the report, the made (or gathered) items go to the crafting line's first place: talk to them when in view
    /// (not tried without effect), else go to where they stand. Null when the quest isn't at such a line, or that was
    /// tried: the general rules decide then.
    /// </summary>
    private static StepPlan? Deliver(QuestScript q, StepSnapshot s, StepMemory m)
    {
        if (CraftLine(q, s) is not { } line) return null;
        var to = line.Spots[0];
        var lines = LineKey(q, s);
        var npc = s.Objects
            .Where(o => o.BaseId == to.Object && o.Kind != StepObjectKind.Other && o.Targetable && !m.Tried.Contains(o.Key) && !m.IsDecoy(lines, o.Key))
            .OrderBy(o => o.Icon != 0 ? 0 : 1)
            .ThenBy(o => Vector3.DistanceSquared(o.Position, s.Player))
            .FirstOrDefault();
        if (npc != null) return Talk(npc, "hand in what was made (the crafting or gathering is done)");
        if (!m.Looked.Contains(to.Level))
            return new StepPlan(StepKind.Go, "where the made items go") { Spot = to.Position, Territory = to.Territory, Within = 6f, Level = to.Level };
        return null;
    }

    /// <summary>
    /// At the crafting line with nothing to craft from (no materials; nothing made yet, or not enough): the line's
    /// second place hands the materials out, so that comes before whoever the work goes to. Crafters only (accepted as
    /// a gatherer, the quest gathers). Null when it doesn't apply or was tried: the general rules decide then.
    /// </summary>
    private static StepPlan? Source(QuestScript q, StepSnapshot s, StepMemory m, bool craftShort)
    {
        if (q.Materials == 0 || s.AcceptJob is >= 16 and <= 18 || s.Count(q.Materials) > 0) return null;
        // Short: by the game's own count when it has one (it counts only what counts: high quality where that's wanted).
        var counted = CraftCount(q, s);
        if (!craftShort && !(counted != null ? counted.Have < counted.Needed : Made(q, s) == 0)) return null;
        if (CraftLine(q, s) is not { } line) return null;
        var from = line.Spots[1];
        var lines = LineKey(q, s);
        var npc = s.Objects
            .Where(o => o.BaseId == from.Object && o.Kind != StepObjectKind.Other && o.Targetable && (o.Active || o.Icon != 0)
                        && !m.Tried.Contains(o.Key) && !m.IsDecoy(lines, o.Key))
            .OrderBy(o => o.Icon != 0 ? 0 : 1)
            .ThenBy(o => Vector3.DistanceSquared(o.Position, s.Player))
            .FirstOrDefault();
        if (npc != null) return Talk(npc, "get the materials for the quest's item");
        if (!m.Looked.Contains(from.Level))
            return new StepPlan(StepKind.Go, "where the materials are handed out") { Spot = from.Position, Territory = from.Territory, Within = 6f, Level = from.Level };
        return null;
    }

    /// <summary>
    /// An item id without its quality mark (high quality adds 1,000,000). Key items (2,000,000 and up) have none: theirs
    /// is the id as it is, so a key item asked for in a hand-over is found among the offered ones.
    /// </summary>
    public static uint BaseItem(uint id) => id >= 2_000_000 ? id : id % 1_000_000;

    /// <summary>Who the quest is reported to: its <c>TargetEnd</c> (the giver for every society daily).</summary>
    public static uint TurnIn(QuestScript q) => q.TargetEnd != 0 ? q.TargetEnd : q.Giver;

    /// <summary>
    /// Where whoever the quest is reported to stands: the giver's spot when it's the giver (the quest's
    /// <c>IssuerLocation</c>), else a to-do line's spot that places them (the last sequence's first).
    /// </summary>
    public static ScriptSpot? TurnInSpot(QuestScript q)
    {
        var turnIn = TurnIn(q);
        if (q.GiverSpot is { } giver && (turnIn == q.Giver || giver.Object == turnIn)) return giver;
        return q.Todos.OrderByDescending(t => t.Sequence).SelectMany(t => t.Spots).FirstOrDefault(p => p.Object == turnIn);
    }

    /// <summary>
    /// The report after the crafting or gathering: talk to whoever the quest is reported to when they're in view
    /// (marked ones first, then the nearest), else go to where they stand; nobody else, and no other marker.
    /// </summary>
    private static StepPlan Report(QuestScript q, StepSnapshot s, StepMemory m)
    {
        var turnIn = TurnIn(q);
        var lines = LineKey(q, s);
        var npc = s.Objects
            .Where(o => o.BaseId == turnIn && o.Kind != StepObjectKind.Other && o.Targetable && !m.Tried.Contains(o.Key) && !m.IsDecoy(lines, o.Key))
            .OrderBy(o => o.Icon != 0 ? 0 : 1)
            .ThenBy(o => Vector3.DistanceSquared(o.Position, s.Player))
            .FirstOrDefault();
        if (npc != null) return Talk(npc, "report the quest (the crafting or gathering is done)");
        if (TurnInSpot(q) is { } spot && !m.Looked.Contains(spot.Level))
            return new StepPlan(StepKind.Go, "where the quest is reported")
            {
                Spot = spot.Position, Territory = spot.Territory, Within = 6f, Level = spot.Level,
            };
        return Ask("Can't find who this quest is reported to (the crafting or gathering is done); report it yourself, then resume.");
    }

    private static StepPlan Talk(StepObject target, string what)
        => new(StepKind.Talk, what) { Target = target, Within = TalkWithin, Key = $"talk:{target.Key}" };

    private static StepPlan Ask(string text) => new(StepKind.AskUser, text);

    /// <summary>The to-do lines of the current sequence still open (all of them when the game reports none).</summary>
    public static List<ScriptTodo> OpenLines(QuestScript q, StepSnapshot s)
    {
        var open = s.Todos.Where(t => !t.Checked).Select(t => t.Index).ToHashSet();
        return q.TodosAt(s.Sequence).Where(t => open.Count == 0 || open.Contains(t.Index)).ToList();
    }

    /// <summary>Which lines are open, as a key: decoys are remembered while it stays the same.</summary>
    public static string LineKey(QuestScript q, StepSnapshot s)
        => $"{s.Sequence}:{string.Join(",", OpenLines(q, s).Select(l => l.Index))}";

    /// <summary>The NPCs and objects the lines' spots name.</summary>
    private static HashSet<uint> SpotObjects(IEnumerable<ScriptTodo> lines)
        => lines.SelectMany(t => t.Spots).Select(p => p.Object).Where(o => o != 0).ToHashSet();

    /// <summary>
    /// The quest's objects the step wants something done with, not yet tried without effect nor found to be a decoy on
    /// these lines. An NPC is a target when this quest marks it (the quest's own icon); an object when the script counts
    /// it active. Anything else the quest's handler holds (a gathering point) isn't talked to.
    /// </summary>
    public static IEnumerable<StepObject> Live(QuestScript q, StepSnapshot s, StepMemory m)
    {
        var lines = LineKey(q, s);
        return s.Objects.Where(o => o.Kind != StepObjectKind.Other && o.Targetable && (o.Active || o.Icon != 0)
                                    && !m.Tried.Contains(o.Key) && !m.IsDecoy(lines, o.Key)
                                    && (o.Kind != StepObjectKind.Npc || o.Icon != 0));
    }

    /// <summary>
    /// The target to act on: one the open lines' spots name first, then marked ones, then the nearest. The giver (and
    /// whoever the quest is reported to) only when a line names them or the quest is at its report: talking to the giver
    /// otherwise opens their list of dailies.
    /// </summary>
    public static StepObject? Pick(QuestScript q, StepSnapshot s, StepMemory m, Func<StepObject, bool>? only = null)
    {
        var named = SpotObjects(OpenLines(q, s));
        var live = Live(q, s, m)
            .Where(o => only?.Invoke(o) ?? true)
            .Where(o => s.Sequence == 255 || (o.BaseId != q.Giver && o.BaseId != q.TargetEnd) || named.Contains(o.BaseId))
            .ToList();
        if (live.Count == 0) return null;
        return live.OrderBy(o => named.Contains(o.BaseId) ? 0 : 1)
            .ThenBy(o => o.Icon != 0 ? 0 : 1)
            .ThenBy(o => Vector3.DistanceSquared(o.Position, s.Player))
            .First();
    }

    /// <summary>
    /// Where to go when nothing to act on is in view: the quest's map markers (this zone first, nearest first), then
    /// the current lines' spots; each once until something changes. A marker over one of the quest's objects is gone
    /// to as that object's spot.
    /// </summary>
    /// <param name="skip">Places (Level rows, as markers and spots carry them) not to go to.</param>
    public static StepPlan? Where(QuestScript q, StepSnapshot s, StepMemory m, IReadOnlySet<uint>? skip = null)
    {
        var markers = s.Markers
            .Where(k => !m.Looked.Contains(MarkerKey(k)) && skip?.Contains(MarkerKey(k)) != true)
            .OrderBy(k => k.Territory == s.Territory ? 0 : 1)
            .ThenBy(k => k.Territory == s.Territory ? Vector3.DistanceSquared(k.Position, s.Player) : 0)
            .ToList();
        if (markers.FirstOrDefault() is { } marker)
            return new StepPlan(StepKind.Go, marker.IsArea ? "the marked area" : "the marked spot")
            {
                Spot = marker.Position, Territory = marker.Territory, Within = marker.IsArea ? Math.Clamp(marker.Radius * 0.5f, 3f, 25f) : 6f,
                Level = MarkerKey(marker),
            };
        var spot = OpenLines(q, s)
            .SelectMany(t => t.Spots)
            .Where(p => !m.Looked.Contains(p.Level) && p.Object != q.Giver && skip?.Contains(p.Level) != true)
            .OrderBy(p => p.Territory == s.Territory ? 0 : 1)
            .ThenBy(p => p.Territory == s.Territory ? Vector3.DistanceSquared(p.Position, s.Player) : 0)
            .FirstOrDefault();
        // The last sequence reports to the giver: their spot is the place to go.
        spot ??= s.Sequence == 255 && q.GiverSpot is { } g && !m.Looked.Contains(g.Level) ? g : null;
        if (spot == null) return null;
        return new StepPlan(StepKind.Go, spot.Area ? "the to-do line's area" : "the to-do line's spot")
        {
            Spot = spot.Position, Territory = spot.Territory, Within = spot.Area ? 10f : 6f, Level = spot.Level,
        };
    }

    /// <summary>A marker's key in <see cref="StepMemory.Looked"/>: its Level row, or its zone and rounded place when it has none.</summary>
    public static uint MarkerKey(StepMarker k)
    {
        if (k.Level != 0) return k.Level;
        unchecked
        {
            var h = k.Territory * 2654435761u ^ (uint)(int)MathF.Round(k.Position.X) * 40503u ^ (uint)(int)MathF.Round(k.Position.Z) * 9973u;
            return 0x8000_0000u | (h & 0x7FFF_FFFF);
        }
    }

    /// <summary>For crafting: the NPC on the craft line that isn't the giver (the Ixal supervisor who grants the facility access).</summary>
    private static StepObject? StatusGiver(QuestScript q, StepSnapshot s, StepMemory m)
    {
        var npcs = q.TodosAt(s.Sequence).SelectMany(t => t.Spots).Where(p => p.Object != 0 && p.Object != q.Giver).Select(p => p.Object).ToHashSet();
        return s.Objects.Where(o => o.Targetable && o.Kind != StepObjectKind.Other && npcs.Contains(o.BaseId) && !m.Tried.Contains(o.Key))
            .OrderBy(o => Vector3.DistanceSquared(o.Position, s.Player)).FirstOrDefault();
    }

    private static StepObject Nearest(IEnumerable<StepObject> objects, Vector3 me)
        => objects.OrderBy(o => Vector3.DistanceSquared(o.Position, me)).First();

    /// <summary>
    /// The line's text names the item, ignoring case: its whole name ("Use the large sack ..."), or the name's last
    /// word ("Place the explosive" for "Precision Explosive"; "Use the fertilizer" for "Qitari Fertilizer"), with or
    /// without a plural s.
    /// </summary>
    public static bool Names(string text, string name)
    {
        if (name.Length == 0 || text.Length == 0) return false;
        if (text.Contains(name, StringComparison.CurrentCultureIgnoreCase)) return true;
        // A short last word is too common to go by ("Large Sack" against "the clucking sack").
        var last = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        if (last.Length < 5) return false;
        var words = text.Split(new[] { ' ', '.', ',', '!', '?', '’', '\'' }, StringSplitOptions.RemoveEmptyEntries);
        var stem = last.EndsWith('s') ? last[..^1] : last;
        return words.Any(w => w.Equals(last, StringComparison.CurrentCultureIgnoreCase) || w.Equals(stem, StringComparison.CurrentCultureIgnoreCase)
                              || w.Equals(stem + "s", StringComparison.CurrentCultureIgnoreCase));
    }

    // ------------------------------------------------------------------ menus and questions

    /// <summary>
    /// Which entry of a menu to pick, or -1 to hand it to the player:
    /// the quest's own title (an NPC with several quests), the cutscene's "skip" question (its first entry), or a
    /// question of the quest's own (its first answer; the next one when the same question comes back with nothing
    /// changed, to get out of a menu that loops; after every answer was tried, the player).
    /// </summary>
    public static int ChooseEntry(string prompt, IReadOnlyList<string> entries, QuestScript q, StepMemory m, IReadOnlyCollection<string> skipPrompts)
    {
        if (entries.Count == 0) return -1;
        var title = Normal(q.Name);
        for (var i = 0; i < entries.Count; i++)
            if (title.Length > 0 && Normal(entries[i]).Equals(title, StringComparison.CurrentCultureIgnoreCase)) return i;
        var p = Normal(prompt);
        if (p.Length > 0 && skipPrompts.Any(sp => Normal(sp).Equals(p, StringComparison.CurrentCultureIgnoreCase))) return 0;

        var answers = q.MenuAnswers.Select(Normal).Where(a => a.Length > 0).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var prompts = q.MenuPrompts.Select(Normal).Where(a => a.Length > 0).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var own = (p.Length > 0 && prompts.Contains(p)) || entries.All(e => answers.Contains(Normal(e)));
        if (!own) return -1;
        var key = p + "|" + string.Join("|", entries.Select(Normal));
        if (!m.Answers.TryGetValue(key, out var before)) return 0;
        // The same question again: when nothing changed since it was answered, the next answer.
        if (before.Signature != m.Signature) return before.Index;
        return before.Times >= entries.Count ? -1 : (before.Index + 1) % entries.Count;
    }

    /// <summary>Notes the answer given to a menu (for <see cref="ChooseEntry"/>).</summary>
    public static void Answered(string prompt, IReadOnlyList<string> entries, int index, StepMemory m)
    {
        var key = Normal(prompt) + "|" + string.Join("|", entries.Select(Normal));
        var times = m.Answers.TryGetValue(key, out var before) ? before.Times : 0;
        m.Answers[key] = (times + 1, index, m.Signature);
    }

    /// <summary>
    /// A Yes/No question is one of the known ones: equal to a known text, or to a known text with an empty
    /// “” in it where the game fills in a name ("Commence “…”?").
    /// </summary>
    /// <summary>
    /// The game's warning before a reward it can't give (Addon 4609: "Warning: You will not be able to receive all the
    /// following:", the rewards one per line, "Do you still wish to proceed?"), when the only one lost is the last of the
    /// template's list, Reputation: a daily handed in with the society's bar full. Seals or tomestones lost are the
    /// player's call (see <see cref="RewardLossAccepted"/> for the setting that takes tomestones too).
    /// </summary>
    public static bool OnlyReputationLost(string prompt, string template) => RewardLossAccepted(prompt, template, false);

    /// <summary>
    /// The reward warning (Addon 4609) is answered Yes: what it lists as lost is Reputation only, or, with
    /// <paramref name="tomestonesToo"/> (the Tribes setting "Hand in even when tomestones would be lost"), Allagan
    /// Tomestones and Reputation only. The template's lines name the three rewards in the client's language, in its
    /// order: Company Seals, Allagan Tomestones, Reputation. Company Seals, or anything the template doesn't name, are
    /// always the player's call.
    /// </summary>
    public static bool RewardLossAccepted(string prompt, string template, bool tomestonesToo)
    {
        static List<string> Lines(string s) => s.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var t = Lines(template);
        var p = Lines(prompt);
        if (t.Count < 3 || p.Count < 3) return false;
        var reputation = t[^2];
        // The tomestones' line comes right before Reputation's in the template (seals, tomestones, reputation).
        var tomestones = tomestonesToo && t.Count >= 5 ? t[^3] : null;
        bool Same(string a, string b) => a.Equals(b, StringComparison.CurrentCultureIgnoreCase);
        if (!Same(p[0], t[0]) || !Same(p[^1], t[^1])) return false;
        return p.Skip(1).Take(p.Count - 2).All(l => Same(l, reputation) || (tomestones != null && Same(l, tomestones)));
    }

    /// <summary>
    /// A Yes/No question that is one of the quest's own (a Q row of its text sheet, "Ride the elbst to Moonshade
    /// Isle?"), asked by the NPC the run is talking to for the step at hand: one a to-do line of the current sequence
    /// names. The quest's transport rides are asked so; the run took that talk for this step, so it says Yes.
    /// </summary>
    public static bool OwnQuestion(QuestScript q, byte sequence, StepPlan? talking, string question)
    {
        if (talking is not { Kind: StepKind.Talk, Target: { Kind: StepObjectKind.Npc } npc }) return false;
        if (!q.TodosAt(sequence).SelectMany(t => t.Spots).Any(p => p.Object == npc.BaseId)) return false;
        var asked = Normal(question);
        return asked.Length > 0 && q.MenuPrompts.Any(k => Normal(k).Equals(asked, StringComparison.CurrentCultureIgnoreCase));
    }

    public static bool KnownQuestion(string prompt, IEnumerable<string> known)
    {
        var p = Normal(prompt);
        if (p.Length == 0) return false;
        foreach (var raw in known)
        {
            var k = Normal(raw);
            if (k.Length == 0) continue;
            if (p.Equals(k, StringComparison.CurrentCultureIgnoreCase)) return true;
            var hole = k.IndexOf("“”", StringComparison.Ordinal);
            if (hole < 0) continue;
            var before = k[..(hole + 1)];
            var after = k[(hole + 1)..];
            if (p.Length > k.Length && p.StartsWith(before, StringComparison.CurrentCultureIgnoreCase) && p.EndsWith(after, StringComparison.CurrentCultureIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// A question is one of the game's texts that have something filled in ("Return to " + a place + "?"): it starts
    /// with what comes before the filled-in part and ends with what comes after it, with something in between. A text
    /// with nothing to fill in (<c>Hole</c> false) must be the question whole. Case and spacing aside.
    /// </summary>
    public static bool FitsShape(string prompt, IEnumerable<(string Before, string After, bool Hole)> shapes)
    {
        const int MinShape = 4;
        var p = Normal(prompt);
        if (p.Length == 0) return false;
        foreach (var (rawBefore, rawAfter, hole) in shapes)
        {
            var before = Normal(rawBefore);
            var after = Normal(rawAfter);
            if (!hole)
            {
                if (before.Length > 0 && p.Equals(before, StringComparison.CurrentCultureIgnoreCase)) return true;
                continue;
            }
            // There must be enough fixed text to go by: both halves, or one of a few characters at least (a language
            // that puts the filled-in part first or last). A text that's all but filled in says nothing.
            if ((before.Length == 0 || after.Length == 0) && before.Length + after.Length < MinShape) continue;
            if (p.Length > before.Length + after.Length && p.StartsWith(before, StringComparison.CurrentCultureIgnoreCase) && p.EndsWith(after, StringComparison.CurrentCultureIgnoreCase)
                && p[before.Length..^after.Length].Trim().Length > 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// What a question fills into the empty “” of a known text ("Marks of Friendship" from "Commence “Marks of
    /// Friendship”?" against "Commence “”?"); null when the question isn't that text.
    /// </summary>
    public static string? Filled(string prompt, string template)
    {
        var p = Normal(prompt);
        var k = Normal(template);
        var hole = k.IndexOf("“”", StringComparison.Ordinal);
        if (p.Length == 0 || hole < 0) return null;
        var before = k[..(hole + 1)];
        var after = k[(hole + 1)..];
        if (p.Length <= k.Length || !p.StartsWith(before, StringComparison.CurrentCultureIgnoreCase) || !p.EndsWith(after, StringComparison.CurrentCultureIgnoreCase))
            return null;
        return p[before.Length..^after.Length].Trim();
    }

    /// <summary>
    /// The menu entry that leaves it the way a player does: the last one whose text is one of the game's own "Nothing" /
    /// "Cancel" texts (a trailing full stop aside); -1 when there's none (then the window is closed instead).
    /// </summary>
    public static int LeaveEntry(IReadOnlyList<string> entries, IEnumerable<string> leaveTexts)
    {
        static string Bare(string t) => Normal(t).TrimEnd('.', '。').Trim();
        var leave = leaveTexts.Select(Bare).Where(t => t.Length > 0).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        for (var i = entries.Count - 1; i >= 0; i--)
            if (leave.Contains(Bare(entries[i]))) return i;
        return -1;
    }

    /// <summary>Text as compared: single spaces, trimmed, without the icon glyphs a menu entry can start with.</summary>
    public static string Normal(string text)
    {
        var chars = text.Where(c => !char.IsControl(c) && !(c >= '' && c <= '')).ToArray();
        return string.Join(' ', new string(chars).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
