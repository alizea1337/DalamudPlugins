using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Provenance.Core.Fishing;
using Xunit;

namespace Provenance.Tests;

public class SpearFishTests
{
    internal static FishInfo Spear(uint id, SpearSize size, SpearSpeed speed, params uint[] spots)
        => new(id, true, 0, Array.Empty<uint>(), Array.Empty<(uint, int)>(), 0, null, Array.Empty<uint>(), Array.Empty<uint>(),
            HookSet.Unknown, Tug.Unknown, false, "None", size, speed, 0, false, false, spots, null);

    [Theory]
    [InlineData(SpearSpeed.SuperSlow, 100)]
    [InlineData(SpearSpeed.ExtremelySlow, 150)]
    [InlineData(SpearSpeed.VerySlow, 200)]
    [InlineData(SpearSpeed.Slow, 250)]
    [InlineData(SpearSpeed.Average, 300)]
    [InlineData(SpearSpeed.Fast, 350)]
    [InlineData(SpearSpeed.VeryFast, 400)]
    [InlineData(SpearSpeed.ExtremelyFast, 450)]
    [InlineData(SpearSpeed.SuperFast, 500)]
    [InlineData(SpearSpeed.HyperFast, 550)]
    [InlineData(SpearSpeed.LynFast, 600)]
    public void Speeds_are_the_window_values_in_steps_of_50(SpearSpeed speed, int game)
    {
        Assert.Equal(game, SpearFish.GameSpeed(speed));
        Assert.Equal(speed, SpearFish.SpeedOf(game));
    }

    [Theory]
    [InlineData(SpearSize.Small, 1)]
    [InlineData(SpearSize.Average, 2)]
    [InlineData(SpearSize.Large, 3)]
    public void Sizes_are_the_window_values(SpearSize size, int game)
    {
        Assert.Equal(game, SpearFish.GameSize(size));
        Assert.Equal(size, SpearFish.SizeOf(game));
    }

    [Fact]
    public void Every_known_size_and_speed_maps_there_and_back()
    {
        foreach (var size in Enum.GetValues<SpearSize>().Where(s => s != SpearSize.Unknown))
            Assert.Equal(size, SpearFish.SizeOf(SpearFish.GameSize(size)!.Value));
        foreach (var speed in Enum.GetValues<SpearSpeed>().Where(s => s != SpearSpeed.Unknown))
            Assert.Equal(speed, SpearFish.SpeedOf(SpearFish.GameSpeed(speed)!.Value));
    }

    [Fact]
    public void Unknown_and_odd_values_stand_for_nothing()
    {
        Assert.Null(SpearFish.GameSize(SpearSize.Unknown));
        Assert.Null(SpearFish.GameSpeed(SpearSpeed.Unknown));
        // The game's "no size" is -1; AutoHook's catalogue had sizes and speeds no window ever shows.
        foreach (var odd in new[] { -1, 0, 4, 255 }) Assert.Equal(SpearSize.Unknown, SpearFish.SizeOf(odd));
        foreach (var odd in new[] { -1, 0, 1, 2, 50, 125, 650, 65535 }) Assert.Equal(SpearSpeed.Unknown, SpearFish.SpeedOf(odd));
    }

    [Fact]
    public void A_fish_is_recognised_only_by_its_exact_size_and_speed()
    {
        var ghost = Spear(27518, SpearSize.Small, SpearSpeed.Average);
        Assert.True(SpearFish.Matches(ghost, 1, 300));
        Assert.False(SpearFish.Matches(ghost, 1, 250));
        Assert.False(SpearFish.Matches(ghost, 1, 350));
        Assert.False(SpearFish.Matches(ghost, 2, 300));
        Assert.False(SpearFish.Matches(ghost, -1, 300));
        var wanted = new[] { ghost };
        Assert.True(SpearFish.Wanted(wanted, 1, 300));
        Assert.False(SpearFish.Wanted(wanted, 3, 300));
    }

    [Fact]
    public void Nothing_wanted_means_every_fish()
    {
        var none = Array.Empty<FishInfo>();
        Assert.True(SpearFish.Wanted(none, 1, 300));
        Assert.True(SpearFish.Wanted(none, -1, 0)); // even one the window can't size
        Assert.Empty(SpearFish.Candidates(none, 1, 300));
    }

    [Fact]
    public void A_fish_without_a_known_size_or_speed_is_never_recognised()
    {
        var noSpeed = Spear(1, SpearSize.Small, SpearSpeed.Unknown);
        var noSize = Spear(2, SpearSize.Unknown, SpearSpeed.Slow);
        Assert.False(SpearFish.Recognisable(noSpeed));
        Assert.False(SpearFish.Recognisable(noSize));
        Assert.True(SpearFish.Recognisable(Spear(3, SpearSize.Large, SpearSpeed.Slow)));
        var wanted = new[] { noSpeed, noSize };
        // Not even against what the window shows for a fish it can't size or time: no wildcards.
        foreach (var (size, speed) in new[] { (1, 0), (1, -1), (1, 250), (0, 250), (-1, 250), (-1, -1), (0, 0) })
            Assert.False(SpearFish.Wanted(wanted, size, speed));
    }

