using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Lumina.Excel.Sheets;
using Provenance.Core.Catalog;

namespace Provenance.Data;

/// <summary>One row of the recipe browser.</summary>
internal sealed record BrowseEntry(uint RecipeId, uint ItemId, string Name, Job Job, int Level, int Stars, uint Icon, int Yield, bool Expert, bool MasterBook)
{
    public readonly string NameLower = Name.ToLowerInvariant();
}

/// <summary>
/// Game data indexed once from the Excel sheets (on a worker thread at load): recipes by result,
/// non-craft sources per item, a name lookup for copied Teamcraft text and the browser rows.
/// </summary>
internal sealed class GameCatalog : IGameCatalog
{
    private static readonly IReadOnlyList<RecipeInfo> NoRecipes = Array.Empty<RecipeInfo>();
    private const char HqGlyph = '';

    private readonly Dictionary<uint, List<RecipeInfo>> recipesByItem = new();
    private readonly Dictionary<uint, RecipeInfo> recipesById = new();
    private readonly Dictionary<uint, ItemSources> sources = new();
    private readonly Dictionary<string, uint> itemsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<uint, (string Name, uint Icon)> itemCache = new();

    public IReadOnlyList<BrowseEntry> Browse { get; private set; } = Array.Empty<BrowseEntry>();

    public static GameCatalog Build()
    {
        var sw = Stopwatch.StartNew();
        var c = new GameCatalog();
        c.IndexRecipes();
        c.IndexGathering();
        c.IndexFishing();
        c.IndexVendors();
        c.IndexDrops();
        c.IndexNames();
        Services.Log.Information("Catalog ready: {Recipes} recipes, {Sources} sourced items in {Ms} ms", c.recipesById.Count, c.sources.Count, sw.ElapsedMilliseconds);
        return c;
    }

    // ------------------------------------------------------------------ IGameCatalog

    public string ItemName(uint itemId) => Lookup(itemId).Name;
    public uint ItemIcon(uint itemId) => Lookup(itemId).Icon;
    public IReadOnlyList<RecipeInfo> RecipesFor(uint itemId) => recipesByItem.TryGetValue(itemId, out var l) ? l : NoRecipes;
    public RecipeInfo? Recipe(uint recipeId) => recipesById.GetValueOrDefault(recipeId);
    public ItemSources Sources(uint itemId) => sources.GetValueOrDefault(itemId);

    public bool TryFindItem(string name, out uint itemId)
        => itemsByName.TryGetValue(CleanName(name), out itemId);

    private (string Name, uint Icon) Lookup(uint itemId) => itemCache.GetOrAdd(itemId, id =>
    {
        var row = Services.Data.GetExcelSheet<Item>().GetRowOrDefault(id);
        return row is { } r ? (r.Name.ExtractText(), (uint)r.Icon) : ("Item #" + id, 0u);
    });

    private static string CleanName(string name) => name.Replace(HqGlyph.ToString(), "").Trim();

    // ------------------------------------------------------------------ indexing

