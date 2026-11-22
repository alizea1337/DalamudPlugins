using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Lumina.Excel.Sheets;

namespace Provenance.Data;

/// <summary>
/// One place to buy a book or tome with scrips or tokens: the NPC, the way into the exchange window
/// (menu entries, then the inclusion shop's page and subpage, or a plain exchange window), and the price.
/// </summary>
/// <param name="InclusionShopId">0 when the NPC opens the exchange window directly.</param>
/// <param name="Page">Inclusion shop page (0-based, as the game's category list).</param>
/// <param name="SubPage">Inclusion shop subpage (1-based).</param>
internal sealed record ExchangeOffer(uint ItemId, uint NpcId, string NpcName, uint TerritoryId, Vector2 Position,
    uint CurrencyItemId, string CurrencyName, int Cost, uint SpecialShopId, uint InclusionShopId, int Page, int SubPage,
    IReadOnlyList<string> MenuLabels, int NpcEntries = 1)
{
    public string PlaceName => Services.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(TerritoryId)?.PlaceName.ValueNullable?.Name.ExtractText() ?? "";

    /// <summary>The NPC for use in a sentence: "the scrip exchange" for generic names, "Hodge" for named ones.</summary>
    public string Who => NpcName.Length > 0 && char.IsLower(NpcName[0]) ? "the " + NpcName : NpcName;
}

/// <summary>
/// Scrip and token exchanges for master books, folklore tomes and materials. Scrip exchanges are "inclusion
/// shops": an NPC (often through a greeting) opens a window with pages and subpages, each subpage one
/// special shop. The currency mapping and the page/subpage routes follow GatherBuddy Reborn's Vulcan
/// vendor resolver (Apache-2.0), which mirrors AllaganLib's.
/// </summary>
internal sealed class ExchangeIndex
{
    // Special shops list some currencies by a small index instead of the item (scrips, tomestones).
    private static readonly Dictionary<uint, uint> CurrencyTypeMap = new()
    {
        { 1, 10309 }, { 2, 33913 }, { 3, 10311 }, { 4, 33914 }, { 5, 10307 },
        { 6, 41784 }, { 7, 41785 }, { 8, 21072 }, { 9, 21073 }, { 10, 21074 },
        { 11, 21075 }, { 12, 21076 }, { 13, 21077 }, { 14, 21078 }, { 15, 21079 },
        { 16, 21080 }, { 17, 21081 }, { 18, 21172 }, { 19, 21173 }, { 20, 21935 },
        { 21, 22525 }, { 22, 26533 }, { 23, 26807 }, { 24, 28063 }, { 25, 28186 },
        { 26, 28187 }, { 27, 28188 }, { 28, 30341 },
    };
    private static readonly HashSet<uint> PrefersCurrencyTypeMap = new() { 1770637, 1770638 };

    private readonly Dictionary<uint, List<ExchangeOffer>> offers = new();

    /// <summary>Scrip item for a currency index as special shops and the collectable appraiser list it (2 = Purple Crafters' Scrip, …).</summary>
    public static uint ScripItem(uint index) => CurrencyTypeMap.GetValueOrDefault(index, index);

    public IReadOnlyList<ExchangeOffer> For(uint itemId) => offers.TryGetValue(itemId, out var l) ? l : Array.Empty<ExchangeOffer>();

    /// <summary>Every item some reachable exchange sells.</summary>
    public IEnumerable<uint> Items => offers.Keys;

    /// <summary>The offers for a set of items.</summary>
    public static ExchangeIndex Build(IReadOnlySet<uint> wanted, IReadOnlyDictionary<uint, (uint Territory, Vector2 Position)> positions)
        => wanted.Count == 0 ? new ExchangeIndex() : Build((item, _) => wanted.Contains(item), positions);

