using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Provenance.Core.Tribes;

/// <summary>
/// An NPC or object that warps the character after a Yes/No question: into a sub-zone (Zirgorteh the Open-armed,
/// Elysion), back out, or across a zone to a part walking can't reach (A-4 Research, Hoper's Hold).
/// </summary>
/// <param name="Territory">Where it stands.</param>
/// <param name="Holder">Its NPC or object id.</param>
/// <param name="Npc">An NPC (otherwise an event object).</param>
/// <param name="At">Where it stands.</param>
/// <param name="Warp">The Warp row (its question is what the game asks).</param>
/// <param name="To">The zone the warp goes to (the same zone for a warp within it).</param>
/// <param name="Arrival">Where the warp lands, when known.</param>
public sealed record Portal(uint Territory, uint Holder, bool Npc, Vector3 At, uint Warp, uint To, Vector3? Arrival)
{
    public bool WithinZone => To == Territory;
}

/// <summary>The first leg of a trip that needs a portal.</summary>
/// <param name="Portal">The portal to take.</param>
/// <param name="Elsewhere">It stands in another zone: get to that zone (and to it) first.</param>
public sealed record PortalLeg(Portal Portal, bool Elsewhere);

/// <summary>
/// When a trip needs a portal, and which: a zone without an aetheryte is entered through the portal that leads there
/// (from where the character is, or from a zone a teleport reaches); within a zone, a portal is taken when walking
/// to it and on from where it lands is much shorter than walking there (the parts of a zone only a portal reaches,
/// such as A-4 Research and Hoper's Hold).
/// </summary>
public static class PortalRoute
{
    /// <summary>What a portal costs over walking (talking, the question, the fade), counted as yalms.</summary>
    public const float PortalCost = 30f;
    /// <summary>A portal within the zone is taken when the way through it is at most this share of walking straight there.</summary>
    public const float ShortcutShare = 0.5f;

    /// <summary>
    /// A warp the trips may take: free (not a ferry or an airship that costs gil), open to everyone (no quest it needs:
    /// some warps show only during a story quest, such as Kholusia's "Return to the soft-faced bloke?"), and in or out
    /// of a zone where the run's society dailies take place. tools/QuestPortals builds the shipped table with it.
    /// </summary>
    /// <param name="territory">Where the warp's holder stands.</param>
    /// <param name="to">Where the warp goes.</param>
    /// <param name="gil">What the warp costs (<c>WarpCondition.Gil</c>).</param>
    /// <param name="needsQuest">The warp asks for quests (<c>WarpCondition.RequiredQuest*</c>).</param>
    /// <param name="societyZones">The zones of the dailies the run does (their givers, to-do spots and script zones).</param>
    public static bool Usable(uint territory, uint to, int gil, bool needsQuest, IReadOnlySet<uint> societyZones)
        => gil == 0 && !needsQuest && (societyZones.Contains(territory) || societyZones.Contains(to));

    /// <param name="canTeleport">The character can teleport to that zone (it has an unlocked aetheryte).</param>
    /// <param name="unusable">Portals that didn't work this trip (no question came, or the warp didn't happen).</param>
    /// <returns>The portal to take first, or null to go the ordinary way (walk, or teleport and walk).</returns>
    public static PortalLeg? First(uint fromZone, Vector3 from, uint toZone, Vector3 to, IReadOnlyList<Portal> portals,
        System.Func<uint, bool> canTeleport, IReadOnlySet<(uint, uint)>? unusable = null)
    {
        bool Usable(Portal p) => unusable == null || !unusable.Contains((p.Holder, p.Warp));

        if (fromZone != toZone)
        {
            // Into a zone a teleport can't reach, or out of one (a sub-zone) to the zone its exit leads to: the portal
            // here. Otherwise, where a teleport goes it's the ordinary way; that zone's own portals are worked out on
            // arrival.
            var here = portals.Where(p => Usable(p) && p.Territory == fromZone && p.To == toZone)
                .OrderBy(p => Vector3.DistanceSquared(p.At, from)).FirstOrDefault();
            if (here != null && (!canTeleport(toZone) || !canTeleport(fromZone))) return new PortalLeg(here, false);
            if (canTeleport(toZone)) return null;
            var there = portals.Where(p => Usable(p) && p.To == toZone && p.Territory != fromZone && canTeleport(p.Territory)).FirstOrDefault();
            if (there != null) return new PortalLeg(there, true);
            // Out of a sub-zone toward somewhere a teleport can't go either: through the exit to a zone it can.
            var exit = portals.Where(p => Usable(p) && p.Territory == fromZone && p.To != fromZone && canTeleport(p.To))
                .OrderBy(p => Vector3.DistanceSquared(p.At, from)).FirstOrDefault();
            return exit != null ? new PortalLeg(exit, false) : null;
        }

        var straight = Vector3.Distance(from, to);
        PortalLeg? best = null;
        var bestCost = straight * ShortcutShare;
        foreach (var p in portals)
        {
            if (!p.WithinZone || p.Territory != fromZone || p.Arrival is not { } lands || !Usable(p)) continue;
            var cost = Vector3.Distance(from, p.At) + PortalCost + Vector3.Distance(lands, to);
            if (cost < bestCost && Vector3.Distance(lands, to) < straight)
            {
                bestCost = cost;
                best = new PortalLeg(p, false);
            }
        }
        return best;
    }
}
