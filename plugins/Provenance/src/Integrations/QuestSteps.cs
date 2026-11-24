using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Automation;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Provenance.Core.Tribes;
using Provenance.Data;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using CsObjectKind = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind;
using SheetAction = Lumina.Excel.Sheets.Action;
using GameMap = FFXIVClientStructs.FFXIV.Client.Game.UI.Map;

namespace Provenance.Integrations;

/// <summary>
/// A quest's steps as the game shows them, and what the step engine does on them: the quest's data for the engine
/// (<see cref="Script"/>), what the game shows now (<see cref="Snapshot"/>: sequence, to-do lines, the quest's own
/// objects with their icons, the map markers, the items), the acts (talk, /say, an emote, a key item, the quest mount's
/// action, getting off it), and the windows that follow (the hand-over, the reward, menus, questions).
/// </summary>
/// <remarks>
/// Facts from the quest-step research (queststeps_engine.md, from the game's quest scripts, sheets and
/// FFXIVClientStructs, MIT): the quest's event handler owns its objects in the zone and says which are active and what
/// icon they show; <c>GetTodoArgs</c> gives a to-do line's count and item (the game fills blank item names in the
/// journal from it); the map keeps the quest's markers. Key items whose <c>EventItem.Action</c> is 0 are handed over
/// by talking; ground-targeted ones are used at a spot (as DailyRoutines does; facts only, no licence). Emotes and
/// /say go through the chat box as a player types them (ECommons' <c>Chat</c>, MIT). The hand-over follows
/// <see cref="DeliveryWindow"/> (PandorasBox's callbacks, BSD-3, as facts). Nothing that is an addon callback is sent
/// while the game has scene tasks pending (<see cref="DeliveryWindow.SceneBusy"/>); clicks on enabled buttons are.
/// A quest's enemies come from its listeners (<see cref="EnemyRules"/>); the mobs around are read from the object table
/// with their own fields (FATE, nameplate icon, the "subdued" flag <c>Character.ActorControlFlags &amp; 0x40</c>:
/// FFXIVClientStructs), which is safe in combat, while what the quest's handler says of them is kept from the last calm look.
/// </remarks>
internal static unsafe class QuestSteps
{
    // ------------------------------------------------------------------ the quest's data

    private static readonly Dictionary<uint, QuestScript?> Scripts = new();

    /// <summary>
    /// The text rows holding the /say phrase of the quests that have no "SAYTODO" row (read from their scripts:
    /// Moogles 2295 compares with SYSTEM_000_019, the Ixal password quests with SYSTEM_100_062; the Vath's 2277
    /// "Fifty Shades of Yellow" keeps its "Uhm Ala" in SYSTEM_000_012, by its quest text sheet).
    /// </summary>
    private static readonly Dictionary<uint, string> SayKeys = new()
    {
        [2295] = "SYSTEM_000_019",
        [2277] = "SYSTEM_000_012",
        [1497] = "SYSTEM_100_062", [1501] = "SYSTEM_100_062", [1507] = "SYSTEM_100_062", [1515] = "SYSTEM_100_062", [1568] = "SYSTEM_100_062",
    };

    /// <summary>The engine's view of a quest's data (row id: 65536 + the quest number); null when it can't be read.</summary>
    public static QuestScript? Script(uint questRow)
    {
        if (questRow is > 0 and < 65536) questRow += 65536;
        lock (Scripts)
        {
            if (Scripts.TryGetValue(questRow, out var known)) return known;
            QuestScript? script = null;
            try { script = Build(questRow); }
            catch (Exception e) { Services.Log.Warning(e, "Quest {Row}'s steps couldn't be read", questRow); }
            Scripts[questRow] = script;
            return script;
        }
    }

    private static readonly Regex Commands = new(@"/[\p{L}\p{N}_-]+", RegexOptions.Compiled);
    private static readonly Regex QuestionKey = new(@"_Q\d+_\d+_\d+$", RegexOptions.Compiled);
    private static readonly Regex AnswerKey = new(@"_A\d+_\d+_\d+$", RegexOptions.Compiled);

