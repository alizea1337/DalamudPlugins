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
}

/// <summary>How a key item is used.</summary>
public enum ItemUse { Target, Ground, Self }

/// <summary>A decision: what to do, on what or where, and how close to get first.</summary>
public sealed record StepPlan(StepKind Kind, string What)
{
    public StepObject? Target { get; init; }
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
    /// <summary>Crafting: the materials item; gathering: the to-do line's item.</summary>
    public uint Other { get; init; }
    /// <summary>The Level row of the spot gone to (to note it was looked at), 0 for none.</summary>
    public uint Level { get; init; }
    /// <summary>What an attempt is counted under (public actions: once per step).</summary>
    public string Key { get; init; } = "";
    /// <summary>A /say or an emote: seen by everyone around, so sent once per step (an emote once more).</summary>
    public bool Public => Kind is StepKind.Say or StepKind.Emote;

    public override string ToString()
    {
        var on = Target != null ? $" -> {Target.Name} ({Target.BaseId}) at {V(Target.Position)}"
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
        return $"{Kind}{extra}{on}{(Kind is StepKind.Talk or StepKind.Say or StepKind.Emote or StepKind.UseItem or StepKind.MountAction or StepKind.Go ? $" within {Within:0.#}" : "")}"
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
    /// <summary>A status the crafting needs (the Ixal facility access) is renewed before crafting when it has less than this left.</summary>
    public static readonly TimeSpan StatusRenewBelow = TimeSpan.FromSeconds(60);

    /// <summary>How often a public action is sent for one step at most: a /say once (the player was told so), an emote once more.</summary>
    public static int PublicTries(StepKind kind) => kind == StepKind.Say ? 1 : 2;

    /// <summary>The next step.</summary>
    public static StepPlan Decide(QuestScript q, StepSnapshot s, StepMemory m, DateTime now, bool leaveOutSay = false)
    {
        if (!s.Accepted)
            return s.Completed ? new StepPlan(StepKind.Done, "The quest is complete.") : new StepPlan(StepKind.Failed, "The quest isn't in the journal any more.");
        // Before the wait for a busy game: a game that stays busy doesn't stop the clock.
        if (m.ProgressAt != default && now - m.ProgressAt > NoProgressLimit)
            return Ask($"Nothing has changed for {NoProgressLimit.TotalMinutes:0} minutes; do this step yourself, then resume.");
        if (s.Busy || !s.ScriptRead) return new StepPlan(StepKind.Wait, "busy");

        // The class first: as another class nobody (the facility supervisor neither) moves the quest on.
        if (s.AcceptJob != 0 && s.Job != s.AcceptJob)
            return new StepPlan(StepKind.SwitchJob, "the quest goes on only as the class it was accepted as") { Id = s.AcceptJob };

        var riding = q.Mount is { } qm && s.Mounted && s.Mount == qm.Mount;

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
                return craft;
            }
            craftShort = true;
        }

        // Gathering: the to-do line is about the item for the class the quest was accepted as.
        if (Gather(q, s, m) is { } gather && gather.Count > 0) return gather;

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

        // A key item to use: the line names it ("Use the firecrackers ..."), or it's the only one held and the line
        // points at objects (not people) to use it on ("Salt the designated locations").
        var usable = q.KeyItems.Where(k => k.Usable && s.Count(k.Id) > 0).ToList();
        foreach (var item in usable)
        {
            // Any of its names whole; its title's last word too (not the singular's or plural's: "sacks" is too common).
            var naming = lines.Where(l => item.Words.Any(w => l.Text.Contains(w, StringComparison.CurrentCultureIgnoreCase)) || Names(l.Text, item.Name)).ToList();
            var named = naming.Count > 0;
            var spotObjects = SpotObjects(named ? naming : lines);
            var onObjects = usable.Count == 1 && lines.Count > 0 && Live(q, s, m).Any(o => o.Kind == StepObjectKind.Object && spotObjects.Contains(o.BaseId));
            if ((named || onObjects) && UseItem(q, s, m, item, carrying, named, spotObjects) is { } use) return use;
        }

        if (Pick(q, s, m) is { } target)
        {
            // Getting the quest mount (or what's carried) back again and again: something takes it away; the player's.
            if (!riding && q.Mount != null && s.Sequence != 255 && m.MountGiver != 0 && target.BaseId == m.MountGiver && m.Mounts > MaxRegains)
                return Ask($"The quest mount was lost {m.Mounts} times; do this step yourself, then resume.");
            if (!carrying && m.CarryGiver != 0 && target.BaseId == m.CarryGiver && m.Pickups > MaxRegains)
                return Ask($"What you carry was dropped {m.Pickups} times; do this step yourself, then resume.");
            return Talk(target, carrying ? "deliver what you carry" : "the step's marked target") with { OnFoot = carrying };
        }

        if (Where(q, s, m) is { } go) return go with { OnFoot = carrying };

        if (craftShort)
            return Ask("Out of the materials for the quest's item, and nobody marked hands out more; get them from the quest's NPC, then resume.");
        return Ask("Can't tell what this step wants (nothing marked nearby, no map marker); do it yourself, then resume.");
    }

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
            var self = mount.Range <= 0;
            var within = mount.Line ? Math.Clamp(mount.EffectRange * 0.6f, 4f, 12f)
                : self ? Math.Max(2f, mount.EffectRange - 2f)
                : Math.Max(2f, mount.Range - 2f);
            return new StepPlan(StepKind.MountAction, "the quest mount's action on the next target")
            {
                Target = t, Territory = s.Territory, Within = within, Id = mount.Action, Face = mount.Line, NoFly = noFly, Key = $"action:{t.Key}",
            };
        }
        if (s.Sequence == 255)
            return new StepPlan(StepKind.Dismount, "the quest mount has nothing more to do (the quest can't be reported while riding it)");
        // Nothing to use the action on in view: the marked area (or the spot of the line).
        if (Where(q, s, m) is { } go)
            return go with { NoFly = noFly, What = mount.Action == 0 ? "ride into the marked area" : go.What };
        return Ask("On the quest mount with nothing left in view to use it on and nowhere marked; do this step yourself, then resume.");
    }

    /// <summary>The craft step, when the current to-do line is about the quest's product (or, for older quests, the materials wait).</summary>
    public static StepPlan? Craft(QuestScript q, StepSnapshot s)
    {
        // Accepted as a gatherer (the Namazu quests take either), the quest gathers instead.
        if (q.Product == 0 || s.AcceptJob is >= 16 and <= 18) return null;
        var have = s.Count(q.Product);
        var line = s.Todos.FirstOrDefault(t => t.ItemId == q.Product && t.Needed > 0);
        int needed;
        if (line != null) needed = (int)line.Needed;
        else if (s.Count(q.Materials) > 0 && q.TodosAt(s.Sequence).Any()) needed = have + s.Count(q.Materials);
        else return null;
        return new StepPlan(StepKind.Craft, "craft the quest's item") { Id = q.Product, Other = q.Materials, Count = Math.Max(0, needed - have) };
    }

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
    private static StepPlan? UseItem(QuestScript q, StepSnapshot s, StepMemory m, KeyItemFact item, bool carrying, bool named, IReadOnlySet<uint> spotObjects)
    {
        var use = item.Ground ? ItemUse.Ground : item.Self ? ItemUse.Self : ItemUse.Target;
        var strict = use == ItemUse.Ground || !named;
        Func<StepObject, bool>? only = strict ? o => spotObjects.Contains(o.BaseId) && (named || o.Kind == StepObjectKind.Object) : null;
        var target = Pick(q, s, m, only);
        var within = use == ItemUse.Self ? 2f : Math.Max(1.5f, item.Range - 1f);
        if (target != null)
            return new StepPlan(StepKind.UseItem, $"use the {item.Name}") { Target = target, Territory = s.Territory, Within = within, Id = item.Id, Use = use, OnFoot = carrying, Key = $"item:{item.Id}:{target.Key}" };
        if (Where(q, s, m) is { } go)
        {
            // An area to use it in (no object to aim at): self-use there; otherwise go and look first.
            if (use == ItemUse.Self && go.Spot != null && go.Target == null)
                return new StepPlan(StepKind.UseItem, $"use the {item.Name} in the marked area") { Spot = go.Spot, Territory = go.Territory, Within = go.Within, Id = item.Id, Use = use, Level = go.Level, Key = $"item:{item.Id}:{go.Level}" };
            return go;
        }
        return null;
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
    public static StepPlan? Where(QuestScript q, StepSnapshot s, StepMemory m)
    {
        var markers = s.Markers
            .Where(k => !m.Looked.Contains(MarkerKey(k)))
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
            .Where(p => !m.Looked.Contains(p.Level) && p.Object != q.Giver)
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
