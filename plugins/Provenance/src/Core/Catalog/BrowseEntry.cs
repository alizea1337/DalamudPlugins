namespace Provenance.Core.Catalog;

/// <summary>One row of the recipe browser.</summary>
public enum BrowseKind { Recipe, Gather, Fish }

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
/// <param name="ItemLevel">The item's item level.</param>
/// <param name="Category">The item's category as its tooltip names it, in the client's language ("Metal").</param>
/// <param name="CategoryEnglish">The same category in English, so English words always work in the search.</param>
/// <param name="Specialist">A recipe only a specialist can craft.</param>
/// <param name="Tome">For gathered and fished items: the folklore tome its nodes need (item id), 0 when none.</param>
public sealed record BrowseEntry(BrowseKind Kind, uint RecipeId, uint ItemId, string Name, uint ClassJobId, int Level, int Stars, uint Icon,
    int Yield, bool Expert, uint MasterBook, bool TimedOnly, bool CanHq = false, Place Places = Place.OpenWorld,
    bool Reduced = false, bool ReducedByRun = false, int ItemLevel = 0, string Category = "", string CategoryEnglish = "",
    bool Specialist = false, uint Tome = 0)
{
    public readonly string NameLower = Name.ToLowerInvariant();
    /// <summary>Both category names, lower case and without accents, for "contains" matching.</summary>
    public readonly string CategoryFolded = BrowseQuery.Fold(Category);
    public readonly string CategoryEnglishFolded = BrowseQuery.Fold(CategoryEnglish);

    public string Key => Kind == BrowseKind.Recipe ? "r" + RecipeId : $"g{ClassJobId}_{ItemId}";
}
