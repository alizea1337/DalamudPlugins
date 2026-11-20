using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Catalog;
using Provenance.Core.Orders;
using Provenance.Core.Planning;
using Xunit;
using static Provenance.Tests.FakeCatalog;

namespace Provenance.Tests;

public class PlannerTests
{
    private static readonly FakeCatalog Catalog = new();
    private static readonly Dictionary<uint, int> Nothing = new();

    private static Plan Build(Order order, Dictionary<uint, int>? owned = null, PlannerOptions? options = null)
        => Planner.Build(order, Catalog, owned ?? Nothing, options ?? new PlannerOptions());

    private static Order OrderOf(params (uint Id, int Qty)[] lines)
    {
        var o = new Order();
        foreach (var (id, qty) in lines) o.Add(id, qty);
        return o;
    }

    [Fact]
    public void Expands_the_full_tree_with_yields()
    {
        // 2 bows: 4 lumber (12 logs, 4 wind), 2 rivets -> 1 craft yields 3 (1 ingot -> 2 ore, 1 fire; +1 fire)
        var plan = Build(OrderOf((Bow, 2)));

        Assert.Equal(2, plan[Bow]!.Crafts);
        Assert.Equal(4, plan[Lumber]!.Crafts);
        Assert.Equal(12, plan[Log]!.Needed);
        Assert.Equal(1, plan[Rivets]!.Crafts);
        Assert.Equal(3, plan[Rivets]!.Produced);
        Assert.Equal(1, plan[Ingot]!.Crafts);
        Assert.Equal(2, plan[Ore]!.Needed);
        Assert.Equal(2, plan[FireShard]!.Needed);
        Assert.Equal(4 + 2, plan[WindShard]!.Needed);
        Assert.True(plan.CanStart);
    }

    [Fact]
    public void Shared_intermediates_are_summed_before_rounding_up()
    {
        // bow needs 1 rivet, shield needs 2: 3 rivets is exactly one craft, not two.
        var plan = Build(OrderOf((Bow, 1), (Shield, 1)));
        Assert.Equal(3, plan[Rivets]!.Needed);
        Assert.Equal(1, plan[Rivets]!.Crafts);
    }

    [Fact]
    public void Items_only_gathered_in_special_content_are_got_by_hand()
    {
        var catalog = new FakeCatalog();
        const uint Special = 9100;
        catalog.SetSources(Special, new ItemSources(Obtain.GatherElsewhere, 0, Places: Place.CosmicExploration));
        var plan = Planner.Build(OrderOf((Special, 3)), catalog, new Dictionary<uint, int>(), new PlannerOptions());
        Assert.Equal(Supply.Manual, plan[Special]!.Supply);
        Assert.Equal("Only gathered in Cosmic Exploration, which a run can't go to", plan[Special]!.ManualReason);
        Assert.False(plan.CanStart);
        Assert.Equal(new[] { Special }, plan.TargetsNeeding(Special));

        // Gatherable in the open world as well: gathered there.
        catalog.SetSources(Special, new ItemSources(Obtain.Gather | Obtain.GatherElsewhere, 0, Places: Place.OpenWorld | Place.Diadem));
        Assert.Equal(Supply.Gather, Planner.Build(OrderOf((Special, 3)), catalog, new Dictionary<uint, int>(), new PlannerOptions())[Special]!.Supply);
    }

    [Theory]
    [InlineData(Obtain.FishElsewhere, Place.Diadem, "Only fished in the Diadem, which a run can't go to")]
    [InlineData(Obtain.FishElsewhere, Place.OceanFishing, "Only fished in Ocean Fishing, which a run can't go to")]
    [InlineData(Obtain.CraftElsewhere, Place.CosmicExploration, "Only crafted in Cosmic Exploration, which a run can't go to")]
    [InlineData(Obtain.GatherElsewhere | Obtain.FishElsewhere, Place.Diadem | Place.Other,
        "Only gathered or fished in the Diadem, which a run can't go to")]
    [InlineData(Obtain.GatherElsewhere, Place.Other, "Only gathered during quests or in content that's no longer in the game")]
    [InlineData(Obtain.GatherElsewhere, Place.Diadem | Place.CosmicExploration,
        "Only gathered in the Diadem or Cosmic Exploration, which a run can't go to")]
    [InlineData(Obtain.None, Place.CosmicExploration, "Only from Cosmic Exploration, which a run can't go to")]
    public void Special_content_is_named_in_the_reason(Obtain how, Place where, string reason)
    {
        var catalog = new FakeCatalog();
        const uint Special = 9101;
        catalog.SetSources(Special, new ItemSources(how, 0, Places: where));
        Assert.Equal(reason, Planner.Build(OrderOf((Special, 1)), catalog, new Dictionary<uint, int>(), new PlannerOptions())[Special]!.ManualReason);
    }

