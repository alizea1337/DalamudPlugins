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
/// <summary>Stats the game asks for before it lets a craft start (0 = no minimum).</summary>
internal readonly record struct RecipeNeeds(int Craftsmanship, int Control, bool Specialist);

/// <summary>What gathering or fishing an item takes: the job, the lowest node level, and the Perception the item asks for.</summary>
internal readonly record struct GatherNeeds(uint ClassJobId, int Level, int Perception);

/// <summary>
/// One gathering point an item can come from: the GatheringPoint row (exact positions are in
/// <see cref="GatherData"/>), its base (the node), zone, the gatherer job, node level, whether it is
/// timed (and when), the folklore tome it needs, and the item's gathering item level.
/// </summary>
internal sealed record GatherPoint(uint PointId, uint BaseId, uint TerritoryId, uint ClassJobId, int Level, bool Timed, uint Mask,
    uint Tome, uint GatheringItemLevel, bool Hidden);

internal sealed record TimedSpot(uint TerritoryId, System.Numerics.Vector2 Position, float Radius, uint Mask);

/// <summary>One row of the recipe browser.</summary>
internal enum BrowseKind { Recipe, Gather, Fish }

/// <summary>One row of the item browser: a recipe, or something a gatherer or fisher collects.</summary>
/// <param name="ClassJobId">Crafter (8–15) or gatherer (16–18).</param>
/// <param name="Level">Recipe level, or the lowest node / fishing hole level.</param>
/// <param name="TimedOnly">Gatherables found only on timed nodes.</param>
/// <param name="Places">
/// <see cref="Place.OpenWorld"/> when a run can get it there, else the special content it comes from
/// (the Diadem, Cosmic Exploration and so on).
/// </param>
/// <param name="Reduced">Comes from aetherial reduction of a collectable this job gathers or fishes.</param>
/// <param name="ReducedByRun">With <paramref name="Reduced"/>: a run gathers and reduces it (miners' and botanists' collectables).</param>
internal sealed record BrowseEntry(BrowseKind Kind, uint RecipeId, uint ItemId, string Name, uint ClassJobId, int Level, int Stars, uint Icon,
    int Yield, bool Expert, uint MasterBook, bool TimedOnly, bool CanHq = false, Place Places = Place.OpenWorld,
    bool Reduced = false, bool ReducedByRun = false)
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

    /// <summary>
    /// Lowest level per (item, gatherer job), whether any of its nodes is always up, and where: the open
    /// world, or the special content it only comes from.
    /// </summary>
    private readonly Dictionary<(uint Item, uint Job), (int Level, bool Normal, BrowseKind Kind, Place Places)> gatherBrowse = new();
    /// <summary>Browser rows (item, job) that come from aetherial reduction, and whether a run can do it.</summary>
    private readonly Dictionary<(uint Item, uint Job), bool> reducedBrowse = new();
    /// <summary>Item → the gatherable collectables (open world) whose aetherial reduction gives it.</summary>
    private readonly Dictionary<uint, List<uint>> reducedFrom = new();
    /// <summary>Item → every collectable that reduces into it (fish and special content too), for the plan's text.</summary>
    private readonly Dictionary<uint, List<uint>> reductionSources = new();
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
        c.Gather = GatherData.Load();
        c.IndexContent();
        c.IndexRecipes();
        c.IndexGathering();
        c.IndexFishing();
        c.ApplyCosmic();
        c.IndexReduction();
        c.IndexVendors();
        c.IndexBells();
        c.IndexDrops();
        c.IndexCollectables();
        c.IndexNames();
        c.IndexBooks();
        c.IndexConsumables();
        c.BuildBrowse();
        c.ApplyTomes();
        Services.Log.Information("Catalog ready: {Recipes} recipes, {Sources} sourced items, {Points} node points, {Fish} fish in {Ms} ms",
            c.recipesById.Count, c.sources.Count, c.Gather.PointCount, c.Gather.FishCount, sw.ElapsedMilliseconds);
        return c;
    }

    // ------------------------------------------------------------------ IGameCatalog

    public string ItemName(uint itemId) => Lookup(itemId).Name;

    /// <summary>"Purple Crafters' Scrips": the plural with the singular's capitals (the game's plurals are lower case).</summary>
    public string ItemNamePlural(uint itemId)
        => Services.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId) is { } row ? Plural(row) : ItemName(itemId);

    public static string Plural(Item row)
    {
        var singular = row.Name.ExtractText();
        var plural = row.Plural.ExtractText();
        if (plural.Length == 0) return singular;
        var s = singular.Split(' ');
        var p = plural.Split(' ');
        if (s.Length != p.Length) return plural;
        for (var i = 0; i < p.Length; i++)
            if (s[i].Length > 0 && p[i].Length > 0 && char.IsUpper(s[i][0])) p[i] = char.ToUpperInvariant(p[i][0]) + p[i][1..];
        return string.Join(' ', p);
    }
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
            if (r.RequiredCraftsmanship > 0 || r.RequiredControl > 0 || r.IsSpecializationRequired)
                recipeNeeds[r.RowId] = new RecipeNeeds(r.RequiredCraftsmanship, r.RequiredControl, r.IsSpecializationRequired);

            var level = r.RecipeLevelTable.ValueNullable;
            var info = new RecipeInfo(
                r.RowId, result, (Job)r.CraftType.RowId,
                level?.ClassJobLevel ?? 0, level?.Stars ?? 0,
                Math.Max(1, (int)r.AmountResult), r.IsExpert, ingredients, r.SecretRecipeBook.RowId, r.CanHq);
            // Cosmic Exploration's recipes only open during its missions: browsable, never planned.
            var place = cosmic.Contains(result) ? Place.CosmicExploration : Place.OpenWorld;
            if (place == Place.OpenWorld)
            {
                recipesById[r.RowId] = info;
                if (!recipesByItem.TryGetValue(result, out var list)) recipesByItem[result] = list = new List<RecipeInfo>(1);
                list.Add(info);
            }
            else Add(result, Obtain.CraftElsewhere, place);

            // Quest recipes only open during their quest: planned when ordered, but not listed.
            if (r.Quest.RowId == 0 && items.GetRowOrDefault(result) is { } item)
            {
                var name = item.Name.ExtractText();
                if (name.Length > 0)
                    browse.Add(new BrowseEntry(BrowseKind.Recipe, r.RowId, result, name, info.Job.ClassJobId(), info.Level, info.Stars, item.Icon, info.Yield, info.Expert, r.SecretRecipeBook.RowId, false, r.CanHq, place));
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
        var territories = Services.Data.GetExcelSheet<TerritoryType>();
        foreach (var gi in gatheringItems)
            if (gi.Item.RowId != 0) gatheredItems.Add(gi.Item.RowId);

        // A point's items are its base's list plus the GatheringItemPoint links, which tie more
        // items to single points (Mythrite Ore to a node in Coerthas Western Highlands, for one).
        var linked = new Dictionary<uint, List<uint>>();
        foreach (var row in Services.Data.GetSubrowExcelSheet<GatheringItemPoint>())
            foreach (var link in row)
            {
                if (link.GatheringPoint.RowId == 0) continue;
                if (!linked.TryGetValue(link.GatheringPoint.RowId, out var l)) linked[link.GatheringPoint.RowId] = l = new List<uint>(2);
                if (!l.Contains(row.RowId)) l.Add(row.RowId);
            }

        var ids = new List<uint>(16);
        foreach (var point in Services.Data.GetExcelSheet<GatheringPoint>())
        {
            if (point.TerritoryType.RowId == 0) continue;
            if (point.GatheringPointBase.ValueNullable is not { } pointBase) continue;
            // Types 0–3 are mining, quarrying, logging and harvesting; spearfishing rows point elsewhere.
            if (pointBase.GatheringType.RowId > 3) continue;
            // Runs only travel to the open world; the Diadem and instanced content are listed for the browser.
            var zone = PlaceOf(territories.GetRowOrDefault(point.TerritoryType.RowId));
            var timedRow = transient.GetRowOrDefault(point.RowId);
            var timed = IsTimed(timedRow);
            var ephemeral = timed && timedRow!.Value.GatheringRarePopTimeTable.RowId == 0;
            var mask = timed ? UptimeMask(timedRow!.Value) : 0u;
            var tome = timed ? TomeOf(point.GatheringSubCategory.ValueNullable) : 0;
            var job = pointBase.GatheringType.RowId <= 1 ? 16u : 17u;

            ids.Clear();
            foreach (var reference in pointBase.Item)
                if (reference.RowId != 0 && !ids.Contains(reference.RowId)) ids.Add(reference.RowId);
            var fromBase = ids.Count;
            if (linked.TryGetValue(point.RowId, out var more))
                foreach (var id in more)
                    if (!ids.Contains(id)) ids.Add(id);

            for (var i = 0; i < ids.Count; i++)
            {
                if (gatheringItems.GetRowOrDefault(ids[i]) is not { } gi) continue;
                var itemId = gi.Item.RowId;
                if (itemId == 0 || itemId >= 1_000_000) continue;
                // Crystals these links add to ephemeral nodes are left out, as GatherBuddy Reborn does.
                if (i >= fromBase && ephemeral && itemId is >= 2 and <= 19) continue;
                var place = zone == Place.Other && cosmic.Contains(itemId) ? Place.CosmicExploration : zone;
                NoteBrowse(itemId, job, pointBase.GatheringLevel, !timed, BrowseKind.Gather, place);
                if (place != Place.OpenWorld)
                {
                    // Listed in Browse; the plan says where to get it by hand.
                    Add(itemId, Obtain.GatherElsewhere, place);
                    continue;
                }
                Add(itemId, timed ? Obtain.GatherTimed : Obtain.Gather, place);
                if (!gatherPoints.TryGetValue(itemId, out var pointList)) gatherPoints[itemId] = pointList = new List<GatherPoint>(2);
                pointList.Add(new GatherPoint(point.RowId, pointBase.RowId, point.TerritoryType.RowId, job, pointBase.GatheringLevel, timed, mask, tome,
                    gi.GatheringItemLevel.RowId, gi.IsHidden));
                if (gi.PerceptionReq > 0)
                    perception[itemId] = perception.TryGetValue(itemId, out var p) ? Math.Min(p, gi.PerceptionReq) : gi.PerceptionReq;
                if (timed) timedMasks[itemId] = timedMasks.GetValueOrDefault(itemId) | mask;
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

    /// <summary>Items of Cosmic Exploration: its mission materials, gathering, fishing and crafts.</summary>
    private readonly HashSet<uint> cosmic = new();
    /// <summary>Every item a gatherer can collect somewhere (GatheringItem rows).</summary>
    private readonly HashSet<uint> gatheredItems = new();

    /// <summary>The Diadem the game sends players to today: the newest of its zones. The older ones are gone.</summary>
    private uint currentDiadem;

    private void IndexContent()
    {
        foreach (var row in Services.Data.GetExcelSheet<WKSItemInfo>())
            if (row.Item.RowId != 0) cosmic.Add(row.Item.RowId);
        foreach (var zone in Services.Data.GetExcelSheet<TerritoryType>())
            if (zone.TerritoryIntendedUse.RowId == 47) currentDiadem = Math.Max(currentDiadem, zone.RowId);
    }

    /// <summary>
    /// Most of Cosmic Exploration's gathering has no gathering points in the sheets; its items still
    /// say where they come from.
    /// </summary>
    private void ApplyCosmic()
    {
        foreach (var itemId in cosmic)
        {
            var src = sources.GetValueOrDefault(itemId);
            var way = gatheredItems.Contains(itemId) && !src.Has(Obtain.Gather | Obtain.GatherTimed | Obtain.Elsewhere) ? Obtain.GatherElsewhere : Obtain.None;
            sources[itemId] = src with { Flags = src.Flags | way, Places = src.Places | Place.CosmicExploration };
        }
    }

    /// <summary>
    /// Where a zone is. Runs go to field zones (intended use 1), cities (0) and housing districts (13).
    /// Zone 1 is the placeholder that quest nodes sit in; it and older versions of the Diadem are
    /// <see cref="Place.Other"/>.
    /// </summary>
    private Place PlaceOf(TerritoryType? zone)
    {
        if (zone is not { } t || t.RowId <= 1) return Place.Other;
        return t.TerritoryIntendedUse.RowId switch
        {
            0 or 1 or 13 => Place.OpenWorld,
            46 => Place.OceanFishing,
            47 => t.RowId == currentDiadem ? Place.Diadem : Place.Other,
            60 => Place.CosmicExploration,
            _ => Place.Other,
        };
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
    private readonly Dictionary<uint, List<GatherPoint>> gatherPoints = new();

    /// <summary>Every gathering point an item comes from (regular and timed; spearfishing excluded).</summary>
    public IReadOnlyList<GatherPoint> GatherPoints(uint itemId) => gatherPoints.TryGetValue(itemId, out var l) ? l : Array.Empty<GatherPoint>();
    private readonly Dictionary<uint, RecipeNeeds> recipeNeeds = new();
    private readonly Dictionary<uint, int> perception = new();

    /// <summary>Minimum Craftsmanship / Control / specialist the game asks for before the craft starts.</summary>
    public RecipeNeeds NeedsFor(RecipeInfo recipe) => recipeNeeds.GetValueOrDefault(recipe.RecipeId);

    /// <summary>
    /// The way to gather or fish an item: a job the character is high enough for when there is one,
    /// else the one with the lowest node level; with the Perception the item asks for.
    /// </summary>
    public GatherNeeds? GatherNeedsFor(uint itemId, Func<uint, int> jobLevel)
    {
        GatherNeeds? best = null;
        foreach (var job in Core.Character.ClassJobs.Gatherers)
        {
            if (!gatherBrowse.TryGetValue((itemId, job), out var e) || e.Places != Place.OpenWorld) continue;
            var needs = new GatherNeeds(job, e.Level, job == 18 ? 0 : perception.GetValueOrDefault(itemId));
            if (best is not { } b) { best = needs; continue; }
            var canNew = jobLevel(job) >= needs.Level;
            var canOld = jobLevel(b.ClassJobId) >= b.Level;
            if ((canNew && !canOld) || (canNew == canOld && needs.Level < b.Level)) best = needs;
        }
        return best;
    }
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
        var territories = Services.Data.GetExcelSheet<TerritoryType>();
        var spotPlaces = new Dictionary<uint, Place>();
        foreach (var spot in Services.Data.GetExcelSheet<FishingSpot>())
        {
            if (spot.GatheringLevel == 0) continue;
            // The Diadem's and Cosmic Exploration's spots have no zone in the sheet; the exported spot
            // data has it. Spots in neither are content that is gone (the first Diadem).
            var zoneId = Gather.Spot(spot.RowId)?.TerritoryId ?? spot.TerritoryType.RowId;
            var zone = zoneId == 0 ? Place.Other : PlaceOf(territories.GetRowOrDefault(zoneId));
            spotPlaces[spot.RowId] = zone;
            foreach (var fish in spot.Item)
            {
                if (fish.RowId is 0 or >= 1_000_000) continue;
                var place = zone == Place.Other && cosmic.Contains(fish.RowId) ? Place.CosmicExploration : zone;
                NoteBrowse(fish.RowId, 18, spot.GatheringLevel, true, BrowseKind.Fish, place);
                Add(fish.RowId, place == Place.OpenWorld ? Obtain.Fish : Obtain.FishElsewhere, place);
            }
        }
        var spearItems = Services.Data.GetExcelSheet<SpearfishingItem>();
        foreach (var note in Services.Data.GetExcelSheet<SpearfishingNotebook>())
        {
            if (note.GatheringPointBase.ValueNullable is not { } spearBase) continue;
            var place = PlaceOf(territories.GetRowOrDefault(note.TerritoryType.RowId));
            foreach (var reference in spearBase.Item)
                if (spearItems.GetRowOrDefault(reference.RowId) is { } si && si.Item.RowId is > 0 and < 1_000_000)
                {
                    NoteBrowse(si.Item.RowId, 18, note.GatheringLevel, true, BrowseKind.Fish, place);
                    Add(si.Item.RowId, place == Place.OpenWorld ? Obtain.Fish : Obtain.FishElsewhere, place);
                    NoteTome(si.Item.RowId, 0);
                }
        }

        foreach (var f in Services.Data.GetExcelSheet<FishParameter>())
            if (f.Item.RowId is > 0 and < 1_000_000 && f.FishingSpot.RowId != 0)
            {
                if (spotPlaces.GetValueOrDefault(f.FishingSpot.RowId) == Place.OpenWorld) Add(f.Item.RowId, Obtain.Fish, Place.OpenWorld);
                NoteTome(f.Item.RowId, TomeOf(f.GatheringSubCategory.ValueNullable));
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
    /// <summary>Collectables the appraiser pays scrips for, one per job tab row.</summary>
    public IReadOnlyList<Core.Scrips.CollectableOption> Collectables { get; private set; } = Array.Empty<Core.Scrips.CollectableOption>();
    private readonly Dictionary<uint, Core.Scrips.CollectableOption> collectableByItem = new();
    public Core.Scrips.CollectableOption? Collectable(uint itemId) => collectableByItem.GetValueOrDefault(itemId);
    /// <summary>Appraiser shop rows (CollectablesShop) that pay scrips, with each row's tab order.</summary>
    public uint AppraiserShop { get; private set; }

    private void IndexCollectables()
    {
        var items = Services.Data.GetExcelSheet<Item>();
        var groups = Services.Data.GetSubrowExcelSheet<CollectablesShopItem>();
        var list = new List<Core.Scrips.CollectableOption>();
        foreach (var shop in Services.Data.GetExcelSheet<CollectablesShop>())
        {
            if (shop.RewardType != 1) continue; // 1 = scrips; 2 = items
            var any = false;
            for (var tab = 0; tab < shop.ShopItems.Count; tab++)
            {
                var groupId = shop.ShopItems[tab].RowId;
                if (groupId == 0 || !groups.HasRow(groupId)) continue;
                foreach (var row in groups.GetRow(groupId))
                {
                    var itemId = row.Item.RowId;
                    if (itemId == 0 || items.GetRowOrDefault(itemId) is not { IsCollectable: true }) continue;
                    if (row.RequiredQuest.RowId != 0) continue; // quest-locked rows are left out
                    if (row.CollectablesShopRewardScrip.ValueNullable is not { Currency: > 0 } reward) continue;
                    if (row.CollectablesShopRefine.ValueNullable is not { } refine) continue;
                    uint job;
                    int level;
                    var crafted = RecipesFor(itemId).Count > 0;
                    if (crafted)
                    {
                        var recipe = RecipesFor(itemId)[0];
                        job = recipe.Job.ClassJobId();
                        level = Math.Max(row.LevelMin, recipe.Level);
                    }
                    else if (gatherBrowse.Where(kv => kv.Key.Item1 == itemId).OrderBy(kv => kv.Value.Level).FirstOrDefault() is { Key.Item1: > 0 } g)
                    {
                        job = g.Key.Item2;
                        level = Math.Max(row.LevelMin, g.Value.Level);
                    }
                    else continue;
                    var option = new Core.Scrips.CollectableOption(itemId, job, level, ExchangeIndex.ScripItem(reward.Currency),
                        reward.LowReward, reward.MidReward, reward.HighReward,
                        refine.LowCollectability, refine.MidCollectability, refine.HighCollectability, tab, crafted);
                    if (collectableByItem.TryAdd(itemId, option)) list.Add(option);
                    any = true;
                }
            }
            if (any && AppraiserShop == 0) AppraiserShop = shop.RowId;
        }
        Collectables = list;
        if (AppraiserShop != 0) IndexAppraisers();
        IndexInns();
    }

    /// <summary>An innkeeper: the NPC, where they stand, and the inn room zone they lead to.</summary>
    internal sealed record Inn(uint NpcId, string NpcName, uint TerritoryId, System.Numerics.Vector2 Position, uint RoomTerritory, string City);

    public IReadOnlyList<Inn> Inns { get; private set; } = Array.Empty<Inn>();

    private void IndexInns()
    {
        var territories = Services.Data.GetExcelSheet<TerritoryType>();
        var warps = Services.Data.GetExcelSheet<Warp>()
            .Where(w => territories.GetRowOrDefault(w.TerritoryType.RowId)?.TerritoryIntendedUse.RowId == 2)
            .ToDictionary(w => w.RowId, w => w.TerritoryType.RowId);
        var pre = Services.Data.GetExcelSheet<PreHandler>();
        var topics = Services.Data.GetExcelSheet<TopicSelect>();
        var npcs = Services.Data.GetExcelSheet<ENpcBase>();
        var names = Services.Data.GetExcelSheet<ENpcResident>();
        uint Room(uint id, int depth)
        {
            if (warps.TryGetValue(id, out var room)) return room;
            if (id == 0 || depth > 2) return 0;
            if (pre.GetRowOrDefault(id) is { } p) return Room(p.Target.RowId, depth + 1);
            if (topics.GetRowOrDefault(id) is { } t) return t.Shop.Select(s => Room(s.RowId, depth + 1)).FirstOrDefault(r => r != 0);
            return 0;
        }
        var found = new List<Inn>();
        foreach (var (npcId, (territory, position)) in VendorIndex.LoadPositions())
        {
            if (npcs.GetRowOrDefault(npcId) is not { } npc) continue;
            var room = npc.ENpcData.Select(d => Room(d.RowId, 0)).FirstOrDefault(r => r != 0);
            if (room == 0 || found.Any(f => f.RoomTerritory == room)) continue;
            var city = territories.GetRowOrDefault(territory)?.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
            found.Add(new Inn(npcId, names.GetRowOrDefault(npcId)?.Singular.ExtractText() ?? "innkeeper", territory, position, room, city));
        }
        Inns = found.OrderBy(i => i.City).ToList();
    }

    /// <summary>Collectable appraisers with a known position (cities and open zones).</summary>
    public IReadOnlyList<(uint NpcId, string Name, uint TerritoryId, System.Numerics.Vector2 Position)> Appraisers { get; private set; }
        = Array.Empty<(uint, string, uint, System.Numerics.Vector2)>();

    private void IndexAppraisers()
    {
        var pre = Services.Data.GetExcelSheet<PreHandler>();
        var topics = Services.Data.GetExcelSheet<TopicSelect>();
        var talk = Services.Data.GetExcelSheet<CustomTalk>();
        var npcs = Services.Data.GetExcelSheet<ENpcBase>();
        var names = Services.Data.GetExcelSheet<ENpcResident>();
        var territories = Services.Data.GetExcelSheet<TerritoryType>();
        bool Leads(uint id, int depth)
        {
            if (id == AppraiserShop) return true;
            if (id == 0 || depth > 3) return false;
            if (pre.GetRowOrDefault(id) is { } p) return Leads(p.Target.RowId, depth + 1);
            if (topics.GetRowOrDefault(id) is { } t) return t.Shop.Any(s => Leads(s.RowId, depth + 1));
            if (talk.GetRowOrDefault(id) is { } c)
                return c.Script.Any(s => s.ScriptArg == AppraiserShop) || c.SpecialLinks.RowId == AppraiserShop;
            return false;
        }
        var found = new List<(uint, string, uint, System.Numerics.Vector2)>();
        foreach (var (npcId, (territory, position)) in VendorIndex.LoadPositions())
        {
            if (territories.GetRowOrDefault(territory) is not { } t || t.TerritoryIntendedUse.RowId is not (0 or 1)) continue;
            if (npcs.GetRowOrDefault(npcId) is not { } npc || !npc.ENpcData.Any(d => Leads(d.RowId, 0))) continue;
            found.Add((npcId, names.GetRowOrDefault(npcId)?.Singular.ExtractText() ?? "collectable appraiser", territory, position));
        }
        Appraisers = found;
    }

    private void IndexDrops()
    {
        foreach (var task in Services.Data.GetExcelSheet<RetainerTaskNormal>())
            if (task.Item.RowId != 0 && task.GatheringLog.RowId == 0 && task.FishingLog.RowId == 0)
                Add(task.Item.RowId, Obtain.Drop);
        Mobs = MobIndex.Load();
        foreach (var item in Mobs.Items) Add(item, Obtain.Drop);
    }

    /// <summary>Node positions, the fish database and fishing spots (shipped data, see <see cref="GatherData"/>).</summary>
    public GatherData Gather { get; private set; } = new();

    /// <summary>Monsters that drop items, and where they spawn.</summary>
    public MobIndex Mobs { get; private set; } = new();

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
        Exchanges = ExchangeIndex.Build(wanted, VendorIndex.LoadPositions());

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

    /// <summary>Scrip and token exchanges that sell master books and folklore tomes.</summary>
    public ExchangeIndex Exchanges { get; private set; } = new();

    public string MasterBookName(uint id) => MasterBooks.FirstOrDefault(b => b.Id == id)?.Name ?? "a master book";

    /// <summary>
    /// Notes a node or fishing hole for the browser row of (item, job). Open-world ones decide the
    /// row's level and timing; special content only counts while the item has no open-world source.
    /// </summary>
    private void NoteBrowse(uint itemId, uint job, int level, bool normal, BrowseKind kind, Place place)
    {
        var key = (itemId, job);
        if (!gatherBrowse.TryGetValue(key, out var e) || (place == Place.OpenWorld && e.Places != Place.OpenWorld))
            gatherBrowse[key] = (level, normal, kind, place);
        else if (place == Place.OpenWorld || e.Places != Place.OpenWorld)
            gatherBrowse[key] = (Math.Min(e.Level, level), e.Normal || normal, kind, e.Places | place);
    }

    /// <summary>Recipes plus everything gatherers and fishers collect, sorted by level then name.</summary>
    private void BuildBrowse()
    {
        var items = Services.Data.GetExcelSheet<Item>();
        var all = new List<BrowseEntry>(recipeBrowse);
        foreach (var ((itemId, job), (level, normal, kind, found)) in gatherBrowse)
        {
            // Quest nodes and content that's gone aren't listed: nothing there can be got any more.
            var places = found & ~Place.Other;
            if (places == Place.None || items.GetRowOrDefault(itemId) is not { } item) continue;
            var name = item.Name.ExtractText();
            if (name.Length == 0) continue;
            var reduced = reducedBrowse.TryGetValue((itemId, job), out var byRun);
            all.Add(new BrowseEntry(kind, 0, itemId, name, job, level, 0, item.Icon, 1, false, 0, !normal, Places: places, Reduced: reduced, ReducedByRun: reduced && byRun));
        }
        all.Sort((a, b) => a.Level != b.Level ? a.Level.CompareTo(b.Level) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Browse = all;
    }

    public IReadOnlyList<uint> ReducedFrom(uint itemId) => reducedFrom.TryGetValue(itemId, out var l) ? l : Array.Empty<uint>();

    /// <summary>Every collectable whose aetherial reduction gives the item (fish and special content included).</summary>
    public IReadOnlyList<uint> ReductionSources(uint itemId) => reductionSources.TryGetValue(itemId, out var l) ? l : Array.Empty<uint>();

    /// <summary>
    /// Aetherial reduction (Resources/reduction.json, from Teamcraft): items that only come from reducing
    /// collectables. A source counts only when it really is a reducible collectable (the data has a stray row of
    /// crystals). Miners' and botanists' collectables in the open world make the item <see cref="Obtain.Reduce"/>;
    /// fish collectables (a run doesn't reduce those yet) and special content make it <see cref="Obtain.ReduceElsewhere"/>.
    /// Crystals are left out: they're gathered directly.
    /// </summary>
    private void IndexReduction()
    {
        var items = Services.Data.GetExcelSheet<Item>();
        System.Text.Json.JsonDocument doc;
        try
        {
            using var stream = typeof(GameCatalog).Assembly.GetManifestResourceStream("Provenance.Resources.reduction.json")
                               ?? throw new System.IO.FileNotFoundException("reduction.json is missing from the plugin");
            doc = System.Text.Json.JsonDocument.Parse(stream);
        }
        catch (Exception e)
        {
            Services.Log.Error(e, "Couldn't read the aetherial reduction data");
            return;
        }
        using (doc)
            foreach (var row in doc.RootElement.GetProperty("reduction").EnumerateObject())
            {
                if (!uint.TryParse(row.Name, out var result) || result < 20 || Items.IsCrystal(result)) continue;
                if (items.GetRowOrDefault(result) is not { } resultItem || resultItem.Name.IsEmpty) continue;
                var land = new List<uint>();
                var all = new List<uint>();
                var elsewhere = Place.None;
                var fish = false;
                foreach (var s in row.Value.EnumerateArray())
                {
                    var source = s.GetUInt32();
                    if (items.GetRowOrDefault(source) is not { IsCollectable: true, AetherialReduce: > 0 }) continue;
                    all.Add(source);
                    var src = Sources(source);
                    if (src.Has(Obtain.Gather | Obtain.GatherTimed))
                    {
                        land.Add(source);
                        foreach (var p in GatherPoints(source).GroupBy(p => p.ClassJobId))
                            NoteReduced(result, p.Key, p.Min(x => x.Level), p.Any(x => !x.Timed), BrowseKind.Gather, Place.OpenWorld, true);
                    }
                    else if (src.Has(Obtain.Fish))
                    {
                        fish = true;
                        var level = Gather.Fish(source)?.Spots.Select(Gather.Spot).OfType<Core.Fishing.FishingSpotInfo>().Select(x => x.Level).DefaultIfEmpty(1).Min() ?? 1;
                        NoteReduced(result, 18, level, true, BrowseKind.Fish, Place.OpenWorld, false);
                    }
                    else if (src.Has(Obtain.GatherElsewhere | Obtain.FishElsewhere))
                    {
                        var where = src.Places & ~Place.OpenWorld;
                        elsewhere |= where;
                        foreach (var p in GatherPoints(source).GroupBy(p => p.ClassJobId))
                            NoteReduced(result, p.Key, p.Min(x => x.Level), true, BrowseKind.Gather, where, false);
                    }
                }
                if (all.Count == 0) continue;
                reductionSources[result] = all;
                if (land.Count > 0)
                {
                    reducedFrom[result] = land;
                    Add(result, Obtain.Reduce, Place.OpenWorld);
                }
                else if (fish || elsewhere != Place.None) Add(result, Obtain.ReduceElsewhere, elsewhere);
            }
    }

    private void NoteReduced(uint item, uint job, int level, bool normal, BrowseKind kind, Place place, bool byRun)
    {
        NoteBrowse(item, job, level, normal, kind, place);
        reducedBrowse[(item, job)] = reducedBrowse.GetValueOrDefault((item, job)) || byRun;
    }

    private void Add(uint itemId, Obtain flag, Place place = Place.None)
    {
        var existing = sources.GetValueOrDefault(itemId);
        sources[itemId] = existing with { Flags = existing.Flags | flag, Places = existing.Places | place };
    }
}
