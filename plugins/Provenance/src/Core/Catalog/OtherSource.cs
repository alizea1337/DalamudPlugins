namespace Provenance.Core.Catalog;

/// <summary>A way to get an item that a run doesn't take; the plan names it when the item is left to you.</summary>
public enum OtherWay
{
    /// <summary>Sold at a shop for a currency a run doesn't spend (Bicolor Gemstones, tomestones, Cosmocredits…).</summary>
    Shop,
    /// <summary>Sold for Grand Company seals.</summary>
    CompanySeals,
    /// <summary>Brought back by retainers on ventures.</summary>
    Venture,
    /// <summary>Tradeable: other players sell it on the market board.</summary>
    MarketBoard,
}

/// <param name="Currency">For a shop: the currency as the cost reads ("3 Bicolor Gemstones" takes the plural).</param>
/// <param name="CurrencyPlural">For a shop: the currency's plural ("Bicolor Gemstones").</param>
/// <param name="Cost">For a shop or seals: what one costs.</param>
/// <param name="Where">For a shop: who sells it and where ("the gemstone trader in Old Sharlayan"), or empty.</param>
public sealed record OtherSource(OtherWay Way, string Currency = "", string CurrencyPlural = "", int Cost = 0, string Where = "");
