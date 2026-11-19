using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Provenance.Core.Fishing;
using Xunit;
using static Provenance.Tests.FishCases;

namespace Provenance.Tests;

/// <summary>
/// Catch plans for real fish from the plugin's fish.json, at their real fishing holes and with their zones'
/// weather: windows that come from a mooch chain or from the fish caught for Fisher's Intuition, and when
/// the predators are started.
/// </summary>
public class FishCatchShippedTests
{
    private const long Hour = 175_000;     // one Eorzea hour in real milliseconds
    private const long Day = 24 * Hour;
    private const long SomeDay = 400_000;  // an Eorzea day in 2023, as good as any

    /// <summary>A moment on <see cref="SomeDay"/> (or days later) at an Eorzea hour.</summary>
    private static DateTimeOffset Et(double hour, long days = 0) => DateTimeOffset.FromUnixTimeMilliseconds((SomeDay + days) * Day + (long)(hour * Hour));

    /// <summary>Between casts at <paramref name="at"/>, with the windows the plan gives then (as the Angler reads them).</summary>
    private static RodSnapshot ReadyAt(CatchPlan plan, DateTimeOffset at, uint bait, params (uint Fish, int Caught)[] progress)
    {
        var predators = plan.PredatorWindows.ToDictionary(kv => kv.Key, kv => kv.Value.At(at));
        return Ready(bait) with
        {
            Window = plan.Window.At(at),
            PredatorWindow = id => predators.TryGetValue(id, out var w) ? w : CatchWindowState.Always,
            Progress = progress.ToDictionary(p => p.Fish, p => p.Caught),
        };
    }

    // ------------------------------------------------------------------ windows from a mooch chain

    [Theory]
    [InlineData(4906u, 4898u, 63u, 18, 6)]   // Nautilus, only mooched from Fullmoon Sardine (18:00-06:00 ET)
    [InlineData(4918u, 4898u, 63u, 18, 6)]   // Coelacanth, the same
    [InlineData(23055u, 22397u, 168u, 4, 8)]
    [InlineData(23056u, 22397u, 168u, 4, 8)]
    [InlineData(24203u, 21177u, 168u, 0, 4)]
    public void A_fish_mooched_from_a_timed_fish_bites_only_while_that_one_does(uint target, uint link, uint spot, int opens, int closes)
    {
        var fish = ShippedFish.Get(target);
        Assert.False(fish.Restricted);
        var plan = ShippedFish.Plan(target, spot);
        Assert.Null(plan.Problem);
        Assert.Equal(new[] { link }, plan.Window.Links.Select(l => l.ItemId));
        Assert.True(plan.Window.Restricted);

        // Closed between its link's hours: wait for them rather than cast for a link that can't bite.
        var before = Et((opens + 20) % 24);
        var closed = plan.Window.At(before);
        Assert.False(closed.Open);
        Assert.Equal(TimeSpan.FromMilliseconds((opens - (opens + 20) % 24 + 24) % 24 * Hour), closed.OpensIn);
        Assert.Equal(RodMove.Idle(RodWait.Window, fish: target), CatchDecider.Next(plan, ReadyAt(plan, before, fish.Bait)));
        // The run times its trips by the fish's own data, which has no window: the session waits however long.
        Assert.Equal(TimeSpan.MaxValue, plan.EarlyLimit);

        // Open with its link: cast for the link, and only until the link's hours end.
        var during = Et(opens + 1);
        var open = plan.Window.At(during);
        Assert.True(open.Open);
        Assert.Equal(TimeSpan.FromMilliseconds(((closes - opens + 24) % 24 - 1) * Hour), open.Left);
        Assert.Equal(new RodMove(RodMoveKind.Cast, FishingIds.Cast, link), CatchDecider.Next(plan, ReadyAt(plan, during, fish.Bait)));
        // Closed after being open: the session ends.
        var after = ReadyAt(plan, Et(closes + 0.5), fish.Bait) with { WindowSeenOpen = true };
        Assert.Equal(CatchEnd.WindowClosed, CatchDecider.Next(plan, after).End);
    }

