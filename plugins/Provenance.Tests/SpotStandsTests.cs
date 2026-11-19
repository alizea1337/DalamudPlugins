using System;
using System.Numerics;
using Provenance.Core.Fishing;
using Xunit;

namespace Provenance.Tests;

public class SpotStandsTests
{
    private static readonly Vector3[] Stands =
    {
        new(0, 0, 0),
        new(4, 0, 0),
        new(15, 0, 0),
        new(30, 0, 0),
    };

    [Fact]
    public void First_is_the_nearest_stand_that_has_not_failed()
    {
        Assert.Equal(2, SpotStands.First(Stands, new Vector3(14, 0, 1), _ => false));
        Assert.Equal(1, SpotStands.First(Stands, new Vector3(14, 0, 1), i => i == 2));
        Assert.Equal(3, SpotStands.First(Stands, new Vector3(26, 0, 1), i => i == 2));
        // No reference point (another zone): the list order, the player's own stands first.
        Assert.Equal(0, SpotStands.First(Stands, null, _ => false));
        Assert.Equal(1, SpotStands.First(Stands, null, i => i == 0));
        Assert.Equal(-1, SpotStands.First(Stands, null, _ => true));
    }

    [Fact]
    public void Wary_fish_move_the_fisher_at_least_ten_yalms()
    {
        // From stand 0: stand 1 is only 4 away, stand 2 is the nearest far enough.
        Assert.Equal(2, SpotStands.Relocate(Stands, Stands[0], i => i == 0));
        // None far enough: the farthest other stand.
        var close = new[] { new Vector3(0, 0, 0), new Vector3(3, 0, 0), new Vector3(6, 0, 0) };
        Assert.Equal(2, SpotStands.Relocate(close, close[0], _ => false));
        // Only the same place left: nowhere to go.
        var one = new[] { new Vector3(0, 0, 0), new Vector3(0.5f, 0, 0) };
        Assert.Equal(-1, SpotStands.Relocate(one, one[0], _ => false));
    }

    [Fact]
    public void Being_at_a_stand_checks_the_height_too()
    {
        var stand = new Vector3(10, 5, 10);
        Assert.True(SpotStands.At(new Vector3(11, 5.5f, 10.5f), stand));
        Assert.False(SpotStands.At(new Vector3(12, 5, 10), stand));       // 2 yalms across
        Assert.False(SpotStands.At(new Vector3(10, 1, 10), stand));       // under the pier
        Assert.Equal(5f, SpotStands.Across(new Vector3(0, 100, 0), new Vector3(3, -7, 4)), 3);
    }

    [Fact]
    public void Facing_wraps_around_and_allows_a_degree()
    {
        Assert.True(SpotStands.Faces(MathF.PI - 0.001f, -MathF.PI + 0.001f));
        Assert.True(SpotStands.Faces(0.3f, 0.3f + 0.01f));
        Assert.False(SpotStands.Faces(0.3f, 0.3f + 0.05f));
        Assert.Equal(0.2f, SpotStands.AngleBetween(0.1f, -0.1f), 4);
        Assert.Equal(0.2f, SpotStands.AngleBetween(-0.1f + 4 * MathF.PI, 0.1f), 3);
        Assert.Equal(MathF.PI, SpotStands.AngleBetween(MathF.PI / 2, -MathF.PI / 2), 4);
    }

    [Fact]
    public void Ahead_follows_the_game_rotation()
    {
        var p = new Vector3(1, 2, 3);
        var south = SpotStands.Ahead(p, 0);
        Assert.Equal(1f, south.X, 4);
        Assert.Equal(2f, south.Y, 4);
        Assert.Equal(4f, south.Z, 4);
        var east = SpotStands.Ahead(p, MathF.PI / 2, 2);
        Assert.Equal(3f, east.X, 4);
        Assert.Equal(3f, east.Z, 4);
    }
}