    [Fact]
    public void A_vendor_over_the_cap_is_named_before_special_content()
    {
        var catalog = new FakeCatalog();
        const uint Special = 9102;
        catalog.SetSources(Special, new ItemSources(Obtain.Vendor | Obtain.GatherElsewhere, 5000, Places: Place.Diadem));
        var line = Planner.Build(OrderOf((Special, 1)), catalog, new Dictionary<uint, int>(), new PlannerOptions { VendorMaxPrice = 1000 })[Special]!;
        Assert.Contains("above your cap", line.ManualReason);
    }

    [Fact]
    public void Finds_the_ordered_items_that_need_a_material()
    {
        var plan = Build(OrderOf((Bow, 1), (Shield, 1), (Ore, 5)));
        Assert.Equal(new[] { Bow, Shield }, plan.TargetsNeeding(Rivets));   // both use rivets
        Assert.Equal(new[] { Bow, Shield, Ore }, plan.TargetsNeeding(Ore)); // ore deep down, and ordered itself
        Assert.Equal(new[] { Shield }, plan.TargetsNeeding(Shield));
        Assert.Empty(plan.TargetsNeeding(Log + 999));
    }

    [Fact]
    public void Craft_steps_put_ingredients_before_products()
    {
        var plan = Build(OrderOf((Bow, 1)));
        var order = plan.CraftSteps.Select(l => l.ItemId).ToList();
        Assert.True(order.IndexOf(Ingot) < order.IndexOf(Rivets));
        Assert.True(order.IndexOf(Rivets) < order.IndexOf(Bow));
        Assert.True(order.IndexOf(Lumber) < order.IndexOf(Bow));
    }

    [Fact]
    public void Owned_intermediates_cut_the_tree_below_them()
    {
        var plan = Build(OrderOf((Bow, 1)), new Dictionary<uint, int> { [Lumber] = 2, [Rivets] = 5 });
        Assert.Equal(0, plan[Lumber]!.Crafts);
        Assert.Equal(0, plan[Rivets]!.Crafts);
        Assert.Null(plan[Log]);
        Assert.Null(plan[Ingot]);
        Assert.Single(plan.CraftSteps);
    }

    [Fact]
    public void Owned_finished_items_are_ignored_unless_asked()
    {
        var owned = new Dictionary<uint, int> { [Bow] = 1 };
        Assert.Equal(1, Build(OrderOf((Bow, 1)), owned)[Bow]!.Crafts);
        Assert.Equal(0, Build(OrderOf((Bow, 1)), owned, new PlannerOptions { CountOwnedTargets = true })[Bow]!.Crafts);
    }

    [Fact]
    public void Timed_only_materials_are_marked_timed()
    {
        var plan = Build(OrderOf((RareLumber, 1)));
        Assert.Equal(Supply.GatherTimed, plan[TimedLog]!.Supply);
        Assert.Equal(1, plan.Count(Supply.GatherTimed));
    }

    [Fact]
    public void Items_only_from_reduction_plan_as_reduce()
    {
        const uint Sand = 12936, Moraine = 5214;
        var catalog = new FakeCatalog();
        catalog.SetSources(Moraine, new ItemSources(Obtain.Gather, 0, Places: Place.OpenWorld));
        catalog.SetReducedFrom(Sand, Moraine);
        var plan = Planner.Build(OrderOf((Sand, 5)), catalog, new Dictionary<uint, int>(), new PlannerOptions());
        Assert.Equal(Supply.Reduce, plan[Sand]!.Supply);
        Assert.Equal(5, plan[Sand]!.Remaining);
        Assert.True(plan.CanStart);
        // Another source the run can use comes first.
        catalog.SetSources(Sand, new ItemSources(Obtain.Gather | Obtain.Reduce, 0, Places: Place.OpenWorld));
        Assert.Equal(Supply.Gather, Planner.Build(OrderOf((Sand, 5)), catalog, new Dictionary<uint, int>(), new PlannerOptions())[Sand]!.Supply);
    }