    [Fact]
    public void Shonisaurus_waits_for_the_blizzards_its_mahar_needs()
    {
        // 8772 bites at any time, but only from a mooched Mahar (8771: Blizzards after Clear Skies or Fair Skies).
        var plan = ShippedFish.Plan(8772, 27);
        var mahar = ShippedFish.Get(8771);
        Assert.Equal(new[] { 8771u }, plan.Window.Links.Select(l => l.ItemId));
        var table = ShippedFish.WeatherOf(27);
        var from = Et(0);
        for (var i = 0; i < 40; i++)
        {
            var at = from + TimeSpan.FromMinutes(37 * i);
            var state = plan.Window.At(at);
            Assert.Equal(FishWindow.IsOpen(mahar, table, at), state.Open);
            if (!state.Open) Assert.Equal(FishWindow.UntilOpen(mahar, table, at, CatchWindow.Horizon), state.OpensIn);
        }
        // Hours away is still waited out at the stand, not ended (the run would only come straight back).
        var far = Enumerable.Range(0, 2000).Select(i => from + TimeSpan.FromMinutes(5 * i))
            .First(t => plan.Window.At(t).OpensIn > TimeSpan.FromHours(2));
        Assert.Equal(RodMove.Idle(RodWait.Window, fish: 8772), CatchDecider.Next(plan, ReadyAt(plan, far, 2605)));
    }

    // ------------------------------------------------------------------ predators in their own windows

    [Theory]
    [InlineData(8775u, 8774u, 17u)]   // Namitaro: Giant Takitaro only bites in Thunder or Thunderstorms
    [InlineData(8763u, 8762u, 94u)]   // Kuno the Killer: Canavan only bites 08:00-18:00 ET in Gloom
    public void A_target_whose_predator_is_out_of_its_window_waits_for_it(uint target, uint predator, uint spot)
    {
        var plan = ShippedFish.Plan(target, spot);
        Assert.Null(plan.Problem);
        Assert.False(plan.Window.Restricted);
        Assert.True(plan.PredatorWindows.ContainsKey(predator));
        var bait = ShippedFish.Get(predator).Bait;
        var moments = Enumerable.Range(0, 3000).Select(i => Et(0) + TimeSpan.FromMinutes(7 * i)).ToList();

        // Out of its window: a window wait for it (not fishing time), never a cast for nothing.
        var closed = moments.First(t => !plan.PredatorWindows[predator].At(t).Open);
        Assert.Equal(RodMove.Idle(RodWait.Predator, fish: predator), CatchDecider.Next(plan, ReadyAt(plan, closed, bait)));
        // In it: fish for it.
        var open = moments.First(t => plan.PredatorWindows[predator].At(t).Open);
        Assert.Equal(RodMoveKind.Cast, CatchDecider.Next(plan, ReadyAt(plan, open, bait)).Kind);
        // Under Fisher's Intuition the target bites whatever the predator's window.
        var intuition = ReadyAt(plan, closed, ShippedFish.Get(target).Bait) with { Statuses = Statuses((FishingIds.IntuitionStatus, 0)) };
        Assert.Equal(new RodMove(RodMoveKind.Cast, FishingIds.Cast, plan.TargetRoute.First.ItemId), CatchDecider.Next(plan, intuition));

        // A predator that doesn't bite within the forecast at all (its weather never comes here): the session ends.
        var never = ShippedFish.Plan(target, spot, new (uint, int)[] { (2, 100) });
        Assert.Equal(CatchEnd.WindowClosed, CatchDecider.Next(never, ReadyAt(never, closed, bait)).End);
    }

