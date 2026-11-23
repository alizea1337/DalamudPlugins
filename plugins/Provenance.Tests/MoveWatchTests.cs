using System;
using System.Numerics;
using Provenance.Core.Run;
using Xunit;

namespace Provenance.Tests;

public class MoveWatchTests
{
    private static readonly DateTime T0 = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
    private const uint Here = 1237, There = 1291;

    private static MoveSample At(double seconds, float x, float z = 0, uint zone = Here, bool loading = false, bool explained = false)
        => new(T0.AddSeconds(seconds), zone, new Vector3(x, 0, z), loading, explained);

    [Fact]
    public void FlyingFastIsNoJump()
    {
        var w = new MoveWatch();
        // 22 yalms a second for a minute, one frame every 1/60 s.
        for (var i = 0; i < 3600; i++)
            Assert.Null(w.Feed(At(i / 60.0, 22f * i / 60f)));
    }

    [Fact]
    public void AJumpWithinTheZoneWithNothingBeforeIsUnexpected()
    {
        var w = new MoveWatch();
        Assert.Null(w.Feed(At(0, 0)));
        Assert.Null(w.Feed(At(30, 0)));
        var move = w.Feed(At(30.016, 200));
        Assert.NotNull(move);
        Assert.False(move!.ZoneChanged);
        Assert.Equal(200f, move.Yalms, 1);
    }

    [Fact]
    public void AJumpAfterACastIsExplained()
    {
        var w = new MoveWatch();
        w.Feed(At(0, 0));
        w.Feed(At(5, 0, explained: true));
        Assert.Null(w.Feed(At(12, 300)));
    }

    [Fact]
    public void AShortSnapBackIsNoJump()
    {
        var w = new MoveWatch();
        w.Feed(At(0, 0));
        Assert.Null(w.Feed(At(0.016, 12)));
    }

    [Fact]
    public void AZoneChangeWithNothingBeforeIsUnexpected()
    {
        var w = new MoveWatch();
        w.Feed(At(0, 10));
        w.Feed(At(60, 10));
        for (var i = 0; i < 100; i++) Assert.Null(w.Feed(At(60.1 + i * 0.05, 0, loading: true)));
        var move = w.Feed(At(66, -40, zone: There));
        Assert.NotNull(move);
        Assert.True(move!.ZoneChanged);
        Assert.Equal(Here, move.From);
        Assert.Equal(There, move.To);
    }

    [Fact]
    public void ATeleportWithALongLoadIsExplained()
    {
        var w = new MoveWatch();
        w.Feed(At(0, 10));
        for (var i = 0; i < 50; i++) w.Feed(At(1 + i * 0.1, 10, explained: true)); // the cast
        for (var i = 0; i < 300; i++) Assert.Null(w.Feed(At(6 + i * 0.1, 0, loading: true))); // 30 s of loading
        Assert.Null(w.Feed(At(36, 500, zone: There)));
    }

    [Fact]
    public void AnExplanationLongBeforeTheLoadDoesNotCount()
    {
        var w = new MoveWatch();
        w.Feed(At(0, 10, explained: true));
        w.Feed(At(40, 10));
        w.Feed(At(40.1, 0, loading: true));
        Assert.NotNull(w.Feed(At(45, 0, zone: There)));
    }

    [Fact]
    public void ALoadingScreenThatLeavesTheCharacterInPlaceIsNoMove()
    {
        var w = new MoveWatch();
        w.Feed(At(0, 10));
        w.Feed(At(60, 10));
        w.Feed(At(60.1, 0, loading: true));
        Assert.Null(w.Feed(At(63, 14)));
    }

    [Fact]
    public void AZoneChangeWithoutALoadingFrameSeenIsUnexpected()
    {
        var w = new MoveWatch();
        w.Feed(At(0, 10));
        w.Feed(At(60, 10));
        Assert.NotNull(w.Feed(At(60.016, 10, zone: There)));
    }

    [Fact]
    public void ResetForgetsWhereTheCharacterWas()
    {
        var w = new MoveWatch();
        w.Feed(At(0, 0));
        w.Reset();
        Assert.Null(w.Feed(At(60, 900)));
        Assert.Null(w.Feed(At(60.016, 901)));
    }

    [Fact]
    public void StartingDuringALoadIsNoMove()
    {
        var w = new MoveWatch();
        w.Feed(At(0, 0, loading: true));
        Assert.Null(w.Feed(At(3, 500, zone: There)));
    }
}