    [Fact]
    public void Items_only_from_a_scrip_exchange_are_bought_there()
    {
        const uint Corn = 49229;
        var catalog = new FakeCatalog();
        catalog.SetSources(Corn, new ItemSources(Obtain.Exchange, 0, Places: Place.OpenWorld));
        var asked = 0;
        var options = new PlannerOptions { CountOwnedTargets = true, BuyExchange = (id, amount) => { asked = amount; return new BookPurchase(true, "bought with scrips"); } };
        var plan = Planner.Build(OrderOf((Corn, 10)), catalog, new Dictionary<uint, int> { [Corn] = 3 }, options);
        Assert.Equal(Supply.Exchange, plan[Corn]!.Supply);
        Assert.Equal(7, asked);              // asked for what's still missing
        Assert.True(plan[Corn]!.WillBuy);
        Assert.True(plan.CanStart);

        // The run can't buy it (no scrips and earning is off): it's one to get yourself, with the reason.
        options = new PlannerOptions { BuyExchange = (_, _) => new BookPurchase(false, "costs 15 Orange Crafters' Scrips each") };
        plan = Planner.Build(OrderOf((Corn, 10)), catalog, new Dictionary<uint, int>(), options);
        Assert.False(plan[Corn]!.WillBuy);
        Assert.Contains(plan.Missing, l => l.ItemId == Corn);
        Assert.Contains("Orange Crafters' Scrips", plan[Corn]!.ManualReason);

        // Anything else that supplies it comes first.
        catalog.SetSources(Corn, new ItemSources(Obtain.Gather | Obtain.Exchange, 0, Places: Place.OpenWorld));
        Assert.Equal(Supply.Gather, Planner.Build(OrderOf((Corn, 10)), catalog, new Dictionary<uint, int>(), options)[Corn]!.Supply);
    }

    [Theory]
    [InlineData(Place.None, "fish collectables")]
    [InlineData(Place.Diadem, "the Diadem")]
    public void Reduction_a_run_cant_do_says_why(Place where, string named)
    {
        const uint Sand = 12940;
        var catalog = new FakeCatalog();
        catalog.SetSources(Sand, new ItemSources(Obtain.ReduceElsewhere, 0, Places: where));
        var plan = Planner.Build(OrderOf((Sand, 2)), catalog, new Dictionary<uint, int>(), new PlannerOptions());
        Assert.Equal(Supply.Manual, plan[Sand]!.Supply);
        Assert.Contains("aetherial reduction", plan[Sand]!.ManualReason);
        Assert.Contains(named, plan[Sand]!.ManualReason);
        Assert.False(plan.CanStart);
    }

    [Fact]
    public void Vendor_over_the_cap_becomes_manual_and_blocks_start()
    {
        var order = OrderOf((FancyDye, 2));
        var capped = Build(order, options: new PlannerOptions { VendorMaxPrice = 1000 });
        Assert.Equal(Supply.Manual, capped[FancyDye]!.Supply);
        Assert.False(capped.CanStart);
        Assert.Contains("above your cap", capped.Missing.Single().ManualReason);

        var uncapped = Build(order, options: new PlannerOptions { VendorMaxPrice = 0 });
        Assert.Equal(Supply.Vendor, uncapped[FancyDye]!.Supply);
        Assert.Equal(10000, uncapped.VendorGil);
        Assert.True(uncapped.CanStart);
    }

    [Fact]
    public void Prefer_vendor_switches_fish_to_buying()
    {
        Assert.Equal(Supply.Vendor, Build(OrderOf((Fish, 1)), options: new PlannerOptions { PreferVendor = true })[Fish]!.Supply);
        Assert.Equal(Supply.Fish, Build(OrderOf((Fish, 1)), options: new PlannerOptions { PreferVendor = false })[Fish]!.Supply);
    }

