using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace Keystroke.Game.Inspector;

/// <summary>
/// Reads the live game state into a <see cref="GameSnapshot"/>. Must be called on the framework
/// thread. Every section is isolated with its own try/catch so one bad pointer never hides the rest.
/// </summary>
internal static unsafe class GameInspector
{
    private const int MaxNodesPerAddon = 600;
    private const int MaxNodeDepth = 5;
    private const int MaxArrayValues = 320;

    public static GameSnapshot Capture(bool includeNodes, bool includeArrays)
    {
        var errors = new List<string>();
        var client = Services.ClientState;
        var territory = (ushort)client.TerritoryType;

        var (name, place) = TerritoryNames(territory, errors);

        return new GameSnapshot
        {
            CapturedAt = DateTime.Now,
            PluginVersion = Plugin.Version,
            IsLoggedIn = client.IsLoggedIn,
            TerritoryId = territory,
            TerritoryName = name,
            TerritoryPlaceName = place,
            InKeyboundDuty = Keybound.IsDuty(territory),
            InKeyboundLobby = Keybound.IsLobby(territory),
            ClientLanguage = client.ClientLanguage.ToString(),
            Conditions = ConditionFlags(),
            Addons = Addons(includeNodes, errors),
            Agents = Agents(errors),
            NumberArray = includeArrays ? NumberArray(Keybound.NumberArrayIndex, errors) : null,
            StringArray = includeArrays ? StringArray(Keybound.StringArrayIndex, errors) : null,
            Casts = Casts(errors),
            Currency = Currency(errors),
            Queue = Queue(errors),
            Target = TargetInfo(errors),
            NearbyNpcs = NearbyNpcs(errors),
            Errors = errors,
        };
    }

    private static ObjectSnapshot? TargetInfo(List<string> errors)
    {
        try
        {
            var t = Services.Targets.Target;
            return t == null ? null : Describe(t);
        }
        catch (Exception e) { errors.Add($"target: {e.Message}"); return null; }
    }