    [Fact]
    public void Warden_s_predators_bite_only_while_their_mooch_links_do()
    {
        // 24994 needs 3 of 23056 (mooched from 22397, 04:00-08:00 ET), 3 of 24203 (from 21177, 00:00-04:00 ET)
        // and 5 of 24204 (its own window); 23056 and 24203 have none of their own.
        var plan = ShippedFish.Plan(24994, 168);
        Assert.Null(plan.Problem);
        Assert.Equal(new[] { 23056u, 24203u, 24204u }, plan.PredatorWindows.Keys.OrderBy(k => k));
        Assert.Equal(new[] { 22397u }, plan.PredatorWindows[23056].Links.Select(l => l.ItemId));
        Assert.Equal(new[] { 21177u }, plan.PredatorWindows[24203].Links.Select(l => l.ItemId));

        var five = Et(5);
        Assert.True(plan.PredatorWindows[23056].At(five).Open);
        Assert.Equal(TimeSpan.FromMilliseconds(19 * Hour), plan.PredatorWindows[24203].At(five).OpensIn);
        // Only 24203 still needed, at noon: wait for 21177's hours instead of casting for it.
        var noon = ReadyAt(plan, Et(12), 20675, (24204u, 5), (23056u, 3));
        Assert.Equal(RodMove.Idle(RodWait.Predator, fish: 24203), CatchDecider.Next(plan, noon));
        // A 21177 bite at noon isn't hooked either.
        var bite = noon with { Phase = RodPhase.Bite, Tug = Tug.Strong, SinceCast = 10, Cast = new RodCast(CastContext.Tackle(false)) };
        Assert.Equal(RodMoveKind.Rest, CatchDecider.Next(plan, bite).Kind);
        // At 01:00 ET it bites: mooch chain on.
        Assert.Equal(new RodMove(RodMoveKind.Cast, FishingIds.Cast, 21177), CatchDecider.Next(plan, ReadyAt(plan, Et(1, 1), 20675, (24204u, 5), (23056u, 3))));
    }

    // ------------------------------------------------------------------ when the predators start

    [Fact]
    public void A_predator_that_only_bites_before_the_window_is_caught_from_its_own_window()
    {
        // 33244 bites 00:00-02:00 ET (Clouds after Fair Skies) and needs 2 of 33325, which only bites
        // 22:00-00:00 ET in Fair Skies: the predators have to come in the two hours before, and the 700 s
        // intuition reaches from there into the window.
        var target = ShippedFish.Get(33244);
        Assert.Equal(TimeSpan.FromSeconds(142), CatchPlanner.PredatorTime(target, ShippedFish.Data));
        var plan = ShippedFish.Plan(33244, 232);
        Assert.Null(plan.Problem);
        Assert.Equal(TimeSpan.FromSeconds(350), plan.PrepLead);
        Assert.Equal(TimeSpan.FromSeconds(670), plan.LastCatchLead);

        var opens = plan.Window.NextOpen(Et(0), CatchWindow.Horizon)!.Value;
        // 22:17 ET: the predator bites, and catching both now still leaves the intuition for the window.
        Assert.Equal(new RodMove(RodMoveKind.Cast, FishingIds.Cast, 33325), CatchDecider.Next(plan, ReadyAt(plan, opens - TimeSpan.FromSeconds(300), 27588)));
        // Before its window there's nothing to do yet.
        Assert.Equal(RodMove.Idle(RodWait.Window, fish: 33244), CatchDecider.Next(plan, ReadyAt(plan, opens - TimeSpan.FromSeconds(400), 27588)));
        // Its window over with one still missing: wait for its next one (the target's own window ends the session first).
        var late = ReadyAt(plan, opens + TimeSpan.FromSeconds(30), 27588, (33325u, 1)) with { WindowSeenOpen = true };
        Assert.Equal(RodMove.Idle(RodWait.Predator, fish: 33325), CatchDecider.Next(plan, late));
    }

