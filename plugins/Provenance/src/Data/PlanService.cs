using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Lumina.Excel.Sheets;
using Provenance.Core.Catalog;
using Provenance.Core.Character;
using Provenance.Core.Crafting;
using Provenance.Core.Estimating;
using Provenance.Core.Fishing;
using Provenance.Integrations;
using Provenance.Core.Planning;

namespace Provenance.Data;

/// <summary>
/// What a run faces fishing one item, as of <see cref="At"/>: whether this character can at all
/// (<see cref="Blocker"/>), the spots it would fish at, and when the fish bites there.
/// </summary>
/// <param name="Blocker">Why this character can't fish it (level, reach, actions, no bait known), or null.</param>
/// <param name="Spots">
/// The spots a run can use, lowest level first; when there are none, the open-world spots the fish lives at
/// (so its window can still be shown).
/// </param>
/// <param name="Windows">When it bites from <see cref="At"/> on, over those spots; null when it bites any time.</param>
internal sealed record FishOutlook(FishInfo Fish, string? Blocker, IReadOnlyList<FishingSpotInfo> Spots,
    IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)>? Windows, DateTimeOffset At)
{
    /// <summary>Time until it bites: zero when it bites now (or any time), null when not within the forecast.</summary>
    public TimeSpan? OpensIn(DateTimeOffset now)
    {
        if (Windows == null) return TimeSpan.Zero;
        foreach (var (start, end) in Windows)
        {
            if (end <= now) continue;
            return start <= now ? TimeSpan.Zero : start - now;
        }
        return null;
    }

    /// <summary>Time left in the window it bites in now: zero when closed, <see cref="TimeSpan.MaxValue"/> for fish that always bite.</summary>
    public TimeSpan OpenFor(DateTimeOffset now)
    {
        if (Windows == null) return TimeSpan.MaxValue;
        foreach (var (start, end) in Windows)
        {
            if (end <= now) continue;
            return start <= now ? end - now : TimeSpan.Zero;
        }
        return TimeSpan.Zero;
    }
}

