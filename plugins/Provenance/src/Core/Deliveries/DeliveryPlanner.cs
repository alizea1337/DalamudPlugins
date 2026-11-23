using System;
using System.Collections.Generic;
using System.Linq;

namespace Provenance.Core.Deliveries;

/// <summary>What the week's deliveries aim for.</summary>
public enum DeliveryGoal
{
    /// <summary>Clients below the top level first, lowest level first; the lowest tier does (satisfaction is the same at every tier).</summary>
    RankUp,
    /// <summary>The most scrips per delivery, at the best tier the gear reliably reaches.</summary>
    Scrips,
    /// <summary>The most gil per delivery, at the best tier the gear reliably reaches.</summary>
    Gil,
}

/// <summary>Whether a request can be made and delivered, by which class, and the best tier it reliably reaches (0-2).</summary>
/// <param name="Why">Why not, when it can't.</param>
public sealed record DeliveryAbility(bool Possible, int BestTier, uint ClassJob, string? Why = null)
{
    public static DeliveryAbility No(string why) => new(false, 0, 0, why);
}

/// <summary>Deliveries of one request to one client: how many, at which tier, by which class, and what each pays.</summary>
public sealed record PlannedDelivery(ClientWeek Week, DeliveryRequest Request, int Count, int Tier, uint ClassJob, int ScripsEach, byte ScripCurrency, int GilEach)
{
    public DeliveryClient Client => Week.Client;
    public DeliveryKind Kind => Request.Item.Kind;
    /// <summary>The collectability the item needs for the tier.</summary>
    public int Collectability => Tier switch { 2 => Request.Item.High, 1 => Request.Item.Mid, _ => Request.Item.Low };
}

/// <summary>The week's plan: the deliveries, and why the other clients get none.</summary>
public sealed record DeliveryPlan(IReadOnlyList<PlannedDelivery> Deliveries, IReadOnlyList<(DeliveryClient Client, string Why)> Skipped)
{
    public int Count => Deliveries.Sum(d => d.Count);
    public static readonly DeliveryPlan Empty = new(Array.Empty<PlannedDelivery>(), Array.Empty<(DeliveryClient, string)>());
}

/// <summary>Settings the plan goes by.</summary>
/// <param name="Clients">Clients ticked on the page.</param>
/// <param name="Kinds">Kinds of request this version can make.</param>
/// <param name="PreferCraftGather">Fish only when neither the crafted nor the gathered request can be done.</param>
/// <param name="DeliverAnywayWhileRanking">Under "Rank up", deliver to clients below the top level even when the scrips would go over the cap.</param>
public sealed record DeliveryOptions(DeliveryGoal Goal, IReadOnlySet<uint> Clients, IReadOnlySet<DeliveryKind> Kinds, bool PreferCraftGather,
    bool DeliverAnywayWhileRanking, int ScripCap = 4000);