    [Fact]
    public void Items_without_a_source_block_until_owned()
    {
        Assert.False(Build(OrderOf((Mystery, 1))).CanStart);
        var owned = Build(OrderOf((Mystery, 1)), options: new PlannerOptions { CountOwnedTargets = true }, owned: new Dictionary<uint, int> { [Mystery] = 1 });
        Assert.True(owned.CanStart);
    }

    [Fact]
    public void Drops_are_a_source()
    {
        Assert.Equal(Supply.Drop, Build(OrderOf((Fang, 3)))[Fang]!.Supply);
    }

    [Fact]
    public void Tree_shows_branch_quantities()
    {
        var plan = Build(OrderOf((Bow, 2)));
        var root = Assert.Single(plan.Tree);
        Assert.Equal(Bow, root.ItemId);
        var lumber = root.Children.Single(c => c.ItemId == Lumber);
        Assert.Equal(4, lumber.Quantity);
        Assert.Equal(12, lumber.Children.Single(c => c.ItemId == Log).Quantity);
    }

    [Fact]
    public void Explicit_recipe_id_is_respected()
    {
        var catalog = new FakeCatalog();
        var alt = catalog.AddRecipe(Lumber, Provenance.Core.Catalog.Job.Weaver, 90, 1, (Ore, 1));
        var order = new Order();
        order.Add(Lumber, 1, alt.RecipeId);
        var plan = Planner.Build(order, catalog, Nothing, new PlannerOptions());
        Assert.Equal(alt.RecipeId, plan[Lumber]!.Recipe!.RecipeId);
        Assert.Null(plan[Log]);
    }

    [Fact]
    public void Empty_order_gives_empty_plan()
    {
        var plan = Build(new Order());
        Assert.False(plan.CanStart);
        Assert.Empty(plan.Lines);
    }

    // ------------------------------------------------------------------ bait

    /// <summary>Fishing options: bait per catch by fish, spare bait, a stand-in and the bags' bait counts.</summary>
    private static PlannerOptions Fishing(Dictionary<uint, (uint Bait, double PerCatch)[]> baits, int spare = 0, uint fallback = 0,
        Dictionary<uint, int>? bags = null, uint cap = 2000, Func<uint, string?>? source = null, bool preferVendor = false)
        => new()
        {
            PreferVendor = preferVendor,
            VendorMaxPrice = cap,
            FishBaits = id => baits.TryGetValue(id, out var b) ? b : Array.Empty<(uint, double)>(),
            SpareBait = spare,
            FallbackBait = fallback,
            BaitOnHand = bags == null ? null : id => bags.GetValueOrDefault(id),
            BaitSource = source,
        };

    [Fact]
    public void Fish_lines_bring_their_bait_bought_from_a_vendor()
    {
        var plan = Build(OrderOf((Trout, 10)), options: Fishing(new() { [Trout] = new[] { (Lugworm, 1.5) } }, spare: 20));
        Assert.Equal(Supply.Fish, plan[Trout]!.Supply);
        var bait = plan[Lugworm]!;
        Assert.Equal(Supply.Vendor, bait.Supply);
        Assert.Equal(15 + 20, bait.Needed);
        Assert.Equal(4u, bait.VendorUnitPrice);
        Assert.False(bait.IsTarget);
        Assert.Equal(new[] { Trout }, bait.NeededFor);
        Assert.Equal(35 * 4, plan.VendorGil);
        Assert.True(plan.CanStart);
        Assert.Empty(plan.CraftSteps);
    }

    [Fact]
    public void Bait_counts_only_what_the_bags_hold()
    {
        var baits = new Dictionary<uint, (uint, double)[]> { [Trout] = new[] { (Lugworm, 1.5) } };
        // 100 in the saddlebag and with retainers, 10 in the bags: only the 10 count.
        var owned = new Dictionary<uint, int> { [Lugworm] = 100 };
        var bagsOnly = Build(OrderOf((Trout, 10)), owned, Fishing(baits, spare: 20, bags: new() { [Lugworm] = 10 }));
        Assert.Equal(10, bagsOnly[Lugworm]!.FromStock);
        Assert.Equal(25, bagsOnly[Lugworm]!.Remaining);

        // Without a bag count, bait counts like anything owned.
        var anyStock = Build(OrderOf((Trout, 10)), owned, Fishing(baits, spare: 20));
        Assert.Equal(35, anyStock[Lugworm]!.FromStock);
        Assert.Equal(0, anyStock[Lugworm]!.Remaining);
    }

