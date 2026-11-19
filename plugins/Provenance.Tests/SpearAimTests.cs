using System;
using Provenance.Core.Fishing;
using Xunit;

namespace Provenance.Tests;

public class SpearAimTests
{
    // A lane area 400 wide at x 100, in a window at scale 1: its middle is at 300.
    private static readonly SpearLanes Lanes = new(100, 400, 1, 1);
    private static readonly FishInfo Ghost = SpearFishTests.Spear(27518, SpearSize.Small, SpearSpeed.Average); // window: 1, 300

    /// <summary>A 100-wide sprite at <paramref name="x"/>; left to right, x 160 puts its anchor (100 + 160 + 40) on the middle.</summary>
    private static SpearLaneFish Lane(int size, int speed, float x, bool available = true, bool rightToLeft = false)
        => new(available, size, speed, rightToLeft, new SpearSprite(x, 100, 1));

    [Fact]
    public void Middle_of_the_lane_area()
    {
        Assert.Equal(300f, SpearAim.Centre(Lanes));
        Assert.Equal(450f, SpearAim.Centre(Lanes with { UiScale = 1.5f })); // 150 + 300
        Assert.Equal(500f, SpearAim.Centre(Lanes with { ScaleX = 2f })); // 100 + 400
    }

    [Fact]
    public void Aimed_40_percent_in_left_to_right_and_halfway_right_to_left()
    {
        var sprite = new SpearSprite(150, 100, 1);
        Assert.Equal(290f, SpearAim.Anchor(Lanes, sprite, rightToLeft: false));
        Assert.Equal(300f, SpearAim.Anchor(Lanes, sprite, rightToLeft: true));
    }

    [Theory]
    [InlineData(135f, true)] // anchor 275: the left edge
    [InlineData(134f, false)]
    [InlineData(160f, true)] // dead centre
    [InlineData(185f, true)] // anchor 325: the right edge
    [InlineData(186f, false)]
    public void Hitbox_reaches_25_pixels_either_side_edges_included(float x, bool hit)
        => Assert.Equal(hit, SpearAim.InHitbox(Lanes, new SpearSprite(x, 100, 1), rightToLeft: false));

    [Fact]
    public void Window_scale_moves_the_fish_but_not_the_hitbox()
    {
        // At scale 2 the middle is at 600 and a sprite at x aims at 2·(100 + x + 40); the hitbox stays 25 scaled pixels.
        var big = Lanes with { UiScale = 2 };
        Assert.True(SpearAim.InHitbox(big, new SpearSprite(147.5f, 100, 1), false)); // 575
        Assert.False(SpearAim.InHitbox(big, new SpearSprite(147f, 100, 1), false)); // 574
        Assert.True(SpearAim.InHitbox(big, new SpearSprite(172.5f, 100, 1), false)); // 625
        Assert.False(SpearAim.InHitbox(big, new SpearSprite(173f, 100, 1), false)); // 626
    }

    [Fact]
    public void A_mirrored_sprite_aims_back_from_its_x()
    {
        // Swimming right to left with a mirrored sprite (ScaleX -1), AutoHook's anchor runs back from X.
        var sprite = new SpearSprite(250, 100, -1);
        Assert.Equal(300f, SpearAim.Anchor(Lanes, sprite, rightToLeft: true));
        Assert.True(SpearAim.InHitbox(Lanes, sprite, rightToLeft: true));
    }

    [Fact]
    public void Offsets_move_the_anchor_a_tenth_of_the_width_per_unit()
    {
        var sprite = new SpearSprite(150, 100, 1);
        Assert.Equal(280f, SpearAim.Anchor(Lanes, sprite, false, leftOffset: 1), 3);
        Assert.Equal(310f, SpearAim.Anchor(Lanes, sprite, true, rightOffset: 1), 3);
        // Each offset only applies to its own direction.
        Assert.Equal(290f, SpearAim.Anchor(Lanes, sprite, false, rightOffset: 5), 3);
    }

    [Fact]
    public void Nothing_is_in_reach_without_the_nodes()
    {
        Assert.False(SpearAim.InHitbox(default, new SpearSprite(160, 100, 1), false));
        Assert.False(SpearAim.InHitbox(Lanes with { UiScale = 0 }, new SpearSprite(160, 100, 1), false));
        // A sprite of no width would sit on the middle at x 200; it's a missing node, not a fish.
        Assert.False(SpearAim.InHitbox(Lanes, new SpearSprite(200, 0, 1), false));
    }

    [Fact]
    public void Picks_the_lowest_lane_with_a_wanted_fish_in_the_hitbox()
    {
        var wanted = new[] { Ghost };
        Assert.Equal(1, SpearAim.Pick(Lanes, new[] { Lane(1, 300, 0), Lane(1, 300, 160), Lane(1, 300, 160) }, wanted));
        Assert.Equal(0, SpearAim.Pick(Lanes, new[] { Lane(1, 300, 160), Lane(1, 300, 160), Lane(1, 300, 160) }, wanted));
        Assert.Equal(2, SpearAim.Pick(Lanes, new[] { Lane(3, 300, 160), Lane(1, 300, 400), Lane(1, 300, 170) }, wanted));
    }

    [Fact]
    public void Unwanted_absent_or_far_fish_are_left_alone()
    {
        var wanted = new[] { Ghost };
        Assert.Equal(-1, SpearAim.Pick(Lanes, new[] { Lane(2, 300, 160) }, wanted)); // wrong size
        Assert.Equal(-1, SpearAim.Pick(Lanes, new[] { Lane(1, 350, 160) }, wanted)); // wrong speed
        Assert.Equal(-1, SpearAim.Pick(Lanes, new[] { Lane(1, 300, 160, available: false) }, wanted)); // empty lane
        Assert.Equal(-1, SpearAim.Pick(Lanes, new[] { new SpearLaneFish(true, 1, 300, false, null) }, wanted)); // no sprite node
        Assert.Equal(-1, SpearAim.Pick(Lanes, new[] { Lane(1, 300, 100) }, wanted)); // still short of the middle
        Assert.Equal(-1, SpearAim.Pick(default, new[] { Lane(1, 300, 160) }, wanted)); // no lane area
        Assert.Equal(-1, SpearAim.Pick(Lanes, Array.Empty<SpearLaneFish>(), wanted));
    }

    [Fact]
    public void With_nothing_wanted_every_fish_in_the_hitbox_is_gigged()
    {
        var all = Array.Empty<FishInfo>();
        Assert.Equal(0, SpearAim.Pick(Lanes, new[] { Lane(-1, 0, 160) }, all));
        Assert.Equal(1, SpearAim.Pick(Lanes, new[] { Lane(2, 250, 0), Lane(3, 600, 150, rightToLeft: true) }, all));
    }

    [Fact]
    public void A_fish_without_known_size_or_speed_is_never_gigged()
    {
        var mystery = SpearFishTests.Spear(1, SpearSize.Unknown, SpearSpeed.Unknown);
        Assert.Equal(-1, SpearAim.Pick(Lanes, new[] { Lane(-1, 0, 160), Lane(1, 300, 160) }, new[] { mystery }));
    }
}