/// <summary>
/// Owns the catalog (built in the background at load), the owned-item snapshot and the current plan.
/// The plan is rebuilt when the order, the settings or the bags change. Fish in the plan come with their
/// bait (<see cref="BaitPerCatch"/>) and an <see cref="Outlook"/>: where this character can fish them and when they bite.
/// </summary>
internal sealed class PlanService : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly Func<Configuration> config;
    private readonly Raphael raphael;
    private DateTime nextEstimate = DateTime.MinValue;
    private readonly Task<GameCatalog> loading;
    private Dictionary<uint, int> owned = new();
    private DateTime nextStockCheck = DateTime.MinValue;
    private DateTime nextRebuild = DateTime.MinValue;
    private bool dirty = true;

    public PlanService(Func<Configuration> config, Raphael raphael)
    {
        this.config = config;
        this.raphael = raphael;
        loading = Task.Run(GameCatalog.Build);
        Services.ClientState.ClassJobChanged += OnClassJobChanged;
        Services.ClientState.LevelChanged += OnLevelChanged;
        Services.ClientState.Login += OnLogin;
    }

    /// <summary>When to check the character by itself: shortly after a job change, a level-up or a login.</summary>
    private DateTime? autoCheckAt;

    // Only the job being played can be measured exactly, so a job change is the moment to look. The new
    // gearset's stats need a moment to settle first.
    private void OnClassJobChanged(uint classJobId) => autoCheckAt = DateTime.UtcNow.AddSeconds(2.5);
    private void OnLevelChanged(uint classJobId, uint level) => autoCheckAt = DateTime.UtcNow.AddSeconds(2.5);
    private void OnLogin() => autoCheckAt = DateTime.UtcNow.AddSeconds(5);

    /// <summary>The automatic check, quietly; it waits while crafting, gathering, fishing or between areas.</summary>
    private void AutoCheck()
    {
        var c = Services.Condition;
        if (!Services.ClientState.IsLoggedIn || Services.Objects.LocalPlayer == null
            || c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51] || c[ConditionFlag.OccupiedInCutSceneEvent]
            || c[ConditionFlag.Crafting] || c[ConditionFlag.ExecutingCraftingAction] || c[ConditionFlag.Gathering] || c[ConditionFlag.ExecutingGatheringAction]
            || c[ConditionFlag.Fishing])
        {
            autoCheckAt = DateTime.UtcNow.AddSeconds(2);
            return;
        }
        autoCheckAt = null;
        CheckCharacter();
    }

    public GameCatalog? Catalog => loading.IsCompletedSuccessfully ? loading.Result : null;
    public string? LoadError => loading.IsFaulted ? loading.Exception?.GetBaseException().Message : null;
    public Plan Plan { get; private set; } = Plan.Empty;
    /// <summary>Collectables to make or gather for scrips before the order (empty when none are needed).</summary>
    public Plan ScripPlan { get; private set; } = Plan.Empty;
    /// <summary>What the scrip plan earns, per scrip.</summary>
    public IReadOnlyList<Core.Scrips.ScripTask> ScripTasks { get; private set; } = Array.Empty<Core.Scrips.ScripTask>();
    /// <summary>Scrips the scrip plan is expected to bring in, by scrip item.</summary>
    private readonly Dictionary<uint, Core.Scrips.ScripTask> earning = new();
    /// <summary>How long the order takes, refreshed every second.</summary>
    public OrderEstimate Estimate { get; private set; } = OrderEstimate.Empty;
    public IReadOnlyDictionary<uint, int> Owned => owned;
    /// <summary>What the character can't do for this order yet, refreshed every second.</summary>
    public IReadOnlyList<ReadinessIssue> Readiness { get; private set; } = Array.Empty<ReadinessIssue>();
    public bool Ready => !Readiness.Any(i => i.Blocking);

    /// <summary>Call after changing the order or any setting that affects planning.</summary>
    public void Invalidate() => dirty = true;

    /// <summary>Framework thread: refreshes the stock snapshot about once a second and rebuilds when needed.</summary>
    public void Tick()
    {
        var catalog = Catalog;
        if (catalog == null) return;
        if (autoCheckAt is { } at && DateTime.UtcNow >= at) AutoCheck();

        if (DateTime.UtcNow >= nextStockCheck && Services.ClientState.IsLoggedIn)
        {
            nextStockCheck = DateTime.UtcNow.AddSeconds(1);
            RetainerCache.Tick(config());
            var snapshot = Stock.Snapshot(config());
            if (!SameCounts(snapshot, owned))
            {
                owned = snapshot;
                dirty = true;
            }
        }

        // Levels and books change without touching the bags; rebuild now and then to pick them up.
        if (DateTime.UtcNow >= nextRebuild) dirty = true;
        // Where bait no gil vendor sells is exchanged instead: once known, the plan's reasons can name it.
        if (baitExchanges is { IsCompleted: true } && !baitExchangesSeen)
        {
            baitExchangesSeen = true;
            if (baitExchanges.IsFaulted) Services.Log.Warning(baitExchanges.Exception!.GetBaseException(), "Couldn't index where bait is exchanged");
            dirty = true;
        }
        if (DateTime.UtcNow >= nextEstimate)
        {
            nextEstimate = DateTime.UtcNow.AddSeconds(1);
            var cfg = config();
            var hqItems = cfg.Order.Lines.Where(o => o.Hq).Select(o => o.ItemId).ToHashSet();
            Estimate = Estimator.Estimate(Plan, cfg.Timings, Steps, hqItems.Contains, CollectableInputsFor(cfg), FishInputsFor());
            Readiness = Data.Readiness.Check(Plan, catalog, Profile, cfg, RotationProblem, Outlook);
        }
        if (!dirty) return;
        Rebuild();
    }

    /// <summary>Plans the order now (framework thread), and the scrips to earn first when books need more than you have.</summary>
    public void Rebuild()
    {
        var catalog = Catalog;
        if (catalog == null) return;
        dirty = false;
        nextRebuild = DateTime.UtcNow.AddSeconds(10);
        var c = config();
        reachable.Clear();
        earning.Clear();
        Plan = BuildPlan(c.Order, catalog);
        ScripTasks = Array.Empty<Core.Scrips.ScripTask>();
        ScripPlan = Plan.Empty;
        if (!c.Run.EarnScrips || !Services.ClientState.IsLoggedIn) return;

        // Scrips the books still need, per scrip, beyond what you have.
        var cost = new Dictionary<uint, int>();
        foreach (var line in Plan.Learn.Where(l => l.Remaining > 0 && !l.WillBuy))
            if (BookOffer(line.ItemId) is { } offer)
                cost[offer.CurrencyItemId] = cost.GetValueOrDefault(offer.CurrencyItemId) + offer.Cost;
        var tasks = new List<Core.Scrips.ScripTask>();
        foreach (var (currency, total) in cost)
        {
            var missing = total - GameState.CurrencyCount(currency);
            if (EasiestEarning(currency, missing, catalog) is { } task)
            {
                tasks.Add(task);
                earning[currency] = task;
            }
        }
        if (tasks.Count == 0) return;
        ScripTasks = tasks;
        var scripOrder = new Core.Orders.Order { Name = "Scrips" };
        foreach (var t in tasks) scripOrder.Lines.Add(new Core.Orders.OrderLine(t.Option.ItemId, t.Count, 0, false));
        ScripPlan = BuildPlan(scripOrder, catalog);
        Plan = BuildPlan(c.Order, catalog); // books now count the scrips the collectables bring in
    }

    /// <summary>
    /// Among the collectables that earn the scrips, the one whose materials are least work: each
    /// candidate is planned like an order. Candidates needing monster drops, books, items to get
    /// yourself or fish this character can't catch are left out; crafts, gathering, timed nodes and
    /// fishing (by the time a catch takes and the wait for its window) add to the effort.
    /// </summary>
    private Core.Scrips.ScripTask? EasiestEarning(uint currency, int missing, GameCatalog catalog)
    {
        Core.Scrips.ScripTask? best = null;
        var bestEffort = double.MaxValue;
        var now = DateTimeOffset.UtcNow;
        var trip = config().Timings.SecondsPerFishingTrip;
        foreach (var task in Core.Scrips.ScripPlanner.Candidates(currency, missing, catalog.Collectables, JobLevel, o => CanEarnWith(o, catalog)).Take(40))
        {
            var order = new Core.Orders.Order { Name = "Scrips" };
            order.Lines.Add(new Core.Orders.OrderLine(task.Option.ItemId, task.Count, 0, false));
            var plan = BuildPlan(order, catalog);
            if (plan.Missing.Count > 0 || plan.Learn.Count > 0) continue;
            if (plan.Lines.Any(l => l.Supply == Supply.Drop && l.Remaining > 0)) continue;
            // Fish the run can't catch for this character would leave the scrip phase waiting for ever.
            var fish = plan.Lines.Where(l => l.Supply == Supply.Fish && l.Remaining > 0).ToList();
            if (fish.Any(l => FishProblem(l.ItemId) != null || Outlook(l.ItemId)?.OpensIn(now) == null || BaitMissing(l.ItemId) != null)) continue;
            // One effort point is about 5 seconds: a gathered item.
            var effort = plan.CraftSteps.Sum(l => l.Crafts) * 6.0
                         + plan.Lines.Where(l => l.Supply is Supply.Gather && !Items.IsCrystal(l.ItemId)).Sum(l => l.Remaining) * 1.0
                         + plan.Lines.Where(l => l.Supply == Supply.GatherTimed).Sum(l => 60.0 + l.Remaining)
                         + plan.Lines.Where(l => l.Supply == Supply.Vendor).Sum(l => 2.0)
                         + plan.Lines.Where(l => l.Supply is Supply.Gather && Items.IsCrystal(l.ItemId)).Sum(l => l.Remaining) * 0.1
                         + fish.Sum(l => (trip + l.Remaining * CatchSeconds(l.ItemId) + (Outlook(l.ItemId)?.OpensIn(now)?.TotalSeconds ?? 0)) / 5.0);
            if (effort < bestEffort) { best = task; bestEffort = effort; }
        }
        return best;
    }

    /// <summary>A collectable the character can make or gather now: level, book, stats and folklore all fine; no fishing.</summary>
    private bool CanEarnWith(Core.Scrips.CollectableOption o, GameCatalog catalog)
    {
        if (o.ClassJobId == 18) return false;
        if (o.Crafted)
        {
            var recipe = catalog.RecipesFor(o.ItemId).FirstOrDefault();
            if (recipe == null || recipe.Expert || RecipeBlocker(recipe) != null || MissingBook(recipe, catalog) != 0) return false;
            var needs = catalog.NeedsFor(recipe);
            var job = Profile?.Job(recipe.Job.ClassJobId());
            if (job == null || job.Source == StatSource.None) return needs == default;
            return needs.Craftsmanship <= job.Stats[0] && needs.Control <= job.Stats[1] && (!needs.Specialist || job.Specialist);
        }
        var src = catalog.Sources(o.ItemId);
        if (src.Tome != 0 && !HasTome(src.Tome)) return false;
        if (catalog.GatherNeedsFor(o.ItemId, JobLevel) is { Perception: > 0 } g && Profile?.Job(g.ClassJobId) is { Source: not StatSource.None } gj)
            return gj.Stats[1] >= g.Perception;
        return true;
    }

    private Plan BuildPlan(Core.Orders.Order order, GameCatalog catalog)
    {
        var c = config();
        // Bait is planned and bought before fishing; with buying bait off the player brings their own
        // (the readiness check looks at the bags instead).
        var buyBait = c.Fishing.BuyBait;
        return Planner.Build(order, catalog, owned, new PlannerOptions
        {
            CountOwnedTargets = c.Stock.CountFinishedItems,
            PreferVendor = c.Buying.PreferVendor,
            VendorMaxPrice = c.Buying.VendorMaxPrice,
            ChooseRecipe = ChooseRecipe,
            RecipeBlocker = RecipeBlocker,
            MissingBook = r => MissingBook(r, catalog),
            HasTome = HasTome,
            BuyBook = BuyBook,
            FishBaits = buyBait ? BaitPerCatch : null,
            SpareBait = Math.Max(0, c.Fishing.SpareBait),
            FallbackBait = buyBait ? LureStandIn(catalog, c) : 0,
            BaitOnHand = id => GameState.BagCount(id),
            BaitSource = id => BaitSource(catalog, id),
        });
    }

    // ------------------------------------------------------------------ fishing

    /// <summary>How far ahead fish windows are looked for (the weather forecast is exact, a plan needs a limit).</summary>
    public static readonly TimeSpan FishHorizon = Core.Fishing.FishWindow.Forecast;

    /// <summary>
    /// How long a fish outlook is kept: reach and levels change now and then (checking reach reads the teleport
    /// list, so not every frame). Its windows are absolute times, so countdowns from them stay right meanwhile.
    /// </summary>
    private static readonly TimeSpan OutlookFor = TimeSpan.FromSeconds(10);

    private readonly Dictionary<uint, IReadOnlyList<(uint Bait, double PerCatch)>> baitPerCatch = new();
    private readonly Dictionary<uint, double> catchSeconds = new();
    private readonly Dictionary<uint, FishOutlook> outlooks = new();
    private Task<ExchangeIndex>? baitExchanges;
    private bool baitExchangesSeen;

    /// <summary>
    /// What a run faces fishing an item: whether this character can, where, and when it bites (worked out
    /// every few seconds). Null for items Provenance has no fishing data for. Framework thread.
    /// </summary>
    public FishOutlook? Outlook(uint itemId)
    {
        if (Catalog is not { } catalog || catalog.Gather.Fish(itemId) is not { } fish) return null;
        var now = DateTimeOffset.UtcNow;
        if (outlooks.TryGetValue(itemId, out var known) && now - known.At < OutlookFor) return known;
        var data = catalog.Gather;
        string? blocker = null;
        var spots = new List<FishingSpotInfo>();
        if (Services.ClientState.IsLoggedIn && Services.PlayerState.IsLoaded)
        {
            var level = JobLevel(FishAccess.Fisher);
            blocker = FishAccess.Blocker(data, fish, level);
            if (level > 0) spots = FishAccess.UsableSpots(data, fish, level);
        }
        // None usable: the window is still worth showing, from every open-world spot it lives at.
        if (spots.Count == 0)
            spots = fish.Spots.Select(data.Spot).OfType<FishingSpotInfo>()
                .Where(s => s.Spear == fish.Spear && FishAccess.OpenWorld(s.TerritoryId))
                .OrderBy(s => s.Level).ThenBy(s => s.Id).ToList();
        var windows = fish.Restricted
            ? FishSchedule.Windows(fish, spots.Select(s => s.TerritoryId).Distinct().Select(data.WeatherTable), now, FishHorizon)
            : null;
        var outlook = new FishOutlook(fish, blocker, spots, windows, now);
        outlooks[itemId] = outlook;
        return outlook;
    }

    /// <summary>
    /// Why a run can't fish an item for this character now (no fishing data, level, reach, actions, no
    /// Fisher gearset), or null when it can. Framework thread.
    /// </summary>
    public string? FishProblem(uint itemId)
    {
        if (Outlook(itemId) is not { } o) return "Provenance has no fishing data for it";
        if (o.Blocker != null) return o.Blocker;
        if (Services.ClientState.IsLoggedIn && GameState.BestGearset(FishAccess.Fisher) < 0) return "no gearset saved for Fisher";
        return null;
    }

    /// <summary>The bait one catch of a fish uses up, by bait (worked out once per fish).</summary>
    public IReadOnlyList<(uint Bait, double PerCatch)> BaitPerCatch(uint fishId)
    {
        if (baitPerCatch.TryGetValue(fishId, out var known)) return known;
        if (Catalog is not { } catalog) return Array.Empty<(uint, double)>();
        var list = catalog.Gather.Fish(fishId) is { } fish ? BaitNeeds.PerCatch(fish, catalog.Gather.Fish) : Array.Empty<(uint, double)>();
        baitPerCatch[fishId] = list;
        return list;
    }

    /// <summary>Fishing time one catch of a fish takes, in seconds (worked out once per fish; 0 without fish data).</summary>
    public double CatchSeconds(uint fishId)
    {
        if (catchSeconds.TryGetValue(fishId, out var known)) return known;
        if (Catalog is not { } catalog) return 0;
        var seconds = catalog.Gather.Fish(fishId) is { } fish ? BaitNeeds.SecondsPerCatch(fish, catalog.Gather.Fish) : 0;
        catchSeconds[fishId] = seconds;
        return seconds;
    }

    /// <summary>
    /// With buying bait off: a bait a fish needs that the bags hold none of (Versatile Lure in the bags counts
    /// when it may stand in), or null. With buying on, the plan's bait lines take care of it.
    /// </summary>
    public uint? BaitMissing(uint fishId)
    {
        var c = config();
        if (c.Fishing.BuyBait || !Services.ClientState.IsLoggedIn) return null;
        if (c.Fishing.VersatileLureFallback && GameState.BagCount(FishAccess.VersatileLure) > 0) return null;
        foreach (var (bait, _) in BaitPerCatch(fishId))
            if (GameState.BagCount(bait) == 0) return bait;
        return null;
    }

    /// <summary>Versatile Lure as the stand-in for bait no vendor sells, when the setting is on and it can be bought under the cap.</summary>
    private static uint LureStandIn(GameCatalog catalog, Configuration c)
    {
        if (!c.Fishing.VersatileLureFallback) return 0;
        var src = catalog.Sources(FishAccess.VersatileLure);
        var cap = c.Buying.VendorMaxPrice;
        return src.Has(Obtain.Vendor) && (cap == 0 || src.VendorPrice <= cap) ? FishAccess.VersatileLure : 0;
    }

    /// <summary>
    /// Where a bait no gil vendor sells is exchanged instead ("Sold for 2 White Gatherers' Scrips by the scrip
    /// exchange in Solution Nine, not for gil"), for the plan's reason; null until that is known, or when nowhere.
    /// </summary>
    private string? BaitSource(GameCatalog catalog, uint bait)
    {
        if (baitExchanges == null)
        {
            // Read once, in the background: the special shop sheets are big.
            var baits = catalog.Gather.AllFish.Select(f => f.Bait).Where(b => b != 0 && catalog.Vendors.For(b).Count == 0).ToHashSet();
            baitExchanges = Task.Run(() => ExchangeIndex.Build(baits, VendorIndex.LoadPositions()));
        }
        if (!baitExchanges.IsCompletedSuccessfully) return null;
        var offers = baitExchanges.Result.For(bait);
        if (offers.Count == 0) return null;
        var offer = offers.Where(o => CanReach(o.TerritoryId)).OrderBy(o => o.Cost).FirstOrDefault() ?? offers.OrderBy(o => o.Cost).First();
        return $"Sold for {offer.Cost:N0} {offer.CurrencyName} by {offer.Who} in {offer.PlaceName}, not for gil";
    }

    /// <summary>The estimate's view of fish: catch time from the fish data, windows from the outlook.</summary>
    private FishInputs FishInputsFor()
        => new(id => Outlook(id) is { } o ? new FishTime(CatchSeconds(id), o.Windows) : null, DateTimeOffset.UtcNow);

    /// <summary>"18:00–06:00 ET in Fog or Rain after Clear Skies": when a fish bites; "" for fish that bite any time.</summary>
    public static string BiteRule(FishInfo fish)
    {
        var parts = new List<string>(3);
        if (fish.Window is { On: > 0, Off: > 0 } w) parts.Add($"{EorzeaClock(w.Shift)}–{EorzeaClock(w.Shift + w.On)} ET");
        if (fish.Weather.Count > 0) parts.Add("in " + WeatherNames(fish.Weather));
        if (fish.PreviousWeather.Count > 0) parts.Add("after " + WeatherNames(fish.PreviousWeather));
        return string.Join(" ", parts);
    }

    /// <summary>Eorzea time of day for a moment given in real milliseconds since the epoch ("18:00").</summary>
    private static string EorzeaClock(long realMs)
    {
        const long day = 4_200_000; // one Eorzea day in real milliseconds
        var minutes = (long)Math.Round((realMs % day + day) % day / (day / 1440.0)) % 1440;
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }

    /// <summary>"Fog", "Fog or Rain", "Fog, Rain or Showers" in the client's language.</summary>
    public static string WeatherNames(IReadOnlyList<uint> weathers)
    {
        var sheet = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Weather>();
        var names = weathers.Select(id => sheet.GetRowOrDefault(id)?.Name.ExtractText() is { Length: > 0 } n ? n : $"weather {id}").Distinct().ToList();
        return names.Count <= 1 ? string.Join("", names) : string.Join(", ", names.Take(names.Count - 1)) + " or " + names[^1];
    }

    private readonly Dictionary<uint, bool> reachable = new();

    private bool CanReach(uint territory)
        => reachable.TryGetValue(territory, out var ok) ? ok : reachable[territory] = Teleporter.CanReach(territory);

    /// <summary>
    /// The exchange to buy a book or tome at: one the character can reach, cheapest first, then the
    /// one closest to an aetheryte (cities before outlying zones). Null when no reachable exchange sells it.
    /// </summary>
    public ExchangeOffer? BookOffer(uint itemId)
    {
        var catalog = Catalog;
        if (catalog == null) return null;
        // Prices in different currencies don't compare: first what you can pay now, then a scrip the
        // run can earn with collectables, then the most of the price you already have; cheapest within that.
        var earnable = catalog.Collectables.Select(c => c.Currency).ToHashSet();
        return catalog.Exchanges.For(itemId).Where(o => CanReach(o.TerritoryId))
            .OrderByDescending(o => GameState.CurrencyCount(o.CurrencyItemId) >= o.Cost)
            .ThenByDescending(o => earnable.Contains(o.CurrencyItemId))
            .ThenByDescending(o => Math.Min(1.0, GameState.CurrencyCount(o.CurrencyItemId) / (double)Math.Max(1, o.Cost)))
            .ThenBy(o => o.Cost)
            .ThenBy(o => o.NpcEntries)
            .ThenBy(o => Services.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(o.TerritoryId)?.TerritoryIntendedUse.RowId == 0 ? 0 : 1)
            .FirstOrDefault();
    }

    /// <summary>What the planner shows for a book that isn't in the bags: bought by the run, or why not.</summary>
    private BookPurchase? BuyBook(uint itemId)
    {
        var catalog = Catalog;
        if (catalog == null || !Services.ClientState.IsLoggedIn) return null;
        var all = catalog.Exchanges.For(itemId);
        if (all.Count == 0) return null;
        if (BookOffer(itemId) is not { } offer)
        {
            var any = all.OrderBy(o => o.Cost).First();
            return new BookPurchase(false, $"Sold for {any.Cost:N0} {any.CurrencyName} by {any.Who} in {any.PlaceName}, which you can't teleport to yet");
        }
        var have = GameState.CurrencyCount(offer.CurrencyItemId);
        if (have < offer.Cost && earning.TryGetValue(offer.CurrencyItemId, out var task) && have + task.Expected >= offer.Cost)
            return new BookPurchase(true, $"Costs {offer.Cost:N0} {offer.CurrencyName} (you have {have:N0}); the run earns the rest first with {task.Count}× {catalog.ItemName(task.Option.ItemId)}, then buys it from {offer.Who} in {offer.PlaceName}");
        if (have < offer.Cost)
            return new BookPurchase(false, $"Costs {offer.Cost:N0} {offer.CurrencyName} from {offer.Who} in {offer.PlaceName}; you have {have:N0}" +
                (config().Run.EarnScrips ? "" : ". Turn on \"Earn scrips for books\" in Settings to have the run earn them"));
        return new BookPurchase(true, $"The run buys it for {offer.Cost:N0} {offer.CurrencyName} from {offer.Who} in {offer.PlaceName} (you have {have:N0})");
    }

    /// <summary>
    /// Rotation length for a recipe at the stats from the last character check. Asks Raphael in the
    /// background the first time; null until it has answered (or when the character isn't checked).
    /// </summary>
    private int? Steps(RecipeInfo recipe, bool hq) => Request(recipe, hq) is { } r ? raphael.Solve(r) is { IsCompletedSuccessfully: true } t ? t.Result.Count : null : null;

    /// <summary>Raphael's "no rotation" answer for a recipe at the checked stats; null when fine or not answered yet.</summary>
    private string? RotationProblem(RecipeInfo recipe, bool hq)
    {
        if (Request(recipe, hq) is not { } r) return null;
        var task = raphael.Solve(r);
        return task.IsFaulted && task.Exception?.GetBaseException() is InvalidOperationException e ? e.Message.TrimEnd('.') + "." : null;
    }

    private RaphaelRequest? Request(RecipeInfo recipe, bool hq)
    {
        var job = Profile?.Job(recipe.Job.ClassJobId());
        if (job == null || job.Source == StatSource.None || job.Stats.Length < 3 || job.Level <= 0) return null;
        return new RaphaelRequest(recipe.RecipeId, job.Level, job.Stats[0], job.Stats[1], job.Stats[2], job.Manipulation,
            config().Crafting.UseSpecialistActions && job.Specialist, hq ? null : 0);
    }

    /// <summary>For the estimate: items that only come as collectables, the gatherer's GP, the cordials in the bags.</summary>
    private CollectableInputs CollectableInputsFor(Configuration cfg)
    {
        var catalog = Catalog;
        var profile = Profile;
        int MaxGp(uint itemId)
            => catalog?.GatherNeedsFor(itemId, JobLevel) is { } g && profile?.Job(g.ClassJobId) is { Stats.Length: >= 3 } js ? js.Stats[2] : 0;
        var cordials = cfg.Run.UseCordials ? Cordials.InBags() : new List<Core.Gathering.Cordial>();
        return new CollectableInputs(GameState.GatheredAsCollectable, MaxGp, cordials, cfg.Run.WaitForCollectableGp);
    }

    /// <summary>Time for one craft of a recipe, from the rotation length when known.</summary>
    public TimeSpan PerCraft(RecipeInfo recipe, bool hq)
    {
        var t = config().Timings;
        var steps = Steps(recipe, hq) ?? (hq ? Estimator.TypicalStepsHq : Estimator.TypicalStepsFast);
        return TimeSpan.FromSeconds(steps * t.SecondsPerAction + t.SecondsBetweenCrafts);
    }

    /// <summary>The logged-in character's last check, if any.</summary>
    public CharacterProfile? Profile
        => Services.PlayerState.IsLoaded && config().Characters.TryGetValue(Services.PlayerState.ContentId, out var p) ? p : null;

    /// <summary>Reads gearsets, stats, master books and folklore tomes now and stores them. Framework thread.</summary>
    public CharacterProfile? CheckCharacter()
    {
        var catalog = Catalog;
        if (catalog == null) return null;
        var profile = CharacterReader.Read(catalog, Profile);
        if (profile == null) return null;
        var c = config();
        c.Characters[profile.ContentId] = profile;
        c.Save();
        dirty = true;
        return profile;
    }

    /// <summary>Live level check, so a level-up counts at once.</summary>
    private static string? RecipeBlocker(RecipeInfo recipe)
    {
        if (!Services.PlayerState.IsLoaded) return null;
        var level = JobLevel(recipe.Job);
        if (level == 0) return $"Needs {recipe.Job}, which this character hasn't unlocked";
        if (level < recipe.Level) return $"Needs {recipe.Job} level {recipe.Level}; yours is {level}";
        return null;
    }

    /// <summary>The master book item a recipe needs and the character hasn't learned, or 0. Live check.</summary>
    private static uint MissingBook(RecipeInfo recipe, GameCatalog catalog)
        => recipe.MasterBook != 0 && !IsBookLearned(recipe.MasterBook) ? catalog.MasterBookItem(recipe.MasterBook) : 0;

    public static unsafe bool IsBookLearned(uint secretRecipeBook)
    {
        if (!Services.PlayerState.IsLoaded) return true;
        var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
        return ps == null || ps->IsSecretRecipeBookUnlocked(secretRecipeBook);
    }

    private static bool HasTome(uint tomeItem)
        => !Services.PlayerState.IsLoaded || ItemUnlocks.IsLearned(tomeItem);

    /// <summary>Among several recipes for one item, take one the character can craft now on its best-levelled job.</summary>
    private static RecipeInfo? ChooseRecipe(IReadOnlyList<RecipeInfo> recipes)
    {
        var usable = recipes.Where(r => JobLevel(r.Job) >= r.Level).ToList();
        return usable.Count > 0
            ? usable.OrderByDescending(r => JobLevel(r.Job)).ThenBy(r => r.RecipeId).First()
            : recipes.OrderBy(r => r.Level).ThenBy(r => r.RecipeId).First();
    }

    public static int JobLevel(Job job) => JobLevel(job.ClassJobId());

    /// <summary>Level of any job by ClassJob row (0 when not unlocked or not logged in).</summary>
    public static int JobLevel(uint classJobId)
    {
        if (!Services.PlayerState.IsLoaded) return 0;
        var row = Services.Data.GetExcelSheet<ClassJob>().GetRowOrDefault(classJobId);
        return row is { } cj ? Services.PlayerState.GetClassJobLevel(cj) : 0;
    }

    public static async Task<string> FetchAsync(string url, CancellationToken ct)
    {
        // Firestore puts its error JSON in non-2xx bodies; the parser reports it, so always return the body.
        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static bool SameCounts(Dictionary<uint, int> a, Dictionary<uint, int> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (k, v) in a)
            if (!b.TryGetValue(k, out var w) || w != v) return false;
        return true;
    }

    public void Dispose()
    {
        Services.ClientState.ClassJobChanged -= OnClassJobChanged;
        Services.ClientState.LevelChanged -= OnLevelChanged;
        Services.ClientState.Login -= OnLogin;
    }
}
