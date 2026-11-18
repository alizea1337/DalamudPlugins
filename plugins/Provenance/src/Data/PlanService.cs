using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lumina.Excel.Sheets;
using Provenance.Core.Catalog;
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
    private readonly Task<GameCatalog> loading;
    private Dictionary<uint, int> owned = new();
    private DateTime nextStockCheck = DateTime.MinValue;
    private bool dirty = true;

    public PlanService(Func<Configuration> config)
    {
        this.config = config;
        loading = Task.Run(GameCatalog.Build);
    }

    public GameCatalog? Catalog => loading.IsCompletedSuccessfully ? loading.Result : null;
    public string? LoadError => loading.IsFaulted ? loading.Exception?.GetBaseException().Message : null;
    public Plan Plan { get; private set; } = Plan.Empty;
    public IReadOnlyDictionary<uint, int> Owned => owned;

    /// <summary>Call after changing the order or any setting that affects planning.</summary>
    public void Invalidate() => dirty = true;

    /// <summary>Framework thread: refreshes the stock snapshot about once a second and rebuilds when needed.</summary>
    public void Tick()
    {
        var catalog = Catalog;
        if (catalog == null) return;

        if (DateTime.UtcNow >= nextStockCheck && Services.ClientState.IsLoggedIn)
        {
            nextStockCheck = DateTime.UtcNow.AddSeconds(1);
            var snapshot = Stock.Snapshot(config().Stock);
            if (!SameCounts(snapshot, owned))
            {
                owned = snapshot;
                dirty = true;
            }
        }

        if (!dirty) return;
        dirty = false;
        var c = config();
        Plan = Planner.Build(c.Order, catalog, owned, new PlannerOptions
        {
            CountOwnedTargets = c.Stock.CountFinishedItems,
            PreferVendor = c.Buying.PreferVendor,
            VendorMaxPrice = c.Buying.VendorMaxPrice,
            ChooseRecipe = ChooseRecipe,
        });
    }

    /// <summary>Among several recipes for one item, take one the character can craft now on its best-levelled job.</summary>
    private static RecipeInfo? ChooseRecipe(IReadOnlyList<RecipeInfo> recipes)
    {
        var usable = recipes.Where(r => JobLevel(r.Job) >= r.Level).ToList();
        return usable.Count > 0
            ? usable.OrderByDescending(r => JobLevel(r.Job)).ThenBy(r => r.RecipeId).First()
            : recipes.OrderBy(r => r.Level).ThenBy(r => r.RecipeId).First();
    }

    public static int JobLevel(Job job)
    {
        if (!Services.PlayerState.IsLoaded) return 0;
        var row = Services.Data.GetExcelSheet<ClassJob>().GetRowOrDefault(job.ClassJobId());
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

    public void Dispose() { }
}