    private static QuestScript? Build(uint row)
    {
        if (QuestFacts.Of(row) is not { } facts) return null;
        var quest = Services.Data.GetExcelSheet<Quest>().GetRow(row);
        var prefix = "TEXT_" + facts.Key.ToUpperInvariant() + "_";
        string Text(string suffix) => facts.Text.TryGetValue(prefix + suffix, out var t) ? t : "";

        var todos = facts.Todos.Select(t => new ScriptTodo(t.Index, t.Sequence, t.Count,
            t.Spots.Select(p => new ScriptSpot(p.Level, p.Territory, p.Position, p.Object, p.Object == 0)).ToList(),
            Text($"TODO_{t.Index:00}"))).ToList();

        ScriptSpot? giverSpot = null;
        if (quest.IssuerLocation.ValueNullable is { } issuer)
            giverSpot = new ScriptSpot(issuer.RowId, issuer.Territory.RowId, new Vector3(issuer.X, issuer.Y, issuer.Z), issuer.Object.RowId, false);

        // Crafting: the product is the script's item that has a recipe; the materials the one that recipe takes.
        var lookup = Services.Data.GetExcelSheet<RecipeLookup>();
        var items = facts.Args.Where(a => (a.Name.StartsWith("RITEM", StringComparison.Ordinal) || a.Name == "QST_PRODUCT_ITEM") && a.Value is > 100 and < 2_000_000)
            .Select(a => a.Value).Distinct().ToList();
        uint product = 0, materials = 0, craftStatus = 0, craftEquip = 0;
        foreach (var item in items)
        {
            if (FirstRecipe(lookup, item) is not { } recipe) continue;
            product = item;
            var ingredients = recipe.Ingredient.Select(i => i.RowId).Where(i => i != 0).ToList();
            materials = items.FirstOrDefault(i => i != item && ingredients.Contains(i));
            if (materials == 0) materials = ingredients.FirstOrDefault(i => i > 19); // ingredient 0; crystals are 2-19
            craftStatus = recipe.StatusRequired.RowId;
            craftEquip = recipe.ItemRequired.RowId;
            break;
        }

        var gather = new Dictionary<uint, uint>();
        if (facts.Arg("QST_GATHERING_ITEM_MIN") is { } min and > 0) gather[16] = min;
        if (facts.Arg("QST_GATHERING_ITEM_HRV") is { } btn and > 0) gather[17] = btn;
        if (facts.Arg("QST_GATHERING_ITEM_FSH") is { } fsh and > 0) gather[18] = fsh;

        var keyItems = new List<KeyItemFact>();
        var eventItems = Services.Data.GetExcelSheet<EventItem>();
        foreach (var (_, value) in facts.ArgsOf("ITEM"))
        {
            if (value < 2_000_000 || eventItems.GetRowOrDefault(value) is not { } ei) continue;
            var act = ei.Action.ValueNullable;
            var ground = act?.TargetArea ?? false;
            var self = act is { } a && (a.Range == 0 || SelfCentred(a));
            var words = new[] { ei.Name.ExtractText(), ei.Singular.ExtractText(), ei.Plural.ExtractText() }.Where(w => w.Length > 0).Distinct().ToList();
            keyItems.Add(new KeyItemFact(value, words.FirstOrDefault() ?? $"item {value}", ei.Action.RowId, ground, self, Math.Max(1, (float)(act?.Range ?? 3)), ei.CastTime, words));
        }

        MountFact? mount = null;
        if (facts.Arg("MOUNT0") is { } mountId and > 0)
        {
            var mountRow = Services.Data.GetExcelSheet<Mount>().GetRowOrDefault(mountId);
            // The actions the script names (ACTION0, ACTION1 ...: the hippo cart has three). When it names none, the
            // mount's own first action (the Ananta's marid: Trample, which the script of "Ants in the Pants" doesn't name).
            var named = facts.ArgsOf("ACTION").Select(a => a.Value).Where(v => v != 0).Distinct().ToList();
            if (named.Count == 0 && mountRow?.MountAction.ValueNullable is { } own)
                named.AddRange(own.Action.Select(a => a.RowId).Where(v => v != 0).Take(1));
            var actionId = named.FirstOrDefault();
            var act = actionId != 0 ? Services.Data.GetExcelSheet<SheetAction>().GetRowOrDefault(actionId) : null;
            var line = act is { CastType: 4 or 12 };
            var self = act is { } a && (a.Range == 0 || SelfCentred(a));
            var canFly = mountRow?.FlyingCondition.RowId is > 0;
            mount = new MountFact(mountId, actionId, self ? 0 : act?.Range ?? 0, act?.EffectRange ?? 0, line, canFly)
            {
                Others = named.Skip(1).ToList(),
                Ground = act?.TargetArea ?? false,
            };
        }

        // /say: the quest's own phrase, on the line whose text names it.
        var phrases = facts.TextWith("_SAYTODO_").ToList();
        if (SayKeys.TryGetValue(row - 65536, out var sayKey) && Text(sayKey) is { Length: > 0 } keyed) phrases.Add(keyed);
        var say = new Dictionary<int, string>();
        foreach (var t in todos)
            if (phrases.FirstOrDefault(p => p.Length > 0 && t.Text.Contains(p, StringComparison.CurrentCultureIgnoreCase)) is { } phrase)
                say[t.Index] = phrase;

        // Emotes: a line that names an emote's command ("/psych").
        var emotes = new Dictionary<int, EmoteFact>();
        foreach (var t in todos)
            foreach (Match match in Commands.Matches(t.Text))
                if (EmoteCommands.Value.TryGetValue(match.Value, out var e)) { emotes[t.Index] = e; break; }

        // Carrying: a status the script checks that no recipe asks for, on a quest that crafts nothing (5356).
        var carry = product == 0 ? facts.ArgsOf("STATUS").Select(a => a.Value).FirstOrDefault(v => v != 0 && v != craftStatus) : 0;

        return new QuestScript
        {
            Quest = row,
            Name = facts.Name,
            Repeatable = quest.IsRepeatable,
            Giver = quest.IssuerStart.RowId,
            GiverSpot = giverSpot,
            TargetEnd = quest.TargetEnd.RowId != 0 ? quest.TargetEnd.RowId : quest.IssuerStart.RowId,
            Todos = todos,
            Product = product,
            Materials = materials,
            ProductHq = product != 0 && facts.WantsHq,
            CraftStatus = craftStatus,
            CraftEquip = craftEquip,
            GatherItems = gather,
            KeyItems = keyItems,
            Mount = mount,
            Say = say,
            Emotes = emotes,
            CarryStatus = carry,
            Territories = facts.ArgsOf("TERRITORYTYPE").Select(a => a.Value).Where(v => v != 0).ToList(),
            OptionalRewards = quest.OptionalItemReward.Select(r => r.RowId).Where(r => r != 0).ToList(),
            MenuPrompts = facts.Text.Where(kv => QuestionKey.IsMatch(kv.Key) && kv.Value.Length > 0).Select(kv => kv.Value).ToList(),
            MenuAnswers = facts.Text.Where(kv => AnswerKey.IsMatch(kv.Key) && kv.Value.Length > 0).Select(kv => kv.Value).ToList(),
            Enemies = EnemyRules.Read(facts.Args, facts.Listeners, QuestFacts.LevelSpot),
            ItemObjects = EnemyRules.ItemObjects(facts.Listeners),
        };
    }

    /// <summary>An action that works around its user (it can only target them, and isn't aimed at a single target).</summary>
    private static bool SelfCentred(SheetAction a) => a.CanTargetSelf && !a.CanTargetHostile && !a.CanTargetAlly && a.CastType != 1;

    private static Recipe? FirstRecipe(Lumina.Excel.ExcelSheet<RecipeLookup> lookup, uint item)
    {
        if (lookup.GetRowOrDefault(item) is not { } r) return null;
        foreach (var rr in new[] { r.CRP, r.BSM, r.ARM, r.GSM, r.LTW, r.WVR, r.ALC, r.CUL })
            if (rr.RowId != 0 && rr.ValueNullable is { } recipe) return recipe;
        return null;
    }

