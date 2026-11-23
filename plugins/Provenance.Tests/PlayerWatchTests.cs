using System;
using System.Numerics;
using Provenance.Core.Run;
using Xunit;

namespace Provenance.Tests;

public class PlayerWatchTests
{
    private static readonly DateTime T0 = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
    private static readonly WatchFor All = new(true, true, true, 15);
    private static readonly Vector3 Me = Vector3.Zero;

    private static SeenPlayer P(ulong id, float x, bool hidden = false, bool gm = false, bool party = false, bool aetheryte = false)
        => new(id, new Vector3(x, 0, 0), hidden, gm, party, aetheryte);

    private static DateTime At(double s) => T0.AddSeconds(s);

    [Fact]
    public void EveryoneAlreadyThereAtTheStartIsKnown()
    {
        var w = new PlayerWatch();
        Assert.Null(w.Feed(At(0), Me, new[] { P(1, 3), P(2, 5) }, All));
        Assert.Null(w.Feed(At(0.1), Me, new[] { P(1, 3), P(2, 5) }, All));
    }

    [Fact]
    public void APlayerAppearingCloseByIsSeen()
    {
        var w = new PlayerWatch();
        w.Feed(At(0), Me, new[] { P(1, 40) }, All);
        var seen = w.Feed(At(1), Me, new[] { P(1, 40), P(2, 6) }, All);
        Assert.NotNull(seen);
        Assert.Equal(WatcherKind.Appeared, seen!.Kind);
        Assert.Equal(2ul, seen.Id);
    }

    [Fact]
    public void APlayerComingIntoRangeFarAwayIsNot()
    {
        var w = new PlayerWatch();
        w.Feed(At(0), Me, new[] { P(1, 40) }, All);
        Assert.Null(w.Feed(At(1), Me, new[] { P(1, 40), P(2, 60) }, All));
        // ...and walking up to the character afterwards is fine.
        Assert.Null(w.Feed(At(9), Me, new[] { P(1, 40), P(2, 4) }, All));
    }

    [Fact]
    public void ArrivalsAtAnAetheryteAndPartyMembersDoNotCount()
    {
        var w = new PlayerWatch();
        w.Feed(At(0), Me, Array.Empty<SeenPlayer>(), All);
        Assert.Null(w.Feed(At(1), Me, new[] { P(2, 6, aetheryte: true), P(3, 4, party: true) }, All));
    }

    [Fact]
    public void NoOneAppearsWhileTheSurroundingsSettle()
    {
        var w = new PlayerWatch();
        w.Feed(At(0), Me, Array.Empty<SeenPlayer>(), All);
        w.Unsettle(At(1));
        Assert.Null(w.Feed(At(2), Me, new[] { P(2, 5) }, All));
        Assert.Null(w.Feed(At(8), Me, new[] { P(2, 5) }, All));
        Assert.NotNull(w.Feed(At(9), Me, new[] { P(2, 5), P(3, 7) }, All));
    }

    [Fact]
    public void AHiddenPlayerCountsOnlyAfterAWhile()
    {
        var w = new PlayerWatch();
        w.Feed(At(0), Me, new[] { P(1, 8, hidden: true) }, All);
        Assert.Null(w.Feed(At(1), Me, new[] { P(1, 8, hidden: true) }, All));
        Assert.Null(w.Feed(At(3.5), Me, new[] { P(1, 8, hidden: true) }, All));
        var seen = w.Feed(At(4.1), Me, new[] { P(1, 8, hidden: true) }, All);
        Assert.Equal(WatcherKind.Hidden, seen!.Kind);
    }

    [Fact]
    public void AHiddenPlayerThatShowsUpStartsOver()
    {
        var w = new PlayerWatch();
        w.Feed(At(0), Me, new[] { P(1, 8, hidden: true) }, All);
        w.Feed(At(1), Me, new[] { P(1, 8, hidden: true) }, All);
        w.Feed(At(2), Me, new[] { P(1, 8) }, All);
        Assert.Null(w.Feed(At(4.5), Me, new[] { P(1, 8, hidden: true) }, All));
    }

    [Fact]
    public void AHiddenPlayerFarAwayDoesNotCount()
    {
        var w = new PlayerWatch();
        w.Feed(At(0), Me, new[] { P(1, 30, hidden: true) }, All);
        Assert.Null(w.Feed(At(10), Me, new[] { P(1, 30, hidden: true) }, All));
    }

    [Fact]
    public void AGameMasterCountsAnywhereAndRightAway()
    {
        var w = new PlayerWatch();
        var seen = w.Feed(At(0), Me, new[] { P(1, 80, gm: true) }, All);
        Assert.Equal(WatcherKind.GameMaster, seen!.Kind);
    }

    [Fact]
    public void NothingCountsWhenNothingIsLookedFor()
    {
        var w = new PlayerWatch();
        var none = new WatchFor(false, false, false, 15);
        w.Feed(At(0), Me, Array.Empty<SeenPlayer>(), none);
        Assert.Null(w.Feed(At(1), Me, new[] { P(1, 80, gm: true), P(2, 3) }, none));
        Assert.Null(w.Feed(At(9), Me, new[] { P(1, 80, gm: true), P(2, 3, hidden: true) }, none));
    }
}