    [Fact]
    public void Twelve_predators_start_on_their_expected_time_and_the_last_waits_for_the_window()
    {
        // 41408 bites 14:00-16:00 ET (Fair Skies after Showers) and needs 12 of 36454, each mooched from
        // 36451: about 1332 s of predators, but only 300 s of intuition.
        var plan = ShippedFish.Plan(41408, 269);
        Assert.Null(plan.Problem);
        Assert.Equal(TimeSpan.FromSeconds(1332), plan.PrepLead);
        Assert.Equal(TimeSpan.FromSeconds(270), plan.LastCatchLead);

        var opens = plan.Window.NextOpen(Et(0), CatchWindow.Horizon)!.Value;
        RodSnapshot Before(double seconds, int caught) => ReadyAt(plan, opens - TimeSpan.FromSeconds(seconds), 36593, (36454u, caught));
        // 08:00 ET on, 36454 bites (Fair Skies): build it up.
        Assert.Equal(new RodMove(RodMoveKind.Cast, FishingIds.Cast, 36451), CatchDecider.Next(plan, Before(1000, 0)));
        Assert.Equal(new RodMove(RodMoveKind.Cast, FishingIds.Cast, 36451), CatchDecider.Next(plan, Before(600, 10)));
        // The twelfth would bring the intuition 600 s early, and it lasts 300 s: hold it, mooch included.
        Assert.Equal(RodMove.Idle(RodWait.Window, fish: 36454), CatchDecider.Next(plan, Before(600, 11)));
        Assert.Equal(RodMove.Idle(RodWait.Window, fish: 36454), CatchDecider.Next(plan, Before(600, 11) with { JustCaught = true, LastCatch = 36451, CanMooch = true }));
        // Close enough: catch it.
        Assert.Equal(new RodMove(RodMoveKind.Cast, FishingIds.Cast, 36451), CatchDecider.Next(plan, Before(200, 11)));
        Assert.Equal(new RodMove(RodMoveKind.Mooch, FishingIds.Mooch, 36454), CatchDecider.Next(plan, Before(200, 11) with { JustCaught = true, LastCatch = 36451, CanMooch = true }));
        // Before 08:00 ET (Showers): wait for 36454's weather.
        Assert.Equal(RodMove.Idle(RodWait.Predator, fish: 36454), CatchDecider.Next(plan, Before(1200, 0)));
    }
}

/// <summary>Fish from the plugin's fish.json (read as GatherData reads them), and the weather of their zones.</summary>
internal static class ShippedFish
{
    private static readonly Lazy<Dictionary<uint, FishInfo>> All = new(Load);

    /// <summary>
    /// The zones' weather tables (TerritoryType → WeatherRate in the game data: weather, chance), for the fishing
    /// holes these tests use: spot → territory.
    /// </summary>
    private static readonly Dictionary<uint, (uint Weather, int Rate)[]> Weather = new()
    {
        [17] = new (uint, int)[] { (4, 5), (10, 5), (9, 15), (4, 5), (3, 10), (2, 30), (1, 30) },   // 153 South Shroud
        [27] = new (uint, int)[] { (16, 20), (15, 40), (2, 10), (1, 5), (3, 15), (4, 10) },          // 155 Coerthas Central Highlands
        [53] = new (uint, int)[] { (4, 5), (1, 45), (2, 30), (3, 10), (7, 5), (8, 5) },             // 137 Eastern La Noscea
        [63] = new (uint, int)[] { (4, 10), (1, 30), (2, 20), (3, 20), (5, 10), (6, 10) },          // 138 Western La Noscea
        [94] = new (uint, int)[] { (3, 15), (4, 15), (17, 30), (1, 15), (2, 25) },                  // 156 Mor Dhona
        [168] = new (uint, int)[] { (8, 5), (7, 10), (4, 10), (3, 15), (2, 40), (1, 20) },           // 614 Yanxia
        [232] = new (uint, int)[] { (3, 20), (2, 60), (1, 20) },                                     // 818 The Tempest
        [269] = new (uint, int)[] { (4, 10), (7, 10), (8, 5), (1, 15), (2, 40), (3, 20) },           // 957 Thavnair
    };

