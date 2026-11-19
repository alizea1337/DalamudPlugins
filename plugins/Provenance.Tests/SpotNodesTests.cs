using System.Collections.Generic;
using System.Numerics;
using Provenance.Core.Fishing;
using Xunit;

namespace Provenance.Tests;

public class SpotNodesTests
{
    [Fact]
    public void Three_points_cycle_without_going_back_to_the_last_two()
    {
        var points = new uint[] { 1, 2, 3 };
        var recent = new List<uint>();
        Assert.Equal(new uint[] { 1, 2, 3 }, SpotNodes.Allowed(points, recent));
        SpotNodes.Remember(recent, 1);
        Assert.Equal(new uint[] { 2, 3 }, SpotNodes.Allowed(points, recent));
        SpotNodes.Remember(recent, 2);
        Assert.Equal(new uint[] { 3 }, SpotNodes.Allowed(points, recent));
        SpotNodes.Remember(recent, 3);
        Assert.Equal(new uint[] { 1 }, SpotNodes.Allowed(points, recent));
        Assert.Equal(new uint[] { 2, 3 }, recent);
    }

    [Fact]
    public void Small_spots_always_leave_a_point_open()
    {
        Assert.Equal(0, SpotNodes.KeepOut(1));
        Assert.Equal(1, SpotNodes.KeepOut(2));
        Assert.Equal(2, SpotNodes.KeepOut(3));
        Assert.Equal(2, SpotNodes.KeepOut(6));

        // A swimming-shadows spot has one point: it can be opened again.
        var recent = new List<uint>();
        SpotNodes.Remember(recent, 9);
        Assert.Equal(new uint[] { 9 }, SpotNodes.Allowed(new uint[] { 9 }, recent));
        // Two points: only the last one is kept out.
        SpotNodes.Remember(recent, 7);
        Assert.Equal(new uint[] { 9 }, SpotNodes.Allowed(new uint[] { 7, 9 }, recent));
    }

    [Fact]
    public void A_point_emptied_again_moves_to_the_end()
    {
        var recent = new List<uint> { 1, 2 };
        SpotNodes.Remember(recent, 1);
        Assert.Equal(new uint[] { 2, 1 }, recent);
    }

    [Fact]
    public void Exploring_goes_to_the_nearest_position_not_seen()
    {
        var positions = new[] { new Vector3(0, 0, 0), new Vector3(50, 0, 0), new Vector3(100, 0, 0) };
        Assert.Equal(1, SpotNodes.NextToExplore(positions, new Vector3(60, 0, 0), _ => false));
        Assert.Equal(2, SpotNodes.NextToExplore(positions, new Vector3(60, 0, 0), i => i == 1));
        Assert.Equal(0, SpotNodes.NextToExplore(positions, null, _ => false));
        Assert.Equal(-1, SpotNodes.NextToExplore(positions, null, _ => true));
    }

    [Fact]
    public void Reach_is_across_and_up_or_down()
    {
        var node = new Vector3(0, 0, 0);
        Assert.True(SpotNodes.InReach(node, new Vector3(3, 2, 0)));
        Assert.False(SpotNodes.InReach(node, new Vector3(3.3f, 0, 0)));
        Assert.False(SpotNodes.InReach(node, new Vector3(1, 2.8f, 0)));
    }
}