/// <summary>
/// Plans the week's deliveries: one request per client, delivered as often as the goal wants, within the 12 a week
/// (and 6 per client). Ranking up serves the clients below the top level first; bonus requests (top level only) come
/// first among the rest, and first of all under the other goals. Pure, so it is tested without the game.
/// </summary>
public static class DeliveryPlanner
{
    /// <param name="weeks">Every client this week (requests worked out).</param>
    /// <param name="deliverable">Null when the client takes deliveries now; otherwise why not (locked, a story quest first, ...).</param>
    /// <param name="ability">Whether a request can be made and delivered, and how well.</param>
    /// <param name="remaining">Deliveries left this week, all clients together.</param>
    /// <param name="scrips">Scrips held now, by reward currency (special id).</param>
    public static DeliveryPlan Plan(IReadOnlyList<ClientWeek> weeks, Func<ClientWeek, string?> deliverable,
        Func<ClientWeek, DeliveryRequest, DeliveryAbility> ability, int remaining, DeliveryOptions options, IReadOnlyDictionary<byte, int> scrips)
    {
        var skipped = new List<(DeliveryClient, string)>();
        var picks = new List<(ClientWeek Week, DeliveryRequest Request, DeliveryAbility Ability, int Count)>();
        foreach (var week in weeks)
        {
            var c = week.Client;
            if (!options.Clients.Contains(c.Id)) continue;
            if (deliverable(week) is { } why) { skipped.Add((c, why)); continue; }
            if (week.LeftThisWeek <= 0) { skipped.Add((c, "done for this week")); continue; }

            var possible = week.Requests
                .Where(r => r != null && options.Kinds.Contains(r.Item.Kind))
                .Select(r => (Request: r!, Ability: ability(week, r!)))
                .ToList();
            var doable = possible.Where(p => p.Ability.Possible).ToList();
            if (options.PreferCraftGather && doable.Any(p => p.Request.Item.Kind != DeliveryKind.Fish))
                doable = doable.Where(p => p.Request.Item.Kind != DeliveryKind.Fish).ToList();
            if (doable.Count == 0)
            {
                var reason = possible.Select(p => p.Ability.Why).FirstOrDefault(w => w != null)
                             ?? "none of this week's requests can be made yet";
                skipped.Add((c, reason));
                continue;
            }

            var best = doable.OrderByDescending(p => p.Request.Bonus)
                .ThenByDescending(p => Value(p.Request, Tier(p.Ability, week, options), options.Goal))
                .ThenBy(p => p.Request.Item.Kind) // crafted before gathered: the components are bought next to the client
                .First();
            // Ranking up: up to the next level (the requests change there), then the run plans again.
            var count = options.Goal == DeliveryGoal.RankUp && !week.Maxed && week.ToNextLevel > 0
                ? Math.Min(week.LeftThisWeek, week.ToNextLevel)
                : week.LeftThisWeek;
            picks.Add((week, best.Request, best.Ability, count));
        }

        // Ranking up: the clients below the top level first (they have no bonus requests: those come at the top level
        // only), then the top-level ones with what's left, bonus first. The other goals: bonus first.
        var ordered = picks
            .OrderBy(p => options.Goal == DeliveryGoal.RankUp ? (p.Week.Maxed ? 1 : 0) : 0)
            .ThenByDescending(p => p.Request.Bonus)
            .ThenBy(p => options.Goal == DeliveryGoal.RankUp && !p.Week.Maxed ? p.Week.Rank : 0)
            .ThenBy(p => options.Goal == DeliveryGoal.RankUp && !p.Week.Maxed ? p.Week.Satisfaction : 0)
            .ThenByDescending(p => Value(p.Request, Tier(p.Ability, p.Week, options), options.Goal))
            .ThenBy(p => p.Week.Client.Id)
            .ToList();

        var held = new Dictionary<byte, int>(scrips);
        var left = remaining;
        var deliveries = new List<PlannedDelivery>();
        foreach (var (week, request, can, count) in ordered)
        {
            if (left <= 0) { skipped.Add((week.Client, "no deliveries left this week")); continue; }
            var tier = Tier(can, week, options);
            var pay = request.Reward;
            var times = pay is { BonusMultiplier: > 0 } ? pay.BonusMultiplier / 100.0 : 1.0;
            var scripsEach = pay == null ? 0 : (int)Math.Floor(pay.First[tier] * times);
            var gilEach = pay == null ? 0 : (int)Math.Floor(pay.Gil[tier] * times);
            var currency = pay?.FirstCurrency ?? 0;
            var n = Math.Min(count, left);

            // The scrip cap: what fits, unless ranking up and told to deliver anyway.
            var anyway = options.Goal == DeliveryGoal.RankUp && options.DeliverAnywayWhileRanking && !week.Maxed;
            if (!anyway && scripsEach > 0)
            {
                var have = held.GetValueOrDefault(currency);
                var fits = Math.Max(0, (options.ScripCap - have) / scripsEach);
                if (fits < n)
                {
                    if (fits == 0) { skipped.Add((week.Client, "its scrips would go over the cap")); continue; }
                    n = fits;
                }
            }
            if (scripsEach > 0) held[currency] = held.GetValueOrDefault(currency) + scripsEach * n;
            deliveries.Add(new PlannedDelivery(week, request, n, tier, can.ClassJob, scripsEach, currency, gilEach));
            left -= n;
        }
        return new DeliveryPlan(deliveries, skipped);
    }

    /// <summary>Ranking up takes the lowest tier (satisfaction is the same at every tier); the other goals the best one reached reliably.</summary>
    private static int Tier(DeliveryAbility can, ClientWeek week, DeliveryOptions options)
        => options.Goal == DeliveryGoal.RankUp && !week.Maxed ? 0 : Math.Clamp(can.BestTier, 0, 2);

    /// <summary>What a delivery is worth for the goal (ranking up: satisfaction, equal for every request of a client).</summary>
    private static double Value(DeliveryRequest r, int tier, DeliveryGoal goal)
    {
        if (r.Reward is not { } pay) return 0;
        var times = pay.BonusMultiplier > 0 ? pay.BonusMultiplier / 100.0 : 1.0;
        return goal switch
        {
            DeliveryGoal.Gil => pay.Gil[tier] * times,
            DeliveryGoal.Scrips => pay.First[tier] * times,
            _ => pay.First[tier] * times,
        };
    }
}
