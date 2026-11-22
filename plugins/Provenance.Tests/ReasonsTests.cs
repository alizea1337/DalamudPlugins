using System;
using System.Collections.Generic;
using Provenance.Core.Catalog;
using Provenance.Core.Planning;
using Xunit;

namespace Provenance.Tests;

public class ReasonsTests
{
    private static readonly OtherSource Gemstones = new(OtherWay.Shop, "Bicolor Gemstones", "Bicolor Gemstones", 3, "the gemstone trader in Old Sharlayan");

    private static Plan Build(FakeCatalog catalog, uint item)
        => Planner.Build(new Core.Orders.Order { Lines = { new Core.Orders.OrderLine { ItemId = item, Quantity = 1 } } },
            catalog, new Dictionary<uint, int>(), new PlannerOptions());

    [Fact]
    public void An_item_sold_for_another_currency_names_the_shop()
    {
        var catalog = new FakeCatalog();
        catalog.SetOthers(FakeCatalog.Mystery, Gemstones);
        var line = Build(catalog, FakeCatalog.Mystery)[FakeCatalog.Mystery]!;
        Assert.Equal(Supply.Manual, line.Supply);
        Assert.Equal("Sold for 3 Bicolor Gemstones by the gemstone trader in Old Sharlayan, and Provenance doesn't spend Bicolor Gemstones",
            line.ManualReason);
    }

    [Fact]
    public void Two_ways_are_named_and_no_more()
    {
        var reason = Reasons.NoWay(default, new[]
        {
            Gemstones, new OtherSource(OtherWay.CompanySeals, Cost: 200), new OtherSource(OtherWay.MarketBoard),
        }, 0);
        Assert.Equal("Sold for 3 Bicolor Gemstones by the gemstone trader in Old Sharlayan, and Provenance doesn't spend Bicolor Gemstones; "
                     + "sold for 200 company seals at your Grand Company, and Provenance doesn't spend seals", reason);
    }

    [Fact]
    public void Content_not_supported_yet_comes_first_and_other_ways_follow()
    {
        var src = new ItemSources(Obtain.GatherElsewhere, 0, Places: Place.Diadem);
        Assert.Equal("Only gathered in the Diadem, which Provenance doesn't support yet; also sold by other players on the market board, which Provenance doesn't use",
            Reasons.NoWay(src, new[] { new OtherSource(OtherWay.MarketBoard) }, 0));
    }

    [Fact]
    public void Ventures_and_shops_without_a_known_seller()
    {
        Assert.Equal("brought back by retainers on ventures, which Provenance doesn't send", Reasons.Describe(new OtherSource(OtherWay.Venture)));
        Assert.Equal("sold for 1 Cosmocredit, and Provenance doesn't spend Cosmocredits",
            Reasons.Describe(new OtherSource(OtherWay.Shop, "Cosmocredit", "Cosmocredits", 1)));
    }

    [Fact]
    public void Nothing_known_says_so()
    {
        var line = Build(new FakeCatalog(), FakeCatalog.Mystery)[FakeCatalog.Mystery]!;
        Assert.Equal(Reasons.Unknown, line.ManualReason);
    }

    [Fact]
    public void The_vendor_cap_says_where_to_change_it()
        => Assert.Equal("Vendor price 12,000 gil is above your cap of 10,000 (Settings, General)", Reasons.VendorCap(12000, 10000));

    [Fact]
    public void Reduction_from_content_not_supported_yet()
    {
        var src = new ItemSources(Obtain.ReduceElsewhere, 0, Places: Place.Diadem);
        Assert.Equal("Only from aetherial reduction of collectables gathered in the Diadem, which Provenance doesn't support yet",
            Reasons.NoWay(src, Array.Empty<OtherSource>(), 0));
    }

    [Fact]
    public void Bait_sold_only_for_another_currency_names_it()
    {
        var catalog = new FakeCatalog();
        catalog.SetOthers(FakeCatalog.BruteLeech, new OtherSource(OtherWay.Shop, "Cosmocredits", "Cosmocredits", 10, ""));
        var plan = Planner.Build(new Core.Orders.Order { Lines = { new Core.Orders.OrderLine { ItemId = FakeCatalog.OldLord, Quantity = 1 } } },
            catalog, new Dictionary<uint, int>(), new PlannerOptions
            {
                VendorMaxPrice = 2000,
                FishBaits = id => id == FakeCatalog.OldLord ? new[] { (FakeCatalog.BruteLeech, 1.0) } : Array.Empty<(uint, double)>(),
            });
        Assert.Contains("Sold for 10 Cosmocredits", plan[FakeCatalog.BruteLeech]?.ManualReason ?? "");
    }
}
