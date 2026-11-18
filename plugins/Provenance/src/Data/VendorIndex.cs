using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Lumina.Excel.Sheets;

namespace Provenance.Data;

/// <summary>One way to buy an item for gil: which NPC, which of its shops, where it stands.</summary>
/// <param name="MenuLabels">Menu entries that lead to the shop when talking to the NPC (outermost first).</param>
internal sealed record VendorOffer(uint NpcId, string NpcName, uint ShopId, uint TerritoryId, Vector2 Position, uint Price, IReadOnlyList<string> MenuLabels)
{
    public string PlaceName => Services.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(TerritoryId)?.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
}

/// <summary>
/// Gil vendors with a known position. Positions come from the shipped vendors.json (built by
/// tools/VendorTable from Teamcraft's NPC data); which shop sells what comes from the game data.
/// Only cities and open zones are used, never housing districts.
/// </summary>
internal sealed class VendorIndex
{
    private const string Resource = "Provenance.Resources.vendors.json";
    private readonly Dictionary<uint, List<VendorOffer>> offers = new();

    public IReadOnlyList<VendorOffer> For(uint itemId) => offers.TryGetValue(itemId, out var l) ? l : Array.Empty<VendorOffer>();
    public IEnumerable<uint> Items => offers.Keys;

    public static VendorIndex Build()
    {
        var index = new VendorIndex();
        var positions = LoadPositions();
        var territories = Services.Data.GetExcelSheet<TerritoryType>();
        var npcs = Services.Data.GetExcelSheet<ENpcBase>();
        var names = Services.Data.GetExcelSheet<ENpcResident>();
        var items = Services.Data.GetExcelSheet<Item>();
        var shops = Services.Data.GetSubrowExcelSheet<GilShopItem>();
        var gilShopNames = Services.Data.GetExcelSheet<GilShop>();
        var topics = Services.Data.GetExcelSheet<TopicSelect>();
        var pre = Services.Data.GetExcelSheet<PreHandler>();

        foreach (var (npcId, (territory, position)) in positions)
        {
            // Cities (0) and open zones (1) only; housing districts and instances are left out.
            if (territories.GetRowOrDefault(territory) is not { } t || t.TerritoryIntendedUse.RowId is not (0 or 1)) continue;
            if (npcs.GetRowOrDefault(npcId) is not { } npc) continue;
            var npcName = names.GetRowOrDefault(npcId)?.Singular.ExtractText() ?? "";

            foreach (var data in npc.ENpcData)
                Collect(data.RowId, new List<string>(), 0);

            void Collect(uint id, List<string> labels, int depth)
            {
                if (id == 0 || depth > 2) return;
                if (shops.HasRow(id))
                {
                    var shopName = gilShopNames.GetRowOrDefault(id)?.Name.ExtractText() ?? "";
                    var path = shopName.Length > 0 ? labels.Append(shopName).ToList() : labels;
                    foreach (var entry in shops.GetRow(id))
                    {
                        var item = entry.Item.RowId;
                        if (item == 0) continue;
                        var price = items.GetRowOrDefault(item)?.PriceMid ?? 0;
                        if (!index.offers.TryGetValue(item, out var list)) index.offers[item] = list = new List<VendorOffer>();
                        if (list.Any(o => o.NpcId == npcId && o.ShopId == id)) continue;
                        list.Add(new VendorOffer(npcId, npcName, id, territory, position, price, path));
                    }
                    return;
                }
                if (topics.GetRowOrDefault(id) is { } topic)
                {
                    var name = topic.Name.ExtractText();
                    foreach (var s in topic.Shop) Collect(s.RowId, name.Length > 0 ? labels.Append(name).ToList() : labels, depth + 1);
                    return;
                }
                if (pre.GetRowOrDefault(id) is { } p) Collect(p.Target.RowId, labels, depth + 1);
            }
        }
        return index;
    }

    /// <summary>NPC id → zone and world X/Z, from the shipped vendors.json (gil vendors and exchanges).</summary>
    internal static Dictionary<uint, (uint Territory, Vector2 Position)> LoadPositions()
    {
        var result = new Dictionary<uint, (uint, Vector2)>();
        using var stream = typeof(VendorIndex).Assembly.GetManifestResourceStream(Resource);
        if (stream == null)
        {
            Services.Log.Warning("vendors.json is missing from the plugin; vendor buying is off");
            return result;
        }
        using var doc = JsonDocument.Parse(stream);
        foreach (var npc in doc.RootElement.GetProperty("vendors").EnumerateObject())
        {
            if (!uint.TryParse(npc.Name, out var id)) continue;
            var v = npc.Value;
            result[id] = ((uint)v[0].GetSingle(), new Vector2(v[1].GetSingle(), v[2].GetSingle()));
        }
        return result;
    }
}
