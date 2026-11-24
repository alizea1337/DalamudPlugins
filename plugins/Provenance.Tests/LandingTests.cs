using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Provenance.Core.Run;
using Xunit;

namespace Provenance.Tests;

public class LandingTests
{
    // Templeton in the Observatorium (Coerthas Central Highlands): the ground floor of a building.
    private static readonly Vector3 Target = new(-240, 280, 330);

    /// <summary>A building reaching 12 yalms each way from the target (its roof covers everything inside), open ground around it.</summary>
    private static bool UnderTheRoof(Vector3 p) => MathF.Abs(p.X - Target.X) < 12 && MathF.Abs(p.Z - Target.Z) < 12;

    [Fact]
    public void TheWalkInComesOutOfTheDoorNotTheWallNearestTheOpenSpot()
    {
        // The open spot is behind the building (south, -Z); the door is on its north side (+Z). The walking path goes
        // round the building and in through the door, so the landing is just outside the door, 2 yalms out.
        var path = new List<Vector3>
        {
            Target + new Vector3(0, 0, -16),   // the open spot behind it
            Target + new Vector3(16, 0, -16),
            Target + new Vector3(16, 0, 16),
            Target + new Vector3(0, 0, 16),    // in front of the door
            Target + new Vector3(0, 0, 0),     // the target, inside
        };
        var landing = Landing.DoorOut(path, UnderTheRoof);
        Assert.NotNull(landing);
        Assert.False(UnderTheRoof(landing!.Value));
        Assert.Equal(Target.X, landing.Value.X, 3);
        Assert.InRange(landing.Value.Z - Target.Z, 12 + Landing.PastDoor - 0.01f, 12 + Landing.PastDoor + Landing.DoorStep + 0.01f);
    }

    [Fact]
    public void AShortWalkOutLandsOnItsStartAndACoveredOneHasNoLanding()
    {
        // Out of the door with less than 2 yalms of path left: the first open point.
        var shortPath = new List<Vector3> { Target + new Vector3(0, 0, 12.5f), Target };
        Assert.Equal(Target + new Vector3(0, 0, 12), Landing.DoorOut(shortPath, UnderTheRoof));
        var covered = new List<Vector3> { Target + new Vector3(0, 0, 5), Target };
        Assert.Null(Landing.DoorOut(covered, UnderTheRoof));
        Assert.Null(Landing.DoorOut(new List<Vector3> { Target }, UnderTheRoof));
    }

    [Fact]
    public void ARingIsTwelvePointsAtItsRadiusAndTheTargetsHeight()
    {
        var ring = Landing.Ring(Target, 10).ToList();
        Assert.Equal(Landing.Directions, ring.Count);
        Assert.All(ring, p =>
        {
            Assert.Equal(Target.Y, p.Y);
            Assert.Equal(10f, Landing.Flat(p, Target), 3);
        });
        Assert.Equal(12, ring.Select(p => (MathF.Round(p.X, 2), MathF.Round(p.Z, 2))).Distinct().Count());
    }

    [Fact]
    public void TheNearestOpenSpotOnTheFirstRingThatHasOneIsTaken()
    {
        var search = new LandingSearch(Target);
        var steps = 0;
        while (!search.Done)
        {
            search.Step(p => p, UnderTheRoof);
            steps++;
        }
        // The 6- and 10-yalm rings are under the roof; the 15-yalm ring is outside the walls.
        Assert.NotNull(search.Found);
        Assert.False(UnderTheRoof(search.Found!.Value));
        Assert.Equal(3, steps);
        Assert.InRange(Vector3.Distance(search.Found.Value, Target), 14.9f, 15.1f);
    }

    [Fact]
    public void ASpotFoundOnTheMeshNearASampleCounts()
    {
        // The mesh only outside the building's east door: every sample within reach of it snaps to it.
        var door = Target + new Vector3(13, 0.4f, 0);
        var search = new LandingSearch(Target);
        while (!search.Done) search.Step(p => Vector3.Distance(p, door) < 9 ? door : null, UnderTheRoof);
        Assert.Equal(door, search.Found);
        Assert.Equal(1, search.Rings);
        // Three samples of the first ring snap to it; it's checked for a roof once.
        Assert.Equal(Landing.Directions, search.Lookups);
        Assert.Equal(1, search.RoofChecks);
    }

    [Fact]
    public void SpotsFarAboveOrBelowTheTargetAreLeftOut()
    {
        // Only the roof (12 up) and the cellar (11 down) are open: another floor, not a way in.
        var search = new LandingSearch(Target);
        var flip = false;
        while (!search.Done)
            search.Step(p => (flip = !flip) ? p + new Vector3(0, 12, 0) : p - new Vector3(0, 11, 0), _ => false);
        Assert.Null(search.Found);
        Assert.True(search.OffLevel > 0);
        Assert.Equal(0, search.RoofChecks);
    }

    [Fact]
    public void NothingOpenWithinFortyYalmsFindsNothing()
    {
        var search = new LandingSearch(Target);
        var lookups = new List<int>();
        while (!search.Done)
        {
            var before = search.Lookups;
            search.Step(p => p, _ => true);
            lookups.Add(search.Lookups - before);
        }
        Assert.Null(search.Found);
        Assert.Equal(Landing.Radii.Length, search.Rings);
        // One ring per step: twelve lookups at most.
        Assert.All(lookups, n => Assert.Equal(Landing.Directions, n));
        Assert.Equal(Landing.Radii.Length * Landing.Directions, search.Covered);
    }

    [Fact]
    public void ADoneSearchAsksNothingMore()
    {
        var search = new LandingSearch(Target);
        search.Step(p => p, _ => false);
        Assert.True(search.Done);
        var asked = 0;
        search.Step(p => { asked++; return p; }, _ => false);
        Assert.Equal(0, asked);
    }

    [Theory]
    [InlineData(0f, 0f, true)]
    [InlineData(1.9f, 0f, true)]
    [InlineData(1.5f, 1.5f, false)]
    [InlineData(3f, 0f, false)]
    public void GettingOffInTheAirOnlyRightAboveTheSpot(float dx, float dz, bool safe)
        // Height doesn't matter: the mount comes straight down.
        => Assert.Equal(safe, Landing.RightAboveIt(Target + new Vector3(dx, 25, dz), Target));
}
