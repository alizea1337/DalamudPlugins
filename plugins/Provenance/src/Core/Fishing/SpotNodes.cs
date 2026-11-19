using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Provenance.Core.Fishing;

/// <summary>
/// Moving between a spearfishing spot's nodes. A spot has a few gathering points and its node is up at one of
/// them at a time; after a node is used up it comes back at another point. So the run never goes back to the
/// last two points it emptied (fewer when the spot has fewer points: a swimming-shadows spot has one), and
/// when no node is in view it looks at the known positions one by one, nearest first. Follows GatherBuddy
/// Reborn's spearfishing movement (AutoGather VisitedNodes, FarNodesSeenSoFar; Apache-2.0). Pure.
/// </summary>
public static class SpotNodes
{
    /// <summary>Points to keep out after emptying them, for a spot with this many points: two, but always leave one.</summary>
    public static int KeepOut(int pointCount) => Math.Clamp(pointCount - 1, 0, 2);

    /// <summary>
    /// The spot's points a node may be at now: all but the most recently emptied ones
    /// (<paramref name="recent"/>, newest last; see <see cref="KeepOut"/>).
    /// </summary>
    public static List<uint> Allowed(IReadOnlyList<uint> points, IReadOnlyList<uint> recent)
    {
        var keepOut = KeepOut(points.Count);
        var excluded = new HashSet<uint>();
        for (var i = recent.Count - 1; i >= 0 && excluded.Count < keepOut; i--) excluded.Add(recent[i]);
        return points.Where(p => !excluded.Contains(p)).ToList();
    }

    /// <summary>Notes a point whose node was just used up (newest last); only the last two are kept.</summary>
    public static void Remember(List<uint> recent, uint point)
    {
        recent.Remove(point);
        recent.Add(point);
        while (recent.Count > 2) recent.RemoveAt(0);
    }

    /// <summary>
    /// The known position to look at next when no node is in view: the nearest to <paramref name="from"/>
    /// not seen in this round (the first one without a reference); -1 when every one was seen.
    /// </summary>
    public static int NextToExplore(IReadOnlyList<Vector3> positions, Vector3? from, Func<int, bool> seen)
    {
        var best = -1;
        var bestDistance = float.MaxValue;
        for (var i = 0; i < positions.Count; i++)
        {
            if (seen(i)) continue;
            if (from is not { } f) return i;
            var d = Vector3.DistanceSquared(positions[i], f);
            if (d < bestDistance) { best = i; bestDistance = d; }
        }
        return best;
    }

    /// <summary>Close enough to a known position to see whether a node is up there (nodes show in the object table well before this).</summary>
    public const float SeenFrom = 40f;

    /// <summary>A node opens from under 3.5 yalms across and under 3 up or down (GBR); a little margin keeps clear of the edge.</summary>
    public static bool InReach(Vector3 node, Vector3 me)
        => Vector2.Distance(new Vector2(node.X, node.Z), new Vector2(me.X, me.Z)) < 3.2f && MathF.Abs(node.Y - me.Y) < 2.7f;
}