    [Fact]
    public void Bait_is_summed_over_the_fish_rounded_up_once_and_spare_comes_once()
    {
        var plan = Build(OrderOf((Trout, 3), (OldLord, 1)), options: Fishing(new()
        {
            [Trout] = new[] { (Lugworm, 1.5) },
            [OldLord] = new[] { (Lugworm, 3.2) },
        }, spare: 5));
        var bait = plan[Lugworm]!;
        Assert.Equal(8 + 5, bait.Needed); // 4.5 + 3.2 = 7.7 → 8, plus 5 spare
        Assert.Equal(new[] { Trout, OldLord }, bait.NeededFor);
    }

    [Fact]
    public void Bait_is_never_crafted()
    {
        var plan = Build(OrderOf((Trout, 5)), options: Fishing(new() { [Trout] = new[] { (Spinnerbait, 2.0) } }));
        var lure = plan[Spinnerbait]!;
        Assert.Equal(Supply.Manual, lure.Supply);
        Assert.Null(lure.Recipe);
        Assert.Contains("Crafted (GSM)", lure.ManualReason);
        Assert.Null(plan[Ingot]);
        Assert.Empty(plan.CraftSteps);
        Assert.False(plan.CanStart);
        Assert.Equal(new[] { Trout }, plan.TargetsNeeding(Spinnerbait));
    }

    [Fact]
    public void Bait_sold_elsewhere_is_named_and_blocks_the_start()
    {
        const string where = "Sold for 2 White Gatherers' Scrips by the scrip exchange in Solution Nine";
        var plan = Build(OrderOf((Trout, 10)), options: Fishing(new() { [Trout] = new[] { (BruteLeech, 2.0) } }, spare: 5,
            bags: new() { [BruteLeech] = 7 }, source: id => id == BruteLeech ? where : null));
        var leech = plan[BruteLeech]!;
        Assert.Equal(Supply.Manual, leech.Supply);
        Assert.Equal(where, leech.ManualReason);
        Assert.Equal(20, leech.Needed); // the estimate only: spare is for bait the run buys
        Assert.Equal(7, leech.FromStock);
        Assert.Contains(leech, plan.Missing);
        Assert.False(plan.CanStart);

        // Without a known source the reason still says why.
        var unknown = Build(OrderOf((Trout, 10)), options: Fishing(new() { [Trout] = new[] { (BruteLeech, 2.0) } }));
        Assert.Equal("Not sold by any gil vendor a run can reach", unknown[BruteLeech]!.ManualReason);
    }

    [Fact]
    public void Bait_the_run_can_not_buy_needs_no_spare()
    {
        // 20 Brute Leech for 10 trout and 30 in the bags: enough, although 20 spare are set for bought bait.
        var plan = Build(OrderOf((Trout, 10)), options: Fishing(new() { [Trout] = new[] { (BruteLeech, 2.0) } }, spare: 20,
            bags: new() { [BruteLeech] = 30 }));
        Assert.Equal(20, plan[BruteLeech]!.Needed);
        Assert.Equal(0, plan[BruteLeech]!.Remaining);
        Assert.Empty(plan.Missing);
        Assert.True(plan.CanStart);
    }