    /// <summary>Emote commands in the client's language ("/psych", its short form and aliases) → the emote.</summary>
    private static readonly Lazy<Dictionary<string, EmoteFact>> EmoteCommands = new(() =>
    {
        var map = new Dictionary<string, EmoteFact>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var e in Services.Data.GetExcelSheet<Emote>())
        {
            if (e.TextCommand.ValueNullable is not { } tc) continue;
            var command = tc.Command.ExtractText();
            if (command.Length == 0) continue;
            foreach (var s in new[] { tc.Command, tc.ShortCommand, tc.Alias, tc.ShortAlias })
                if (s.ExtractText() is { Length: > 1 } text && text[0] == '/') map.TryAdd(text, new EmoteFact((ushort)e.RowId, command));
        }
        return map;
    });

    /// <summary>The gathering point base and the area the game exports for it, for a gathered quest item.</summary>
    public static (uint Base, Vector2 Centre, float Radius)? GatherPlace(uint itemId)
    {
        var gatheringItem = Services.Data.GetExcelSheet<GatheringItem>().FirstOrDefault(g => g.Item.RowId == itemId);
        if (gatheringItem.RowId == 0) return null;
        foreach (var b in Services.Data.GetExcelSheet<GatheringPointBase>())
        {
            if (!b.Item.Any(i => i.RowId == gatheringItem.RowId)) continue;
            if (Services.Data.GetExcelSheet<ExportedGatheringPoint>().GetRowOrDefault(b.RowId) is { } p)
                return (b.RowId, new Vector2(p.X, p.Y), p.Radius);
            return (b.RowId, Vector2.Zero, 0);
        }
        return null;
    }

    // ------------------------------------------------------------------ what the game shows

    private static QuestEventHandler* Handler(uint questRow)
    {
        var ef = EventFramework.Instance();
        if (ef == null) return null;
        var h = ef->GetEventHandlerById((ushort)(questRow - 65536));
        return h == null ? null : (QuestEventHandler*)h;
    }

    /// <summary>The quest is in the journal.</summary>
    public static bool Accepted(uint questRow)
    {
        var qm = QuestManager.Instance();
        return qm != null && qm->IsQuestAccepted((ushort)(questRow - 65536));
    }

    /// <summary>The class the quest was accepted as (0 when it isn't in the journal).</summary>
    public static uint AcceptJob(uint questRow)
    {
        var qm = QuestManager.Instance();
        var work = qm == null ? null : qm->GetQuestById((ushort)(questRow - 65536));
        return work == null ? 0u : work->AcceptClassJob;
    }

    /// <summary>
    /// Casting, in an event or a cutscene, crafting or gathering, getting on a mount, jumping, in combat, between areas,
    /// or the game has scene tasks pending: the quest's script isn't asked anything, and nothing of a talk is started.
    /// </summary>
    public static bool Busy => Occupied || InCombat;

    /// <summary>
    /// <see cref="Busy"/> without the combat: the game itself is in the way (a cast, an event, a cutscene, crafting,
    /// gathering, mounting, a jump, loading, scene tasks pending) and nothing can be started. In combat alone the
    /// character can still act: fight back, walk on, use a key item.
    /// </summary>
    public static bool Occupied
    {
        get
        {
            var c = Services.Condition;
            return c[ConditionFlag.Casting] || c[ConditionFlag.Casting87] || c[ConditionFlag.OccupiedInEvent]
                   || c[ConditionFlag.OccupiedInQuestEvent] || c[ConditionFlag.OccupiedInCutSceneEvent]
                   || c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51]
                   || c[ConditionFlag.WatchingCutscene] || c[ConditionFlag.WatchingCutscene78]
                   || c[ConditionFlag.Crafting] || c[ConditionFlag.PreparingToCraft] || c[ConditionFlag.ExecutingCraftingAction]
                   || c[ConditionFlag.Gathering] || c[ConditionFlag.ExecutingGatheringAction]
                   || c[ConditionFlag.Mounting] || c[ConditionFlag.Mounting71] || c[ConditionFlag.Jumping] || c[ConditionFlag.Jumping61]
                   || DeliveryWindow.SceneBusy;
        }
    }

    /// <summary>In combat, as the game counts it.</summary>
    public static bool InCombat => Services.Condition[ConditionFlag.InCombat];

    public static bool InCutscene
        => Services.Condition[ConditionFlag.OccupiedInCutSceneEvent] || Services.Condition[ConditionFlag.WatchingCutscene]
           || Services.Condition[ConditionFlag.WatchingCutscene78];

    /// <summary>Between areas (a zone change or a warp under way): nothing is read from the quest's script then.</summary>
    public static bool Loading => Services.Condition[ConditionFlag.BetweenAreas] || Services.Condition[ConditionFlag.BetweenAreas51];

    /// <summary>After a zone change (or a warp within one), this long before the quest's script is asked anything.</summary>
    private static readonly TimeSpan ZoneSettle = TimeSpan.FromSeconds(2);
    private static uint zoneSeen;
    private static DateTime zoneSince;

    /// <summary>
    /// Not between areas, and the zone hasn't changed for <see cref="ZoneSettle"/>: the quest's objects are set up
    /// again. Called by whoever reads the script (the stepper, the watch), often enough to notice each change.
    /// </summary>
    public static bool Settled(DateTime now)
    {
        var zone = Services.ClientState.TerritoryType;
        if (Loading || zone != zoneSeen || zoneSince == default)
        {
            zoneSeen = zone;
            zoneSince = now;
        }
        return now - zoneSince >= ZoneSettle;
    }

    /// <summary>The quest's state as far as it reads without its script (journal step and variables), for the busy watch.</summary>
    public static string Progress(uint questRow)
    {
        var qm = QuestManager.Instance();
        if (qm == null) return "";
        var id = (ushort)(questRow - 65536);
        if (!qm->IsQuestAccepted(id)) return "gone";
        var work = qm->GetQuestById(id);
        if (work == null) return "";
        var v = work->Variables;
        var vars = new byte[6];
        for (var i = 0; i < 6 && i < v.Length; i++) vars[i] = v[i];
        return $"{work->Sequence}|{Convert.ToHexString(vars)}";
    }

    /// <summary>What the game shows about the quest and the character now; null when not logged in.</summary>
    public static StepSnapshot? Snapshot(QuestScript q)
    {
        var player = Services.Objects.LocalPlayer;
        var qm = QuestManager.Instance();
        if (player == null || qm == null) return null;
        var id = (ushort)(q.Quest - 65536);
        var accepted = qm->IsQuestAccepted(id);
        var work = accepted ? qm->GetQuestById(id) : null;
        var sequence = work != null ? work->Sequence : (byte)0;
        var vars = new byte[6];
        if (work != null)
        {
            var v = work->Variables;
            for (var i = 0; i < 6 && i < v.Length; i++) vars[i] = v[i];
        }
        var completed = qm->IsDailyQuestCompleted(id) || (!q.Repeatable && QuestManager.IsQuestComplete(id));

        // The quest's script is asked only when the game is idle (not in combat either), a while after a zone change,
        // and when its script is loaded: calling into it while an event, a cutscene or scene tasks run, in a fight, or
        // while the zone sets up, can crash.
        var occupied = Occupied;
        var inCombat = InCombat;
        var busy = occupied || inCombat;
        var settled = Settled(DateTime.UtcNow);
        var handler = accepted && !busy && settled ? Handler(q.Quest) : null;
        var loaded = handler != null && handler->LuaEventHandler.LuaState != null;
        var read = !accepted || (!busy && settled && (handler == null || loaded));
        if (!loaded) handler = null;
        var todos = new List<TodoProgress>();
        var objects = new List<StepObject>();
        var remembered = false;
        if (!accepted) CalmLooks.Remove(q.Quest);
        if (handler != null)
        {
            var me = (BattleChara*)player.Address;
            foreach (var t in q.TodosAt(sequence))
            {
                uint a0 = 0, a1 = 0, a2 = 0;
                handler->GetTodoArgs(me, (byte)t.Index, &a0, &a1, &a2);
                todos.Add(new TodoProgress(t.Index, handler->IsTodoChecked(me, (byte)t.Index), a0, a1, a2));
            }
            var marks = new Dictionary<ulong, uint>();
            objects = Objects(handler, marks);
            CalmLooks[q.Quest] = new CalmLook(Progress(sequence, vars), todos, objects, marks);
        }
        else if (accepted && inCombat && !occupied && settled && CalmLooks.TryGetValue(q.Quest, out var calm) && calm.Progress == Progress(sequence, vars))
        {
            // In combat the script isn't asked. While the quest hasn't moved since the last calm look, its lines and
            // objects are still those (the objects where they stand now): a class that doesn't fight carries on by them.
            todos = calm.Todos;
            objects = Refreshed(calm.Objects);
            remembered = true;
            read = true;
        }

        var items = new Dictionary<uint, int>();
        void Count(uint item)
        {
            if (item != 0 && !items.ContainsKey(item)) items[item] = ItemCount(item);
        }
        Count(q.Product);
        Count(q.Materials);
        foreach (var g in q.GatherItems.Values) Count(g);
        foreach (var k in q.KeyItems) Count(k.Id);
        foreach (var t in todos) Count(t.ItemId);
        // The high-quality copies on their own, for a product (or a line's item) only they count for.
        if (q.Product != 0) items[q.Product + 1_000_000] = ItemCountHq(q.Product);
        foreach (var t in todos)
            if (t.WantsHq && t.ItemId != 0) items[t.ItemId + 1_000_000] = ItemCountHq(t.ItemId);

        var chara = (Character*)player.Address;
        var statuses = new HashSet<uint>();
        var expiring = new HashSet<uint>();
        foreach (var st in player.StatusList)
        {
            if (st.StatusId == 0) continue;
            statuses.Add(st.StatusId);
            if (st.RemainingTime > 0 && st.RemainingTime < StepRules.StatusRenewBelow.TotalSeconds) expiring.Add(st.StatusId);
        }
        return new StepSnapshot
        {
            Quest = q.Quest,
            Accepted = accepted,
            Completed = completed,
            Sequence = sequence,
            Variables = vars,
            AcceptJob = work != null ? work->AcceptClassJob : 0u,
            Job = player.ClassJob.RowId,
            Territory = Services.ClientState.TerritoryType,
            Player = player.Position,
            Mounted = Services.Condition[ConditionFlag.Mounted],
            Mount = Services.Condition[ConditionFlag.Mounted] ? chara->Mount.MountId : 0u,
            Flying = Services.Condition[ConditionFlag.InFlight],
            Busy = occupied,
            InCombat = inCombat,
            Fighter = Combat.BasicCombo.For(player.ClassJob.RowId) != null,
            ScriptRead = read,
            Remembered = remembered,
            Todos = todos,
            Objects = objects,
            Markers = Markers(q.Quest),
            Hostiles = accepted ? Mobs(q, player) : new List<StepEnemy>(),
            Items = items,
            Statuses = statuses,
            Expiring = expiring,
        };
    }

    /// <summary>
    /// What the quest's script said at the last calm look: its to-do lines, its objects, and which mobs its handler
    /// held (with the icon it gave each), together with the quest's state then. In combat the script isn't asked, so
    /// the marks on mobs come from here, and so do the lines and objects while the state hasn't changed.
    /// </summary>
    private sealed record CalmLook(string Progress, List<TodoProgress> Todos, List<StepObject> Objects, Dictionary<ulong, uint> Marks);

    private static readonly Dictionary<uint, CalmLook> CalmLooks = new();

    private static string Progress(byte sequence, byte[] vars) => $"{sequence}|{Convert.ToHexString(vars)}";

    /// <summary>Remembered objects as they are now: those still loaded, where they stand, whether they can be targeted. No script is asked.</summary>
    private static List<StepObject> Refreshed(List<StepObject> objects)
    {
        var list = new List<StepObject>(objects.Count);
        foreach (var o in objects)
            if (Services.Objects.SearchById(o.Id) is { } live && live.BaseId == o.BaseId)
                list.Add(o with { Position = live.Position, Targetable = live.IsTargetable });
        return list;
    }

    /// <summary>
    /// The mobs around, from the object table (safe whatever the game is doing: only their own fields are read): every
    /// hostile one within <see cref="EnemyRules.ViewRange"/>, and the quest's own kinds even when not hostile (one that
    /// was subdued waits for the quest's item). What the quest's handler said of each comes from the last calm look.
    /// </summary>
    private static List<StepEnemy> Mobs(QuestScript q, IPlayerCharacter me)
    {
        var list = new List<StepEnemy>();
        var marks = CalmLooks.TryGetValue(q.Quest, out var calm) ? calm.Marks : null;
        var kinds = q.Enemies.Count == 0 ? null : q.Enemies.Select(e => e.Base).ToHashSet();
        var buddy = Services.Buddies.CompanionBuddy?.GameObject?.GameObjectId ?? 0;
        foreach (var o in Services.Objects)
        {
            if (o is not IBattleNpc n || n.BattleNpcKind != BattleNpcSubKind.Combatant || n.IsDead || n.CurrentHp == 0) continue;
            if (Vector3.Distance(n.Position, me.Position) > EnemyRules.ViewRange) continue;
            var flags = n.StatusFlags;
            var hostile = flags.HasFlag(StatusFlags.Hostile);
            uint icon = 0;
            var held = marks != null && marks.TryGetValue(n.GameObjectId, out icon);
            if (!hostile && !held && kinds?.Contains(n.BaseId) != true) continue;
            var chara = (Character*)n.Address;
            var targetId = n.TargetObjectId;
            var target = targetId is 0 or 0xE000_0000 ? EnemyTarget.None
                : targetId == me.GameObjectId ? EnemyTarget.You
                : buddy != 0 && targetId == buddy ? EnemyTarget.YourChocobo
                : Services.Objects.SearchById(targetId) is IPlayerCharacter ? EnemyTarget.OtherPlayer
                : EnemyTarget.Other;
            list.Add(new StepEnemy(n.GameObjectId, n.BaseId, n.NameId, n.Name.TextValue, n.Position)
            {
                Radius = n.HitboxRadius,
                Hostile = hostile,
                Targetable = n.IsTargetable,
                InCombat = flags.HasFlag(StatusFlags.InCombat),
                Target = target,
                Weakened = (chara->ActorControlFlags & SubduedFlag) != 0,
                Fate = chara->FateId,
                Icon = icon,
                Held = held,
                Drawn = chara->NamePlateIconId,
                Hp = n.MaxHp > 0 ? (int)(n.CurrentHp * 100 / n.MaxHp) : 100,
                Tagger = chara->CombatTaggerId,
            });
        }
        return list;
    }

    /// <summary>
    /// <c>Character.ActorControlFlags</c> bit of a quest mob that was subdued: its attacks stop, it takes no more damage
    /// and it waits for the quest's item (FFXIVClientStructs' note on the field; battle-society research).
    /// </summary>
    private const byte SubduedFlag = 0x40;

    /// <summary>The loaded mob for an id, when it's still there.</summary>
    public static IBattleNpc? FindEnemy(ulong gameObjectId) => gameObjectId == 0 ? null : Services.Objects.SearchById(gameObjectId) as IBattleNpc;

    /// <summary>The mob was subdued: it takes no more damage and waits for the quest's item.</summary>
    public static bool Subdued(IGameObject mob) => mob.Address != 0 && (((Character*)mob.Address)->ActorControlFlags & SubduedFlag) != 0;

    // ------------------------------------------------------------------ enemies, for the logs

    /// <summary>The quest's enemies as its data has them, in one line: kind (and name), sequence, what's done with it, where it's from.</summary>
    public static string EnemyData(QuestScript q)
        => q.Enemies.Count == 0 ? "none" : string.Join("; ", q.Enemies.GroupBy(e => (e.Base, e.Name, e.Sequence, e.Field, e.Task)).Select(g =>
            $"{g.Key.Base}{(g.Key.Name != 0 ? $"/{g.Key.Name}" : "")} seq {g.Key.Sequence} {g.Key.Task}"
            + (g.Key.Field ? " field" : g.Any(e => e.Spawn != null) ? $" at {g.Count(e => e.Spawn != null)} spawn point{(g.Count(e => e.Spawn != null) == 1 ? "" : "s")}" : "")));

    /// <summary>
    /// A mob with everything the enemy filter goes by, and the filter's verdict: kind and name ids, how far, health,
    /// hostile, the quest's own icon and whether its handler holds it (as of the last calm look), the icon drawn now,
    /// its FATE, subdued, in combat, whom it targets and who first hit it (never a name). For the run's log.
    /// </summary>
    /// <param name="m">What the run remembers (the enemies it fought), when it's the run that asks.</param>
    public static string EnemyLine(QuestScript q, StepSnapshot s, StepEnemy e, StepMemory? m = null)
        => $"\"{e.Name}\" {e.BaseId}/{e.NameId} {Vector3.Distance(e.Position, s.Player):0} y, health {e.Hp}%, {EnemyFacts(q, s, e, m)}";

    /// <summary>What the filter goes by for a mob, and its verdict; without its distance and health (those change all the time).</summary>
    private static string EnemyFacts(QuestScript q, StepSnapshot s, StepEnemy e, StepMemory? m)
    {
        var verdict = EnemyRules.Judge(q, s, e, m);
        var me = Services.Objects.LocalPlayer?.GameObjectId ?? 0;
        var tag = e.Tagger is 0 or 0xE000_0000 ? "nobody" : e.Tagger == me ? "you" : "another";
        var target = e.Target switch
        {
            EnemyTarget.You => "you",
            EnemyTarget.YourChocobo => "your chocobo",
            EnemyTarget.OtherPlayer => "another player",
            EnemyTarget.Other => "something else",
            _ => "nobody",
        };
        return $"{(e.Hostile ? "hostile" : "not hostile")}{(e.Targetable ? "" : ", untargetable")}, icon {e.Icon}, drawn {e.Drawn}, {(e.Held ? "held" : "not held")}, fate {e.Fate}"
               + $"{(e.Weakened ? ", SUBDUED" : "")}, {(e.InCombat ? "in combat" : "idle")}, targets {target}, first hit by {tag} => {verdict.Verdict} ({verdict.Why})";
    }

    /// <summary>
    /// The mobs within <paramref name="within"/> yalms, nearest first. Mobs of one kind and name that the filter sees
    /// alike are one entry (with how far each is), <paramref name="most"/> entries at most. <paramref name="key"/>
    /// changes when an entry comes, goes or changes in what the filter goes by, not with distance, health or how many
    /// of an entry there are: a line is written only then, so a herd wandering by doesn't fill the log.
    /// </summary>
    public static string EnemiesSeen(QuestScript q, StepSnapshot s, float within, int most, out string key, StepMemory? m = null)
    {
        var groups = s.Hostiles.Where(e => Vector3.Distance(e.Position, s.Player) <= within).OrderBy(e => Vector3.DistanceSquared(e.Position, s.Player))
            .GroupBy(e => (e.Name, e.BaseId, e.NameId, Facts: EnemyFacts(q, s, e, m))).ToList();
        key = string.Join("|", groups.Select(g => $"{g.Key.BaseId}/{g.Key.NameId}:{g.Key.Facts}").OrderBy(x => x, StringComparer.Ordinal));
        if (groups.Count == 0) return $"(none within {within:0} yalms)";
        return string.Join("; ", groups.Take(most).Select(g =>
                   $"{(g.Count() > 1 ? $"{g.Count()}x " : "")}\"{g.Key.Name}\" {g.Key.BaseId}/{g.Key.NameId} at {string.Join("/", g.Take(6).Select(e => Vector3.Distance(e.Position, s.Player).ToString("0")))} y, "
                   + $"health {g.First().Hp}%, {g.Key.Facts}"))
               + (groups.Count > most ? $"; and {groups.Count - most} more kinds" : "");
    }

    /// <summary>The character has the status, and it lasts at least this long more (or doesn't run out).</summary>
    public static bool StatusLasts(uint statusId, TimeSpan atLeast)
    {
        if (Services.Objects.LocalPlayer is not { } me) return false;
        foreach (var st in me.StatusList)
            if (st.StatusId == statusId) return st.RemainingTime <= 0 || st.RemainingTime >= atLeast.TotalSeconds;
        return false;
    }

    /// <summary>
    /// The quest handler's objects that are loaded: what they are, where, the icon this quest gives them (and the one
    /// the game draws, for the log), whether the quest's script counts them active, whether they can be targeted. Only
    /// objects still in the game's object table are asked about: a pointer the handler still holds to one that was
    /// unloaded isn't touched. A hostile mob the handler holds isn't one of these objects (it would be "talked to"):
    /// it goes into <paramref name="mobs"/> with the icon the quest gives it, for the enemy filter.
    /// </summary>
    private static List<StepObject> Objects(QuestEventHandler* handler, Dictionary<ulong, uint> mobs)
    {
        var list = new List<StepObject>();
        var table = Services.Objects;
        var present = new HashSet<nint>();
        for (var i = 0; i < table.Length; i++)
            if (table.GetObjectAddress(i) is var address && address != 0) present.Add(address);
        foreach (var p in handler->EventObjects)
        {
            var go = p.Value;
            if (go == null || !present.Contains((nint)go)) continue;
            var kind = go->ObjectKind switch
            {
                CsObjectKind.EventNpc or CsObjectKind.BattleNpc => StepObjectKind.Npc,
                CsObjectKind.EventObj => StepObjectKind.Object,
                _ => StepObjectKind.Other,
            };
            // The quest's own icon for it: the drawn one comes from every handler (a giver showing "dailies available").
            var icon = handler->GetNameplateIconForObject(go);
            if (go->ObjectKind == CsObjectKind.BattleNpc)
            {
                mobs[go->GetGameObjectId()] = icon;
                if (((Character*)go)->IsHostile) continue;
            }
            list.Add(new StepObject(go->GetGameObjectId(), go->BaseId, kind, go->NameString, new Vector3(go->Position.X, go->Position.Y, go->Position.Z),
                icon, handler->IsActive(go), go->GetIsTargetable())
            {
                Drawn = go->NamePlateIconId,
                Instance = go->LayoutId,
            });
        }
        return list;
    }

    /// <summary>The quest's map markers, in any zone. The map keys them by the quest's short id or its row (checked both ways).</summary>
    public static List<StepMarker> Markers(uint questRow)
    {
        var list = new List<StepMarker>();
        var map = GameMap.Instance();
        if (map == null) return list;
        foreach (ref var info in map->QuestMarkers)
        {
            if (info.ObjectiveId != questRow - 65536 && info.ObjectiveId != questRow) continue;
            foreach (var d in info.MarkerData.AsSpan())
                list.Add(new StepMarker(d.TerritoryTypeId, new Vector3(d.Position.X, d.Position.Y, d.Position.Z), d.Radius, d.IconId, d.LevelId, d.DataId));
        }
        return list;
    }

    /// <summary>Raw map-marker slots for the watch (to check how the map keys quests): objective id, label, marker count.</summary>
    public static List<(uint Objective, string Label, int Markers)> MarkerSlots()
    {
        var list = new List<(uint, string, int)>();
        var map = GameMap.Instance();
        if (map == null) return list;
        foreach (ref var info in map->QuestMarkers)
            if (info.ObjectiveId != 0) list.Add((info.ObjectiveId, info.Label.ToString(), (int)info.MarkerData.LongCount));
        return list;
    }

    /// <summary>How many of an item the character holds: key items in their own bag, other items NQ and HQ in the bags.</summary>
    /// <summary>High-quality copies of an item in the bags.</summary>
    public static int ItemCountHq(uint itemId)
    {
        var inv = InventoryManager.Instance();
        if (inv == null || itemId == 0 || itemId >= 2_000_000) return 0;
        return inv->GetInventoryItemCount(itemId % 1_000_000, true, false, false, 0);
    }

    public static int ItemCount(uint itemId)
    {
        var inv = InventoryManager.Instance();
        if (inv == null || itemId == 0) return 0;
        if (itemId >= 2_000_000) return inv->GetItemCountInContainer(itemId, InventoryType.KeyItems, false, 0);
        return inv->GetInventoryItemCount(itemId, false, false, false, 0) + inv->GetInventoryItemCount(itemId, true, false, false, 0);
    }

    // ------------------------------------------------------------------ acts

    /// <summary>The loaded game object for an id, when it's still there.</summary>
    public static IGameObject? Find(ulong gameObjectId) => gameObjectId == 0 ? null : Services.Objects.SearchById(gameObjectId);

    /// <summary>An object's place in the zone's layout (0 when it has none); see <see cref="StepObject.Instance"/>.</summary>
    public static uint Instance(IGameObject o) => o.Address == 0 ? 0 : ((GameObjectStruct*)o.Address)->LayoutId;

    /// <summary>Targets an object (as a click on it does).</summary>
    public static bool Target(IGameObject o)
    {
        var ts = TargetSystem.Instance();
        if (ts == null) return false;
        ts->Target = (GameObjectStruct*)o.Address;
        return true;
    }

    /// <summary>Talks to (interacts with) an object of the quest.</summary>
    public static bool Talk(IGameObject o, bool travels = false) => Interact.With(o, travels);

    /// <summary>Says the quest's phrase in /say, as typed in the chat box. Once per call; the caller keeps the count.</summary>
    public static bool Say(string phrase)
    {
        var text = Chat.SanitiseText(phrase).Trim();
        if (text.Length == 0 || text.Length > 400) return false;
        Chat.SendMessage("/say " + text);
        return true;
    }

    /// <summary>Does an emote at an object, as typing its command with the object targeted does.</summary>
    public static bool Emote(IGameObject target, string command)
    {
        if (command.Length < 2 || command[0] != '/' || !Target(target)) return false;
        Chat.SendMessage(command);
        return true;
    }

    /// <summary>
    /// Uses a key item: on the target (targeted, then the item, as the key item menu does), on the ground at the target
    /// or a spot, or on yourself; only when the game says it can be used now. Why the game refused comes back in
    /// <paramref name="why"/> (its action status, 0 is fine). Never thrown at no place (a spot of all zeros).
    /// </summary>
    public static bool UseKeyItem(uint itemId, ItemUse use, IGameObject? target, Vector3? spot, out uint why)
    {
        why = 0;
        var am = ActionManager.Instance();
        if (am == null) return false;
        switch (use)
        {
            case ItemUse.Ground:
            {
                if ((target?.Position ?? spot) is not { } at || at == Vector3.Zero) return false;
                why = am->GetActionStatus(ActionType.EventItem, itemId);
                return why == 0 && am->UseActionLocation(ActionType.EventItem, itemId, 0xE000_0000, &at);
            }
            case ItemUse.Target when target != null:
            {
                Target(target);
                why = am->GetActionStatus(ActionType.EventItem, itemId, target.GameObjectId);
                if (why != 0) return false;
                var agent = AgentInventoryContext.Instance();
                return agent != null && agent->UseItem(itemId) is 0 or 1;
            }
            case ItemUse.Target:
                return false;
            default:
                why = am->GetActionStatus(ActionType.EventItem, itemId);
                return why == 0 && am->UseAction(ActionType.EventItem, itemId);
        }
    }

    /// <summary>Uses the quest mount's action: on the target, around the rider (null target), or thrown at a place on the ground (<paramref name="at"/>).</summary>
    public static bool UseMountAction(uint actionId, IGameObject? target, out uint why, Vector3? at = null)
    {
        var am = ActionManager.Instance();
        why = 0;
        if (am == null) return false;
        if (at is { } place)
        {
            // Never thrown at no place (a spot of all zeros).
            if (place == Vector3.Zero) return false;
            why = am->GetActionStatus(ActionType.Action, actionId);
            return why == 0 && am->UseActionLocation(ActionType.Action, actionId, 0xE000_0000, &place);
        }
        if (target != null)
        {
            Target(target);
            why = am->GetActionStatus(ActionType.Action, actionId, target.GameObjectId);
            return why == 0 && am->UseAction(ActionType.Action, actionId, target.GameObjectId);
        }
        why = am->GetActionStatus(ActionType.Action, actionId);
        return why == 0 && am->UseAction(ActionType.Action, actionId);
    }

    /// <summary>Turns the character to face a spot (client side, as the fishing step turns to the water).</summary>
    public static float Face(Vector3 spot)
    {
        var me = Services.Objects.LocalPlayer;
        if (me == null) return 0;
        var facing = MathF.Atan2(spot.X - me.Position.X, spot.Z - me.Position.Z);
        ((GameObjectStruct*)me.Address)->SetRotation(facing);
        return facing;
    }

    /// <summary>The character faces the spot, within about 10 degrees.</summary>
    public static bool Facing(Vector3 spot)
    {
        var me = Services.Objects.LocalPlayer;
        if (me == null) return false;
        var want = MathF.Atan2(spot.X - me.Position.X, spot.Z - me.Position.Z);
        var diff = MathF.Abs(MathF.IEEERemainder(me.Rotation - want, MathF.Tau));
        return diff < 0.18f;
    }

    /// <summary>General action "Dismount" (from the air it lands first).</summary>
    private const uint Dismount = 23;

    public static bool GetOff()
    {
        var am = ActionManager.Instance();
        return am != null && am->GetActionStatus(ActionType.GeneralAction, Dismount) == 0 && am->UseAction(ActionType.GeneralAction, Dismount);
    }

    // ------------------------------------------------------------------ windows

    /// <summary>The open Yes/No question's text, or null.</summary>
    public static string? Question()
        => Interact.Ready("SelectYesno") is var a && a != null ? new AddonMaster.SelectYesno((nint)a).Text : null;

    /// <summary>Answers Yes, as a click on the Yes button (only when it's enabled).</summary>
    public static bool Yes()
    {
        if (Interact.Ready("SelectYesno") is var a && a == null) return false;
        var yn = (AddonSelectYesno*)a;
        if (yn->YesButton == null || !yn->YesButton->IsEnabled) return false;
        new AddonMaster.SelectYesno((nint)a) { RespectDisabledButtons = true }.Yes();
        return true;
    }

    /// <summary>The open menu: its prompt (or title) and entries; null when none is open.</summary>
    public static (string Prompt, List<string> Entries, Action<int> Select)? Menu()
    {
        if (Interact.Ready("SelectString") is var s && s != null)
        {
            var m = new AddonMaster.SelectString((nint)s);
            var entries = m.Entries;
            return (m.Text, entries.Select(e => e.Text).ToList(), i => entries[i].Select());
        }
        if (Interact.Ready("SelectIconString") is var icon && icon != null)
        {
            var m = new AddonMaster.SelectIconString((nint)icon);
            var entries = m.Entries;
            var title = icon->AtkValuesCount > 0 && icon->AtkValues[0].Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString
                ? icon->AtkValues[0].GetValueAsString() : "";
            return (title, entries.Select(e => e.Text).ToList(), i => entries[i].Select());
        }
        return null;
    }

    /// <summary>
    /// Picks a daily from a giver's list ("Daily Quests") by its name in the client's language. False while the list
    /// isn't open, the game is between script steps, or the quest isn't in it (<paramref name="missing"/>).
    /// </summary>
    public static bool PickDaily(string questName, out bool missing)
    {
        missing = false;
        if (DeliveryWindow.SceneBusy || Menu() is not { } menu) return false;
        var want = StepRules.Normal(questName);
        var at = menu.Entries.FindIndex(e => StepRules.Normal(e).Equals(want, StringComparison.CurrentCultureIgnoreCase));
        if (at < 0) { missing = true; return false; }
        menu.Select(at);
        return true;
    }

    /// <summary>Accepts the quest the journal window offers (a click on Accept).</summary>
    public static bool Accept()
    {
        if (Interact.Ready("JournalAccept") is var a && a == null) return false;
        var j = (AddonJournalAccept*)a;
        if (j->AcceptButton == null || !j->AcceptButton->IsEnabled) return false;
        new AddonMaster.JournalAccept((nint)a).Accept();
        return true;
    }

    /// <summary>The title of the quest the journal window offers (its title text, as shown), or null when none is offered.</summary>
    public static string? OfferTitle()
    {
        if (Interact.Ready("JournalAccept") is var a && a == null) return null;
        var j = (AddonJournalAccept*)a;
        return j->QuestTitleText == null ? null : j->QuestTitleText->NodeText.ToString();
    }

    /// <summary>Declines a quest offer that isn't the run's (a click on Decline, when it can be pressed).</summary>
    public static bool Decline()
    {
        if (Interact.Ready("JournalAccept") is var a && a == null) return false;
        var j = (AddonJournalAccept*)a;
        if (j->DeclineButton == null || !j->DeclineButton->IsEnabled) return false;
        new AddonMaster.JournalAccept((nint)a).Decline();
        return true;
    }

    /// <summary>Whose the reward window is, as far as its texts tell.</summary>
    public enum ResultOwner { Unknown, This, Other }

    /// <summary>
    /// Whose the open reward window is: this quest's when one of its texts is the quest's title; another quest's when
    /// one is the title of another quest in the journal (<paramref name="shown"/>); otherwise unknown.
    /// </summary>
    public static ResultOwner ResultFor(QuestScript q, out string shown)
    {
        shown = "";
        if (Interact.Ready("JournalResult") is var a && a == null) return ResultOwner.Unknown;
        var texts = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        for (var i = 0; i < a->AtkValuesCount; i++)
        {
            var v = a->AtkValues[i];
            if (v.Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString && StepRules.Normal(v.GetValueAsString()) is { Length: > 0 } t)
                texts.Add(t);
        }
        for (var i = 0; i < a->UldManager.NodeListCount; i++)
        {
            var node = a->UldManager.NodeList[i];
            if (node == null || node->Type != NodeType.Text) continue;
            if (StepRules.Normal(Dalamud.Utility.Utf8StringExtensions.ExtractText(((AtkTextNode*)node)->NodeText)) is { Length: > 0 } t) texts.Add(t);
        }
        var mine = StepRules.Normal(q.Name);
        if (mine.Length > 0 && texts.Contains(mine)) return ResultOwner.This;
        var qm = QuestManager.Instance();
        if (qm == null) return ResultOwner.Unknown;
        var sheet = Services.Data.GetExcelSheet<Quest>();
        foreach (ref var work in qm->NormalQuests)
        {
            if (work.QuestId == 0 || work.QuestId + 65536u == q.Quest) continue;
            var name = StepRules.Normal(sheet.GetRowOrDefault(work.QuestId + 65536u)?.Name.ExtractText() ?? "");
            if (name.Length == 0 || !texts.Contains(name)) continue;
            shown = name;
            return ResultOwner.Other;
        }
        return ResultOwner.Unknown;
    }

    /// <summary>Completes the quest in the reward window: a click on its Complete button, only when it can be pressed.</summary>
    public static bool PressComplete()
    {
        if (Interact.Ready("JournalResult") is var a && a == null) return false;
        var j = (AddonJournalResult*)a;
        if (j->CompleteButton == null || !j->CompleteButton->IsEnabled) return false;
        new AddonMaster.JournalResult((nint)a).Complete();
        return true;
    }

    /// <summary>The reward window's slots of rewards to choose from (five at most).</summary>
    private const int RewardSlots = 5;

    /// <summary>
    /// The rewards the reward window asks you to choose from, in the order listed; empty when there's no choice. Each
    /// value is read only when it's there and of the expected type.
    /// </summary>
    /// <remarks>
    /// Where the window keeps them, as facts from TextAdvance (no licence; facts only, own code): AtkValues 82–86 hold the
    /// items (above 1,000,000 is high quality), 88–92 their icons (26001 is a sack of gil), 93–97 the amounts and
    /// 98–102 the names. What a vendor pays is the item's <c>PriceLow</c> (game data).
    /// </remarks>
    public static List<RewardOption> RewardOptions()
    {
        var list = new List<RewardOption>();
        if (Interact.Ready("JournalResult") is var a && a == null) return list;
        var items = Services.Data.GetExcelSheet<Item>();
        for (var i = 0; i < RewardSlots; i++)
        {
            if (AtkNumber(a, 82 + i) is not { } raw || raw == 0) continue;
            var hq = raw > 1_000_000;
            var id = hq ? raw - 1_000_000 : raw;
            var icon = AtkNumber(a, 88 + i) ?? 0;
            var amount = AtkNumber(a, 93 + i) ?? 1;
            var name = AtkText(a, 98 + i) is { Length: > 0 } shown ? shown : ItemName(id);
            var price = id == RewardChoice.GilItem ? 0 : items.GetRowOrDefault(id)?.PriceLow ?? 0;
            list.Add(new RewardOption(i, id, hq, icon, amount, name, price));
        }
        return list;
    }

    /// <summary>An AtkValue that's a number (its low 32 bits), or null when it's missing or something else.</summary>
    private static uint? AtkNumber(AtkUnitBase* a, int index)
    {
        if (index < 0 || index >= a->AtkValuesCount || a->AtkValues == null) return null;
        var v = a->AtkValues[index];
        return v.Type is AtkValueType.Int or AtkValueType.UInt or AtkValueType.Int64 or AtkValueType.UInt64 ? v.UInt : null;
    }

    /// <summary>An AtkValue that's text, or null when it's missing or something else.</summary>
    private static string? AtkText(AtkUnitBase* a, int index)
    {
        if (index < 0 || index >= a->AtkValuesCount || a->AtkValues == null) return null;
        var v = a->AtkValues[index];
        return v.Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString ? StepRules.Normal(v.GetValueAsString()) : null;
    }

    /// <summary>
    /// Picks a reward in the reward window as a click on it does: the reward canvas's own click event for the slot.
    /// Never while the game has scene tasks pending.
    /// </summary>
    /// <remarks>
    /// The event (a mouse click, parameter 7 + the slot, sent to the window's journal canvas with empty event data) is a
    /// fact from TextAdvance (no licence; facts only, own code). The canvas is <c>AddonJournalResult</c>'s
    /// <c>AtkComponentJournalCanvas268</c> (FFXIVClientStructs, MIT).
    /// </remarks>
    public static bool PickReward(int slot)
    {
        if (slot is < 0 or >= RewardSlots || DeliveryWindow.SceneBusy) return false;
        if (Interact.Ready("JournalResult") is var a && a == null) return false;
        var canvas = ((AddonJournalResult*)a)->AtkComponentJournalCanvas268;
        if (canvas == null) return false;
        var evt = stackalloc byte[64];
        var data = stackalloc byte[64];
        new Span<byte>(evt, 64).Clear();
        new Span<byte>(data, 64).Clear();
        canvas->AtkComponentBase.AtkEventListener.ReceiveEvent(AtkEventType.MouseClick, 7 + slot, (AtkEvent*)evt, (AtkEventData*)data);
        return true;
    }

    /// <summary>The quest's sequence as the journal holds it (no script is asked); null when it isn't in the journal.</summary>
    public static byte? Sequence(uint questRow)
    {
        var qm = QuestManager.Instance();
        if (qm == null) return null;
        var id = (ushort)(questRow - 65536);
        if (!qm->IsQuestAccepted(id)) return null;
        var work = qm->GetQuestById(id);
        return work == null ? null : work->Sequence;
    }

    public enum TradeStep { Wait, Opened, Picked, HandedOver, Missing }

    /// <summary>
    /// One step of the hand-over window (<c>Request</c>) for a quest: hand over once every slot is filled; otherwise
    /// open the next slot's picker and pick its item, normal quality first (high quality when the slot wants it, or
    /// when that's all there is; the game then asks, and the question is on the known list). Key items are handed over
    /// the same way. <paramref name="slot"/> is the next slot to fill, kept by the caller.
    /// </summary>
    /// <param name="settled">
    /// The window has been open for a moment. Before that nothing is pressed: in its first frames its Hand Over button
    /// can still read as enabled with nothing in the slots.
    /// </param>
    /// <param name="forceOpen">
    /// Open the slot's picker even though the game still has a slot selected: a picker closed without a pick (an item
    /// not found) leaves the selection behind, and the next window would wait on it for good.
    /// </param>
    /// <param name="hqItem">
    /// An item to hand over high quality when there's a choice (the quest counts only high-quality copies of what it had
    /// made, though the window takes either); 0 for none: normal quality first then.
    /// </param>
    public static TradeStep Trade(ref int slot, bool settled, bool forceOpen, uint hqItem, out string detail)
    {
        detail = "";
        if (!settled) return TradeStep.Wait;
        if (DeliveryWindow.CanHandOver)
        {
            DeliveryWindow.HandOver();
            return TradeStep.HandedOver;
        }
        if (DeliveryWindow.SceneBusy) return TradeStep.Wait;
        var trade = AgentNpcTrade.Instance();
        var ui = UIState.Instance();
        if (trade == null || ui == null) return TradeStep.Wait;
        var requests = ui->NpcTrade.Requests;
        if (Interact.Ready("ContextIconMenu") is var menu && menu != null)
        {
            var at = trade->SelectedTurnInSlot;
            if (at < 0 || at >= requests.Count) { Interact.Close("ContextIconMenu"); return TradeStep.Wait; }
            var want = requests.Items[at];
            var wanted = StepRules.BaseItem(want.ItemId);
            var pick = -1;
            var pickHq = true;
            var offered = new List<uint>();
            for (var i = 0; i < trade->SelectedTurnInSlotItemOptions && i < 140; i++)
            {
                var item = trade->SelectedTurnInSlotItemOptionValues[i].Value;
                if (item == null) continue;
                offered.Add(item->ItemId);
                if (item->GetBaseItemId() != wanted && StepRules.BaseItem(item->ItemId) != wanted) continue;
                var hq = item->IsHighQuality();
                if (want.WantHQ && !hq) continue;
                var preferHq = hqItem != 0 && wanted == hqItem;
                if (pick < 0 || (preferHq ? !pickHq && hq : pickHq && !hq)) { pick = i; pickHq = hq; }
            }
            if (pick < 0)
            {
                detail = $"no {ItemName(want.ItemId)} ({want.ItemId}{(want.WantHQ ? ", high quality" : "")}) to hand over for slot {at + 1}; the picker offers [{string.Join(",", offered)}]";
                Interact.Close("ContextIconMenu");
                return TradeStep.Missing;
            }
            Callback.Fire(menu, true, 0, pick, 1021003, 0, 0);
            detail = $"slot {at + 1}: {ItemName(want.ItemId)}{(pickHq ? " (high quality)" : "")}";
            slot = at + 1;
            return TradeStep.Picked;
        }
        if (Interact.Ready("Request") is var request && request != null && (trade->SelectedTurnInSlot < 0 || forceOpen))
        {
            if (slot >= requests.Count) return TradeStep.Wait;
            Callback.Fire(request, false, 2, slot, 0, 0);
            detail = $"opening slot {slot + 1} of {requests.Count}";
            return TradeStep.Opened;
        }
        return TradeStep.Wait;
    }

    /// <summary>Closes the hand-over window (its Cancel button, when it can be pressed) when the items aren't there.</summary>
    public static bool CancelTrade()
    {
        if (Interact.Ready("Request") is var a && a == null) return false;
        var r = (AddonRequest*)a;
        if (r->CancelButton == null || !r->CancelButton->IsEnabled) return false;
        new AddonMaster.Request((nint)a).Cancel();
        return true;
    }

    public static bool TradeOpen => Interact.IsOpen("Request") || Interact.IsOpen("ContextIconMenu");

    /// <summary>The Yes/No questions the engine may answer Yes to, from the game's own texts: the high-quality hand-over and the trade confirmation.</summary>
    public static IReadOnlyList<string> KnownQuestions => known.Value;

    private static readonly Lazy<IReadOnlyList<string>> known = new(() => AddonTexts(102434, 5450, 11514));

    /// <summary>The cutscene's "Skip cutscene?" question (Addon 281, 5882).</summary>
    public static IReadOnlyList<string> SkipQuestions => skip.Value;

    private static readonly Lazy<IReadOnlyList<string>> skip = new(() => AddonTexts(281, 5882));

    /// <summary>
    /// A menu's own way out, in the client's language: "Nothing" (Addon 102441, the last entry of a giver's daily list;
    /// also 622, 6350, 6750) and "Cancel" (Addon 2).
    /// </summary>
    public static IReadOnlyList<string> LeaveTexts => leave.Value;

    private static readonly Lazy<IReadOnlyList<string>> leave = new(() => AddonTexts(102441, 622, 6350, 6750, 2));

    /// <summary>The accept confirmation "Commence “…”?" (Addon 102445).</summary>
    public static string CommenceQuestion => commence.Value;

    private static readonly Lazy<string> commence = new(() => AddonTexts(102445).FirstOrDefault() ?? "");

    private static IReadOnlyList<string> AddonTexts(params uint[] rows)
    {
        var sheet = Services.Data.GetExcelSheet<Addon>();
        return rows.Select(r => sheet.GetRowOrDefault(r)?.Text.ExtractText() ?? "").Where(t => t.Length > 0).Distinct().ToList();
    }

    public static string ItemName(uint id)
    {
        if (id >= 2_000_000) return Services.Data.GetExcelSheet<EventItem>().GetRowOrDefault(id)?.Name.ExtractText() ?? $"item {id}";
        id %= 1_000_000;
        return Services.Data.GetExcelSheet<Item>().GetRowOrDefault(id)?.Name.ExtractText() ?? $"item {id}";
    }
}