    [Fact]
    public void Two_wanted_fish_of_one_shape_are_both_candidates()
    {
        // Kobayashi Maru's Northern Wake: Eastern Sea Pickle and Horned Turban are both small and extremely slow.
        var pickle = Spear(20165, SpearSize.Small, SpearSpeed.ExtremelySlow);
        var turban = Spear(20174, SpearSize.Small, SpearSpeed.ExtremelySlow);
        var other = Spear(21180, SpearSize.Average, SpearSpeed.Fast);
        var candidates = SpearFish.Candidates(new[] { pickle, other, turban }, 1, 150);
        Assert.Equal(new uint[] { 20165, 20174 }, candidates.Select(f => f.ItemId));
        Assert.True(SpearFish.Wanted(new[] { other, turban }, 1, 150));
    }

    [Fact]
    public void Look_alikes_are_unwanted_fish_of_the_pool_with_the_same_size_and_speed()
    {
        // Eastern Isle of Ken (notebook 33): Purple Ghost and Shade Gudgeon are small and of average speed,
        // Lakelouse and Platinum Bream average and slow.
        var ghost = Spear(27518, SpearSize.Small, SpearSpeed.Average);
        var lakelouse = Spear(27519, SpearSize.Average, SpearSpeed.Slow);
        var gudgeon = Spear(27523, SpearSize.Small, SpearSpeed.Average);
        var bream = Spear(27525, SpearSize.Average, SpearSpeed.Slow);
        var bismarck = Spear(27534, SpearSize.Large, SpearSpeed.VeryFast);
        var pool = new[] { ghost, lakelouse, gudgeon, bream, bismarck };

        var pairs = SpearFish.LookAlikes(new[] { ghost, bismarck }, pool);
        Assert.Equal(new[] { (27518u, 27523u) }, pairs.Select(p => (p.Wanted.ItemId, p.LookAlike.ItemId)));
        Assert.Equal(2, SpearFish.LookAlikes(new[] { ghost, bream }, pool).Count);
        // Both twins wanted: neither is mistaken for something unwanted.
        Assert.Empty(SpearFish.LookAlikes(new[] { ghost, gudgeon }, pool));
        Assert.Empty(SpearFish.LookAlikes(new[] { bismarck }, pool));
        // A fish without a known size or speed looks like nothing.
        var mystery = Spear(9, SpearSize.Unknown, SpearSpeed.Unknown);
        Assert.Empty(SpearFish.LookAlikes(new[] { mystery }, pool.Append(Spear(10, SpearSize.Unknown, SpearSpeed.Unknown))));
    }

    [Fact]
    public void Shipped_spearfish_with_a_spot_can_all_be_recognised()
    {
        var fish = ShippedSpearfish();
        Assert.True(fish.Count > 250, $"only {fish.Count} spearfish read");
        var unrecognisable = fish.Where(f => f.Spots.Count > 0 && !SpearFish.Recognisable(f)).Select(f => f.ItemId).ToList();
        Assert.Empty(unrecognisable);
    }

    [Fact]
    public void Shipped_look_alikes_are_the_four_known_pairs()
    {
        // A tripwire for new data: these four pairs (all in swimming-shadow pools) can't be told apart in the
        // window. Gigging for one may land the other; the bags tell which it was.
        var fish = ShippedSpearfish();
        var pairs = new SortedSet<(uint Notebook, uint A, uint B)>();
        foreach (var spot in fish.SelectMany(f => f.Spots).Distinct())
        {
            var pool = fish.Where(f => f.Spots.Contains(spot)).ToList();
            foreach (var f in pool)
            foreach (var (wanted, other) in SpearFish.LookAlikes(new[] { f }, pool))
                pairs.Add((spot - FishingSpotInfo.SpearOffset, Math.Min(wanted.ItemId, other.ItemId), Math.Max(wanted.ItemId, other.ItemId)));
        }
        Assert.Equal(new[] { (29u, 20165u, 20174u), (33u, 27518u, 27523u), (33u, 27519u, 27525u), (34u, 27550u, 27555u) }, pairs.ToArray());
    }

    /// <summary>The spearfish in the plugin's fish.json, read the way GatherData reads them.</summary>
    private static List<FishInfo> ShippedSpearfish()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FishJson()));
        var list = new List<FishInfo>();
        foreach (var f in doc.RootElement.GetProperty("fish").EnumerateObject())
        {
            var v = f.Value;
            if (!v.GetProperty("spear").GetBoolean()) continue;
            var size = Enum.TryParse<SpearSize>(v.TryGetProperty("size", out var s) ? s.GetString() : "", out var sz) ? sz : SpearSize.Unknown;
            var speed = Enum.TryParse<SpearSpeed>(v.TryGetProperty("speed", out var p) ? p.GetString() : "", out var sp) ? sp : SpearSpeed.Unknown;
            var spots = v.TryGetProperty("spots", out var a) ? a.EnumerateArray().Select(x => x.GetUInt32()).ToArray() : Array.Empty<uint>();
            list.Add(Spear(uint.Parse(f.Name), size, speed, spots));
        }
        return list;
    }

    private static string FishJson()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        foreach (var path in new[] { Path.Combine(dir.FullName, "Provenance", "Resources", "fish.json"), Path.Combine(dir.FullName, "plugins", "Provenance", "Resources", "fish.json") })
            if (File.Exists(path)) return path;
        throw new FileNotFoundException("Provenance/Resources/fish.json wasn't found above " + AppContext.BaseDirectory);
    }
}
