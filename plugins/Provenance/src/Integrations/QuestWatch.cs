using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Text;
using FFXIVClientStructs.FFXIV.Client.Game;
using Provenance.Core.Tribes;

namespace Provenance.Integrations;

/// <summary>
/// The step engine's watch: while the player does an allied-society quest by hand and no Provenance loop is working,
/// it writes to the debug log (area "steps", in a session of its own, "watch") what the engine sees and would do, so a
/// bug report shows whether the engine reads the quest right before it ever drives one. It never acts. A line is written
/// only when something changed (the sequence, the to-do counts, the quest's objects and icons, the map markers, the
/// decision), from a look twice a second.
/// </summary>
/// <remarks>
/// It runs only when the Tribes page has been opened (or its run started) and "note what the run would do" is on
/// (<see cref="TribeSettings.Seen"/>, <see cref="TribeSettings.WatchByHand"/>), and it looks only outside duties, not
/// between areas and not just after a zone change; the quest's script is asked nothing while the game is busy
/// (<see cref="QuestSteps.Snapshot"/>).
///
/// What its lines let the in-game checks of queststeps_engine.md §9 be answered from a bug report:
/// V1 which of a random-target quest's objects show the quest's icon and count as active ("objects");
/// V2 how the map keys a quest's markers and whether markers in other zones are listed ("marker slots", "markers");
/// V3 whether a quest mount flies ("riding");
/// V4 whether mounting or flying drops the carried box (status 404, "carrying");
/// V5/V6 whether a /say counted, from how far, and whether its wording matched the phrase exactly or only ignoring
/// case ("said", then "progress after saying") — the line itself isn't logged;
/// V7 the reward window's optional rewards ("reward window");
/// V8 what GetTodoArgs returns for each line ("todo");
/// V9 that IsActive and the icon are read on every look ("objects", and the session ending there if not);
/// V10 the menus a quest shows and whether one comes back ("menu").
///
/// For a quest with enemies (the battle societies' dailies, the Ixal's fights) it also writes what the enemy filter
/// (<see cref="EnemyRules"/>) goes by, so the filter can be checked from a bug report before the run fights by it:
/// "enemies" is the quest's data (kinds, names, the sequence each is active in, what's done with it, its spawn points);
/// "mobs" lists the hostile mobs within 60 yalms (ten kinds at most, nearest first) whenever one comes, goes or
/// changes: kind and name ids, the icon drawn, its FATE, whether it's subdued (waiting for the quest's item), whom it
/// targets and who hit it first, and then for each quest its own icon for the mob, whether its handler holds it, and
/// the filter's verdict with the reason. In combat the quest's script isn't asked, so the icon and "held" are then
/// those of the last calm look (the line says so). "would Fight ..." is what the run would do about them.
///
/// To keep the log short where it tells nothing, the mobs are listed, and a decision in combat is written, only for
/// quests that have enemies and only while the character is in one of the quest's zones (<see cref="Fights"/>): not
/// for a crafter's daily, and not while farming elsewhere with a battle daily in the journal. The list is made once a
/// look for all such quests together (one line, with each quest's verdict), at most every <see cref="MobsEvery"/>;
/// in combat a quest's "would" line comes at most every <see cref="WouldEvery"/>.
/// </remarks>
internal static class QuestWatch
{
    /// <summary>How often the watch looks.</summary>
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(0.5);
    /// <summary>The mobs around a quest with enemies: within this many yalms, this many kinds at most, a line this often at most.</summary>
    private const float MobsWithin = 60f;
    private const int MobsMost = 10;
    private static readonly TimeSpan MobsEvery = TimeSpan.FromSeconds(1.5);
    /// <summary>In combat, a quest's "would" line this often at most (the nearest attacker changes all the time).</summary>
    private static readonly TimeSpan WouldEvery = TimeSpan.FromSeconds(3);

