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
/// </remarks>
internal static class QuestWatch
{
    /// <summary>How often the watch looks.</summary>
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(0.5);

    private sealed class Watched
    {
        public QuestScript Script = null!;
        public readonly StepMemory Memory = new();
        public StepSnapshot? Last;
        public string Header = "", Todos = "", Objects = "", Markers = "", Plan = "";
    }

    private static readonly Dictionary<uint, Watched> watched = new();
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
            foreach (var q in quests) Look(q, now);
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

    private static void Look(uint row, DateTime now)
    {
        if (!watched.TryGetValue(row, out var w))
        {
            if (QuestSteps.Script(row) is not { } script) return;
            w = watched[row] = new Watched { Script = script };
            Write($"{row - 65536}: \"{script.Name}\" giver {script.Giver}; product {script.Product}/{script.Materials}, gather [{string.Join(",", script.GatherItems.Select(kv => $"{kv.Key}:{kv.Value}"))}], "
                  + $"key items [{string.Join(",", script.KeyItems.Select(k => $"{k.Id}{(k.Usable ? $"(act {k.Action} {(k.Ground ? "ground" : k.Self ? "self" : "target")} r{k.Range})" : "")}"))}], "
                  + $"mount {(script.Mount is { } m ? $"{m.Mount}/{m.Action}{(m.Line ? " line" : "")}{(m.CanFly ? " flies" : "")}" : "none")}, say [{string.Join(",", script.Say.Select(kv => $"{kv.Key}:\"{kv.Value}\""))}], "
                  + $"emotes [{string.Join(",", script.Emotes.Select(kv => $"{kv.Key}:{kv.Value.Command}"))}], carry {script.CarryStatus}; reading its objects through its event handler (IsActive, icon) twice a second");
        }
        if (QuestSteps.Snapshot(w.Script) is not { } s) return;
        // The script wasn't asked (the game is busy): nothing to compare.
        if (!s.ScriptRead) return;
        w.Last = s;
        var id = row - 65536;
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

        // What the stepper would do now (never done here). Positions left out of the comparison: a wandering target
        // doesn't write a line per step it takes.
        var plan = StepRules.Decide(w.Script, s, w.Memory, now);
        var key = $"{plan.Kind}|{plan.Target?.BaseId}|{plan.Level}|{plan.Id}|{plan.Text}|{plan.What}";
        if (key != w.Plan && plan.Kind != StepKind.Wait) { w.Plan = key; Write($"{id}: would {plan}"); }
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

        var reward = Interact.IsOpen("JournalResult");
        if (reward && !rewardOpen)
        {
            var list = string.Join(" | ", watched.Values.Where(w => w.Script.OptionalRewards.Count > 0).Select(w => $"{w.Script.Quest - 65536}: [{string.Join(",", w.Script.OptionalRewards)}]"));
            Write($"reward window open; optional rewards: {(list.Length > 0 ? list : "none")}");
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

    private static string V(Vector3 v) => $"{v.X:0.#},{v.Y:0.#},{v.Z:0.#}";

    private static void Write(string text) => DebugLog.Write("steps", "watch " + text);
}
