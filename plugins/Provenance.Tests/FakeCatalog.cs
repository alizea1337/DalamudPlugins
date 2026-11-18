using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Catalog;

namespace Provenance.Tests;

/// <summary>A tiny hand-built catalog. Ids are arbitrary except crystals (2–19).</summary>
internal sealed class FakeCatalog : IGameCatalog
{
    public const uint WindShard = 4, FireShard = 2;
    public const uint Log = 100, Lumber = 101, Rivets = 102, Ore = 103, Ingot = 104;
    public const uint Bow = 200, Shield = 201;
    public const uint TimedLog = 300, RareLumber = 301;
    public const uint FancyDye = 400, Mystery = 401, Fish = 402, Fang = 403;

    private readonly Dictionary<uint, string> names = new();
    private readonly Dictionary<uint, List<RecipeInfo>> recipes = new();
    private readonly Dictionary<uint, ItemSources> sources = new();
    private uint nextRecipe = 1;

    public FakeCatalog()
    {
        Name(WindShard, "Wind Shard"); Gatherable(WindShard);
        Name(FireShard, "Fire Shard"); Gatherable(FireShard);
        Name(Log, "Maple Log"); Gatherable(Log);
        Name(Ore, "Copper Ore"); Gatherable(Ore);
        Name(TimedLog, "Elder Log"); sources[TimedLog] = new ItemSources(Obtain.GatherTimed, 0);
        Name(FancyDye, "Fancy Dye"); sources[FancyDye] = new ItemSources(Obtain.Vendor, 5000);
        Name(Mystery, "Mystery Thing");
        Name(Fish, "Pipira"); sources[Fish] = new ItemSources(Obtain.Fish | Obtain.Vendor, 50);
        Name(Fang, "Wolf Fang"); sources[Fang] = new ItemSources(Obtain.Drop, 0);

        Name(Lumber, "Maple Lumber"); AddRecipe(Lumber, Job.Carpenter, 1, yield: 1, (Log, 3), (WindShard, 1));
        Name(Ingot, "Copper Ingot"); AddRecipe(Ingot, Job.Blacksmith, 1, yield: 1, (Ore, 2), (FireShard, 1));
        Name(Rivets, "Copper Rivets"); AddRecipe(Rivets, Job.Blacksmith, 2, yield: 3, (Ingot, 1), (FireShard, 1));
        Name(Bow, "Maple Shortbow"); AddRecipe(Bow, Job.Carpenter, 5, yield: 1, (Lumber, 2), (Rivets, 1), (WindShard, 1));
        Name(Shield, "Maple Round Shield"); AddRecipe(Shield, Job.Carpenter, 6, yield: 1, (Lumber, 1), (Rivets, 2), (WindShard, 1));
        Name(RareLumber, "Elder Lumber"); AddRecipe(RareLumber, Job.Carpenter, 50, yield: 1, (TimedLog, 3));
    }

    public void SetSources(uint id, ItemSources src) => sources[id] = src;

    private void Name(uint id, string name) => names[id] = name;
    private void Gatherable(uint id) => sources[id] = new ItemSources(Obtain.Gather, 0);

    public RecipeInfo AddRecipe(uint item, Job job, int level, int yield, params (uint Id, int Amount)[] ingredients)
    {
        var r = new RecipeInfo(nextRecipe++, item, job, level, 0, yield, false, ingredients.Select(i => new Ingredient(i.Id, i.Amount)).ToList());
        if (!recipes.TryGetValue(item, out var list)) recipes[item] = list = new List<RecipeInfo>();
        list.Add(r);
        return r;
    }

    public string ItemName(uint itemId) => names.GetValueOrDefault(itemId, "#" + itemId);
    public uint ItemIcon(uint itemId) => 0;
    public IReadOnlyList<RecipeInfo> RecipesFor(uint itemId) => recipes.GetValueOrDefault(itemId) ?? (IReadOnlyList<RecipeInfo>)new List<RecipeInfo>();
    public RecipeInfo? Recipe(uint recipeId) => recipes.Values.SelectMany(l => l).FirstOrDefault(r => r.RecipeId == recipeId);
    public ItemSources Sources(uint itemId) => sources.GetValueOrDefault(itemId);

    public bool TryFindItem(string name, out uint itemId)
    {
        var hit = names.FirstOrDefault(kv => string.Equals(kv.Value, name.Trim(), System.StringComparison.OrdinalIgnoreCase));
        itemId = hit.Key;
        return hit.Value != null;
    }
}
