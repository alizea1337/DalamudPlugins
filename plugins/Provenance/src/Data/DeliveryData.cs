using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Lumina.Excel.Sheets;
using Provenance.Core.Deliveries;
using DeliveryQuest = Provenance.Core.Deliveries.DeliveryQuest;

namespace Provenance.Data;

/// <summary>
/// Custom deliveries from the game data: the clients (<c>SatisfactionNpc</c>), what each level asks for
/// (<c>SatisfactionSupply</c>), what a delivery pays (<c>SatisfactionSupplyReward</c>), the week's bonus guarantee
/// rotation (<c>SatisfactionBonusGuarantee</c>) and the story quests tied to each client.
/// </summary>
internal sealed class DeliveryData
{
    private readonly Dictionary<int, List<DeliveryCandidate>> supply = new();
    private readonly Dictionary<uint, DeliveryReward> rewards = new();
    private readonly List<(uint[] Craft, uint[] Gather, uint[] Fish)> guarantees = new();

    public IReadOnlyList<DeliveryClient> Clients { get; private set; } = Array.Empty<DeliveryClient>();

    /// <summary>The candidates of a supply row (empty when the row is unknown).</summary>
    public IReadOnlyList<DeliveryCandidate> Supply(int index) => supply.TryGetValue(index, out var list) ? list : Array.Empty<DeliveryCandidate>();

    public DeliveryReward? Reward(uint row) => rewards.GetValueOrDefault(row);

    /// <summary>Rows of the bonus guarantee rotation (it grows when a client is added).</summary>
    public int GuaranteeRows => guarantees.Count;

    /// <summary>The kinds whose bonus item the guarantee row gives this client.</summary>
    public IReadOnlySet<DeliveryKind> Guaranteed(int row, uint client)
    {
        var kinds = new HashSet<DeliveryKind>();
        if (row < 0 || row >= guarantees.Count) return kinds;
        var g = guarantees[row];
        if (g.Craft.Contains(client)) kinds.Add(DeliveryKind.Craft);
        if (g.Gather.Contains(client)) kinds.Add(DeliveryKind.Gather);
        if (g.Fish.Contains(client)) kinds.Add(DeliveryKind.Fish);
        return kinds;
    }

    /// <summary>First achievement ids per client: "Satisfaction Guaranteed" I and "The Customer Is Always Right" I.</summary>
    private static readonly uint[] AchievementBase = { 1774, 1969, 2067, 2183, 2425, 2623, 2835, 3059, 3163, 3351, 3592, 3986 };

    public static DeliveryData Build()
    {
        var d = new DeliveryData();
        try
        {
            d.IndexSupply();
            d.IndexRewards();
            d.IndexGuarantees();
            d.IndexClients();
        }
        catch (Exception e)
        {
            Services.Log.Error(e, "Couldn't read the custom deliveries");
        }
        return d;
    }

    private void IndexSupply()
    {
        foreach (var row in Services.Data.GetSubrowExcelSheet<SatisfactionSupply>())
        {
            var list = new List<DeliveryCandidate>();
            foreach (var s in row)
            {
                if (s.Item.RowId == 0 || s.Slot is < 1 or > 3) continue;
                list.Add(new DeliveryCandidate(s.SubrowId, s.Item.RowId, (DeliveryKind)s.Slot, s.ProbabilityPercent, s.IsBonus,
                    s.CollectabilityLow, s.CollectabilityMid, s.CollectabilityHigh, s.Reward.RowId, s.SpearFishingSpotId != 0));
            }
            if (list.Count > 0) supply[(int)row.RowId] = list;
        }
    }

    private void IndexRewards()
    {
        foreach (var r in Services.Data.GetExcelSheet<SatisfactionSupplyReward>())
        {
            var data = r.SatisfactionSupplyRewardData;
            if (data.Count < 2) continue;
            rewards[r.RowId] = new DeliveryReward(r.RowId,
                (byte)data[0].RewardCurrency, new int[] { data[0].QuantityLow, data[0].QuantityMid, data[0].QuantityHigh },
                (byte)data[1].RewardCurrency, new int[] { data[1].QuantityLow, data[1].QuantityMid, data[1].QuantityHigh },
                new int[] { r.SatisfactionLow, r.SatisfactionMid, r.SatisfactionHigh },
                new int[] { r.GilLow, r.GilMid, r.GilHigh },
                r.BonusMultiplier);
        }
    }

