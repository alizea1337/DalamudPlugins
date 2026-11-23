using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Provenance.Core.Deliveries;
using Xunit;

namespace Provenance.Tests;

public class DeliveryPickTests
{
    private static List<DeliveryCandidate> Fixed() => new()
    {
        new(0, 100, DeliveryKind.Craft, 1, false, 55, 65, 75, 1),
        new(1, 200, DeliveryKind.Gather, 1, false, 240, 450, 600, 2),
        new(2, 300, DeliveryKind.Fish, 1, false, 100, 200, 300, 2),
    };

    /// <summary>A level-5 row: per slot four items at weight 23 and a bonus item at weight 8.</summary>
    private static List<DeliveryCandidate> Top()
    {
        var list = new List<DeliveryCandidate>();
        var sub = 0;
        foreach (var kind in new[] { DeliveryKind.Craft, DeliveryKind.Gather, DeliveryKind.Fish })
        {
            for (var i = 0; i < 4; i++) list.Add(new(sub++, (uint)(1000 * (int)kind + i), kind, 23, false, 1, 2, 3, 10));
            list.Add(new(sub++, (uint)(1000 * (int)kind + 9), kind, 8, true, 1, 2, 3, 11));
        }
        return list;
    }

    private static DeliveryClient Client() => new(2, "Client", 1, 1, "Somewhere", Vector3.Zero, 1, "Unlock", 60, 6, true,
        Enumerable.Range(1, 5).Select(l => new DeliveryLevel(l, 5 + l, l < 5 ? 90 * l : 0, Array.Empty<(uint, int, bool)>())).ToList(),
        Array.Empty<DeliveryQuest>(), (1, 6));

    [Fact]
    public void BelowTheTopLevelEachSlotHasItsOneItem()
    {
        foreach (var seed in new byte[] { 0, 1, 77, 255 })
        {
            var picks = DeliveryPick.Pick(3, seed, Fixed());
            Assert.Equal(new uint[] { 100, 200, 300 }, picks.Select(p => p!.ItemId));
        }
    }

    [Fact]
    public void ThePickIsTheSameForTheSameWeek()
    {
        var a = DeliveryPick.Pick(10, 42, Top()).Select(p => p!.ItemId).ToArray();
        var b = DeliveryPick.Pick(10, 42, Top()).Select(p => p!.ItemId).ToArray();
        Assert.Equal(a, b);
    }

    [Fact]
    public void EachPickComesFromItsOwnSlot()
    {
        for (var seed = 0; seed < 256; seed++)
        {
            var picks = DeliveryPick.Pick(25, (byte)seed, Top());
            for (var i = 0; i < 3; i++) Assert.Equal((DeliveryKind)(i + 1), picks[i]!.Kind);
        }
    }

    [Fact]
    public void TheBonusItemComesUpRoughlyAsOftenAsItsWeight()
    {
        // 8 in 100 per slot: over every seed and many rows, somewhere near 8 %.
        int bonus = 0, total = 0;
        for (var row = 1; row <= 60; row++)
        for (var seed = 0; seed < 256; seed++)
        {
            foreach (var p in DeliveryPick.Pick(row, (byte)seed, Top())) { total++; if (p!.IsBonus) bonus++; }
        }
        var share = bonus / (double)total;
        Assert.InRange(share, 0.05, 0.11);
    }

    [Fact]
    public void ASlotWithNothingStaysEmpty()
    {
        var onlyCraft = Fixed().Where(c => c.Kind == DeliveryKind.Craft).ToList();
        var picks = DeliveryPick.Pick(3, 9, onlyCraft);
        Assert.NotNull(picks[0]);
        Assert.Null(picks[1]);
        Assert.Null(picks[2]);
    }

    [Fact]
    public void TheGuaranteeRotationCountsWeeksFromItsEpoch()
    {
        // The week starting Tue 2026-11-17 08:00 UTC is week 228 since the epoch: row 0 of 12.
        var start = new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.Equal(0, DeliveryPick.GuaranteeRow(start + 3600, 0, 12));
        Assert.Equal(11, DeliveryPick.GuaranteeRow(start - 3600, 0, 12));
        Assert.Equal(1, DeliveryPick.GuaranteeRow(start + DeliveryPick.Week, 0, 12));
    }

    [Fact]
    public void AGuaranteedClientAtTheTopLevelGetsTheBonusItem()
    {
        var supply = Top();
        var rewards = new Dictionary<uint, DeliveryReward>
        {
            [10] = new(10, 2, new[] { 1, 2, 3 }, 6, new[] { 1, 2, 3 }, new[] { 0, 0, 0 }, new[] { 1, 2, 3 }, 100),
            [11] = new(11, 2, new[] { 1, 2, 3 }, 6, new[] { 1, 2, 3 }, new[] { 0, 0, 0 }, new[] { 1, 2, 3 }, 150),
        };
        var guaranteed = new HashSet<DeliveryKind> { DeliveryKind.Gather };
        var requests = DeliveryPick.Requests(Client(), 5, 0, supply, guaranteed, id => rewards.GetValueOrDefault(id));
        Assert.True(requests[1]!.Bonus);
        Assert.Equal(2009u, requests[1]!.Item.ItemId);
        Assert.Equal(150, requests[1]!.Reward!.BonusMultiplier);
    }

    [Fact]
    public void BelowTheTopLevelTheGuaranteeChangesNothing()
    {
        var guaranteed = new HashSet<DeliveryKind> { DeliveryKind.Craft, DeliveryKind.Gather, DeliveryKind.Fish };
        var requests = DeliveryPick.Requests(Client(), 3, 0, Fixed(), guaranteed, _ => null);
        Assert.All(requests, r => Assert.False(r!.Bonus));
    }

    [Fact]
    public void DeliveriesToTheNextLevelCountUp()
    {
        var reward = new DeliveryReward(1, 2, new[] { 1, 2, 3 }, 6, new[] { 1, 2, 3 }, new[] { 80, 80, 80 }, new[] { 1, 2, 3 }, 100);
        var req = new DeliveryRequest(Fixed()[0], reward, false);
        var week = new ClientWeek(Client(), 2, 100, 1, new DeliveryRequest?[] { req, req, req });
        // Level 2 needs 180 here (90 x 2): 80 left at 80 a delivery.
        Assert.Equal(1, week.ToNextLevel);
        Assert.Equal(5, week.LeftThisWeek);
    }
}
