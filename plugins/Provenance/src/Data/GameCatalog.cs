using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Lumina.Excel.Sheets;
using Provenance.Core.Catalog;
using Provenance.Core.Character;

namespace Provenance.Data;

internal sealed record MasterBook(uint Id, string Name, Job? Job, uint ItemId);

internal sealed record FolkloreTome(uint ItemId, string Name, uint ClassJobId);

/// <summary>Where a timed node shows up: its zone, the centre of its spawn area (world X/Z) and that area's radius.</summary>
internal sealed record TimedSpot(uint TerritoryId, System.Numerics.Vector2 Position, float Radius, uint Mask);

/// <summary>One row of the recipe browser.</summary>
internal enum BrowseKind { Recipe, Gather, Fish }

/// <summary>One row of the item browser: a recipe, or something a gatherer or fisher collects.</summary>
/// <param name="ClassJobId">Crafter (8–15) or gatherer (16–18).</param>
/// <param name="Level">Recipe level, or the lowest node / fishing hole level.</param>
/// <param name="TimedOnly">Gatherables found only on timed nodes.</param>
internal sealed record BrowseEntry(BrowseKind Kind, uint RecipeId, uint ItemId, string Name, uint ClassJobId, int Level, int Stars, uint Icon,
    int Yield, bool Expert, uint MasterBook, bool TimedOnly, bool CanHq = false)
{
    public readonly string NameLower = Name.ToLowerInvariant();
    public string Key => Kind == BrowseKind.Recipe ? "r" + RecipeId : $"g{ClassJobId}_{ItemId}";
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
    private List<BrowseEntry> recipeBrowse = new();

    /// <summary>Lowest level per (item, gatherer job), and whether any of its nodes is always up.</summary>
    private readonly Dictionary<(uint Item, uint Job), (int Level, bool Normal, BrowseKind Kind)> gatherBrowse = new();
    public IReadOnlyList<MasterBook> MasterBooks { get; private set; } = Array.Empty<MasterBook>();
    public IReadOnlyList<FolkloreTome> FolkloreTomes { get; private set; } = Array.Empty<FolkloreTome>();
    /// <summary>Foods and medicines that raise crafting or gathering stats, highest item level first.</summary>
    public IReadOnlyList<ConsumableInfo> Consumables { get; private set; } = Array.Empty<ConsumableInfo>();

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
        c.IndexBells();
        c.IndexDrops();
        c.IndexNames();
        c.IndexBooks();
        c.IndexConsumables();
        c.BuildBrowse();
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
                Math.Max(1, (int)r.AmountResult), r.IsExpert, ingredients, r.SecretRecipeBook.RowId, r.CanHq);
            recipesById[r.RowId] = info;
            if (!recipesByItem.TryGetValue(result, out var list)) recipesByItem[result] = list = new List<RecipeInfo>(1);
            list.Add(info);

            if (items.GetRowOrDefault(result) is { } item)
            {
                var name = item.Name.ExtractText();
                if (name.Length > 0)
                    browse.Add(new BrowseEntry(BrowseKind.Recipe, r.RowId, result, name, info.Job.ClassJobId(), info.Level, info.Stars, item.Icon, info.Yield, info.Expert, r.SecretRecipeBook.RowId, false, r.CanHq));
            }
        }
        browse.Sort((a, b) => a.Level != b.Level ? a.Level.CompareTo(b.Level) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        recipeBrowse = browse;
    }

    private void IndexGathering()
    {
        var gatheringItems = Services.Data.GetExcelSheet<GatheringItem>();
        var transient = Services.Data.GetExcelSheet<GatheringPointTransient>();
        var exported = Services.Data.GetExcelSheet<ExportedGatheringPoint>();
        foreach (var point in Services.Data.GetExcelSheet<GatheringPoint>())
        {
            if (point.TerritoryType.RowId == 0) continue;
            if (point.GatheringPointBase.ValueNullable is not { } pointBase) continue;
            // Types 0–3 are mining, quarrying, logging and harvesting; spearfishing rows point elsewhere.
            if (pointBase.GatheringType.RowId > 3) continue;
            var timedRow = transient.GetRowOrDefault(point.RowId);
            var timed = IsTimed(timedRow);
            var mask = timed ? UptimeMask(timedRow!.Value) : 0u;
            var tome = timed ? TomeOf(point.GatheringSubCategory.ValueNullable) : 0;
            var job = pointBase.GatheringType.RowId <= 1 ? 16u : 17u;
            foreach (var reference in pointBase.Item)
            {
                if (reference.RowId == 0 || gatheringItems.GetRowOrDefault(reference.RowId) is not { } gi) continue;
                var itemId = gi.Item.RowId;
                if (itemId == 0 || itemId >= 1_000_000) continue;
                Add(itemId, timed ? Obtain.GatherTimed : Obtain.Gather);
                if (timed) timedMasks[itemId] = timedMasks.GetValueOrDefault(itemId) | mask;
                NoteBrowse(itemId, job, pointBase.GatheringLevel, !timed, BrowseKind.Gather);
                if (timed) NoteTome(itemId, tome);
                if (timed && exported.GetRowOrDefault(pointBase.RowId) is { } at)
                {
                    if (!timedSpots.TryGetValue(itemId, out var spots)) timedSpots[itemId] = spots = new List<TimedSpot>(1);
                    if (!spots.Any(x => x.TerritoryId == point.TerritoryType.RowId && x.Position == new System.Numerics.Vector2(at.X, at.Y)))
                        spots.Add(new TimedSpot(point.TerritoryType.RowId, new System.Numerics.Vector2(at.X, at.Y), at.Radius, mask));
                }
            }
        }
    }

    /// <summary>Unspoiled/legendary nodes have a rare-pop table; ephemeral ones a start and end time (HHMM).</summary>
    /// <summary>Eorzea hours a timed node is up, from its rare-pop table or ephemeral times.</summary>
    private static uint UptimeMask(GatheringPointTransient t)
    {
        if (t.GatheringRarePopTimeTable.ValueNullable is { } table)
        {
            uint mask = 0;
            for (var i = 0; i < table.StartTime.Count && i < table.Duration.Count; i++)
                if (table.StartTime[i] != 65535 && table.Duration[i] != 0)
                    mask |= Core.Timing.Eorzea.Window(table.StartTime[i], table.Duration[i]);
            return mask;
        }
        return Core.Timing.Eorzea.Between(t.EphemeralStartTime, t.EphemeralEndTime);
    }

    private readonly Dictionary<uint, uint> timedMasks = new();
    private readonly Dictionary<uint, List<TimedSpot>> timedSpots = new();

    /// <summary>The timed nodes an item comes from, with where they spawn.</summary>
    public IReadOnlyList<TimedSpot> TimedSpots(uint itemId) => timedSpots.TryGetValue(itemId, out var s) ? s : Array.Empty<TimedSpot>();

    /// <summary>Eorzea hours an item's timed nodes are up (24-bit mask); 0 when it has none.</summary>
    public uint TimedMask(uint itemId) => timedMasks.GetValueOrDefault(itemId);

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
        foreach (var spot in Services.Data.GetExcelSheet<FishingSpot>())
        {
            if (spot.GatheringLevel == 0 || spot.TerritoryType.RowId == 0) continue;
            foreach (var fish in spot.Item)
                if (fish.RowId is > 0 and < 1_000_000) NoteBrowse(fish.RowId, 18, spot.GatheringLevel, true, BrowseKind.Fish);
        }
        var spearItems = Services.Data.GetExcelSheet<SpearfishingItem>();
        foreach (var note in Services.Data.GetExcelSheet<SpearfishingNotebook>())
        {
            if (note.GatheringPointBase.ValueNullable is not { } spearBase) continue;
            foreach (var reference in spearBase.Item)
                if (spearItems.GetRowOrDefault(reference.RowId) is { } si && si.Item.RowId is > 0 and < 1_000_000)
                    NoteBrowse(si.Item.RowId, 18, note.GatheringLevel, true, BrowseKind.Fish);
        }

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

    /// <summary>Event objects that are summoning bells (every row named like the classic one).</summary>
    public HashSet<uint> SummoningBells { get; } = new();

    private void IndexBells()
    {
        var names = Services.Data.GetExcelSheet<EObjName>();
        var bell = names.GetRowOrDefault(2000401)?.Singular.ExtractText();
        if (string.IsNullOrEmpty(bell)) return;
        foreach (var row in names)
            if (row.Singular.ExtractText().Equals(bell, StringComparison.OrdinalIgnoreCase)) SummoningBells.Add(row.RowId);
    }

    /// <summary>Gil vendors the run can actually reach (known position, city or open zone).</summary>
    public VendorIndex Vendors { get; private set; } = new();

    private void IndexVendors()
    {
        Vendors = VendorIndex.Build();
        foreach (var id in Vendors.Items)
        {
            var price = Vendors.For(id).Min(o => o.Price);
            var existing = sources.GetValueOrDefault(id);
            sources[id] = existing with { Flags = existing.Flags | Obtain.Vendor, VendorPrice = price };
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
            books.Add(new MasterBook(b.RowId, name, jobs.Count == 1 ? jobs.First() : null, b.Item.RowId));
        }
        MasterBooks = books;
        bookItemById = books.Where(b => b.ItemId != 0).ToDictionary(b => b.Id, b => b.ItemId);

        var tomes = new Dictionary<uint, FolkloreTome>();
        foreach (var sc in Services.Data.GetExcelSheet<GatheringSubCategory>())
        {
            var item = sc.Item.RowId;
            if (sc.FolkloreBook.IsEmpty || item == 0 || tomes.ContainsKey(item)) continue;
            var name = sc.Item.ValueNullable?.Name.ExtractText() is { Length: > 0 } n ? n : sc.FolkloreBook.ExtractText();
            tomes[item] = new FolkloreTome(item, name, sc.ClassJob.RowId);
        }
        FolkloreTomes = tomes.Values.OrderBy(t => t.ClassJobId).ThenBy(t => t.ItemId).ToList();

        // Where books and tomes are sold (scrip and tomestone exchanges).
        var wanted = new HashSet<uint>(books.Select(b => b.ItemId).Concat(tomes.Keys).Where(i => i != 0));
        foreach (var shop in Services.Data.GetExcelSheet<SpecialShop>())
        {
            var shopName = shop.Name.ExtractText();
            if (shopName.Length == 0) continue;
            foreach (var entry in shop.Item)
            foreach (var receive in entry.ReceiveItems)
                if (wanted.Contains(receive.Item.RowId)) soldAt.TryAdd(receive.Item.RowId, shopName);
        }

        // Manipulation is learned through a class quest; the action rows (one per crafter) name it.
        foreach (var action in Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>())
            if (action.RowId is >= 4574 and <= 4581 && action.UnlockLink.RowId != 0)
                ManipulationQuest[action.ClassJob.RowId] = action.UnlockLink.RowId;
    }

    /// <summary>Class quest that teaches Manipulation, per crafter ClassJob.</summary>
    public Dictionary<uint, uint> ManipulationQuest { get; } = new();

    private const uint WellFedStatus = 48;
    private const uint MedicatedStatus = 49;

    /// <summary>Items whose action applies Well Fed or Medicated with crafting or gathering stats.</summary>
    private void IndexConsumables()
    {
        var foods = Services.Data.GetExcelSheet<ItemFood>();
        var result = new List<ConsumableInfo>();
        foreach (var item in Services.Data.GetExcelSheet<Item>())
        {
            if (item.ItemAction.RowId == 0 || item.ItemAction.ValueNullable is not { } action || action.Data.Count < 2) continue;
            var status = action.Data[0];
            if (status != WellFedStatus && status != MedicatedStatus) continue;
            if (foods.GetRowOrDefault(action.Data[1]) is not { } food) continue;

            Discipline? discipline = null;
            var parameters = new List<ConsumableParam>();
            foreach (var p in food.Params)
            {
                var id = p.BaseParam.RowId;
                var (d, index) = id switch
                {
                    70 => (Discipline.Hand, 0), 71 => (Discipline.Hand, 1), 11 => (Discipline.Hand, 2),
                    72 => (Discipline.Land, 0), 73 => (Discipline.Land, 1), 10 => (Discipline.Land, 2),
                    _ => ((Discipline?)null, -1),
                };
                if (d == null) continue;
                discipline ??= d;
                if (d != discipline) continue;
                parameters.Add(new ConsumableParam(index, p.IsRelative, p.Value, p.Max, p.ValueHQ, p.MaxHQ));
            }
            if (discipline == null || parameters.Count == 0) continue;
            var name = item.Name.ExtractText();
            if (name.Length == 0) continue;
            result.Add(new ConsumableInfo(item.RowId, name, item.Icon, status == WellFedStatus ? ConsumableKind.Food : ConsumableKind.Medicine,
                discipline.Value, (int)item.LevelItem.RowId, item.CanBeHq, parameters));
        }
        Consumables = result.OrderByDescending(c => c.ItemLevel).ThenBy(c => c.Name).ToList();
    }

    public ConsumableInfo? Consumable(uint itemId) => itemId == 0 ? null : Consumables.FirstOrDefault(c => c.ItemId == itemId);

    private Dictionary<uint, uint> bookItemById = new();
    private readonly Dictionary<uint, string> soldAt = new();

    /// <summary>Item id of a master book (SecretRecipeBook row), or 0.</summary>
    public uint MasterBookItem(uint bookId) => bookItemById.GetValueOrDefault(bookId);

    /// <summary>The exchange that sells a book or tome, when the game data names one.</summary>
    public string? SoldAt(uint itemId) => soldAt.GetValueOrDefault(itemId);

    public string MasterBookName(uint id) => MasterBooks.FirstOrDefault(b => b.Id == id)?.Name ?? "a master book";

    private void NoteBrowse(uint itemId, uint job, int level, bool normal, BrowseKind kind)
    {
        if (gatherBrowse.TryGetValue((itemId, job), out var e))
            gatherBrowse[(itemId, job)] = (Math.Min(e.Level, level), e.Normal || normal, kind);
        else
            gatherBrowse[(itemId, job)] = (level, normal, kind);
    }

    /// <summary>Recipes plus everything gatherers and fishers collect, sorted by level then name.</summary>
    private void BuildBrowse()
    {
        var items = Services.Data.GetExcelSheet<Item>();
        var all = new List<BrowseEntry>(recipeBrowse);
        foreach (var ((itemId, job), (level, normal, kind)) in gatherBrowse)
        {
            if (items.GetRowOrDefault(itemId) is not { } item) continue;
            var name = item.Name.ExtractText();
            if (name.Length == 0) continue;
            all.Add(new BrowseEntry(kind, 0, itemId, name, job, level, 0, item.Icon, 1, false, 0, !normal));
        }
        all.Sort((a, b) => a.Level != b.Level ? a.Level.CompareTo(b.Level) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Browse = all;
    }

    private void Add(uint itemId, Obtain flag)
    {
        var existing = sources.GetValueOrDefault(itemId);
        sources[itemId] = new ItemSources(existing.Flags | flag, existing.VendorPrice);
    }
}
