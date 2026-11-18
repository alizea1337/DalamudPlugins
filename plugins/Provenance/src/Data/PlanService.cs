using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lumina.Excel.Sheets;
using Provenance.Core.Catalog;
using Provenance.Core.Character;
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
    private DateTime nextRebuild = DateTime.MinValue;
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

        // Levels and books change without touching the bags; rebuild now and then to pick them up.
        if (DateTime.UtcNow >= nextRebuild) dirty = true;
        if (!dirty) return;
        dirty = false;
        nextRebuild = DateTime.UtcNow.AddSeconds(10);
        var c = config();
        Plan = Planner.Build(c.Order, catalog, owned, new PlannerOptions
        {
            CountOwnedTargets = c.Stock.CountFinishedItems,
            PreferVendor = c.Buying.PreferVendor,
            VendorMaxPrice = c.Buying.VendorMaxPrice,
            ChooseRecipe = ChooseRecipe,
            RecipeBlocker = r => RecipeBlocker(r, catalog),
            HasTome = HasTome,
        });
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

    /// <summary>Live checks, so a newly learned book or level counts at once.</summary>
    private static unsafe string? RecipeBlocker(RecipeInfo recipe, GameCatalog catalog)
    {
        if (!Services.PlayerState.IsLoaded) return null;
        var level = JobLevel(recipe.Job);
        if (level == 0) return $"Needs {recipe.Job}, which this character hasn't unlocked";
        if (level < recipe.Level) return $"Needs {recipe.Job} level {recipe.Level}; yours is {level}";
        if (recipe.MasterBook != 0)
        {
            var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
            if (ps != null && !ps->IsSecretRecipeBookUnlocked(recipe.MasterBook))
                return $"Needs the master book {catalog.MasterBookName(recipe.MasterBook)}";
        }
        return null;
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
