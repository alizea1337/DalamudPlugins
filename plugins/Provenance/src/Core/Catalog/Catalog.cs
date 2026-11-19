using System;
using System.Collections.Generic;
using System.Linq;

namespace Provenance.Core.Catalog;

/// <summary>The eight crafters, numbered like the game's CraftType sheet (row 0 = Carpenter).</summary>
public enum Job : byte
{
    Carpenter = 0,
    Blacksmith = 1,
    Armorer = 2,
    Goldsmith = 3,
    Leatherworker = 4,
    Weaver = 5,
    Alchemist = 6,
    Culinarian = 7,
}

public static class Jobs
{
    public static readonly Job[] All =
    {
        Job.Carpenter, Job.Blacksmith, Job.Armorer, Job.Goldsmith,
        Job.Leatherworker, Job.Weaver, Job.Alchemist, Job.Culinarian,
    };

    public static string Abbreviation(this Job job) => job switch
    {
        Job.Carpenter => "CRP",
        Job.Blacksmith => "BSM",
        Job.Armorer => "ARM",
        Job.Goldsmith => "GSM",
        Job.Leatherworker => "LTW",
        Job.Weaver => "WVR",
        Job.Alchemist => "ALC",
        Job.Culinarian => "CUL",
        _ => "?",
    };

    /// <summary>ClassJob sheet row for the crafter (Carpenter = 8 … Culinarian = 15).</summary>
    public static uint ClassJobId(this Job job) => 8u + (uint)job;
}

public readonly record struct Ingredient(uint ItemId, int Amount);

public sealed record RecipeInfo(
    uint RecipeId,
    uint ItemId,
    Job Job,
    int Level,
    int Stars,
    int Yield,
    bool Expert,
    IReadOnlyList<Ingredient> Ingredients,
    /// <summary>SecretRecipeBook row the recipe needs, 0 when it needs none.</summary>
    uint MasterBook = 0,
    /// <summary>The result can come out high quality.</summary>
    bool CanHq = true);

/// <summary>Ways to get an item without crafting it. An item can have several.</summary>
[Flags]
public enum Obtain
{
    None = 0,
    /// <summary>At least one gathering point that is always up.</summary>
    Gather = 1 << 0,
    /// <summary>Only on unspoiled, legendary or ephemeral nodes.</summary>
    GatherTimed = 1 << 1,
    Fish = 1 << 2,
    /// <summary>Sold by a gil shop.</summary>
    Vendor = 1 << 3,
    /// <summary>Dropped by monsters.</summary>
    Drop = 1 << 4,
    /// <summary>Gathered in content a run can't go to (<see cref="ItemSources.Places"/> says which).</summary>
    GatherElsewhere = 1 << 5,
    /// <summary>Fished in content a run can't go to.</summary>
    FishElsewhere = 1 << 6,
    /// <summary>Crafted only in content a run can't go to (Cosmic Exploration missions).</summary>
    CraftElsewhere = 1 << 7,
    Elsewhere = GatherElsewhere | FishElsewhere | CraftElsewhere,
}

/// <summary>Where an item is gathered, fished or crafted. A run only goes to the open world.</summary>
[Flags]
public enum Place : byte
{
    None = 0,
    /// <summary>Field zones, cities and housing districts.</summary>
    OpenWorld = 1 << 0,
    Diadem = 1 << 1,
    CosmicExploration = 1 << 2,
    OceanFishing = 1 << 3,
    /// <summary>Quest nodes and content that is no longer in the game (older versions of the Diadem).</summary>
    Other = 1 << 4,
}

public static class Places
{
    /// <summary>"the Diadem", "Cosmic Exploration", "Ocean Fishing".</summary>
    public static string Name(Place place) => place switch
    {
        Place.OpenWorld => "the open world",
        Place.Diadem => "the Diadem",
        Place.CosmicExploration => "Cosmic Exploration",
        Place.OceanFishing => "Ocean Fishing",
        Place.Other => "quests or content that's no longer in the game",
        _ => "special content",
    };

    /// <summary>Every flag named, joined with "or": "the Diadem or Cosmic Exploration".</summary>
    public static string Describe(Place places)
    {
        var names = Enum.GetValues<Place>().Where(p => p != Place.None && (places & p) != 0).Select(Name).ToList();
        return names.Count == 0 ? Name(Place.None) : string.Join(" or ", names);
    }
}

/// <param name="Tome">
/// Item id of the folklore tome the item's timed nodes or fishing holes need, or 0 when at least one
/// of them needs no tome.
/// </param>
/// <param name="Places">Everywhere the item is gathered, fished or crafted (not where it's sold or dropped).</param>
public readonly record struct ItemSources(Obtain Flags, uint VendorPrice, uint Tome = 0, Place Places = Place.None)
{
    public bool Has(Obtain flag) => (Flags & flag) != 0;
}

/// <summary>Read-only game data the planner and importers need. The plugin backs it with Lumina sheets.</summary>
public interface IGameCatalog
{
    string ItemName(uint itemId);
    uint ItemIcon(uint itemId);
    IReadOnlyList<RecipeInfo> RecipesFor(uint itemId);
    RecipeInfo? Recipe(uint recipeId);
    ItemSources Sources(uint itemId);
    /// <summary>Exact item-name lookup in the client language, ignoring case and the HQ glyph.</summary>
    bool TryFindItem(string name, out uint itemId);
}

public static class Items
{
    /// <summary>Shards, crystals and clusters are item ids 2–19.</summary>
    public static bool IsCrystal(uint itemId) => itemId is >= 2 and <= 19;
}
