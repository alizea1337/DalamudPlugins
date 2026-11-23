using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Conditions;
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
/// </remarks>
internal static unsafe class QuestSteps
{
    // ------------------------------------------------------------------ the quest's data

    private static readonly Dictionary<uint, QuestScript?> Scripts = new();

    /// <summary>
    /// The text rows holding the /say phrase of the quests that have no "SAYTODO" row (read from their scripts:
    /// Moogles 2295 compares with SYSTEM_000_019, the Ixal password quests with SYSTEM_100_062).
    /// </summary>
    private static readonly Dictionary<uint, string> SayKeys = new()
    {
        [2295] = "SYSTEM_000_019",
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
            var actionId = facts.Arg("ACTION0") ?? 0;
            var act = actionId != 0 ? Services.Data.GetExcelSheet<SheetAction>().GetRowOrDefault(actionId) : null;
            var line = act is { CastType: 4 or 12 };
            var self = act is { } a && (a.Range == 0 || SelfCentred(a));
            var canFly = Services.Data.GetExcelSheet<Mount>().GetRowOrDefault(mountId)?.FlyingCondition.RowId is > 0;
            mount = new MountFact(mountId, actionId, self ? 0 : act?.Range ?? 0, act?.EffectRange ?? 0, line, canFly);
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

    /// <summary>Casting, in an event or a cutscene, or between areas: the engine starts nothing now.</summary>
    public static bool Busy
        => Services.Condition[ConditionFlag.Casting] || Services.Condition[ConditionFlag.Casting87] || Services.Condition[ConditionFlag.OccupiedInEvent]
           || Services.Condition[ConditionFlag.OccupiedInQuestEvent] || Services.Condition[ConditionFlag.OccupiedInCutSceneEvent]
           || Services.Condition[ConditionFlag.BetweenAreas] || Services.Condition[ConditionFlag.BetweenAreas51]
           || Services.Condition[ConditionFlag.WatchingCutscene] || Services.Condition[ConditionFlag.WatchingCutscene78]
           || DeliveryWindow.SceneBusy;

    public static bool InCutscene
        => Services.Condition[ConditionFlag.OccupiedInCutSceneEvent] || Services.Condition[ConditionFlag.WatchingCutscene]
           || Services.Condition[ConditionFlag.WatchingCutscene78];

    /// <summary>Between areas (a zone change or a warp under way): nothing is read from the quest's script then.</summary>
    public static bool Loading => Services.Condition[ConditionFlag.BetweenAreas] || Services.Condition[ConditionFlag.BetweenAreas51];

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

        // The quest's script isn't asked anything during a zone change.
        var handler = accepted && !Loading ? Handler(q.Quest) : null;
        var todos = new List<TodoProgress>();
        if (handler != null)
        {
            var me = (BattleChara*)player.Address;
            foreach (var t in q.TodosAt(sequence))
            {
                uint a0 = 0, a1 = 0, a2 = 0;
                handler->GetTodoArgs(me, (byte)t.Index, &a0, &a1, &a2);
                todos.Add(new TodoProgress(t.Index, handler->IsTodoChecked(me, (byte)t.Index), a0, a1, a2));
            }
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

        var chara = (Character*)player.Address;
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
            Busy = Busy,
            Todos = todos,
            Objects = handler != null ? Objects(handler) : new List<StepObject>(),
            Markers = Markers(q.Quest),
            Items = items,
            Statuses = player.StatusList.Select(s => s.StatusId).Where(s => s != 0).ToHashSet(),
        };
    }