    private static List<ObjectSnapshot> NearbyNpcs(List<string> errors)
    {
        var result = new List<ObjectSnapshot>();
        try
        {
            var player = Services.Objects.LocalPlayer;
            if (player == null) return result;
            foreach (var obj in Services.Objects)
            {
                if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc) continue;
                var d = System.Numerics.Vector3.Distance(player.Position, obj.Position);
                if (d > 12f) continue;
                result.Add(Describe(obj) with { Distance = d });
            }
            result.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        }
        catch (Exception e) { errors.Add($"npcs: {e.Message}"); }
        return result;
    }

    private static ObjectSnapshot Describe(Dalamud.Game.ClientState.Objects.Types.IGameObject obj)
    {
        var player = Services.Objects.LocalPlayer;
        return new ObjectSnapshot
        {
            Name = obj.Name.TextValue,
            Kind = obj.ObjectKind.ToString(),
            EntityId = obj.EntityId,
            DataId = obj.DataId,
            Distance = player == null ? 0 : System.Numerics.Vector3.Distance(player.Position, obj.Position),
        };
    }

    private static (string, string) TerritoryNames(ushort id, List<string> errors)
    {
        try
        {
            var sheet = Services.Data.GetExcelSheet<TerritoryType>();
            if (sheet.TryGetRow(id, out var row))
                return (row.Name.ExtractText(), row.PlaceName.ValueNullable?.Name.ExtractText() ?? "");
        }
        catch (Exception e) { errors.Add($"territory: {e.Message}"); }
        return ("", "");
    }

    private static string ConditionFlags()
    {
        var sb = new StringBuilder();
        foreach (var flag in Enum.GetValues<ConditionFlag>())
        {
            if (flag == ConditionFlag.None) continue;
            if (Services.Condition[flag]) sb.Append(flag).Append(", ");
        }
        return sb.Length > 2 ? sb.ToString(0, sb.Length - 2) : "";
    }

    // ------------------------------------------------------------------ addons

    private static List<AddonSnapshot> Addons(bool includeNodes, List<string> errors)
    {
        var result = new List<AddonSnapshot>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Any loaded addon whose name starts with KTG, so we discover ones we don't know yet.
        try
        {
            var manager = RaptureAtkUnitManager.Instance();
            if (manager != null)
            {
                var list = &manager->AllLoadedUnitsList;
                var count = Math.Min(list->Count, list->Entries.Length);
                for (var i = 0; i < count; i++)
                {
                    var unit = list->Entries[i].Value;
                    if (unit == null) continue;
                    var n = unit->NameString;
                    if (string.IsNullOrEmpty(n) || !n.StartsWith("KTG", StringComparison.Ordinal)) continue;
                    if (seen.Add(n)) result.Add(Addon(n, unit, includeNodes, errors));
                }
            }
        }
        catch (Exception e) { errors.Add($"addon enumeration: {e.Message}"); }

        foreach (var n in Keybound.KnownAddons)
        {
            if (!seen.Add(n)) continue;
            try
            {
                var ptr = Services.GameGui.GetAddonByName(n);
                var unit = ptr.IsNull ? null : (AtkUnitBase*)ptr.Address;
                result.Add(unit == null ? new AddonSnapshot { Name = n, Present = false } : Addon(n, unit, includeNodes, errors));
            }
            catch (Exception e) { errors.Add($"addon {n}: {e.Message}"); }
        }

        return result.OrderBy(a => !a.Present).ThenBy(a => a.Name, StringComparer.Ordinal).ToList();
    }

    private static AddonSnapshot Addon(string name, AtkUnitBase* unit, bool includeNodes, List<string> errors)
    {
        var nodes = new List<NodeSnapshot>();
        var total = 0;
        if (includeNodes)
        {
            try { WalkNodes(&unit->UldManager, 0, nodes, ref total); }
            catch (Exception e) { errors.Add($"nodes {name}: {e.Message}"); }
        }
        var root = unit->RootNode;
        return new AddonSnapshot
        {
            Name = name,
            Present = true,
            Address = $"0x{(nint)unit:X}",
            Visible = unit->IsVisible,
            X = unit->X,
            Y = unit->Y,
            Width = root != null ? root->Width * unit->Scale : 0,
            Height = root != null ? root->Height * unit->Scale : 0,
            NodeCount = total,
            Nodes = nodes,
        };
    }

    private static void WalkNodes(AtkUldManager* uld, int depth, List<NodeSnapshot> into, ref int total)
    {
        if (uld == null || uld->NodeList == null || depth > MaxNodeDepth) return;
        var count = uld->NodeListCount;
        for (var i = 0; i < count; i++)
        {
            if (total >= MaxNodesPerAddon) return;
            var node = uld->NodeList[i];
            if (node == null) continue;
            total++;

            string? text = null;
            string? component = null;
            AtkUldManager* child = null;
            var type = (int)node->Type;
            if (node->Type == NodeType.Text)
            {
                text = ((AtkTextNode*)node)->NodeText.ToString();
            }
            else if (type >= 1000)
            {
                var comp = ((AtkComponentNode*)node)->Component;
                if (comp != null)
                {
                    component = comp->GetComponentType().ToString();
                    child = &comp->UldManager;
                }
            }

            into.Add(new NodeSnapshot
            {
                Depth = depth,
                NodeId = node->NodeId,
                Type = type >= 1000 ? $"Component({type})" : node->Type.ToString(),
                Visible = node->IsVisible(),
                X = node->ScreenX,
                Y = node->ScreenY,
                Width = node->Width,
                Height = node->Height,
                Text = text,
                ComponentType = component,
            });

            if (child != null) WalkNodes(child, depth + 1, into, ref total);
        }
    }

    // ------------------------------------------------------------------ agents

    private static List<AgentSnapshot> Agents(List<string> errors)
    {
        var result = new List<AgentSnapshot>();
        try
        {
            var module = AgentModule.Instance();
            if (module == null) return result;
            foreach (var (name, id) in Keybound.Agents)
            {
                var agent = module->GetAgentByInternalId(id);
                result.Add(new AgentSnapshot
                {
                    Name = name,
                    Id = (uint)id,
                    Address = agent == null ? "null" : $"0x{(nint)agent:X}",
                    Active = agent != null && agent->IsAgentActive(),
                    AddonId = agent != null ? agent->GetAddonId() : 0,
                });
            }
        }
        catch (Exception e) { errors.Add($"agents: {e.Message}"); }
        return result;
    }

    // ------------------------------------------------------------------ arrays

    private static ArraySnapshot NumberArray(int index, List<string> errors)
    {
        try
        {
            var holder = AtkStage.Instance()->GetNumberArrayData();
            var arr = holder[index];
            if (arr == null) return new ArraySnapshot { Index = index, Present = false };
            var size = arr->AtkArrayData.Size;
            var values = new List<string>();
            for (var i = 0; i < Math.Min(size, MaxArrayValues); i++) values.Add(arr->IntArray[i].ToString());
            return new ArraySnapshot { Index = index, Present = true, Size = size, Values = values };
        }
        catch (Exception e)
        {
            errors.Add($"number array {index}: {e.Message}");
            return new ArraySnapshot { Index = index, Present = false };
        }
    }

    private static ArraySnapshot StringArray(int index, List<string> errors)
    {
        try
        {
            var holder = AtkStage.Instance()->GetStringArrayData();
            var arr = holder[index];
            if (arr == null) return new ArraySnapshot { Index = index, Present = false };
            var size = arr->AtkArrayData.Size;
            var values = new List<string>();
            for (var i = 0; i < Math.Min(size, MaxArrayValues); i++)
            {
                byte* p = arr->StringArray[i];
                values.Add(p == null ? "" : ReadCString(p, 512));
            }
            return new ArraySnapshot { Index = index, Present = true, Size = size, Values = values };
        }
        catch (Exception e)
        {
            errors.Add($"string array {index}: {e.Message}");
            return new ArraySnapshot { Index = index, Present = false };
        }
    }

    private static string ReadCString(byte* p, int max)
    {
        var len = 0;
        while (len < max && p[len] != 0) len++;
        return Encoding.UTF8.GetString(p, len);
    }

    // ------------------------------------------------------------------ casts

    private static List<CastSnapshot> Casts(List<string> errors)
    {
        var result = new List<CastSnapshot>();
        try
        {
            var actions = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>();
            foreach (var obj in Services.Objects)
            {
                if (obj is not IBattleChara chara || !chara.IsCasting) continue;
                var actionName = actions.TryGetRow(chara.CastActionId, out var row) ? row.Name.ExtractText() : "";
                result.Add(new CastSnapshot
                {
                    EntityId = chara.EntityId,
                    Name = chara.Name.TextValue,
                    ActionId = chara.CastActionId,
                    ActionName = actionName,
                    CurrentTime = chara.CurrentCastTime,
                    TotalTime = chara.TotalCastTime,
                });
            }
        }
        catch (Exception e) { errors.Add($"casts: {e.Message}"); }
        return result;
    }

    // ------------------------------------------------------------------ currency / queue

    private static CurrencySnapshot Currency(List<string> errors)
    {
        try
        {
            var inv = InventoryManager.Instance();
            var cur = CurrencyManager.Instance();
            if (inv == null) return new CurrencySnapshot();
            return new CurrencySnapshot
            {
                Mgp = inv->GetInventoryItemCount(Keybound.MgpItemId),
                Mgc = inv->GetInventoryItemCount(Keybound.MgcItemId),
                MgcCap = cur != null ? cur->GetItemMaxCount(Keybound.MgcItemId) : 0,
                KeyboundCoffers = inv->GetInventoryItemCount(Keybound.CofferItemId),
            };
        }
        catch (Exception e)
        {
            errors.Add($"currency: {e.Message}");
            return new CurrencySnapshot();
        }
    }

    private static QueueSnapshot Queue(List<string> errors)
    {
        try
        {
            var cf = ContentsFinder.Instance();
            if (cf == null) return new QueueSnapshot();
            var q = &cf->QueueInfo;
            var ids = new List<uint>();
            foreach (ref var entry in q->QueuedEntries)
                if (entry.Id != 0) ids.Add(entry.Id);
            return new QueueSnapshot
            {
                State = q->QueueState.ToString(),
                PoppedContentId = q->PoppedQueueEntry.Id,
                QueuedContentIds = ids,
                Position = q->PositionInQueue,
            };
        }
        catch (Exception e)
        {
            errors.Add($"queue: {e.Message}");
            return new QueueSnapshot();
        }
    }
}
