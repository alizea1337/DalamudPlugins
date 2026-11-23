using System;
using System.Collections.Generic;
using System.Numerics;

namespace Provenance.Core.Run;

/// <summary>
/// Getting down to a target under a roof, or up on a raised floor, without dropping beside it: where to land near a
/// covered target (an open spot on the walkable mesh around it, so the character walks in through the door instead of
/// landing on the roof or outside the walls), and when getting off in the air is safe (only right above the spot: the
/// mount comes straight down).
/// </summary>
public static class Landing
{
    /// <summary>The rings searched around a covered target, nearest first, in yalms.</summary>
    public static readonly float[] Radii = { 6f, 10f, 15f, 22f, 30f, 40f };
    /// <summary>Directions sampled on each ring.</summary>
    public const int Directions = 12;
    /// <summary>A landing spot is this far above or below the target at most (more is another floor, or the roof).</summary>
    public const float MaxHeightOff = 10f;
    /// <summary>Within this far of a spot on the ground, the character is right above it.</summary>
    public const float RightAbove = 2f;
    /// <summary>A roof is looked for this far up, starting this far above the spot (above a standing character's head).</summary>
    public const float RoofLookUp = 60f;
    public const float RoofLookFrom = 2f;

    /// <summary>A ring's sample points around the target, at its height, a twelfth of a turn apart.</summary>
    public static IEnumerable<Vector3> Ring(Vector3 target, float radius)
    {
        for (var i = 0; i < Directions; i++)
        {
            var angle = i * MathF.Tau / Directions;
            yield return target + new Vector3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius);
        }
    }

    /// <summary>The character is right above the spot: getting off in the air brings it straight down onto it.</summary>
    public static bool RightAboveIt(Vector3 me, Vector3 spot) => Flat(me, spot) < RightAbove;

    /// <summary>The distance on the ground (height left out).</summary>
    public static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));
}

/// <summary>
/// The search for a spot to land near a target under a roof, one ring per step so the game isn't asked about every
/// point in one frame: each sample is snapped to the walkable mesh, kept when it's near the target's height and open to
/// the sky, and the search stops at the first ring that has one, taking the one nearest the target (a farther ring's
/// spots are farther away).
/// </summary>
public sealed class LandingSearch
{
    private readonly Vector3 target;
    private readonly List<Vector3> checkedPoints = new();
    private int ring;

    public LandingSearch(Vector3 target) => this.target = target;

    public Vector3 Target => target;
    public bool Done { get; private set; }
    /// <summary>The spot to land on; null when none was found (or not yet).</summary>
    public Vector3? Found { get; private set; }
    /// <summary>Lookups on the walkable mesh and roof checks so far (what the search cost).</summary>
    public int Lookups { get; private set; }
    public int RoofChecks { get; private set; }
    /// <summary>Mesh points left out: a roof over them, or too far above or below the target.</summary>
    public int Covered { get; private set; }
    public int OffLevel { get; private set; }
    /// <summary>Rings searched.</summary>
    public int Rings => ring;

    /// <summary>Searches the next ring.</summary>
    /// <param name="snap">The walkable point near a sample (null when there's none).</param>
    /// <param name="roofOver">Something solid is over the point.</param>
    public void Step(Func<Vector3, Vector3?> snap, Func<Vector3, bool> roofOver)
    {
        if (Done) return;
        Vector3? best = null;
        var bestDistance = float.MaxValue;
        foreach (var sample in Landing.Ring(target, Landing.Radii[ring]))
        {
            Lookups++;
            if (snap(sample) is not { } p) continue;
            // The same mesh point from a neighbouring sample is checked once.
            if (checkedPoints.Exists(c => Vector3.DistanceSquared(c, p) < 1f)) continue;
            checkedPoints.Add(p);
            if (MathF.Abs(p.Y - target.Y) > Landing.MaxHeightOff) { OffLevel++; continue; }
            RoofChecks++;
            if (roofOver(p)) { Covered++; continue; }
            var d = Vector3.Distance(p, target);
            if (d < bestDistance) { best = p; bestDistance = d; }
        }
        ring++;
        if (best != null) Found = best;
        Done = best != null || ring >= Landing.Radii.Length;
    }
}