    private void IndexGuarantees()
    {
        foreach (var g in Services.Data.GetExcelSheet<SatisfactionBonusGuarantee>())
            guarantees.Add((g.BonusDoH.Select(x => (uint)x).ToArray(), g.BonusDoL.Select(x => (uint)x).ToArray(), g.BonusFisher.Select(x => (uint)x).ToArray()));
    }

    private void IndexClients()
    {
        // Quests that hold a client's deliveries back until they're done.
        var gating = new HashSet<uint>();
        foreach (var row in Services.Data.GetSubrowExcelSheet<SatisfactionArbitration>())
            foreach (var a in row)
                if (a.Quest.RowId != 0) gating.Add(a.Quest.RowId);

        // Story quests per client, offered at a satisfaction level (level 0 is the unlock quest).
        var quests = new Dictionary<uint, List<DeliveryQuest>>();
        foreach (var q in Services.Data.GetExcelSheet<Quest>())
        {
            var client = q.SatisfactionNpc.RowId;
            if (client == 0 || q.SatisfactionLevel == 0) continue;
            var items = new List<uint>();
            foreach (var r in q.Reward)
                if (r.RowId != 0 && r.Is<Item>()) items.Add(r.RowId);
            foreach (var r in q.OptionalItemReward)
                if (r.RowId != 0) items.Add(r.RowId);
            if (!quests.TryGetValue(client, out var list)) quests[client] = list = new List<DeliveryQuest>();
            list.Add(new DeliveryQuest(q.RowId, q.Name.ExtractText(), q.SatisfactionLevel, items, gating.Contains(q.RowId)));
        }

        var places = Services.Data.GetExcelSheet<TerritoryType>();
        var clients = new List<DeliveryClient>();
        foreach (var row in Services.Data.GetExcelSheet<SatisfactionNpc>())
        {
            // Row 0 is empty, and a client to come can sit there as an all-zero row.
            if (row.RowId == 0 || row.Npc.RowId == 0) continue;
            var name = row.Npc.ValueNullable?.Singular.ExtractText() is { Length: > 0 } n ? n : $"Client {row.RowId}";
            var level = row.Level.ValueNullable;
            var territory = level?.Territory.RowId ?? 0;
            var place = places.GetRowOrDefault(territory)?.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
            var levels = new List<DeliveryLevel>();
            for (var i = 1; i < row.SatisfactionNpcParams.Count; i++)
            {
                var p = row.SatisfactionNpcParams[i];
                var items = new List<(uint, int, bool)>();
                for (var k = 0; k < p.Item.Count; k++)
                    if (p.Item[k].RowId != 0) items.Add((p.Item[k].RowId, p.ItemCount[k], p.IsHQ[k]));
                levels.Add(new DeliveryLevel(i, p.SupplyIndex, p.SatisfactionRequired, items));
            }
            var index = (int)row.RowId - 1;
            var achievements = index < AchievementBase.Length ? (AchievementBase[index], AchievementBase[index] + 5) : (0u, 0u);
            clients.Add(new DeliveryClient(row.RowId, name, row.Npc.RowId, territory, place,
                level is { } l ? new Vector3(l.X, l.Y, l.Z) : Vector3.Zero,
                row.QuestRequired.RowId, row.QuestRequired.ValueNullable?.Name.ExtractText() ?? "",
                row.LevelUnlock, row.DeliveriesPerWeek is > 0 and var per ? per : 6, row.GlamourIndex != 99, levels,
                quests.GetValueOrDefault(row.RowId)?.OrderBy(q => q.Level).ThenBy(q => q.QuestId).ToList() ?? new List<DeliveryQuest>(),
                achievements));
        }
        Clients = clients;
    }
}
