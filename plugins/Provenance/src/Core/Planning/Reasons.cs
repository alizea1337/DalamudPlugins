using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Catalog;

namespace Provenance.Core.Planning;

/// <summary>
/// Why a run leaves an item to you: what the game offers for it, and whether the game or Provenance is the limit.
/// Content Provenance means to support later (the Diadem, Cosmic Exploration, Ocean Fishing) says "yet".
/// </summary>
public static class Reasons
{
    /// <summary>When nothing at all is known about an item.</summary>
    public const string Unknown = "Provenance knows no source for it: it isn't gathered, fished or sold anywhere, "
                                  + "and no monster it has data for drops it";

    /// <summary>The reason for an item no way of a run gets.</summary>
    /// <param name="others">Ways a run doesn't take, from the catalog; the first two are named.</param>
    public static string NoWay(ItemSources src, IReadOnlyList<OtherSource> others, uint vendorMaxPrice)
    {
        var elsewhere = src.Places & ~Place.OpenWorld;
        var main = src.Has(Obtain.Vendor) ? VendorCap(src.VendorPrice, vendorMaxPrice)
            : src.Has(Obtain.Reduce) ? "Only from aetherial reduction of fish, and fishing for reductions is off or the fish only bite in a time window or weather (Settings, Fishing)"
            : src.Has(Obtain.ReduceElsewhere)
                ? elsewhere != Place.None
                    ? $"Only from aetherial reduction of collectables gathered in {Places.Describe(elsewhere)}, {Limit(elsewhere)}"
                    : "Only from aetherial reduction of collectables a run can't get"
            : elsewhere != Place.None ? Elsewhere(src)
            : null;
        var also = others.Take(2).Select(Describe).ToList();
        if (main == null) return also.Count == 0 ? Unknown : Capital(string.Join("; ", also));
        return also.Count == 0 ? main : main + "; also " + string.Join("; ", also);
    }

    /// <summary>"Vendor price 12,000 gil is above your cap of 10,000 (Settings, General)".</summary>
    public static string VendorCap(uint price, uint cap) => $"Vendor price {price:N0} gil is above your cap of {cap:N0} (Settings, General)";

    /// <summary>"Only gathered in the Diadem, which Provenance doesn't support yet."</summary>
    public static string Elsewhere(ItemSources src)
    {
        var ways = new List<string>(3);
        if (src.Has(Obtain.GatherElsewhere)) ways.Add("gathered");
        if (src.Has(Obtain.FishElsewhere)) ways.Add("fished");
        if (src.Has(Obtain.CraftElsewhere)) ways.Add("crafted");
        var how = string.Join(" or ", ways);
        // Quest nodes and content that's gone only count when nothing else is left.
        var where = src.Places & ~Place.OpenWorld;
        if (where != Place.Other) where &= ~Place.Other;
        if (where == Place.Other)
            return ways.Count == 0
                ? "Only from quests or content that's no longer in the game"
                : $"Only {how} during quests or in content that's no longer in the game";
        return ways.Count == 0
            ? $"Only from {Places.Describe(where)}, {Limit(where)}"
            : $"Only {how} in {Places.Describe(where)}, {Limit(where)}";
    }

    /// <summary>A way a run doesn't take, as a clause: "sold for 3 Bicolor Gemstones by …, and Provenance doesn't spend them".</summary>
    public static string Describe(OtherSource o) => o.Way switch
    {
        OtherWay.Shop => $"sold for {o.Cost:N0} {o.Currency}{(o.Where.Length > 0 ? " by " + o.Where : "")}, and Provenance doesn't spend {o.CurrencyPlural}",
        OtherWay.CompanySeals => $"sold for {o.Cost:N0} company seals at your Grand Company, and Provenance doesn't spend seals",
        OtherWay.Venture => "brought back by retainers on ventures, which Provenance doesn't send",
        _ => "sold by other players on the market board, which Provenance doesn't use",
    };

    /// <summary>Whose limit it is: content Provenance doesn't do yet, or that a run can't reach at all.</summary>
    private static string Limit(Place where)
        => (where & ~(Place.Diadem | Place.CosmicExploration | Place.OceanFishing)) == Place.None
            ? "which Provenance doesn't support yet"
            : "which a run can't go to";

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
