using System.Collections.Generic;
using Provenance.Core.Fishing;
using Xunit;

namespace Provenance.Tests;

public class SpearCatchesTests
{
    [Fact]
    public void Rises_are_catches_and_falls_are_not()
    {
        var before = new Dictionary<uint, int> { [27518] = 5, [27519] = 3, [27523] = 1 };
        var now = new Dictionary<uint, int> { [27518] = 7, [27519] = 2, [27534] = 1 }; // 27523 left the bags, 27534 came in
        Assert.Equal(new[] { (27518u, 2), (27534u, 1) }, SpearCatches.Gains(before, now));
    }

    [Fact]
    public void Nothing_changed_nothing_caught()
    {
        var counts = new Dictionary<uint, int> { [27518] = 5 };
        Assert.Empty(SpearCatches.Gains(counts, new Dictionary<uint, int>(counts)));
        Assert.Empty(SpearCatches.Gains(new Dictionary<uint, int>(), new Dictionary<uint, int>()));
        Assert.Empty(SpearCatches.Gains(counts, new Dictionary<uint, int>()));
    }

    [Fact]
    public void A_first_catch_counts_from_none()
    {
        var gains = SpearCatches.Gains(new Dictionary<uint, int>(), new Dictionary<uint, int> { [27534] = 1, [20165] = 3 });
        Assert.Equal(new[] { (20165u, 3), (27534u, 1) }, gains); // by item id
    }
}
