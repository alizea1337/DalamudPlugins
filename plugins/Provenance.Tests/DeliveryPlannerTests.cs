using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Provenance.Core.Deliveries;
using Xunit;

namespace Provenance.Tests;

public class DeliveryPlannerTests
{
    private static DeliveryReward Pay(int scrips, int gil, int multiplier = 100, int satisfaction = 80, byte currency = 2)
        => new(1, currency, new[] { scrips - 40, scrips - 20, scrips }, 6, new[] { 0, 0, 0 }, new[] { satisfaction, satisfaction, satisfaction },
            new[] { gil - 300, gil - 150, gil }, multiplier);

    private static DeliveryClient Client(uint id) => new(id, $"Client {id}", id, 1, "Somewhere", Vector3.Zero, 1, "Unlock", 60, 6, false,
        Enumerable.Range(1, 5).Select(l => new DeliveryLevel(l, l, l < 5 ? 480 : 0, Array.Empty<(uint, int, bool)>())).ToList(),
        Array.Empty<DeliveryQuest>(), (0, 0));

    private static DeliveryRequest Req(DeliveryKind kind, DeliveryReward pay, bool bonus = false)
        => new(new DeliveryCandidate(0, (uint)(100 + (int)kind), kind, 1, bonus, 50, 60, 70, 1), pay, bonus);

    private static ClientWeek Week(uint id, int rank, int satisfaction = 0, int used = 0, DeliveryReward? pay = null, bool bonus = false)
    {
        pay ??= Pay(150, 1000, bonus ? 150 : 100, rank < 5 ? 80 : 0);
        return new ClientWeek(Client(id), rank, satisfaction, used,
            new DeliveryRequest?[] { Req(DeliveryKind.Craft, pay, bonus), Req(DeliveryKind.Gather, pay, bonus), Req(DeliveryKind.Fish, pay, bonus) });
    }

    private static DeliveryOptions Options(DeliveryGoal goal, bool anyway = false, params uint[] clients)
        => new(goal, new HashSet<uint>(clients.Length > 0 ? clients : Enumerable.Range(1, 12).Select(i => (uint)i)),
            new HashSet<DeliveryKind> { DeliveryKind.Craft, DeliveryKind.Gather }, true, anyway);

    private static readonly DeliveryAbility Can = new(true, 2, 8);
    private static readonly Dictionary<byte, int> NoScrips = new();

    [Fact]
    public void RankingUpGoesToTheLowestClientsFirstAndStopsAtTheLevelUp()
    {
        var weeks = new[] { Week(1, 5), Week(2, 3, 400), Week(3, 2, 0) };
        var plan = DeliveryPlanner.Plan(weeks, _ => null, (_, _) => Can, 12, Options(DeliveryGoal.RankUp), NoScrips);
        Assert.Equal(new uint[] { 3, 2, 1 }, plan.Deliveries.Select(d => d.Client.Id));
        // Level 2 at 0 of 480, 80 a delivery: 6. Level 3 at 400 of 480: 1. The top-level client gets the 5 left.
        Assert.Equal(new[] { 6, 1, 5 }, plan.Deliveries.Select(d => d.Count));
        Assert.All(plan.Deliveries.Where(d => !d.Week.Maxed), d => Assert.Equal(0, d.Tier));
    }

    [Fact]
    public void TheWeeksTwelveAreNeverExceeded()
    {
        var weeks = Enumerable.Range(1, 5).Select(i => Week((uint)i, 5)).ToArray();
        var plan = DeliveryPlanner.Plan(weeks, _ => null, (_, _) => Can, 12, Options(DeliveryGoal.Scrips), NoScrips);
        Assert.Equal(12, plan.Count);
        Assert.All(plan.Deliveries, d => Assert.True(d.Count <= 6));
    }

    [Fact]
    public void BonusRequestsComeFirst()
    {
        var weeks = new[] { Week(1, 5, pay: Pay(300, 5000)), Week(2, 5, bonus: true) };
        var plan = DeliveryPlanner.Plan(weeks, _ => null, (_, _) => Can, 6, Options(DeliveryGoal.Scrips), NoScrips);
        Assert.Single(plan.Deliveries);
        Assert.Equal(2u, plan.Deliveries[0].Client.Id);
        Assert.Equal(225, plan.Deliveries[0].ScripsEach); // 150 x 1.5
    }