    [Fact]
    public void An_ordered_crafted_bait_is_cast_from_the_bags_or_the_stand_in_never_from_its_crafts()
    {
        // 3 Spinnerbait ordered (one craft makes 3) and 10 trout cast with it (14 casts). Crafting comes after the
        // fishing, so Versatile Lure is bought for the casts; the line keeps the 3 ordered.
        var baits = new Dictionary<uint, (uint, double)[]> { [Trout] = new[] { (Spinnerbait, 1.4) } };
        var order = OrderOf((Spinnerbait, 3), (Trout, 10));
        var plan = Build(order, options: Fishing(baits, spare: 20, fallback: VersatileLure, bags: new()));
        var spinner = plan[Spinnerbait]!;
        Assert.Equal(Supply.Craft, spinner.Supply);
        Assert.Equal(3, spinner.Needed);
        Assert.Equal(1, spinner.Crafts);
        Assert.Equal(0, spinner.MissingBait);
        Assert.Equal(new[] { Trout }, spinner.NeededFor);
        var lure = plan[VersatileLure]!;
        Assert.Equal(14 + 20, lure.Needed);
        Assert.Equal(new[] { Trout }, lure.NeededFor);
        Assert.Contains(lure.ItemId, Assert.Single(plan.Tree, n => n.ItemId == Trout).Children.Select(c => c.ItemId));
        Assert.True(plan.CanStart);

        // What the bags hold is cast first; the ordered ones are still crafted on top.
        var some = Build(order, options: Fishing(baits, spare: 20, fallback: VersatileLure, bags: new() { [Spinnerbait] = 5 }));
        Assert.Equal(3, some[Spinnerbait]!.Remaining);
        Assert.Equal(9 + 20, some[VersatileLure]!.Needed);

        // No stand-in: the casts the bags don't cover are missing.
        var none = Build(order, options: Fishing(baits, spare: 20, bags: new() { [Spinnerbait] = 5 }));
        Assert.Equal(9, none[Spinnerbait]!.MissingBait);
        Assert.Equal(3, none[Spinnerbait]!.Needed);
        Assert.Contains("only after the fishing", none[Spinnerbait]!.ManualReason);
        Assert.Contains(none[Spinnerbait], none.Missing);
        Assert.False(none.CanStart);
        Assert.Null(none[VersatileLure]);

        // Bags that cover the casts: nothing missing.
        var covered = Build(order, options: Fishing(baits, spare: 20, bags: new() { [Spinnerbait] = 14 }));
        Assert.Equal(0, covered[Spinnerbait]!.MissingBait);
        Assert.True(covered.CanStart);
    }

    [Fact]
    public void A_crafted_bait_a_vendor_also_sells_is_bought_by_the_fisher_not_stood_in_for()
    {
        // 3 Crow Fly ordered (one craft makes 3; a vendor sells it for 240 gil too) and 10 trout cast with it (14 casts).
        // The fisher buys the Crow Fly it casts when it gets there: no Versatile Lure, nothing missing, and the line
        // keeps only the 3 ordered.
        var baits = new Dictionary<uint, (uint, double)[]> { [Trout] = new[] { (CrowFly, 1.4) } };
        var order = OrderOf((CrowFly, 3), (Trout, 10));
        var plan = Build(order, options: Fishing(baits, spare: 20, fallback: VersatileLure, bags: new()));
        var fly = plan[CrowFly]!;
        Assert.Equal(Supply.Craft, fly.Supply);
        Assert.Equal(3, fly.Needed);
        Assert.Equal(1, fly.Crafts);
        Assert.Equal(0, fly.MissingBait);
        Assert.Equal(new[] { Trout }, fly.NeededFor);
        Assert.Null(plan[VersatileLure]);
        Assert.DoesNotContain(VersatileLure, Assert.Single(plan.Tree, n => n.ItemId == Trout).Children.Select(c => c.ItemId));
        Assert.Empty(plan.Missing);
        Assert.True(plan.CanStart);
        Assert.True(Planner.BuysBait(plan, Catalog, CrowFly, 2000));

        // Without the stand-in nothing is missing either.
        var none = Build(order, options: Fishing(baits, spare: 20, bags: new()));
        Assert.Equal(0, none[CrowFly]!.MissingBait);
        Assert.Null(none[VersatileLure]);
        Assert.True(none.CanStart);

        // Another fish's bait the stand-in covers leaves the Crow Fly alone.
        var both = Build(OrderOf((CrowFly, 3), (Trout, 10), (OldLord, 2)), options: Fishing(new()
        {
            [Trout] = new[] { (CrowFly, 1.4) },
            [OldLord] = new[] { (BruteLeech, 3.0) },
        }, spare: 20, fallback: VersatileLure, bags: new()));
        Assert.Equal(6 + 20, both[VersatileLure]!.Needed);
        Assert.Equal(new[] { OldLord }, both[VersatileLure]!.NeededFor);
        Assert.Equal(3, both[CrowFly]!.Needed);
        Assert.True(Planner.BuysBait(both, Catalog, CrowFly, 2000));
        Assert.False(Planner.BuysBait(both, Catalog, BruteLeech, 2000));
        Assert.True(Planner.BuysBait(both, Catalog, VersatileLure, 2000));

        // Over the cap it's crafted bait the run can't buy: what the bags lack is missing, and the reason says why.
        var capped = Build(order, options: Fishing(baits, spare: 20, bags: new(), cap: 200));
        Assert.Equal(14, capped[CrowFly]!.MissingBait);
        Assert.Contains("vendor price 240 gil is above your cap of 200", capped[CrowFly]!.ManualReason);
        Assert.False(capped.CanStart);
        Assert.False(Planner.BuysBait(capped, Catalog, CrowFly, 200));
    }