    public static FishInfo Get(uint id) => All.Value[id];

    public static FishInfo? Data(uint id) => All.Value.GetValueOrDefault(id);

    public static IReadOnlyList<(uint Weather, int Rate)> WeatherOf(uint spot) => Weather[spot];

    /// <summary>The catch plan at a fishing hole, with every fish living there, all actions learned, buffs off.</summary>
    public static CatchPlan Plan(uint target, uint spot, IReadOnlyList<(uint, int)>? table = null)
        => CatchPlanner.Plan(new CatchRequest(Get(target), 0, All.Value.Values.Where(f => f.Spots.Contains(spot)).ToList(), Data,
            AllLearned, 900, table ?? WeatherOf(spot), UseBuffs: false, UseCordials: false));

    private static Dictionary<uint, FishInfo> Load()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FishJson()));
        var fish = new Dictionary<uint, FishInfo>();
        foreach (var f in doc.RootElement.GetProperty("fish").EnumerateObject())
            fish[uint.Parse(f.Name)] = Read(uint.Parse(f.Name), f.Value);
        return fish;
    }

    private static FishInfo Read(uint id, JsonElement f)
    {
        uint[] Ids(string name) => f.TryGetProperty(name, out var a) ? a.EnumerateArray().Select(x => x.GetUInt32()).ToArray() : Array.Empty<uint>();
        string Str(string name) => f.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
        Uptime? window = null;
        if (f.TryGetProperty("interval", out var iv))
        {
            var a = iv.EnumerateArray().Select(x => x.GetInt64()).ToArray();
            window = new Uptime(a[0], a[1], a[2]);
        }
        (float, float)? bite = null;
        if (f.TryGetProperty("bite", out var b))
        {
            var a = b.EnumerateArray().Select(x => x.GetSingle()).ToArray();
            bite = (a[0], a[1]);
        }
        var predators = f.TryGetProperty("predators", out var pr)
            ? pr.EnumerateArray().Select(p => { var a = p.EnumerateArray().Select(x => x.GetUInt32()).ToArray(); return (a[0], (int)a[1]); }).ToArray()
            : Array.Empty<(uint, int)>();
        return new FishInfo(id, f.GetProperty("spear").GetBoolean(), f.TryGetProperty("bait", out var bait) ? bait.GetUInt32() : 0, Ids("mooch"),
            predators, f.TryGetProperty("intuition", out var it) ? it.GetInt32() : 0, window, Ids("weather"), Ids("previousWeather"),
            Str("hookset") switch
            {
                "Precise" => HookSet.Precise, "Powerful" => HookSet.Powerful, "Hook" => HookSet.Hook,
                "DoubleHook" => HookSet.DoubleHook, "TripleHook" => HookSet.TripleHook, "Stellar" => HookSet.Stellar,
                _ => HookSet.Unknown,
            },
            Str("tug") switch { "Weak" => Tug.Weak, "Strong" => Tug.Strong, "Legendary" => Tug.Legendary, _ => Tug.Unknown },
            Str("snag") == "Required", Str("lure"), SpearSize.Unknown, SpearSpeed.Unknown,
            f.TryGetProperty("surfaceSlap", out var slap) ? slap.GetUInt32() : 0,
            f.TryGetProperty("ocean", out var ocean) && ocean.GetBoolean(),
            f.TryGetProperty("big", out var big) && big.GetBoolean(),
            Ids("spots"), bite);
    }

    private static string FishJson()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        foreach (var path in new[] { Path.Combine(dir.FullName, "Provenance", "Resources", "fish.json"), Path.Combine(dir.FullName, "plugins", "Provenance", "Resources", "fish.json") })
            if (File.Exists(path)) return path;
        throw new FileNotFoundException("Provenance/Resources/fish.json wasn't found above " + AppContext.BaseDirectory);
    }
}