    private sealed class Watched
    {
        public QuestScript Script = null!;
        public readonly StepMemory Memory = new();
        /// <summary>The last look that read the quest's script (a calm one).</summary>
        public StepSnapshot? Last;
        /// <summary>The last look of any kind, in combat too (its mobs are read whatever the game does), and when: for a bug report.</summary>
        public StepSnapshot? Seen;
        public DateTime SeenAt;
        /// <summary>The zones the quest's data sends you to (<see cref="ZonesOf"/>).</summary>
        public HashSet<uint> Zones = new();
        public string Header = "", Todos = "", Objects = "", Markers = "", Plan = "";
        public DateTime NextWould;
    }

    private static readonly Dictionary<uint, Watched> watched = new();
    /// <summary>This look's quests whose enemies matter here (<see cref="Fights"/>), with what each saw: the mobs are listed once for all of them.</summary>
    private static readonly List<(Watched Quest, StepSnapshot Seen, uint Id)> fighting = new();
    private static string mobsKey = "";
    private static DateTime nextMobs;
    private static readonly Dictionary<ushort, bool> society = new();
    private static DateTime next;
    private static bool on;
    /// <summary>The debug log's session is the watch's own (no loop started one since it began).</summary>
    private static bool session;
    private static bool chatAttached;
    private static string riding = "", carrying = "", menu = "", question = "", slots = "";
    private static bool rewardOpen;
    /// <summary>The player's /say or emote, seen in the chat: what it held, per watched quest; written at the next look.</summary>
    private static (DateTime At, string What, List<(uint Quest, string Words)> Parts, Dictionary<uint, string> Before)? said;
    /// <summary>A /say or an emote written out: when, and the quests' signatures then (to tell whether it counted).</summary>
    private static (DateTime At, string What, Dictionary<uint, string> Before)? pending;

