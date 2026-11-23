using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Provenance.Core.Deliveries;
using Provenance.Data;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace Provenance.Integrations;

/// <summary>
/// Custom deliveries in the game: each client's satisfaction, level and deliveries this week, the week's seed and
/// bonus row, and what an open delivery window shows. The game sends this data once its Custom Deliveries window has
/// been opened (or a client talked to); until then every level reads 0.
/// </summary>
internal static unsafe class Deliveries
{
    /// <summary>Deliveries a week, shared by all clients.</summary>
    public const int WeeklyAllowance = 12;

    private static SatisfactionSupplyManager* Manager
        => Services.ClientState.IsLoggedIn && Services.Objects.LocalPlayer != null ? SatisfactionSupplyManager.Instance() : null;

    /// <summary>The game has sent the delivery data.</summary>
    public static bool Loaded
    {
        get
        {
            var m = Manager;
            if (m == null) return false;
            foreach (var r in m->SatisfactionRanks)
                if (r != 0) return true;
            return false;
        }
    }

    /// <summary>A client this version can read (one added later sits beyond the game's arrays as Provenance knows them).</summary>
    public static bool Known(uint client)
    {
        var m = Manager;
        return m != null && client >= 1 && client <= m->SatisfactionRanks.Length;
    }

    public static int Rank(uint client) => Known(client) ? Manager->SatisfactionRanks[(int)client - 1] : 0;
    public static int Satisfaction(uint client) => Known(client) ? Manager->Satisfaction[(int)client - 1] : 0;
    public static int Used(uint client) => Known(client) ? Manager->UsedAllowances[(int)client - 1] : 0;

    public static int UsedTotal
    {
        get
        {
            var m = Manager;
            if (m == null) return 0;
            var sum = 0;
            foreach (var u in m->UsedAllowances) sum += u;
            return sum;
        }
    }

    public static int Remaining => Math.Max(0, WeeklyAllowance - UsedTotal);

    public static byte Seed => Manager is var m && m != null ? m->SupplySeed : (byte)0;

    /// <summary>Server time (Unix seconds), or the PC's clock when the game has none.</summary>
    public static long ServerTime()
    {
        var t = GameFramework.GetServerTime();
        return t > 0 ? t : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// <summary>The week (counted from the bonus rotation's start) the server time falls in.</summary>
    public static long WeekNumber => (ServerTime() - DeliveryPick.GuaranteeEpoch) / DeliveryPick.Week;

    /// <summary>When the week's deliveries reset (Tuesday 08:00 UTC), as local time.</summary>
    public static DateTime NextReset
        => DateTimeOffset.FromUnixTimeSeconds(DeliveryPick.GuaranteeEpoch + (WeekNumber + 1) * DeliveryPick.Week).LocalDateTime;

    /// <summary>The week's bonus guarantee row: the game's once it has it, otherwise from the server time (-1 without data).</summary>
    public static int GuaranteeRow(int rows)
    {
        var m = Manager;
        if (m == null) return -1;
        if (m->BonusGuaranteeRowId != 0xFF) return m->BonusGuaranteeRowId;
        return DeliveryPick.GuaranteeRow(ServerTime(), m->TimeAdjustmentForBonusGuarantee, rows);
    }

    public static bool QuestDone(uint quest) => quest != 0 && QuestManager.IsQuestComplete(quest);

    /// <summary>An achievement is complete (null until the game has the achievement data, once the Achievements window was opened).</summary>
    public static bool? AchievementDone(uint id)
    {
        var a = Achievement.Instance();
        return a != null && a->IsLoaded() ? a->IsComplete((int)id) : null;
    }

    /// <summary>The item a reward currency is paid in (2/4 purple, 6/7 orange crafters'/gatherers' scrips).</summary>
    public static uint CurrencyItem(byte special)
    {
        var cm = CurrencyManager.Instance();
        if (cm != null && cm->GetItemIdBySpecialId(special) is var id and > 0) return id;
        return special switch { 2 => 33913, 4 => 33914, 6 => 41784, 7 => 41785, _ => 0 };
    }

    /// <summary>The client's level, satisfaction, deliveries and the week's requests worked out (no requests without data).</summary>
    public static ClientWeek Week(DeliveryData data, DeliveryClient client)
    {
        var rank = Rank(client.Id);
        var requests = new DeliveryRequest?[3];
        if (rank > 0 && client.Level(rank) is { } level)
            requests = DeliveryPick.Requests(client, rank, Seed, data.Supply(level.SupplyIndex),
                data.Guaranteed(GuaranteeRow(data.GuaranteeRows), client.Id), data.Reward);
        return new ClientWeek(client, rank, Satisfaction(client.Id), Used(client.Id), requests);
    }

    /// <summary>What an open, ready delivery window shows: its client and the three items (craft, gather, fish).</summary>
    public static (uint Client, uint[] Items, bool[] Bonus)? Window()
    {
        var agent = AgentSatisfactionSupply.Instance();
        if (agent == null || !agent->IsAgentActive() || !agent->NpcInfo.Valid || !agent->NpcInfo.Initialized) return null;
        var items = new uint[3];
        var bonus = new bool[3];
        for (var i = 0; i < 3; i++)
        {
            items[i] = agent->Items[i].Id;
            bonus[i] = agent->Items[i].IsBonus;
        }
        return items.All(i => i == 0) ? null : (agent->NpcInfo.Id, items, bonus);
    }

    // ------------------------------------------------------------------ checking the worked-out requests

    /// <summary>Per client, for this week: whether the requests worked out match what the game's window showed.</summary>
    private static readonly Dictionary<uint, (long Week, bool Match)> Checked = new();

    /// <summary>The check for a client this week: true when the game showed the same items, false when not, null when not seen.</summary>
    public static bool? Matches(uint client) => Checked.TryGetValue(client, out var c) && c.Week == WeekNumber ? c.Match : null;

    /// <summary>
    /// Every frame: when a client's delivery window is open, compares its three items with the ones worked out for the
    /// week (once per client and week), notes the result for the page and writes it to the debug log.
    /// </summary>
    public static void Observe(DeliveryData? data)
    {
        if (data == null || data.Clients.Count == 0 || Window() is not { } window) return;
        var week = WeekNumber;
        if (Checked.TryGetValue(window.Client, out var seen) && seen.Week == week) return;
        if (data.Clients.FirstOrDefault(c => c.Id == window.Client) is not { } client) return;
        var worked = Week(data, client);
        var mine = worked.Requests.Select(r => r?.Item.ItemId ?? 0).ToArray();
        var mineBonus = worked.Requests.Select(r => r?.Bonus ?? false).ToArray();
        var match = mine.SequenceEqual(window.Items) && mineBonus.SequenceEqual(window.Bonus);
        Checked[window.Client] = (week, match);
        DebugLog.Write("deliveries", $"{(match ? "" : "[!] ")}client {client.Id} level {worked.Rank}, seed {Seed}, bonus row {GuaranteeRow(data.GuaranteeRows)}: "
                                     + $"game [{string.Join(",", window.Items)}] bonus [{string.Join(",", window.Bonus)}], worked out [{string.Join(",", mine)}] bonus [{string.Join(",", mineBonus)}]: "
                                     + (match ? "match" : "MISMATCH"));
    }
}