    [Fact]
    public void The_stand_in_covers_what_the_bags_lack()
    {
        // 20 Brute Leech for 10 trout, 8 in the bags: those are cast first, Versatile Lure takes the other 12 (plus spare).
        var plan = Build(OrderOf((Trout, 10)), options: Fishing(new() { [Trout] = new[] { (BruteLeech, 2.0) } }, spare: 10,
            fallback: VersatileLure, bags: new() { [BruteLeech] = 8, [VersatileLure] = 2 }));
        var leech = plan[BruteLeech]!;
        Assert.Equal(8, leech.Needed);
        Assert.Equal(0, leech.Remaining);
        var lure = plan[VersatileLure]!;
        Assert.Equal(Supply.Vendor, lure.Supply);
        Assert.Equal(12 + 10, lure.Needed);
        Assert.Equal(2, lure.FromStock);
        Assert.Equal(new[] { Trout }, lure.NeededFor);
        Assert.True(plan.CanStart);
        Assert.Empty(plan.Missing);
        Assert.Equal(new[] { Trout }, plan.TargetsNeeding(VersatileLure));
    }

    [Fact]
    public void Bags_that_cover_the_estimate_need_no_stand_in()
    {
        var plan = Build(OrderOf((Trout, 10)), options: Fishing(new() { [Trout] = new[] { (BruteLeech, 2.0) } }, spare: 10,
            fallback: VersatileLure, bags: new() { [BruteLeech] = 25 }));
        Assert.Equal(20, plan[BruteLeech]!.Needed);
        Assert.Equal(0, plan[BruteLeech]!.Remaining);
        Assert.Null(plan[VersatileLure]);
        Assert.True(plan.CanStart);
    }

    [Fact]
    public void Bait_over_the_cap_takes_the_stand_in_or_says_why()
    {
        var baits = new Dictionary<uint, (uint, double)[]> { [Trout] = new[] { (CactuarJig, 1.0) } };
        var withStandIn = Build(OrderOf((Trout, 5)), options: Fishing(baits, fallback: VersatileLure, bags: new()));
        Assert.Equal(5, withStandIn[VersatileLure]!.Needed);
        Assert.Null(withStandIn[CactuarJig]);
        Assert.True(withStandIn.CanStart);

        var without = Build(OrderOf((Trout, 5)), options: Fishing(baits));
        Assert.Equal(Supply.Manual, without[CactuarJig]!.Supply);
        Assert.Contains("above your cap", without[CactuarJig]!.ManualReason);

        var noCap = Build(OrderOf((Trout, 5)), options: Fishing(baits, cap: 0));
        Assert.Equal(Supply.Vendor, noCap[CactuarJig]!.Supply);
    }

    [Fact]
    public void A_stand_in_that_can_not_be_bought_is_no_stand_in()
    {
        var plan = Build(OrderOf((Trout, 10)), options: Fishing(new() { [Trout] = new[] { (BruteLeech, 2.0) } },
            fallback: VersatileLure, cap: 100)); // the lure costs 300
        Assert.Equal(20, plan[BruteLeech]!.Needed);
        Assert.Null(plan[VersatileLure]);
        Assert.False(plan.CanStart);
    }

    [Fact]
    public void Fish_cast_with_the_stand_in_itself_buy_it_like_any_bait()
    {
        var plan = Build(OrderOf((Trout, 10), (OldLord, 2)), options: Fishing(new()
        {
            [Trout] = new[] { (VersatileLure, 1.5) },
            [OldLord] = new[] { (BruteLeech, 3.0) },
        }, spare: 10, fallback: VersatileLure, bags: new()));
        var lure = plan[VersatileLure]!;
        Assert.Equal(15 + 6 + 10, lure.Needed); // its own 15, the 6 Brute Leech it stands in for, spare once
        Assert.Equal(new[] { Trout, OldLord }, lure.NeededFor);
        Assert.Null(plan[BruteLeech]);
        Assert.True(plan.CanStart);
    }

