using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Orders;
using Provenance.Core.Teamcraft;
using Xunit;

namespace Provenance.Tests;

public class FavouritesTests
{
    private static readonly Dictionary<uint, string> Names = new() { [1] = "Caramel Popcorn", [2] = "Sungilt Aethersand", [3] = "Flint Corn" };

    [Fact]
    public void Exported_text_is_teamcrafts_and_comes_back_in()
    {
        var lines = new List<OrderLine> { new(1, 30, 0, true), new(2, 10), new(1, 5, 0, false), new(3, 0) };
        var text = Favourites.ToTeamcraftText(lines, id => Names[id]);
        Assert.Equal("35x Caramel Popcorn\n10x Sungilt Aethersand", text);
        // Provenance's import (and Teamcraft's) reads it back line by line.
        Assert.Equal(InputKind.Text, TeamcraftFormats.Detect(text).Kind);
        Assert.Equal(new[] { (35, "Caramel Popcorn"), (10, "Sungilt Aethersand") }, TeamcraftFormats.ParseText(text));
    }

    [Fact]
    public void Names_stay_apart()
    {
        Assert.Equal("Popcorn", Favourites.UniqueName("Popcorn", new[] { "Other" }));
        Assert.Equal("Popcorn (2)", Favourites.UniqueName(" Popcorn ", new[] { "popcorn" }));
        Assert.Equal("Popcorn (3)", Favourites.UniqueName("Popcorn", new[] { "Popcorn", "Popcorn (2)" }));
        Assert.Equal("Favourite order", Favourites.UniqueName("  ", Array.Empty<string>()));
    }

    [Fact]
    public void A_favourite_is_a_copy_both_ways()
    {
        var order = new Order { Name = "Popcorn run" };
        order.Add(1, 30);
        order.Add(2, 10, hq: false);
        var fav = FavouriteOrder.From(order, "Popcorn", DateTime.UnixEpoch);
        order.Lines[0].Quantity = 99;
        Assert.Equal(30, fav.Lines[0].Quantity);

        var loaded = fav.ToOrder();
        Assert.Equal("Popcorn", loaded.Name);
        loaded.Add(3, 10);
        Assert.Equal(2, fav.Lines.Count);
        Assert.True(Favourites.SameLines(fav.Lines, new[] { new OrderLine(2, 10, 0, false), new OrderLine(1, 30) }));
        Assert.False(Favourites.SameLines(fav.Lines, new[] { new OrderLine(1, 30), new OrderLine(2, 10) })); // quality differs
    }
}