    private void IndexRecipes()
    {
        var items = Services.Data.GetExcelSheet<Item>();
        var browse = new List<BrowseEntry>();
        foreach (var r in Services.Data.GetExcelSheet<Recipe>())
        {
            var result = r.ItemResult.RowId;
            if (result == 0 || r.CraftType.RowId > 7) continue;
            var ingredients = new List<Ingredient>(8);
            for (var i = 0; i < r.Ingredient.Count && i < r.AmountIngredient.Count; i++)
            {
                var id = r.Ingredient[i].RowId;
                var amount = r.AmountIngredient[i];
                if (id != 0 && amount != 0) ingredients.Add(new Ingredient(id, amount));
            }
            if (ingredients.Count == 0) continue;

            var level = r.RecipeLevelTable.ValueNullable;
            var info = new RecipeInfo(
                r.RowId, result, (Job)r.CraftType.RowId,
                level?.ClassJobLevel ?? 0, level?.Stars ?? 0,
                Math.Max(1, (int)r.AmountResult), r.IsExpert, ingredients);
            recipesById[r.RowId] = info;
            if (!recipesByItem.TryGetValue(result, out var list)) recipesByItem[result] = list = new List<RecipeInfo>(1);
            list.Add(info);

            if (items.GetRowOrDefault(result) is { } item)
            {
                var name = item.Name.ExtractText();
                if (name.Length > 0)
                    browse.Add(new BrowseEntry(r.RowId, result, name, info.Job, info.Level, info.Stars, item.Icon, info.Yield, info.Expert, r.SecretRecipeBook.RowId != 0));
            }
        }
        browse.Sort((a, b) => a.Level != b.Level ? a.Level.CompareTo(b.Level) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Browse = browse;
    }

    private void IndexGathering()
    {
        var gatheringItems = Services.Data.GetExcelSheet<GatheringItem>();
        var transient = Services.Data.GetExcelSheet<GatheringPointTransient>();
        foreach (var point in Services.Data.GetExcelSheet<GatheringPoint>())
        {
            if (point.TerritoryType.RowId == 0) continue;
            if (point.GatheringPointBase.ValueNullable is not { } pointBase) continue;
            // Types 0–3 are mining, quarrying, logging and harvesting; spearfishing rows point elsewhere.
            if (pointBase.GatheringType.RowId > 3) continue;
            var timed = IsTimed(transient.GetRowOrDefault(point.RowId));
            foreach (var reference in pointBase.Item)
            {
                if (reference.RowId == 0 || gatheringItems.GetRowOrDefault(reference.RowId) is not { } gi) continue;
                var itemId = gi.Item.RowId;
                if (itemId == 0 || itemId >= 1_000_000) continue;
                Add(itemId, timed ? Obtain.GatherTimed : Obtain.Gather);
            }
        }
    }

    /// <summary>Unspoiled/legendary nodes have a rare-pop table; ephemeral ones a start and end time (HHMM).</summary>
    private static bool IsTimed(GatheringPointTransient? row)
    {
        if (row is not { } t) return false;
        if (t.GatheringRarePopTimeTable.RowId != 0) return true;
        var start = t.EphemeralStartTime;
        var end = t.EphemeralEndTime;
        return start != end && start <= 2400 && end <= 2400;
    }

    private void IndexFishing()
    {
        foreach (var f in Services.Data.GetExcelSheet<FishParameter>())
            if (f.Item.RowId is > 0 and < 1_000_000 && f.FishingSpot.RowId != 0) Add(f.Item.RowId, Obtain.Fish);
        foreach (var s in Services.Data.GetExcelSheet<SpearfishingItem>())
            if (s.Item.RowId is > 0 and < 1_000_000) Add(s.Item.RowId, Obtain.Fish);
    }

    private void IndexVendors()
    {
        var items = Services.Data.GetExcelSheet<Item>();
        foreach (var shop in Services.Data.GetSubrowExcelSheet<GilShopItem>())
        foreach (var entry in shop)
        {
            var id = entry.Item.RowId;
            if (id == 0) continue;
            var price = items.GetRowOrDefault(id)?.PriceMid ?? 0;
            var existing = sources.GetValueOrDefault(id);
            sources[id] = new ItemSources(existing.Flags | Obtain.Vendor, price);
        }
    }

    /// <summary>Hunting-venture items are monster drops. M6 replaces this with real spawn data.</summary>
    private void IndexDrops()
    {
        foreach (var task in Services.Data.GetExcelSheet<RetainerTaskNormal>())
            if (task.Item.RowId != 0 && task.GatheringLog.RowId == 0 && task.FishingLog.RowId == 0)
                Add(task.Item.RowId, Obtain.Drop);
    }

    private void IndexNames()
    {
        foreach (var item in Services.Data.GetExcelSheet<Item>())
        {
            var name = item.Name.ExtractText();
            if (name.Length == 0) continue;
            // Several rows can share a name: keep the first, unless a later one is craftable or obtainable and it isn't.
            if (!itemsByName.TryGetValue(name, out var existing) || (!IsUseful(existing) && IsUseful(item.RowId)))
                itemsByName[name] = item.RowId;
        }

        bool IsUseful(uint id) => recipesByItem.ContainsKey(id) || sources.ContainsKey(id);
    }

    private void Add(uint itemId, Obtain flag)
    {
        var existing = sources.GetValueOrDefault(itemId);
        sources[itemId] = new ItemSources(existing.Flags | flag, existing.VendorPrice);
    }
}
