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
    /// <summary>A /say or an emote: seen by everyone around, so tried at most twice.</summary>
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
/// came; objects tried without effect and spots looked at since then; the public actions tried (kept for the whole
/// quest); the quest mount's giver.
/// </summary>
public sealed class StepMemory
{
    public string Signature { get; private set; } = "";
    public DateTime ProgressAt { get; private set; }
    /// <summary>Objects talked to (or acted on) since the last progress without anything changing.</summary>
    public HashSet<ulong> Tried { get; } = new();
    /// <summary>To-do spots and markers gone to and looked at since the last progress.</summary>
    public HashSet<uint> Looked { get; } = new();
    /// <summary>Attempts at the current step's interactions, cleared by progress.</summary>
    public Dictionary<string, Attempt> Attempts { get; } = new();
    /// <summary>Attempts at /say and emotes, kept for the whole quest (they're public: once, and one retry).</summary>
    public Dictionary<string, Attempt> PublicAttempts { get; } = new();
    /// <summary>Answers given in menus: prompt and entries → (times answered, last index).</summary>
    public Dictionary<string, (int Times, int Index, string Signature)> Answers { get; } = new();
    /// <summary>The NPC that put the character on the quest mount (not a target of the mount's action).</summary>
    public uint MountGiver { get; set; }

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

    public void Note(string key, DateTime now, bool isPublic = false)
    {
        var book = isPublic ? PublicAttempts : Attempts;
        if (!book.TryGetValue(key, out var a)) book[key] = a = new Attempt();
        a.Count++;
        a.Last = now;
    }

    public int Count(string key) => Attempts.TryGetValue(key, out var a) ? a.Count : 0;
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
    /// <summary>A public action (/say, an emote) may be tried once more after this long with nothing changing.</summary>
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

    /// <summary>The next step.</summary>
    public static StepPlan Decide(QuestScript q, StepSnapshot s, StepMemory m, DateTime now, bool leaveOutSay = false)
    {
        if (!s.Accepted)
            return s.Completed ? new StepPlan(StepKind.Done, "The quest is complete.") : new StepPlan(StepKind.Failed, "The quest isn't in the journal any more.");
        if (s.Busy) return new StepPlan(StepKind.Wait, "busy");
        if (m.ProgressAt != default && now - m.ProgressAt > NoProgressLimit)
            return Ask($"Nothing has changed for {NoProgressLimit.TotalMinutes:0} minutes; do this step yourself, then resume.");

        var riding = q.Mount is { } qm && s.Mounted && s.Mount == qm.Mount;

        // Crafting: the to-do line is about the product, or (older quests) the materials are in the bags and products
        // are still short. Short of both: talk to whoever the step marks (a crate, the materials NPC hands more out).
        var craftShort = false;
        if (Craft(q, s) is { } craft && craft.Count > 0)
        {
            if (s.Count(q.Materials) > 0 || q.Materials == 0)
            {
                if (q.CraftStatus != 0 && !s.Statuses.Contains(q.CraftStatus))
                {
                    if (StatusGiver(q, s, m) is { } giver) return Talk(giver, "get the facility access back before crafting");
                    return Ask("The crafting needs the facility access status; talk to the supervisor for it, then resume.");
                }
                return craft;
            }
            craftShort = true;
        }

        // Gathering: the to-do line is about the item for the class the quest was accepted as.
        if (Gather(q, s, m) is { } gather && gather.Count > 0) return gather;

        if (s.AcceptJob != 0 && s.Job != s.AcceptJob)
            return new StepPlan(StepKind.SwitchJob, "the quest goes on only as the class it was accepted as") { Id = s.AcceptJob };

        if (riding) return Riding(q, s, m);

        var carrying = q.CarryStatus != 0 && s.Statuses.Contains(q.CarryStatus);
        var open = s.Todos.Where(t => !t.Checked).Select(t => t.Index).ToHashSet();
        var lines = q.TodosAt(s.Sequence).Where(t => open.Count == 0 || open.Contains(t.Index)).ToList();

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

        // A key item to use: the line names it ("Use the firecrackers ..."), or it's the only one held and the step
        // marks objects (not people) to use it on ("Salt the designated locations").
        var usable = q.KeyItems.Where(k => k.Usable && s.Count(k.Id) > 0).ToList();
        foreach (var item in usable)
        {
            // Any of its names whole; its title's last word too (not the singular's or plural's: "sacks" is too common).
            var named = lines.Any(l => item.Words.Any(w => l.Text.Contains(w, StringComparison.CurrentCultureIgnoreCase)) || Names(l.Text, item.Name));
            var onObjects = usable.Count == 1 && lines.Count > 0 && Live(q, s, m).Any(o => o.Kind == StepObjectKind.Object);
            if ((named || onObjects) && UseItem(q, s, m, item, carrying, named ? null : o => o.Kind == StepObjectKind.Object) is { } use) return use;
        }

        if (Pick(q, s, m) is { } target) return Talk(target, carrying ? "deliver what you carry" : "the step's marked target") with { OnFoot = carrying };

        if (Where(q, s, m) is { } go) return go with { OnFoot = carrying };

        if (craftShort)
            return Ask("Out of the materials for the quest's item, and nobody marked hands out more; get them from the quest's NPC, then resume.");
        return Ask("Can't tell what this step wants (nothing marked nearby, no map marker); do it yourself, then resume.");
    }