    /// <summary>
    /// The quest handler's objects that are loaded: what they are, where, the icon over them, whether the quest's
    /// script counts them active, whether they can be targeted.
    /// </summary>
    private static List<StepObject> Objects(QuestEventHandler* handler)
    {
        var list = new List<StepObject>();
        foreach (var p in handler->EventObjects)
        {
            var go = p.Value;
            if (go == null) continue;
            var kind = go->ObjectKind switch
            {
                CsObjectKind.EventNpc or CsObjectKind.BattleNpc => StepObjectKind.Npc,
                CsObjectKind.EventObj => StepObjectKind.Object,
                _ => StepObjectKind.Other,
            };
            var icon = go->NamePlateIconId;
            if (icon == 0) icon = handler->GetNameplateIconForObject(go);
            list.Add(new StepObject(go->GetGameObjectId(), go->BaseId, kind, go->NameString, new Vector3(go->Position.X, go->Position.Y, go->Position.Z),
                icon, handler->IsActive(go), go->GetIsTargetable()));
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
    /// Uses a key item: on the target (targeted, then the item, as the key item menu does), on the ground at a spot,
    /// or on yourself. Why the game refused comes back in <paramref name="why"/> (its action status, 0 is fine).
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
                var at = target?.Position ?? spot ?? Vector3.Zero;
                why = am->GetActionStatus(ActionType.EventItem, itemId);
                return am->UseActionLocation(ActionType.EventItem, itemId, 0xE000_0000, &at);
            }
            case ItemUse.Target when target != null:
            {
                Target(target);
                why = am->GetActionStatus(ActionType.EventItem, itemId, target.GameObjectId);
                var agent = AgentInventoryContext.Instance();
                return agent != null && agent->UseItem(itemId) is 0 or 1;
            }
            default:
                why = am->GetActionStatus(ActionType.EventItem, itemId);
                return am->UseAction(ActionType.EventItem, itemId);
        }
    }

    /// <summary>Uses the quest mount's action: on the target, or around the rider (null target).</summary>
    public static bool UseMountAction(uint actionId, IGameObject? target, out uint why)
    {
        var am = ActionManager.Instance();
        why = 0;
        if (am == null) return false;
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

    /// <summary>Declines a quest offer that isn't the run's (a click on Decline).</summary>
    public static bool Decline()
    {
        if (Interact.Ready("JournalAccept") is var a && a == null) return false;
        new AddonMaster.JournalAccept((nint)a).Decline();
        return true;
    }

    /// <summary>
    /// Completes the quest in the reward window: the Complete button when it can be pressed; when a reward has to be
    /// picked first, the window's confirm with the reward (once, not while scene tasks are pending).
    /// </summary>
    public static bool Complete(IReadOnlyList<uint> rewards, ref bool rewardSent, out string how)
    {
        how = "";
        if (Interact.Ready("JournalResult") is var a && a == null) return false;
        var j = (AddonJournalResult*)a;
        if (j->CompleteButton != null && j->CompleteButton->IsEnabled)
        {
            new AddonMaster.JournalResult((nint)a).Complete();
            how = "button";
            return true;
        }
        if (rewards.Count == 0 || rewardSent || DeliveryWindow.SceneBusy) return false;
        Callback.Fire(a, true, 0, (int)rewards[0]);
        rewardSent = true;
        how = $"reward {rewards[0]}";
        return true;
    }

    public enum TradeStep { Wait, Opened, Picked, HandedOver, Missing }

    /// <summary>
    /// One step of the hand-over window (<c>Request</c>) for a quest: hand over once every slot is filled; otherwise
    /// open the next slot's picker and pick its item, normal quality first (high quality when the slot wants it, or
    /// when that's all there is; the game then asks, and the question is on the known list). <paramref name="slot"/>
    /// is the next slot to fill, kept by the caller.
    /// </summary>
    public static TradeStep Trade(ref int slot, out string detail)
    {
        detail = "";
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
            var pick = -1;
            var pickHq = true;
            for (var i = 0; i < trade->SelectedTurnInSlotItemOptions && i < 140; i++)
            {
                var item = trade->SelectedTurnInSlotItemOptionValues[i].Value;
                if (item == null || item->GetBaseItemId() != want.ItemId % 1_000_000) continue;
                var hq = item->IsHighQuality();
                if (want.WantHQ && !hq) continue;
                if (pick < 0 || (pickHq && !hq)) { pick = i; pickHq = hq; }
            }
            if (pick < 0)
            {
                detail = $"no {ItemName(want.ItemId)} to hand over for slot {at + 1}";
                Interact.Close("ContextIconMenu");
                return TradeStep.Missing;
            }
            Callback.Fire(menu, true, 0, pick, 1021003, 0, 0);
            detail = $"slot {at + 1}: {ItemName(want.ItemId)}{(pickHq ? " (high quality)" : "")}";
            slot = at + 1;
            return TradeStep.Picked;
        }
        if (Interact.Ready("Request") is var request && request != null && trade->SelectedTurnInSlot < 0)
        {
            if (slot >= requests.Count) return TradeStep.Wait;
            Callback.Fire(request, false, 2, slot, 0, 0);
            detail = $"opening slot {slot + 1} of {requests.Count}";
            return TradeStep.Opened;
        }
        return TradeStep.Wait;
    }

    /// <summary>Closes the hand-over window (its Cancel button) when the items aren't there.</summary>
    public static bool CancelTrade()
    {
        if (Interact.Ready("Request") is var a && a == null) return false;
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