    /// <summary>Every frame: looks (twice a second) while a society quest is in the journal and no Provenance loop works.</summary>
    public static void Observe(Plugin plugin)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (now < next) return;
            next = now + Every;
            var settings = plugin.Config.Tribes;
            if (!settings.WatchByHand || !settings.Seen)
            {
                if (on) Off("turned off in the Tribes options");
                return;
            }
            if (!Services.ClientState.IsLoggedIn)
            {
                if (on) Off("logged out");
                return;
            }
            if (plugin.Runs.Active || plugin.CosmicLoop.Active || plugin.DeliveryLoop.Active || plugin.TribeLoop.Active)
            {
                // The loop writes its own session: nothing of the watch goes into it.
                if (on) Off(null);
                session = false;
                return;
            }
            // Between areas, in a duty or just after a zone change: no look now, and the watch stays as it is.
            if (Services.Objects.LocalPlayer == null || QuestSteps.Loading || InDuty || !QuestSteps.Settled(now)) return;
            var quests = SocietyQuests();
            if (quests.Count == 0)
            {
                if (on) Off("no allied society quest in the journal");
                return;
            }
            if (!on)
            {
                on = true;
                Attach();
                if (!session) DebugLog.BeginSession("watch");
                session = true;
                Write($"watch on: allied society quests in the journal [{string.Join(",", quests.Select(q => q - 65536))}]");
            }
            foreach (var gone in watched.Keys.Where(k => !quests.Contains(k)).ToList())
            {
                Write($"{gone - 65536}: no longer in the journal (completed today: {Completed(gone)})");
                watched.Remove(gone);
            }
            fighting.Clear();
            foreach (var q in quests) Look(q, now, plugin.Config.Combat.ClassJob);
            Mobs(now);
            Said();
            Surroundings();
            Settle(now);
        }
        catch (Exception e)
        {
            Services.Log.Warning(e, "Quest watch");
            next = DateTime.UtcNow.AddSeconds(10);
        }
    }

    /// <summary>Stops listening to the chat (on unload).</summary>
    public static void Dispose()
    {
        if (!chatAttached) return;
        chatAttached = false;
        Services.Chat.ChatMessage -= OnChat;
    }

    private static bool InDuty
        => Services.Condition[ConditionFlag.BoundByDuty] || Services.Condition[ConditionFlag.BoundByDuty56] || Services.Condition[ConditionFlag.BoundByDuty95];

    /// <param name="why">Written to the log; null writes nothing (a loop's session has begun).</param>
    private static void Off(string? why)
    {
        on = false;
        watched.Clear();
        fighting.Clear();
        mobsKey = "";
        riding = carrying = menu = question = slots = "";
        rewardOpen = false;
        said = null;
        pending = null;
        if (why != null) Write($"watch off: {why}");
    }

    /// <summary>The allied-society quests in the journal (Quest sheet rows).</summary>
    private static unsafe List<uint> SocietyQuests()
    {
        var list = new List<uint>();
        var qm = QuestManager.Instance();
        if (qm == null) return list;
        foreach (ref var q in qm->NormalQuests)
        {
            if (q.QuestId == 0) continue;
            if (!society.TryGetValue(q.QuestId, out var yes))
            {
                yes = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Quest>().GetRowOrDefault(q.QuestId + 65536u)?.BeastTribe.RowId is > 0;
                society[q.QuestId] = yes;
            }
            if (yes) list.Add(q.QuestId + 65536u);
        }
        return list;
    }

    private static unsafe bool Completed(uint row)
    {
        var qm = QuestManager.Instance();
        return qm != null && qm->IsDailyQuestCompleted((ushort)(row - 65536));
    }

    /// <param name="combatJob">The job under Settings, Monster drops: what a fight of a quest accepted as a crafter would be done on.</param>
    private static void Look(uint row, DateTime now, uint combatJob)
    {
        if (!watched.TryGetValue(row, out var w))
        {
            if (QuestSteps.Script(row) is not { } script) return;
            w = watched[row] = new Watched { Script = script, Zones = ZonesOf(script) };
            Write($"{row - 65536}: \"{script.Name}\" giver {script.Giver}; product {script.Product}/{script.Materials}, gather [{string.Join(",", script.GatherItems.Select(kv => $"{kv.Key}:{kv.Value}"))}], "
                  + $"key items [{string.Join(",", script.KeyItems.Select(k => $"{k.Id}{(k.Usable ? $"(act {k.Action} {(k.Ground ? "ground" : k.Self ? "self" : "target")} r{k.Range})" : "")}"))}], "
                  + $"mount {(script.Mount is { } m ? $"{m.Mount}/{m.Action}{(m.Line ? " line" : "")}{(m.CanFly ? " flies" : "")}" : "none")}, say [{string.Join(",", script.Say.Select(kv => $"{kv.Key}:\"{kv.Value}\""))}], "
                  + $"emotes [{string.Join(",", script.Emotes.Select(kv => $"{kv.Key}:{kv.Value.Command}"))}], carry {script.CarryStatus}; reading its objects through its event handler (IsActive, icon) twice a second");
            if (script.Enemies.Count > 0)
                Write($"{row - 65536}: enemies [{QuestSteps.EnemyData(script)}], key item on objects [{string.Join(",", script.ItemObjects.Select(o => $"{o.Object}@{o.Sequence}"))}]; "
                      + $"in its zones [{string.Join(",", w.Zones.OrderBy(z => z))}] the hostile mobs within {MobsWithin:0} yalms are listed as they change, with what the enemy filter makes of each");
        }
        if (QuestSteps.Snapshot(w.Script) is not { } s) return;
        var id = row - 65536;
        w.Seen = s;
        w.SeenAt = now;
        // The mobs around are read whatever the game is doing (their own fields; the quest's script isn't asked for
        // them). They're listed after every quest has been looked at, once for all the quests they matter to.
        var fights = Fights(w, s);
        if (fights) fighting.Add((w, s, id));
        // The script wasn't asked (the game is busy, or in combat): nothing more to compare. In combat, what the run
        // would do about what attacks is still worth a line, where the quest's enemies are.
        if (!s.ScriptRead || s.Remembered)
        {
            if (fights && s.InCombat && !s.Busy) Would(w, s, id, now, combatJob, fights);
            return;
        }
        w.Last = s;
        w.Memory.Observe(s.Signature(), now);

        var header = $"seq {s.Sequence} vars [{Convert.ToHexString(s.Variables)}] accepted as {s.AcceptJob}, now {s.Job}";
        if (header != w.Header) { w.Header = header; Write($"{id}: {header} in zone {s.Territory}"); }

        var todos = string.Join("; ", s.Todos.Select(t => $"#{t.Index} {(t.Checked ? "done" : "open")} args {t.Have}/{t.Needed} item {t.Item}"));
        if (todos != w.Todos)
        {
            w.Todos = todos;
            var texts = w.Script.TodosAt(s.Sequence).ToDictionary(t => t.Index, t => t.Text);
            Write($"{id}: todo {(s.Todos.Count == 0 ? "(none read)" : string.Join("; ", s.Todos.Select(t => $"#{t.Index} {(t.Checked ? "done" : "open")} args {t.Have}/{t.Needed} item {t.Item} \"{texts.GetValueOrDefault(t.Index, "")}\"")))}");
        }

        var objects = string.Join("; ", s.Objects.OrderBy(o => o.BaseId).Select(o => $"{o.Kind} {o.BaseId}/{o.Instance} icon {o.Icon} drawn {o.Drawn} {(o.Active ? "active" : "inactive")}{(o.Targetable ? "" : " untargetable")}"));
        if (objects != w.Objects)
        {
            w.Objects = objects;
            Write($"{id}: objects {(s.Objects.Count == 0 ? "(none loaded)" : string.Join("; ", s.Objects.OrderBy(o => o.BaseId).Select(o =>
                $"{o.Kind} {o.BaseId}/{o.Instance} \"{o.Name}\" at {V(o.Position)} ({Vector3.Distance(o.Position, s.Player):0} y) icon {o.Icon} drawn {o.Drawn} {(o.Active ? "active" : "inactive")}{(o.Targetable ? "" : " untargetable")}")))}");
        }

        var markers = string.Join("; ", s.Markers.Select(k => $"zone {k.Territory} {V(k.Position)} r {k.Radius:0.#} icon {k.Icon} level {k.Level} data {k.DataId}"));
        if (markers != w.Markers) { w.Markers = markers; Write($"{id}: markers {(markers.Length == 0 ? "(none)" : markers)}"); }

        Would(w, s, id, now, combatJob, fights);
    }

    /// <summary>
    /// The zones a quest's data sends you to: where its to-do lines point, where its enemies appear, the zones its
    /// script names, and its giver's. Empty when the data names none (then it counts as everywhere).
    /// </summary>
    private static HashSet<uint> ZonesOf(QuestScript q)
    {
        var zones = new HashSet<uint>();
        foreach (var t in q.Todos)
        foreach (var spot in t.Spots)
            zones.Add(spot.Territory);
        foreach (var e in q.Enemies)
            if (e.Spawn is { } spawn) zones.Add(spawn.Territory);
        foreach (var z in q.Territories) zones.Add(z);
        if (q.GiverSpot is { } giver) zones.Add(giver.Territory);
        zones.Remove(0);
        return zones;
    }

    /// <summary>
    /// The quest's enemies matter where the character is: it has enemies, and the character is in one of its zones (or
    /// the map has one of its markers here). Only then are the mobs around listed for it and a decision in combat
    /// written: not for a crafter's daily, and not while fighting elsewhere with a battle daily in the journal.
    /// </summary>
    private static bool Fights(Watched w, StepSnapshot s)
        => w.Script.Enemies.Count > 0 && (w.Zones.Count == 0 || w.Zones.Contains(s.Territory) || s.Markers.Any(m => m.Territory == s.Territory));

    /// <summary>
    /// What the stepper would do now (never done here). Positions left out of the comparison: a wandering target
    /// doesn't write a line per step it takes. A fight is only written where the quest's enemies are
    /// (<paramref name="fights"/>). In combat the decision follows the nearest attacker, which changes with every step
    /// of a fight: the first such line is written at once, the next ones at most every <see cref="WouldEvery"/>, each
    /// with the decision at that moment (so the last one of a fight still shows once things are calm).
    /// </summary>
    private static void Would(Watched w, StepSnapshot s, uint id, DateTime now, uint combatJob, bool fights)
    {
        var plan = StepRules.Decide(w.Script, s, w.Memory, now, combatJob: combatJob);
        if (plan.Kind == StepKind.Wait || (plan.Kind == StepKind.Fight && !fights)) return;
        var key = $"{plan.Kind}|{plan.Target?.BaseId}|{plan.Enemy?.Id}|{plan.Level}|{plan.Id}|{plan.Text}|{plan.What}";
        if (key == w.Plan) return;
        if (s.InCombat || plan.Kind == StepKind.Fight)
        {
            if (now < w.NextWould) return;
            w.NextWould = now + WouldEvery;
        }
        w.Plan = key;
        Write($"{id}: would {plan}");
    }

    /// <summary>
    /// The hostile mobs within <see cref="MobsWithin"/> yalms, once for all the quests they matter to here
    /// (<see cref="fighting"/>): each kind of mob with what the enemy filter goes by, then per quest that quest's icon
    /// for it, whether its handler holds it, and the filter's verdict with the reason (quests that agree share one
    /// entry). Mobs of one kind and name that read alike are one entry, with how far each is. Written when an entry
    /// comes, goes or changes in what the filter goes by (not with distance, health or how many there are, so a herd
    /// wandering by doesn't fill the log), and not more often than <see cref="MobsEvery"/>.
    /// </summary>
    private static void Mobs(DateTime now)
    {
        if (fighting.Count == 0)
        {
            // Out of the quests' zones (or their quests are gone): back there, the mobs are listed afresh.
            mobsKey = "";
            return;
        }
        var first = fighting[0].Seen;
        var me = Services.Objects.LocalPlayer?.GameObjectId ?? 0;
        // Each quest's look read the same mobs; a quest's own kinds are in its look even when they aren't hostile.
        var mobs = new Dictionary<ulong, StepEnemy>();
        var mine = new List<Dictionary<ulong, StepEnemy>>(fighting.Count);
        foreach (var (_, seen, _) in fighting)
        {
            var own = new Dictionary<ulong, StepEnemy>();
            foreach (var e in seen.Hostiles)
            {
                if (Vector3.Distance(e.Position, seen.Player) > MobsWithin) continue;
                own[e.Id] = e;
                mobs.TryAdd(e.Id, e);
            }
            mine.Add(own);
        }
        var groups = mobs.Values.OrderBy(e => Vector3.DistanceSquared(e.Position, first.Player))
            .GroupBy(e => (e.Name, e.BaseId, e.NameId, Facts: MobFacts(e, me), Verdicts: Verdicts(e.Id, mine))).ToList();
        var key = string.Join("|", fighting.Select(f => f.Id)) + "#"
                  + string.Join("|", groups.Select(g => $"{g.Key.BaseId}/{g.Key.NameId}:{g.Key.Facts}:{g.Key.Verdicts}").OrderBy(x => x, StringComparer.Ordinal));
        if (key == mobsKey || now < nextMobs) return;
        mobsKey = key;
        nextMobs = now + MobsEvery;
        var text = groups.Count == 0
            ? $"(none within {MobsWithin:0} yalms)"
            : string.Join("; ", groups.Take(MobsMost).Select(g =>
                  $"{(g.Count() > 1 ? $"{g.Count()}x " : "")}\"{g.Key.Name}\" {g.Key.BaseId}/{g.Key.NameId} at {string.Join("/", g.Take(6).Select(e => Vector3.Distance(e.Position, first.Player).ToString("0")))} y, "
                  + $"health {g.First().Hp}%, {g.Key.Facts}; {g.Key.Verdicts}"))
              + (groups.Count > MobsMost ? $"; and {groups.Count - MobsMost} more kinds" : "");
        Write($"mobs for {string.Join(", ", fighting.Select(f => $"{f.Id} (seq {f.Seen.Sequence} vars [{Convert.ToHexString(f.Seen.Variables)}])"))}, "
              + $"you on job {first.Job} ({(first.Fighter ? "fights" : "doesn't fight")})"
              + $"{(first.InCombat ? ", in combat (the quests' icons and \"held\" are the last calm look's)" : "")}: {text}");
    }

    /// <summary>What the enemy filter goes by for a mob that's the same whichever quest asks; without its distance and health (those change all the time).</summary>
    private static string MobFacts(StepEnemy e, ulong me)
    {
        var tag = e.Tagger is 0 or 0xE000_0000 ? "nobody" : e.Tagger == me ? "you" : "another";
        var target = e.Target switch
        {
            EnemyTarget.You => "you",
            EnemyTarget.YourChocobo => "your chocobo",
            EnemyTarget.OtherPlayer => "another player",
            EnemyTarget.Other => "something else",
            _ => "nobody",
        };
        return $"{(e.Hostile ? "hostile" : "not hostile")}{(e.Targetable ? "" : ", untargetable")}, drawn {e.Drawn}, fate {e.Fate}"
               + $"{(e.Weakened ? ", SUBDUED" : "")}, {(e.InCombat ? "in combat" : "idle")}, targets {target}, first hit by {tag}";
    }

    /// <summary>
    /// Each quest's own view of a mob: the icon its handler gives it, whether the handler holds it, and the filter's
    /// verdict with the reason. Quests that read alike share one entry ("1242,1243: icon 0, not held => ..."); a quest
    /// whose look doesn't hold the mob (another quest's kind that isn't hostile) is left out.
    /// </summary>
    private static string Verdicts(ulong mob, List<Dictionary<ulong, StepEnemy>> mine)
    {
        var parts = new List<(uint Id, string Text)>();
        for (var i = 0; i < fighting.Count; i++)
        {
            if (!mine[i].TryGetValue(mob, out var e)) continue;
            var (w, seen, id) = fighting[i];
            var verdict = EnemyRules.Judge(w.Script, seen, e);
            parts.Add((id, $"icon {e.Icon}, {(e.Held ? "held" : "not held")} => {verdict.Verdict} ({verdict.Why})"));
        }
        return string.Join(" / ", parts.GroupBy(x => x.Text).Select(g => $"{string.Join(",", g.Select(x => x.Id))}: {g.Key}"));
    }

    /// <summary>The character and the windows, for all watched quests: riding, carrying, menus, questions, the reward window, the map's slots.</summary>
    private static unsafe void Surroundings()
    {
        var mount = Services.Condition[ConditionFlag.Mounted] && Services.Objects.LocalPlayer is { } p
            ? ((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)p.Address)->Mount.MountId : (ushort)0;
        var questMount = watched.Values.Any(w => w.Script.Mount is { } m && m.Mount == mount);
        var r = mount == 0 ? "" : $"riding mount {mount}{(questMount ? " (the quest's)" : "")}, flying {Services.Condition[ConditionFlag.InFlight]}";
        if (r != riding) { riding = r; Write(r.Length == 0 ? "not riding" : r); }

        var carryStatuses = watched.Values.Select(w => w.Script.CarryStatus).Where(c => c != 0).ToHashSet();
        if (carryStatuses.Count > 0 && Services.Objects.LocalPlayer is { } me)
        {
            var has = me.StatusList.Any(st => carryStatuses.Contains(st.StatusId));
            var c = has ? $"carrying (status {string.Join(",", carryStatuses)}), mounted {Services.Condition[ConditionFlag.Mounted]}, flying {Services.Condition[ConditionFlag.InFlight]}" : "not carrying";
            if (c != carrying) { carrying = c; Write(c); }
        }

        var m2 = QuestSteps.Menu() is { } open ? $"menu \"{open.Prompt}\": [{string.Join(" / ", open.Entries.Select(StepRules.Normal))}]" : "";
        if (m2 != menu) { menu = m2; if (m2.Length > 0) Write(m2); }

        var q = QuestSteps.Question() is { } text ? $"question \"{text}\" (on the known list: {StepRules.KnownQuestion(text, QuestSteps.KnownQuestions)})" : "";
        if (q != question) { question = q; if (q.Length > 0) Write(q); }

        // Once it's ready, so its choices can be read: what it offers and what the run would take.
        var reward = Interact.Ready("JournalResult") != null;
        if (reward && !rewardOpen)
        {
            var list = string.Join(" | ", watched.Values.Where(w => w.Script.OptionalRewards.Count > 0).Select(w => $"{w.Script.Quest - 65536}: [{string.Join(",", w.Script.OptionalRewards)}]"));
            var options = QuestSteps.RewardOptions();
            var pick = RewardChoice.Pick(options);
            Write($"reward window open; optional rewards: {(list.Length > 0 ? list : "none")}; the window offers "
                  + (options.Count == 0 ? "no choice" : $"[{string.Join("; ", options)}], the run would pick {pick!.Slot + 1}: {RewardChoice.Why(pick, options)}"));
        }
        rewardOpen = reward;

        var sl = string.Join("; ", QuestSteps.MarkerSlots().Select(x => $"{x.Objective} \"{x.Label}\" x{x.Markers}"));
        if (sl != slots) { slots = sl; Write($"marker slots: {(sl.Length == 0 ? "(none)" : sl)}"); }
    }

    // ------------------------------------------------------------------ /say and emotes by the player

    private static void Attach()
    {
        if (chatAttached) return;
        chatAttached = true;
        Services.Chat.ChatMessage += OnChat;
    }

    /// <summary>
    /// The player's own /say or emote while the watch is on: whether the line holds a watched quest's phrase (exactly,
    /// or only ignoring case) and what was targeted; never the line itself, and no other player's name. The quest is
    /// read at the next look (<see cref="Said"/>), not from here.
    /// </summary>
    private static void OnChat(IHandleableChatMessage message)
    {
        try
        {
            if (!on || message.SourceKind != XivChatRelationKind.LocalPlayer) return;
            if (message.LogKind is not (XivChatType.Say or XivChatType.StandardEmote or XivChatType.CustomEmote)) return;
            var text = message.Message.TextValue;
            var parts = new List<(uint, string)>();
            foreach (var w in watched.Values)
            {
                if (message.LogKind == XivChatType.Say)
                {
                    var phrase = w.Script.Say.Values.FirstOrDefault();
                    if (phrase == null) continue;
                    var exact = text.Contains(phrase, StringComparison.Ordinal);
                    var loose = text.Contains(phrase, StringComparison.CurrentCultureIgnoreCase);
                    parts.Add((w.Script.Quest, $"holds the phrase {(exact ? "exactly" : loose ? "ignoring case only" : "no")}, {(text.Trim().Length > phrase.Length ? "with other words" : "alone")}"));
                }
                else if (w.Script.Emotes.Count > 0)
                    parts.Add((w.Script.Quest, $"target {TargetText()}"));
            }
            if (parts.Count == 0) return;
            said = (DateTime.UtcNow, message.LogKind == XivChatType.Say ? "said a line in /say" : "did an emote", parts,
                watched.ToDictionary(kv => kv.Key, kv => kv.Value.Memory.Signature));
        }
        catch (Exception e)
        {
            Services.Log.Warning(e, "Quest watch chat");
        }
    }

    /// <summary>The player's target for the log: its kind and id, and its name only when it isn't a player.</summary>
    private static string TargetText()
    {
        if (Services.Targets.Target is not { } t) return "none";
        return t.ObjectKind == ObjectKind.Pc ? $"{t.ObjectKind} {t.BaseId}" : $"{t.ObjectKind} {t.BaseId} \"{t.Name}\"";
    }

    /// <summary>The /say or emote seen since the last look, with how far the nearest of each quest's active objects was then.</summary>
    private static void Said()
    {
        if (said is not { } c) return;
        said = null;
        var lines = new List<string>();
        foreach (var (quest, words) in c.Parts)
        {
            var where = "nothing read";
            if (watched.TryGetValue(quest, out var w) && w.Last is { } s)
            {
                var nearest = StepRules.Live(w.Script, s, new StepMemory()).OrderBy(o => Vector3.Distance(o.Position, s.Player)).FirstOrDefault();
                where = nearest != null ? $"{Vector3.Distance(nearest.Position, s.Player):0.0} y from {nearest.Name} ({nearest.BaseId})" : "nothing active in view";
            }
            lines.Add($"{quest - 65536}: {words}, {where}");
        }
        Write($"{c.What}: {string.Join("; ", lines)}");
        pending = (c.At, c.What, c.Before);
    }

    /// <summary>Three seconds after the player's /say or emote: did any watched quest move on?</summary>
    private static void Settle(DateTime now)
    {
        if (pending is not { } p || now - p.At < TimeSpan.FromSeconds(3)) return;
        pending = null;
        var moved = watched.Where(kv => p.Before.TryGetValue(kv.Key, out var before) && before != kv.Value.Memory.Signature).Select(kv => kv.Key - 65536).ToList();
        Write($"progress after that ({p.What}): {(moved.Count > 0 ? "yes, " + string.Join(",", moved) : "none")}");
    }

    /// <summary>
    /// For a bug report made next to a quest's enemies while doing it by hand: each allied society quest in the journal
    /// that has enemies, with its enemy data (from the sheets) and the mobs around as the enemy filter saw them at the
    /// watch's last look. Only the journal's quest list is read from the game here; the quest's script is asked
    /// nothing: a bug report is made from the window's draw, and the script is only ever asked from the watch's own
    /// look (the game idle, <see cref="QuestSteps.Snapshot"/>). So a quest the watch hasn't looked at (it's off, or a
    /// run is going, whose own section has the fight) reads "nothing seen yet". No names but the mobs'.
    /// </summary>
    public static List<string> DescribeEnemies()
    {
        var list = new List<string>();
        if (!Services.ClientState.IsLoggedIn || Services.Objects.LocalPlayer == null || QuestSteps.Loading || InDuty) return list;
        var now = DateTime.UtcNow;
        foreach (var row in SocietyQuests())
        {
            if (QuestSteps.Script(row) is not { Enemies.Count: > 0 } script) continue;
            list.Add($"{row - 65536} \"{script.Name}\": enemies [{QuestSteps.EnemyData(script)}], key item on objects [{string.Join(",", script.ItemObjects.Select(o => $"{o.Object}@{o.Sequence}"))}]");
            if (!watched.TryGetValue(row, out var w) || w.Seen is not { } s)
            {
                list.Add("  nothing seen yet (the hand watch hasn't looked at this quest: it's off, or a run is going)");
                continue;
            }
            var state = $"  as the hand watch saw it {(now - w.SeenAt).TotalSeconds:0.0} s ago: seq {s.Sequence} vars [{Convert.ToHexString(s.Variables)}], on job {s.Job} ({(s.Fighter ? "fights" : "doesn't fight")}), "
                        + $"{(s.InCombat ? "in combat (the quest's icon and \"held\" are the last calm look's)" : s.ScriptRead ? "script read" : "script not read (the game is busy)")}; ";
            list.Add(Fights(w, s)
                ? state + $"mobs within {MobsWithin:0} yalms: {QuestSteps.EnemiesSeen(script, s, MobsWithin, MobsMost, out _)}"
                : state + $"in zone {s.Territory}, not one of the quest's [{string.Join(",", w.Zones.OrderBy(z => z))}]: the mobs around aren't listed");
        }
        return list;
    }

    private static string V(Vector3 v) => $"{v.X:0.#},{v.Y:0.#},{v.Z:0.#}";

    private static void Write(string text) => DebugLog.Write("steps", "watch " + text);
}
