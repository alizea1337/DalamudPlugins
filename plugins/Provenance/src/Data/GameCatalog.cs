using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Lumina.Excel.Sheets;
using Provenance.Core.Catalog;

namespace Provenance.Data;

internal sealed record MasterBook(uint Id, string Name, Job? Job);

internal sealed record FolkloreTome(uint ItemId, string Name, uint ClassJobId);

/// <summary>One row of the recipe browser.</summary>
internal sealed record BrowseEntry(uint RecipeId, uint ItemId, string Name, Job Job, int Level, int Stars, uint Icon, int Yield, bool Expert, uint MasterBook)
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
    public IReadOnlyList<MasterBook> MasterBooks { get; private set; } = Array.Empty<MasterBook>();
    public IReadOnlyList<FolkloreTome> FolkloreTomes { get; private set; } = Array.Empty<FolkloreTome>();

    /// <summary>Folklore tome needed per item for timed nodes / fishing holes; 0 = at least one needs none.</summary>
    private readonly Dictionary<uint, uint> tomeByItem = new();
    private readonly HashSet<uint> tomeFree = new();

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
        c.IndexBooks();
        c.ApplyTomes();
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
                Math.Max(1, (int)r.AmountResult), r.IsExpert, ingredients, r.SecretRecipeBook.RowId);
            recipesById[r.RowId] = info;
            if (!recipesByItem.TryGetValue(result, out var list)) recipesByItem[result] = list = new List<RecipeInfo>(1);
            list.Add(info);

            if (items.GetRowOrDefault(result) is { } item)
            {
                var name = item.Name.ExtractText();
                if (name.Length > 0)
                    browse.Add(new BrowseEntry(r.RowId, result, name, info.Job, info.Level, info.Stars, item.Icon, info.Yield, info.Expert, r.SecretRecipeBook.RowId));
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
            var tome = timed ? TomeOf(point.GatheringSubCategory.ValueNullable) : 0;
            foreach (var reference in pointBase.Item)
            {
                if (reference.RowId == 0 || gatheringItems.GetRowOrDefault(reference.RowId) is not { } gi) continue;
                var itemId = gi.Item.RowId;
                if (itemId == 0 || itemId >= 1_000_000) continue;
                Add(itemId, timed ? Obtain.GatherTimed : Obtain.Gather);
                if (timed) NoteTome(itemId, tome);
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
            if (f.Item.RowId is > 0 and < 1_000_000 && f.FishingSpot.RowId != 0)
            {
                Add(f.Item.RowId, Obtain.Fish);
                NoteTome(f.Item.RowId, TomeOf(f.GatheringSubCategory.ValueNullable));
            }
        foreach (var s in Services.Data.GetExcelSheet<SpearfishingItem>())
            if (s.Item.RowId is > 0 and < 1_000_000)
            {
                Add(s.Item.RowId, Obtain.Fish);
                NoteTome(s.Item.RowId, 0);
            }
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

    /// <summary>
    /// A sub category locks its nodes behind the tome item it names. Mining and botany tomes of one
    /// region share a Division, so the item is the only reliable key. Rows without an item
    /// (Diadem, restoration) are not book-locked.
    /// </summary>
    private static uint TomeOf(GatheringSubCategory? sub)
        => sub is { } sc && !sc.FolkloreBook.IsEmpty ? sc.Item.RowId : 0u;

    private void NoteTome(uint itemId, uint tome)
    {
        if (tome == 0) tomeFree.Add(itemId);
        else tomeByItem.TryAdd(itemId, tome);
    }

    private void ApplyTomes()
    {
        foreach (var (item, tome) in tomeByItem)
        {
            if (tomeFree.Contains(item) || !sources.TryGetValue(item, out var src)) continue;
            sources[item] = src with { Tome = tome };
        }
    }

    private void IndexBooks()
    {
        var jobsByBook = new Dictionary<uint, HashSet<Job>>();
        foreach (var r in recipesById.Values)
        {
            if (r.MasterBook == 0) continue;
            if (!jobsByBook.TryGetValue(r.MasterBook, out var set)) jobsByBook[r.MasterBook] = set = new HashSet<Job>();
            set.Add(r.Job);
        }
        var books = new List<MasterBook>();
        foreach (var b in Services.Data.GetExcelSheet<SecretRecipeBook>())
        {
            if (!jobsByBook.TryGetValue(b.RowId, out var jobs)) continue;
            var name = b.Name.ExtractText();
            if (name.Length == 0) name = b.Item.ValueNullable?.Name.ExtractText() ?? "Master book " + b.RowId;
            books.Add(new MasterBook(b.RowId, name, jobs.Count == 1 ? jobs.First() : null));
        }
        MasterBooks = books;

        var tomes = new Dictionary<uint, FolkloreTome>();
        foreach (var sc in Services.Data.GetExcelSheet<GatheringSubCategory>())
        {
            var item = sc.Item.RowId;
            if (sc.FolkloreBook.IsEmpty || item == 0 || tomes.ContainsKey(item)) continue;
            var name = sc.Item.ValueNullable?.Name.ExtractText() is { Length: > 0 } n ? n : sc.FolkloreBook.ExtractText();
            tomes[item] = new FolkloreTome(item, name, sc.ClassJob.RowId);
        }
        FolkloreTomes = tomes.Values.OrderBy(t => t.ClassJobId).ThenBy(t => t.ItemId).ToList();

        // Manipulation is learned through a class quest; the action rows (one per crafter) name it.
        foreach (var action in Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>())
            if (action.RowId is >= 4574 and <= 4581 && action.UnlockLink.RowId != 0)
                ManipulationQuest[action.ClassJob.RowId] = action.UnlockLink.RowId;
    }

    /// <summary>Class quest that teaches Manipulation, per crafter ClassJob.</summary>
    public Dictionary<uint, uint> ManipulationQuest { get; } = new();

    public string MasterBookName(uint id) => MasterBooks.FirstOrDefault(b => b.Id == id)?.Name ?? "a master book";

    private void Add(uint itemId, Obtain flag)
    {
        var existing = sources.GetValueOrDefault(itemId);
        sources[itemId] = new ItemSources(existing.Flags | flag, existing.VendorPrice);
    }
}