    /// <summary>On the quest mount: its action on the next target, riding into the area, or getting off.</summary>
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
                Target = t, Territory = s.Territory, Within = within, Id = mount.Action, Face = mount.Line, NoFly = noFly, Key = $"action:{t.Id}",
            };
        }
        // Nothing to use the action on in view: the marked area (or the spot of the line), unless the quest is at its
        // report already.
        if (s.Sequence != 255 && Where(q, s, m) is { } go)
            return go with { NoFly = noFly, What = mount.Action == 0 ? "ride into the marked area" : go.What };
        return new StepPlan(StepKind.Dismount, "the quest mount has nothing more to do (the quest can't be reported while riding it)");
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
    /// A /say or an emote at the line's target: once, and once more only after the quest stayed unchanged for
    /// <see cref="PublicRetryAfter"/>; then the player does it.
    /// </summary>
    private static StepPlan Public(QuestScript q, StepSnapshot s, StepMemory m, DateTime now, ScriptTodo line, StepKind kind, string text, uint id, string what)
    {
        var target = Pick(q, s, m, kind == StepKind.Emote ? o => o.Kind == StepObjectKind.Npc : null) ?? Pick(q, s, m);
        StepPlan plan;
        if (target != null)
            plan = new StepPlan(kind, what) { Target = target, Territory = s.Territory, Within = PublicWithin, Text = text, Id = id, Key = $"{kind}:{line.Index}:{target.BaseId}" };
        else if (Where(q, s, m) is { } go && go.Target == null && go.Spot != null)
        {
            // Nothing in view yet: go there first (the target comes into view on the way).
            return go;
        }
        else return Ask($"Can't find where to {what}; do it yourself, then resume.");

        var verdict = PublicVerdict(m.PublicAttempts.GetValueOrDefault(plan.Key), m.ProgressAt, now);
        return verdict switch
        {
            PublicTry.Yes => plan,
            PublicTry.NotYet => new StepPlan(StepKind.Wait, $"waiting to see whether the {(kind == StepKind.Say ? "phrase" : "emote")} counted"),
            _ => Ask(kind == StepKind.Say
                ? $"Saying “{text}” didn't move the quest on; say it yourself near {target!.Name}, then resume."
                : $"{text} didn't move the quest on; do it yourself at {target!.Name}, then resume."),
        };
    }

    public enum PublicTry { Yes, NotYet, GiveUp }

    /// <summary>
    /// Whether a public action may be sent: never tried, yes; tried once, again only once both the try and the last
    /// change of the quest are <see cref="PublicRetryAfter"/> old; tried twice, never.
    /// </summary>
    public static PublicTry PublicVerdict(Attempt? tried, DateTime progressAt, DateTime now)
    {
        if (tried == null || tried.Count == 0) return PublicTry.Yes;
        if (tried.Count >= 2) return PublicTry.GiveUp;
        return now - tried.Last >= PublicRetryAfter && now - progressAt >= PublicRetryAfter ? PublicTry.Yes : PublicTry.NotYet;
    }

    /// <summary>Using a key item the line names: on the target, on the ground at it, or on yourself next to it.</summary>
    private static StepPlan? UseItem(QuestScript q, StepSnapshot s, StepMemory m, KeyItemFact item, bool carrying, Func<StepObject, bool>? only)
    {
        var target = Pick(q, s, m, only);
        var use = item.Ground ? ItemUse.Ground : item.Self ? ItemUse.Self : ItemUse.Target;
        var within = use == ItemUse.Self ? 2f : Math.Max(1.5f, item.Range - 1f);
        if (target != null)
            return new StepPlan(StepKind.UseItem, $"use the {item.Name}") { Target = target, Territory = s.Territory, Within = within, Id = item.Id, Use = use, OnFoot = carrying, Key = $"item:{item.Id}:{target.Id}" };
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
        => new(StepKind.Talk, what) { Target = target, Within = TalkWithin, Key = $"talk:{target.Id}" };

    private static StepPlan Ask(string text) => new(StepKind.AskUser, text);

    /// <summary>The quest's objects the step wants something done with, not yet tried without effect.</summary>
    public static IEnumerable<StepObject> Live(QuestScript q, StepSnapshot s, StepMemory m)
        => s.Objects.Where(o => o.Targetable && (o.Active || o.Icon != 0) && !m.Tried.Contains(o.Id)
                                // An NPC is a target when marked (an icon over its head); an object when the script counts it active.
                                && (o.Kind != StepObjectKind.Npc || o.Icon != 0));

    /// <summary>
    /// The target to act on: one the open lines' spots name first, then marked ones, then (before the last sequence)
    /// anyone but the giver, then the nearest.
    /// </summary>
    public static StepObject? Pick(QuestScript q, StepSnapshot s, StepMemory m, Func<StepObject, bool>? only = null)
    {
        var live = Live(q, s, m).Where(o => only?.Invoke(o) ?? true).ToList();
        if (live.Count == 0) return null;
        var open = s.Todos.Where(t => !t.Checked).Select(t => t.Index).ToHashSet();
        var named = q.TodosAt(s.Sequence).Where(t => open.Count == 0 || open.Contains(t.Index))
            .SelectMany(t => t.Spots).Select(p => p.Object).Where(o => o != 0).ToHashSet();
        return live.OrderBy(o => named.Contains(o.BaseId) ? 0 : 1)
            .ThenBy(o => o.Icon != 0 ? 0 : 1)
            .ThenBy(o => s.Sequence != 255 && (o.BaseId == q.Giver || o.BaseId == q.TargetEnd) ? 1 : 0)
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
        var open = s.Todos.Where(t => !t.Checked).Select(t => t.Index).ToHashSet();
        var spot = q.TodosAt(s.Sequence)
            .Where(t => open.Count == 0 || open.Contains(t.Index))
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

    /// <summary>For crafting: the marked NPC on the craft line that isn't the giver (the Ixal supervisor who grants the facility access).</summary>
    private static StepObject? StatusGiver(QuestScript q, StepSnapshot s, StepMemory m)
    {
        var npcs = q.TodosAt(s.Sequence).SelectMany(t => t.Spots).Where(p => p.Object != 0 && p.Object != q.Giver).Select(p => p.Object).ToHashSet();
        return s.Objects.Where(o => o.Targetable && npcs.Contains(o.BaseId) && !m.Tried.Contains(o.Id)).OrderBy(o => Vector3.DistanceSquared(o.Position, s.Player)).FirstOrDefault();
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

    /// <summary>Text as compared: single spaces, trimmed, without the icon glyphs a menu entry can start with.</summary>
    public static string Normal(string text)
    {
        var chars = text.Where(c => !char.IsControl(c) && !(c >= '' && c <= '')).ToArray();
        return string.Join(' ', new string(chars).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