    [Fact]
    public void Predator_bait_comes_with_the_fish()
    {
        var plan = Build(OrderOf((OldLord, 1)), options: Fishing(new() { [OldLord] = new[] { (Lugworm, 3.0), (VersatileLure, 4.2) } }));
        Assert.Equal(3, plan[Lugworm]!.Needed);
        Assert.Equal(5, plan[VersatileLure]!.Needed);
        Assert.Equal(new[] { OldLord }, plan[VersatileLure]!.NeededFor);
    }

    [Fact]
    public void Fish_in_a_recipe_bring_bait_for_what_is_still_to_catch()
    {
        var baits = new Dictionary<uint, (uint, double)[]> { [Trout] = new[] { (Lugworm, 1.5) } };
        var plan = Build(OrderOf((TroutStew, 3)), new Dictionary<uint, int> { [Trout] = 2 }, Fishing(baits, bags: new()));
        Assert.Equal(4, plan[Trout]!.Remaining);           // 6 trout, 2 owned
        Assert.Equal(6, plan[Lugworm]!.Needed);            // 4 × 1.5
        Assert.Equal(new[] { TroutStew }, plan.TargetsNeeding(Lugworm));

        // Trout already owned: no bait at all.
        var covered = Build(OrderOf((TroutStew, 3)), new Dictionary<uint, int> { [Trout] = 6 }, Fishing(baits, spare: 20));
        Assert.Null(covered[Lugworm]);
    }

    [Fact]
    public void Bought_fish_bring_no_bait()
    {
        var plan = Build(OrderOf((Fish, 5)), options: Fishing(new() { [Fish] = new[] { (Lugworm, 1.0) } }, spare: 20, preferVendor: true));
        Assert.Equal(Supply.Vendor, plan[Fish]!.Supply);
        Assert.Null(plan[Lugworm]);
    }

    [Fact]
    public void Several_baits_can_hand_over_to_the_stand_in()
    {
        var plan = Build(OrderOf((Trout, 10), (OldLord, 3)), options: Fishing(new()
        {
            [Trout] = new[] { (BruteLeech, 2.0) },
            [OldLord] = new[] { (Spinnerbait, 1.0) },
        }, spare: 10, fallback: VersatileLure, bags: new() { [BruteLeech] = 5 }));
        var lure = plan[VersatileLure]!;
        Assert.Equal(15 + 3 + 10, lure.Needed); // 20 - 5 Brute Leech, 3 Spinnerbait, spare once
        Assert.Equal(new[] { Trout, OldLord }, lure.NeededFor);
        Assert.Equal(5, plan[BruteLeech]!.Needed);
        Assert.Null(plan[Spinnerbait]);
        Assert.Empty(plan.CraftSteps);
        Assert.Equal(new[] { Trout, OldLord }, plan.TargetsNeeding(VersatileLure));
    }

    [Fact]
    public void Bait_that_is_also_ordered_adds_the_bait_on_top()
    {
        var plan = Build(OrderOf((Lugworm, 5), (Trout, 10)), options: Fishing(new() { [Trout] = new[] { (Lugworm, 1.5) } }, spare: 20,
            bags: new() { [Lugworm] = 50 }));
        var worm = plan[Lugworm]!;
        Assert.True(worm.IsTarget);
        Assert.Equal(Supply.Vendor, worm.Supply);
        Assert.Equal(5 + 15 + 20, worm.Needed);
        Assert.Equal(0, worm.FromStock); // ordered items come on top of what's owned
        Assert.Equal(new[] { Trout }, worm.NeededFor);
    }

    [Fact]
    public void Without_bait_options_fish_lines_stand_alone()
    {
        var plan = Build(OrderOf((Trout, 10)));
        Assert.Equal(Supply.Fish, plan[Trout]!.Supply);
        Assert.Single(plan.Lines);
        Assert.Empty(Assert.Single(plan.Tree).Children);
    }
}
