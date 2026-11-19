using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Provenance.Data;
using Provenance.Integrations;

namespace Provenance.Run.Fishing;

/// <summary>
/// Where the fisher buys bait mid-run: a gil vendor under the price cap in a zone the character can get to,
/// the one closest to an aetheryte (the same choice as the run's vendor stage, <c>RunEngine.Purchases</c>).
/// Offers are remembered for a minute; finding them reads the teleport list.
/// </summary>
internal sealed class BaitShop
{
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(1);

    private readonly Plugin plugin;
    private readonly Dictionary<uint, (DateTime At, VendorOffer? Offer)> offers = new();

    public BaitShop(Plugin plugin) => this.plugin = plugin;

    public void Clear() => offers.Clear();

    /// <summary>The vendor to buy an item from, or null when no reachable gil vendor sells it under the cap.</summary>
    public VendorOffer? Offer(uint itemId)
    {
        if (itemId == 0) return null;
        if (offers.TryGetValue(itemId, out var known) && DateTime.UtcNow - known.At < Keep) return known.Offer;
        var offer = Find(itemId);
        offers[itemId] = (DateTime.UtcNow, offer);
        return offer;
    }

    private VendorOffer? Find(uint itemId)
    {
        if (plugin.Plans.Catalog is not { } catalog) return null;
        var cap = plugin.Config.Buying.VendorMaxPrice;
        var reach = new Dictionary<uint, bool>();
        var candidates = catalog.Vendors.For(itemId)
            .Where(o => cap == 0 || o.Price <= cap)
            .Where(o => reach.TryGetValue(o.TerritoryId, out var r) ? r : reach[o.TerritoryId] = Teleporter.CanReach(o.TerritoryId))
            .ToList();
        if (candidates.Count == 0) return null;

        var unlocked = Teleporter.Unlocked();
        var here = Services.ClientState.TerritoryType;
        float Score(VendorOffer o)
        {
            if (o.TerritoryId == here && Services.Objects.LocalPlayer is { } me)
                return Vector2.Distance(new Vector2(me.Position.X, me.Position.Z), o.Position) - 10000;
            var best = float.MaxValue;
            foreach (var d in unlocked.Where(d => d.TerritoryId == o.TerritoryId))
                if (Teleporter.Position(d.AetheryteId) is { } a)
                    best = Math.Min(best, Vector2.Distance(new Vector2(a.X, a.Z), o.Position));
            // Areas reached through the aethernet: a bit further than a direct teleport.
            return best == float.MaxValue ? 5000 : best;
        }
        return candidates.OrderBy(Score).First();
    }
}
