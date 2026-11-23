using System;
using System.Collections.Generic;
using System.Linq;

namespace Provenance.Core.Deliveries;

/// <summary>
/// The week's requests of a client, worked out the way the game does it: a seeded weighted pick per slot from the
/// supply row of the client's level, then the week's bonus guarantee. At levels 1-4 each slot has a single item, so
/// the pick is fixed; at level 5 it changes every week.
/// </summary>
/// <remarks>
/// The game's own algorithm (an xorshift128-style hash of the supply row and the weekly seed, then a walk over the
/// slot's weights) as described by the custom-delivery research. Its only public description is
/// reverse-engineered (ffxiv_satisfy, which has no licence), so this is written from the description, not copied.
/// It is checked against the game whenever a client's delivery window is open.
/// </remarks>
public static class DeliveryPick
{
    /// <summary>Tuesday 2022-07-05 08:00 UTC: the week the bonus guarantee rotation counts from.</summary>
    public const long GuaranteeEpoch = 1657008000;
    public const long Week = 604800;

    /// <summary>Craft, gather and fish picks from a supply row's candidates (null for a slot with none).</summary>
    public static DeliveryCandidate?[] Pick(int supplyIndex, byte seed, IReadOnlyList<DeliveryCandidate> candidates)
    {
        unchecked
        {
            var s = (uint)supplyIndex;
            uint h1 = (0x03CEA65Cu * s) ^ (0x1A0DD20Eu * seed);
            uint h2 = (0xDF585D5Du * s) ^ (0x3057656Eu * seed);
            uint h3 = (0xED69E442u * s) ^ (0x2202EA5Au * seed);
            uint h4 = (0xAEFC3901u * s) ^ (0xE70723F6u * seed);
            var h5 = h1;
            var picks = new DeliveryCandidate?[3];
            for (var slot = 1; slot <= 3; slot++)
            {
                var inSlot = candidates.Where(c => (int)c.Kind == slot).OrderBy(c => c.SubRow).ToList();
                var sum = (uint)inSlot.Sum(c => Math.Max(0, c.Weight));
                var t = h5 ^ (h5 << 11);
                h1 = h3;
                h3 = h4;
                h5 = h2;
                h4 = h4 ^ t ^ ((t ^ (h4 >> 11)) >> 8);
                h2 = h1;
                if (sum == 0) continue;
                var roll = h4 % sum;
                foreach (var c in inSlot)
                {
                    var w = (uint)Math.Max(0, c.Weight);
                    if (roll < w) { picks[slot - 1] = c; break; }
                    roll -= w;
                }
            }
            return picks;
        }
    }

    /// <summary>The week's row of the bonus guarantee rotation, from the server time.</summary>
    public static int GuaranteeRow(long serverUnixSeconds, int adjustment, int rowCount)
        => rowCount <= 0 ? 0 : (int)(Math.Max(0, serverUnixSeconds + adjustment - GuaranteeEpoch) / Week % rowCount);

    /// <summary>
    /// A client's requests this week: the seeded pick at its level, and at the top level the bonus item for each kind
    /// the week's guarantee names this client in.
    /// </summary>
    /// <param name="guaranteed">Kinds whose bonus item this client is guaranteed this week.</param>
    public static DeliveryRequest?[] Requests(DeliveryClient client, int rank, byte seed, IReadOnlyList<DeliveryCandidate> supply,
        IReadOnlySet<DeliveryKind> guaranteed, Func<uint, DeliveryReward?> reward)
    {
        var result = new DeliveryRequest?[3];
        if (client.Level(rank) is not { } level) return result;
        var picks = Pick(level.SupplyIndex, seed, supply);
        for (var i = 0; i < 3; i++)
        {
            var kind = (DeliveryKind)(i + 1);
            var pick = picks[i];
            if (rank >= DeliveryClient.TopLevel && guaranteed.Contains(kind)
                && supply.FirstOrDefault(c => c.Kind == kind && c.IsBonus) is { } bonus)
                pick = bonus;
            if (pick == null) continue;
            result[i] = new DeliveryRequest(pick, reward(pick.RewardRow), pick.IsBonus);
        }
        return result;
    }
}
