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
using Provenance.Integrations;
using Provenance.Core.Planning;

namespace Provenance.Data;

/// <summary>
/// Owns the catalog (built in the background at load), the owned-item snapshot and the current plan.
/// The plan is rebuilt when the order, the settings or the bags change.
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

    /// <summary>The automatic check, quietly; it waits while crafting, gathering or between areas.</summary>
    private void AutoCheck()
    {
        var c = Services.Condition;
        if (!Services.ClientState.IsLoggedIn || Services.Objects.LocalPlayer == null
            || c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51] || c[ConditionFlag.OccupiedInCutSceneEvent]
            || c[ConditionFlag.Crafting] || c[ConditionFlag.ExecutingCraftingAction] || c[ConditionFlag.Gathering] || c[ConditionFlag.ExecutingGatheringAction])
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
        if (DateTime.UtcNow >= nextEstimate)
        {
            nextEstimate = DateTime.UtcNow.AddSeconds(1);
            var cfg = config();
            var hqItems = cfg.Order.Lines.Where(o => o.Hq).Select(o => o.ItemId).ToHashSet();
            Estimate = Estimator.Estimate(Plan, cfg.Timings, Steps, hqItems.Contains, CollectableInputsFor(cfg));
            Readiness = Data.Readiness.Check(Plan, catalog, Profile, cfg, RotationProblem);
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
    /// candidate is planned like an order. Candidates needing monster drops, books or items to get
    /// yourself are left out; crafts, gathering and especially timed nodes add to the effort.
    /// </summary>
    private Core.Scrips.ScripTask? EasiestEarning(uint currency, int missing, GameCatalog catalog)
    {
        Core.Scrips.ScripTask? best = null;
        var bestEffort = double.MaxValue;
        foreach (var task in Core.Scrips.ScripPlanner.Candidates(currency, missing, catalog.Collectables, JobLevel, o => CanEarnWith(o, catalog)).Take(40))
        {
            var order = new Core.Orders.Order { Name = "Scrips" };
            order.Lines.Add(new Core.Orders.OrderLine(task.Option.ItemId, task.Count, 0, false));
            var plan = BuildPlan(order, catalog);
            if (plan.Missing.Count > 0 || plan.Learn.Count > 0) continue;
            if (plan.Lines.Any(l => l.Supply == Supply.Drop && l.Remaining > 0)) continue;
            var effort = plan.CraftSteps.Sum(l => l.Crafts) * 6.0
                         + plan.Lines.Where(l => l.Supply is Supply.Gather or Supply.Fish && !Items.IsCrystal(l.ItemId)).Sum(l => l.Remaining) * 1.0
                         + plan.Lines.Where(l => l.Supply == Supply.GatherTimed).Sum(l => 60.0 + l.Remaining)
                         + plan.Lines.Where(l => l.Supply == Supply.Vendor).Sum(l => 2.0)
                         + plan.Lines.Where(l => l.Supply is Supply.Gather && Items.IsCrystal(l.ItemId)).Sum(l => l.Remaining) * 0.1;
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
        });
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