    /// <summary>The offers for every item <paramref name="want"/> takes, asked with the item and the currency it costs.</summary>
    public static ExchangeIndex Build(Func<uint, uint, bool> want, IReadOnlyDictionary<uint, (uint Territory, Vector2 Position)> positions)
    {
        var index = new ExchangeIndex();
        var items = Services.Data.GetExcelSheet<Item>();
        var sales = Sales(want);
        if (sales.Count == 0) return index;

        // Inclusion shop pages and subpages that show those special shops.
        var routes = new Dictionary<uint, List<(uint Special, int Page, int SubPage)>>();
        var categories = Services.Data.GetExcelSheet<InclusionShopCategory>();
        var series = Services.Data.GetSubrowExcelSheet<InclusionShopSeries>();
        foreach (var inclusion in Services.Data.GetExcelSheet<InclusionShop>())
            for (var page = 0; page < inclusion.Category.Count; page++)
            {
                var categoryId = inclusion.Category[page].RowId;
                if (categoryId == 0 || categories.GetRowOrDefault(categoryId) is not { } category) continue;
                var seriesId = category.InclusionShopSeries.RowId;
                if (seriesId == 0 || !series.HasRow(seriesId)) continue;
                var rows = series.GetRow(seriesId);
                for (var sub = 0; sub < rows.Count; sub++)
                {
                    var special = rows[sub].SpecialShop.RowId;
                    if (!sales.ContainsKey(special)) continue;
                    if (!routes.TryGetValue(inclusion.RowId, out var list)) routes[inclusion.RowId] = list = new();
                    list.Add((special, page, sub + 1));
                }
            }

        // NPCs with a known position that lead to one of them, directly or through a greeting or topic menu.
        var npcs = Services.Data.GetExcelSheet<ENpcBase>();
        var names = Services.Data.GetExcelSheet<ENpcResident>();
        var topics = Services.Data.GetExcelSheet<TopicSelect>();
        var pre = Services.Data.GetExcelSheet<PreHandler>();
        var territories = Services.Data.GetExcelSheet<TerritoryType>();
        foreach (var (npcId, (territory, position)) in positions)
        {
            if (territories.GetRowOrDefault(territory) is not { } t || t.TerritoryIntendedUse.RowId is not (0 or 1)) continue;
            if (npcs.GetRowOrDefault(npcId) is not { } npc) continue;
            var npcName = names.GetRowOrDefault(npcId)?.Singular.ExtractText() ?? "";
            // How much else the NPC does: a dedicated exchange (one or two entries) opens straight away.
            var services = npc.ENpcData.Count(d => d.RowId != 0);
            foreach (var data in npc.ENpcData) Collect(data.RowId, new List<string>(), 0);

            void Collect(uint id, List<string> labels, int depth)
            {
                if (id == 0 || depth > 2) return;
                if (routes.TryGetValue(id, out var pages))
                {
                    foreach (var (special, page, sub) in pages) Add(special, id, page, sub, labels);
                    return;
                }
                if (sales.ContainsKey(id)) { Add(id, 0, -1, 0, labels); return; }
                if (pre.GetRowOrDefault(id) is { } p) { Collect(p.Target.RowId, labels, depth + 1); return; }
                if (topics.GetRowOrDefault(id) is { } topic)
                {
                    var name = topic.Name.ExtractText();
                    foreach (var s in topic.Shop) Collect(s.RowId, name.Length > 0 ? labels.Append(name).ToList() : labels, depth + 1);
                }
            }

            void Add(uint special, uint inclusion, int page, int sub, List<string> labels)
            {
                foreach (var (item, currency, cost) in sales[special])
                {
                    if (!index.offers.TryGetValue(item, out var list)) index.offers[item] = list = new();
                    if (list.Any(o => o.NpcId == npcId && o.SpecialShopId == special)) continue;
                    var row = items.GetRowOrDefault(currency);
                    var currencyName = row is { } r ? (cost > 1 ? GameCatalog.Plural(r) : r.Name.ExtractText()) : "currency";
                    list.Add(new ExchangeOffer(item, npcId, npcName, territory, position, currency, currencyName, cost, special, inclusion, page, sub, labels, services));
                }
            }
        }
        return index;
    }

    /// <summary>
    /// Special shop entries that sell an item <paramref name="want"/> takes, with their (single) price, per special
    /// shop row: whoever sells them, wherever they stand.
    /// </summary>
    public static Dictionary<uint, List<(uint Item, uint Currency, int Cost)>> Sales(Func<uint, uint, bool> want)
    {
        var tomes = new Dictionary<uint, uint>();
        foreach (var row in Services.Data.GetExcelSheet<TomestonesItem>())
            if (row.Tomestones.RowId > 0 && row.CurrencyInventorySlot > 0) tomes.TryAdd(row.Tomestones.RowId, row.Item.RowId);
        var sales = new Dictionary<uint, List<(uint Item, uint Currency, int Cost)>>();
        foreach (var shop in Services.Data.GetExcelSheet<SpecialShop>())
            foreach (var entry in shop.Item)
            {
                var costs = entry.ItemCosts.Where(c => c.ItemCost.RowId != 0 && c.CurrencyCost > 0).ToList();
                if (costs.Count != 1) continue; // exchanges that take several things at once are left alone
                var currency = ConvertCurrency(shop.RowId, costs[0].ItemCost.RowId, shop.UseCurrencyType, tomes);
                var receive = entry.ReceiveItems.Select(r => r.Item.RowId).FirstOrDefault(id => id != 0 && want(id, currency));
                if (receive == 0) continue;
                if (!sales.TryGetValue(shop.RowId, out var list)) sales[shop.RowId] = list = new();
                list.Add((receive, currency, (int)costs[0].CurrencyCost));
            }
        return sales;
    }

    /// <summary>Currency item behind a special shop's cost (scrips and tomestones are listed by index).</summary>
    private static uint ConvertCurrency(uint shopId, uint raw, ushort useCurrencyType, Dictionary<uint, uint> tomes)
    {
        if (PrefersCurrencyTypeMap.Contains(shopId) && CurrencyTypeMap.TryGetValue(raw, out var preferred)) return preferred;
        if ((shopId == 1770446 || (shopId is 1770699 or 1770803 && raw < 10))
            && (tomes.TryGetValue(raw, out var v) || CurrencyTypeMap.TryGetValue(raw, out v))) return v;
        if (useCurrencyType == 16 && raw != 25 && CurrencyTypeMap.TryGetValue(raw, out var scrip)) return scrip;
        if (useCurrencyType == 2 && raw < 10 && tomes.TryGetValue(raw, out var tome)) return tome;
        if (useCurrencyType == 4 && raw < 10 && (tomes.TryGetValue(raw, out var w) || CurrencyTypeMap.TryGetValue(raw, out w))) return w;
        return raw;
    }
}
