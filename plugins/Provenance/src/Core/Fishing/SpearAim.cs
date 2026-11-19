using System.Collections.Generic;

namespace Provenance.Core.Fishing;

/// <summary>
/// The lane area of the SpearFishing window (node 43, "FishLines") as the game lays it out: its X and
/// width in the window, its horizontal scale, and the window's own scale (AtkUnitBase.Scale).
/// </summary>
public readonly record struct SpearLanes(float X, float Width, float ScaleX, float UiScale)
{
    /// <summary>The node was found and has a size (a missing node reads as all zero).</summary>
    public bool Valid => Width > 0 && UiScale > 0;
}

/// <summary>A fish's sprite node (61, 60, 59 for the lanes bottom to top): X inside the lane area, width and horizontal scale.</summary>
public readonly record struct SpearSprite(float X, float Width, float ScaleX);

/// <summary>
/// One lane of the SpearFishing window: whether a fish swims in it, its size and speed as the game gives
/// them, whether it swims right to left, and its sprite (null when the node is missing).
/// </summary>
public readonly record struct SpearLaneFish(bool Available, int Size, int Speed, bool RightToLeft, SpearSprite? Sprite);

/// <summary>
/// When to gig: the moment a wanted fish's anchor point is within the hitbox of the lane area's middle.
/// The anchor sits 40% into the sprite for a fish swimming left to right and 50% for one swimming right
/// to left; positions are in window pixels times the window's scale, and the hitbox (25 by default) is
/// in those scaled pixels. No lead or speed prediction: the gig goes on the first frame the anchor is in.
/// Ported from AutoHook's AutoGig (BSD-3, Puni.sh): GigComponent.IsFishInHitbox, default hitbox 25 with
/// no offsets, as GatherBuddy Reborn's AutoGig presets set it.
/// </summary>
public static class SpearAim
{
    /// <summary>Half the gig window around the middle, in scaled pixels (AutoHook's default).</summary>
    public const float Hitbox = 25f;
    /// <summary>Where along the sprite's width the fish is aimed at, swimming left to right.</summary>
    public const float AnchorLeftToRight = 0.4f;
    /// <summary>Where along the sprite's width the fish is aimed at, swimming right to left.</summary>
    public const float AnchorRightToLeft = 0.5f;

    /// <summary>The lane area's horizontal middle, in scaled pixels.</summary>
    public static float Centre(SpearLanes lanes) => lanes.X * lanes.UiScale + lanes.Width * lanes.ScaleX * lanes.UiScale / 2f;

    /// <summary>
    /// The point of the fish that has to cross the middle, in scaled pixels. The offsets (AutoHook's per-fish
    /// "Offset (L->R)" and "Offset (R->L)") move it by a tenth of the sprite's width per unit.
    /// </summary>
    public static float Anchor(SpearLanes lanes, SpearSprite fish, bool rightToLeft, float leftOffset = 0f, float rightOffset = 0f)
    {
        var along = rightToLeft ? AnchorRightToLeft + rightOffset / 10f : AnchorLeftToRight - leftOffset / 10f;
        return lanes.X * lanes.UiScale + fish.X * lanes.UiScale + fish.Width * fish.ScaleX * lanes.UiScale * along;
    }

    /// <summary>The fish's anchor is within <paramref name="hitbox"/> scaled pixels of the middle (edges included).</summary>
    public static bool InHitbox(SpearLanes lanes, SpearSprite fish, bool rightToLeft, float hitbox = Hitbox, float leftOffset = 0f, float rightOffset = 0f)
    {
        if (!lanes.Valid || fish.Width <= 0) return false;
        var centre = Centre(lanes);
        var anchor = Anchor(lanes, fish, rightToLeft, leftOffset, rightOffset);
        return anchor >= centre - hitbox && anchor <= centre + hitbox;
    }

    /// <summary>
    /// The lane to gig now: the first one, bottom to top, with a wanted fish (<see cref="SpearFish.Wanted"/>)
    /// in the hitbox; -1 when there is none. Unwanted fish are left alone.
    /// </summary>
    public static int Pick(SpearLanes lanes, IReadOnlyList<SpearLaneFish> fish, IReadOnlyList<FishInfo> wanted, float hitbox = Hitbox)
    {
        if (!lanes.Valid) return -1;
        for (var i = 0; i < fish.Count; i++)
        {
            var f = fish[i];
            if (!f.Available || f.Sprite is not { } sprite) continue;
            if (!SpearFish.Wanted(wanted, f.Size, f.Speed)) continue;
            if (InHitbox(lanes, sprite, f.RightToLeft, hitbox)) return i;
        }
        return -1;
    }
}
