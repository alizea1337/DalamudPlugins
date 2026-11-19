using System;
using System.Collections.Generic;
using System.Numerics;

namespace Provenance.Core.Fishing;

/// <summary>
/// Where to stand at a fishing hole: the first stand to try (the nearest that hasn't failed; the player's own
/// learned stands come first in the list and win ties), a stand far enough away when the fish turn wary,
/// being at a stand (across the ground plus a height check, so a pier and the beach under it differ), facing
/// the water within a degree, and the one-yalm step forward when a cast doesn't work. Follows GatherBuddy
/// Reborn's fishing approach (AutoGather, FishRecorder; Apache-2.0). Pure.
/// </summary>
public static class SpotStands
{
    /// <summary>A stand this far from the old one counts as another position for wary fish (GBR's MinRelocationDistance).</summary>
    public const float MinRelocation = 10f;

    /// <summary>At the stand: this close across the ground (vnavmesh stops within about a yalm).</summary>
    public const float StandRange = 1.5f;

    /// <summary>At the stand: at most this far above or below (another level, a pier over the water, is further).</summary>
    public const float StandHeight = 2f;

    /// <summary>Facing counts as right within one degree (GBR).</summary>
    public const float FacingTolerance = MathF.PI / 180f;

    /// <summary>Distance across the ground (X and Z).</summary>
    public static float Across(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));

    /// <summary>Standing on the stand: within <see cref="StandRange"/> across and <see cref="StandHeight"/> up or down.</summary>
    public static bool At(Vector3 me, Vector3 stand) => Across(me, stand) <= StandRange && MathF.Abs(me.Y - stand.Y) <= StandHeight;

    /// <summary>The angle between two facings in radians, 0 to π (wrapping around).</summary>
    public static float AngleBetween(float a, float b)
    {
        var d = (a - b) % (2 * MathF.PI);
        if (d > MathF.PI) d -= 2 * MathF.PI;
        else if (d < -MathF.PI) d += 2 * MathF.PI;
        return MathF.Abs(d);
    }

    /// <summary>The player faces the way the stand was cast from, within <see cref="FacingTolerance"/>.</summary>
    public static bool Faces(float rotation, float facing) => AngleBetween(rotation, facing) <= FacingTolerance;

    /// <summary>
    /// The point <paramref name="yalms"/> ahead of a position, facing <paramref name="rotation"/> (the game's
    /// rotation: 0 faces +Z, π/2 faces +X).
    /// </summary>
    public static Vector3 Ahead(Vector3 position, float rotation, float yalms = 1f)
        => position + new Vector3(MathF.Sin(rotation), 0, MathF.Cos(rotation)) * yalms;

    /// <summary>
    /// The stand to try first: the nearest to <paramref name="from"/> (the player, or the aetheryte a teleport
    /// lands at), skipping <paramref name="skip"/>; with no reference, the first one not skipped. -1 when every
    /// stand is skipped.
    /// </summary>
    public static int First(IReadOnlyList<Vector3> stands, Vector3? from, Func<int, bool> skip)
    {
        var best = -1;
        var bestDistance = float.MaxValue;
        for (var i = 0; i < stands.Count; i++)
        {
            if (skip(i)) continue;
            if (from is not { } f) return i;
            var d = Vector3.DistanceSquared(stands[i], f);
            if (d < bestDistance) { best = i; bestDistance = d; }
        }
        return best;
    }

    /// <summary>
    /// A stand away from <paramref name="from"/> after the fish turned wary there: the nearest one at least
    /// <paramref name="minDistance"/> away; when none is that far, the farthest other one. -1 when there is no
    /// other stand (stands within a yalm of <paramref name="from"/> are the same place).
    /// </summary>
    public static int Relocate(IReadOnlyList<Vector3> stands, Vector3 from, Func<int, bool> skip, float minDistance = MinRelocation)
    {
        int near = -1, far = -1;
        float nearDistance = float.MaxValue, farDistance = 1f;
        for (var i = 0; i < stands.Count; i++)
        {
            if (skip(i)) continue;
            var d = Vector3.Distance(stands[i], from);
            if (d >= minDistance && d < nearDistance) { near = i; nearDistance = d; }
            if (d > farDistance) { far = i; farDistance = d; }
        }
        return near >= 0 ? near : far;
    }
}