    [Fact]
    public void RankingUpServesClientsBelowTheTopBeforeBonusRequests()
    {
        // Two top-level clients with bonus requests, two below the top: ranking up goes to the lower ones first.
        var weeks = new[] { Week(2, 5, bonus: true), Week(4, 5, bonus: true), Week(5, 2, 0), Week(6, 1, 0, pay: Pay(150, 1000, satisfaction: 30)) };
        var plan = DeliveryPlanner.Plan(weeks, _ => null, (_, _) => Can, 12, Options(DeliveryGoal.RankUp), NoScrips);
        Assert.Equal(new uint[] { 6, 5 }, plan.Deliveries.Select(d => d.Client.Id));
        // Level 1 needs 480 here at 30 a delivery: the week's 6. Level 2 at 80: 6 more. Nothing left for the bonus ones.
        Assert.Equal(12, plan.Count);
        Assert.Equal(6, plan.Deliveries[0].Count);
        // With fewer below the top, the bonus requests take what's left, the first of them before plain ones.
        var some = DeliveryPlanner.Plan(new[] { Week(1, 5), Week(2, 5, bonus: true), Week(6, 4, 400) }, _ => null, (_, _) => Can, 12, Options(DeliveryGoal.RankUp), NoScrips);
        Assert.Equal(new uint[] { 6, 2, 1 }, some.Deliveries.Select(d => d.Client.Id));
        Assert.Equal(new[] { 1, 6, 5 }, some.Deliveries.Select(d => d.Count));
    }

    [Fact]
    public void FishWaitsWhenCraftingOrGatheringWorks()
    {
        var options = Options(DeliveryGoal.Scrips) with { Kinds = new HashSet<DeliveryKind> { DeliveryKind.Craft, DeliveryKind.Gather, DeliveryKind.Fish } };
        var plan = DeliveryPlanner.Plan(new[] { Week(1, 5) }, _ => null,
            (_, r) => r.Item.Kind == DeliveryKind.Craft ? DeliveryAbility.No("no crafter") : Can, 12, options, NoScrips);
        Assert.Equal(DeliveryKind.Gather, plan.Deliveries.Single().Kind);
    }

    [Fact]
    public void ClientsThatCantTakeDeliveriesAreSkippedWithTheReason()
    {
        var weeks = new[] { Week(1, 3), Week(2, 3), Week(3, 3, used: 6) };
        var plan = DeliveryPlanner.Plan(weeks, w => w.Client.Id == 1 ? "story quest first" : null,
            (w, _) => w.Client.Id == 2 ? DeliveryAbility.No("needs a level 80 crafter") : Can, 12, Options(DeliveryGoal.RankUp), NoScrips);
        Assert.Empty(plan.Deliveries);
        Assert.Contains(plan.Skipped, s => s.Client.Id == 1 && s.Why == "story quest first");
        Assert.Contains(plan.Skipped, s => s.Client.Id == 2 && s.Why == "needs a level 80 crafter");
        Assert.Contains(plan.Skipped, s => s.Client.Id == 3 && s.Why == "done for this week");
    }

    [Fact]
    public void UntickedClientsAreLeftOut()
    {
        var plan = DeliveryPlanner.Plan(new[] { Week(1, 5), Week(2, 5) }, _ => null, (_, _) => Can, 12, Options(DeliveryGoal.Scrips, false, 2), NoScrips);
        Assert.Equal(2u, plan.Deliveries.Single().Client.Id);
    }

    [Fact]
    public void TheScripCapCutsDeliveriesUnlessRankingUpAnyway()
    {
        var held = new Dictionary<byte, int> { [2] = 3700 };
        var capped = DeliveryPlanner.Plan(new[] { Week(1, 3) }, _ => null, (_, _) => Can, 12, Options(DeliveryGoal.RankUp), held);
        // Tier 0 pays 110: two fit under 4,000.
        Assert.Equal(2, capped.Count);
        var anyway = DeliveryPlanner.Plan(new[] { Week(1, 3) }, _ => null, (_, _) => Can, 12, Options(DeliveryGoal.RankUp, true), held);
        Assert.Equal(6, anyway.Count);
        var full = DeliveryPlanner.Plan(new[] { Week(1, 5) }, _ => null, (_, _) => Can, 12, Options(DeliveryGoal.Scrips), new Dictionary<byte, int> { [2] = 3990 });
        Assert.Empty(full.Deliveries);
        Assert.Contains(full.Skipped, s => s.Why.Contains("cap"));
    }

    [Fact]
    public void OtherGoalsUseTheBestReliableTier()
    {
        var plan = DeliveryPlanner.Plan(new[] { Week(1, 5) }, _ => null, (_, _) => new DeliveryAbility(true, 1, 8), 12, Options(DeliveryGoal.Gil), NoScrips);
        Assert.Equal(1, plan.Deliveries.Single().Tier);
        Assert.Equal(60, plan.Deliveries.Single().Collectability);
    }
}
