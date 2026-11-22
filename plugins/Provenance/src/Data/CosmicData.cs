using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Lumina.Excel.Sheets;
using Provenance.Core.Cosmic;

namespace Provenance.Data;

/// <summary>Something the Cosmocredit Exchange sells, with where it sits in the shop.</summary>
/// <param name="Index">Its row in the special shop, which is the order the shop window lists it in.</param>
/// <param name="MateriaParam">For materia: the stat it adds (BaseParam), else 0; <paramref name="MateriaGrade"/> its grade.</param>
internal sealed record CosmicShopItem(uint ShopId, int Index, uint ItemId, string Name, int Count, int Cost, uint Icon,
    uint MateriaParam = 0, int MateriaGrade = -1);

/// <summary>An NPC on a planet the loop walks to.</summary>
internal sealed record CosmicNpc(uint Id, uint Territory, Vector3 Position);

/// <summary>
/// Cosmic Exploration from the game data: the stellar missions with their recipes, supplies and rewards, the
/// Cosmocredit Exchange's stock, and the NPCs a run goes to. Built with the catalog, off the game thread.
/// </summary>
internal sealed class CosmicData
{
    /// <summary>What every crafting mission hands out to craft from.</summary>
    public const uint CosmicContainer = 48233;

    /// <summary>Mare Lamentorum's Bestways Burrow aetheryte: "Travel to …" takes you to the newest planet.</summary>
    public const uint BestwaysBurrow = 175;
    public const uint MareLamentorum = 959;
    /// <summary>Drivingway in Mare Lamentorum: goes to any unlocked planet.</summary>
    public static readonly CosmicNpc Drivingway = new(1052581, MareLamentorum, new Vector3(27.2f, -137.4f, -411.7f));

    public const uint MaterialMiracle = 41269;
    public const uint StellarSteadyHand = 46843;

    /// <summary>Cosmocredit Exchange rows: cosmetics and mounts, and materials, materia and items.</summary>
    public static readonly uint[] CosmocreditShops = { 1770945, 1770977 };

    private readonly Dictionary<uint, CosmicMission> missions = new();
    private readonly Dictionary<(Planet, uint), List<CosmicMission>> byClass = new();

    public IReadOnlyCollection<CosmicMission> Missions => missions.Values;
    public IReadOnlyList<CosmicShopItem> Shop { get; private set; } = Array.Empty<CosmicShopItem>();

    /// <summary>The highest-grade crafter and gatherer materia the exchange sells, one per stat.</summary>
    public IReadOnlyList<CosmicShopItem> TopMateria { get; private set; } = Array.Empty<CosmicShopItem>();

    public CosmicMission? Mission(uint id) => missions.GetValueOrDefault(id);

    /// <summary>A class's missions on a planet (as main class or as either class of a two-class mission).</summary>
    public IReadOnlyList<CosmicMission> For(Planet planet, uint classJob)
        => byClass.TryGetValue((planet, classJob), out var l) ? l : Array.Empty<CosmicMission>();

    /// <summary>The Cosmocredit trader on a planet.</summary>
    public static CosmicNpc Trader(Planet p) => p switch
    {
        Planet.SinusArdorum => new CosmicNpc(1052608, 1237, new Vector3(18.3f, 1.7f, 19.4f)),
        Planet.Phaenna => new CosmicNpc(1052640, 1291, new Vector3(358.3f, 52.7f, -400.4f)),
        Planet.Oizys => new CosmicNpc(1052650, 1310, new Vector3(-202.2f, 0.7f, 152.5f)),
        _ => new CosmicNpc(1056824, 1319, new Vector3(317.7f, 205.8f, 376.7f)),
    };

    /// <summary>Every NPC id the trader has across a planet's phases (Sinus Ardorum swaps hers as it's built up).</summary>
    public static readonly HashSet<uint> TraderIds = new() { 1052588, 1052600, 1052607, 1052608, 1052640, 1052650, 1056824 };

    /// <summary>Cruisingway, who flies between planets.</summary>
    public static CosmicNpc Cruiser(Planet p) => p switch
    {
        Planet.SinusArdorum => new CosmicNpc(1052624, 1237, new Vector3(-42.0f, 11.9f, -95.7f)),
        Planet.Phaenna => new CosmicNpc(1052625, 1291, new Vector3(277.3f, 52.0f, -377.4f)),
        Planet.Oizys => new CosmicNpc(1052644, 1310, new Vector3(-204.4f, 0.5f, 66.5f)),
        _ => new CosmicNpc(1056818, 1319, new Vector3(366.1f, 204.1f, 395.6f)),
    };

    public static CosmicData Build()
    {
        var d = new CosmicData();
        try
        {
            d.IndexMissions();
            d.IndexShop();
        }
        catch (Exception e)
        {
            Services.Log.Error(e, "Couldn't read Cosmic Exploration's missions");
        }
        return d;
    }

    private void IndexMissions()
    {
        var recipes = Services.Data.GetExcelSheet<Recipe>();
        foreach (var row in Services.Data.GetExcelSheet<WKSMissionUnit>())
        {
            var name = CleanName(row.Name.ExtractText());
            if (name.Length == 0 || row.ClassJobCategory.Count == 0) continue;
            var first = row.ClassJobCategory[0].RowId;
            if (first is < 9 or > 19) continue;
            var second = row.ClassJobCategory.Count > 1 && row.ClassJobCategory[1].RowId is >= 9 and <= 19 ? row.ClassJobCategory[1].RowId - 1 : 0;

            var kinds = MissionKind.None;
            if (row.IsSpecialQuest) kinds |= MissionKind.Critical;
            if (row.LockedBehind.RowId != 0) kinds |= MissionKind.Chained;
            if (row.Unknown2) kinds |= MissionKind.Master;
            if (second != 0) kinds |= MissionKind.TwoClass;
            if (row.WKSMissionLotterySpecialCond.RowId != 0 && row.WKSMissionLotterySpecialCond.ValueNullable is { } cond)
            {
                if (cond.WeatherRequired.RowId != 0) kinds |= MissionKind.Weather;
                else if (cond.StartTimeHour != cond.EndTimeHour) kinds |= MissionKind.Time;
            }

            // The recipes, the ones whose result another one takes first.
            var steps = new List<MissionStep>();
            if (row.WKSMissionRecipe.RowId != 0 && row.WKSMissionRecipe.ValueNullable is { } mr)
                foreach (var r in mr.Recipe)
                {
                    if (r.RowId == 0 || recipes.GetRowOrDefault(r.RowId) is not { } recipe || recipe.ItemResult.RowId == 0) continue;
                    var ingredient = recipe.Ingredient.Count > 0 ? recipe.Ingredient[0].RowId : 0;
                    var amount = recipe.AmountIngredient.Count > 0 ? recipe.AmountIngredient[0] : 0;
                    if (ingredient == 0) continue;
                    steps.Add(new MissionStep(recipe.RowId, recipe.ItemResult.RowId, ingredient, Math.Max(1, (int)amount), Math.Max(1, (int)recipe.AmountResult)));
                }
            steps = Ordered(steps);

            var todo = row.MissionToDo.Count > 0 ? row.MissionToDo[0].ValueNullable : null;
            var targets = new List<MissionTarget>();
            var action = StellarAction.None;
            var charges = 0;
            if (todo is { } t)
            {
                for (var i = 0; i < t.RequiredItem.Count && i < t.RequiredItemQuantity.Count; i++)
                    if (t.RequiredItem[i].ValueNullable?.Item.RowId is > 0 and var item && t.RequiredItemQuantity[i] > 0)
                        targets.Add(new MissionTarget(item, t.RequiredItemQuantity[i]));
                action = t.TemporaryAction.RowId switch
                {
                    MaterialMiracle => StellarAction.MaterialMiracle,
                    StellarSteadyHand => StellarAction.SteadyHand,
                    _ => StellarAction.None,
                };
                charges = action == StellarAction.None ? 0 : Math.Max(1, (int)t.Unknown14);
            }
            // A crafting mission with no item named: whatever its last recipe makes, once.
            if (targets.Count == 0 && steps.Count > 0) targets.Add(new MissionTarget(steps[^1].ItemId, 1));

            uint supplied = 0;
            var suppliedCount = 0;
            if (row.WKSMissionSupplyItem.ValueNullable is { } supply && supply.Item.Count > 0)
            {
                supplied = supply.Item[0].ValueNullable?.Item.RowId ?? 0;
                suppliedCount = supply.ItemCount.Count > 0 ? supply.ItemCount[0] : 0;
            }

            var reward = row.MissionReward.ValueNullable is { } rw
                ? new MissionReward(rw.CosmoCredits, rw.PlanetCredits, rw.BaseDronebits, rw.Item.RowId, rw.Item.RowId != 0 ? rw.ItemCount : 0)
                : new MissionReward(0, 0, 0);

            var mission = new CosmicMission(row.RowId, name, Planets.OfMission(row.RowId), first - 1, second,
                (MissionRank)Math.Clamp((int)row.LevelGroup, 1, 6), kinds, row.MissionTime, (int)row.SilverStarRequirement,
                (int)row.GoldStarRequirement, steps, supplied, suppliedCount, targets, action, charges, reward, row.LockedBehind.RowId);
            missions[row.RowId] = mission;
            Note(mission.Planet, mission.ClassJob, mission);
            if (second != 0) Note(mission.Planet, second, mission);
        }
        Services.Log.Information("Cosmic Exploration: {Missions} missions, {Crafts} of them crafting", missions.Count, missions.Values.Count(m => m.IsCraft));
    }

    private void Note(Planet planet, uint classJob, CosmicMission mission)
    {
        if (!byClass.TryGetValue((planet, classJob), out var list)) byClass[(planet, classJob)] = list = new();
        list.Add(mission);
    }

    /// <summary>A step after the step that makes its ingredient; otherwise as listed.</summary>
    private static List<MissionStep> Ordered(List<MissionStep> steps)
    {
        var ordered = new List<MissionStep>(steps.Count);
        var left = new List<MissionStep>(steps);
        while (left.Count > 0)
        {
            var next = left.FirstOrDefault(s => !left.Any(o => o != s && o.ItemId == s.IngredientId)) ?? left[0];
            ordered.Add(next);
            left.Remove(next);
        }
        return ordered;
    }

    /// <summary>The name without the level-sync glyph and the "EX: ", "EX+: " and "Master: " the rank already says.</summary>
    private static string CleanName(string name)
    {
        name = name.TrimStart('', ' ');
        foreach (var prefix in new[] { "EX+: ", "EX: ", "Master: " })
            if (name.StartsWith(prefix, StringComparison.Ordinal)) return name[prefix.Length..].Trim();
        return name.Trim();
    }

    private void IndexShop()
    {
        var items = Services.Data.GetExcelSheet<Item>();
        var materiaSheet = Services.Data.GetExcelSheet<Materia>();
        // Materia item id -> (stat, grade).
        var materia = new Dictionary<uint, (uint Param, int Grade)>();
        foreach (var m in materiaSheet)
            for (var g = 0; g < m.Item.Count; g++)
                if (m.Item[g].RowId != 0) materia[m.Item[g].RowId] = (m.BaseParam.RowId, g);

        var stock = new List<CosmicShopItem>();
        foreach (var shopId in CosmocreditShops)
        {
            if (Services.Data.GetExcelSheet<SpecialShop>().GetRowOrDefault(shopId) is not { } shop) continue;
            var index = 0;
            foreach (var entry in shop.Item)
            {
                // A default struct from Lumina throws on its fields, so no FirstOrDefault here.
                uint received = 0, count = 0, price = 0;
                foreach (var r in entry.ReceiveItems)
                    if (r.Item.RowId != 0) { received = r.Item.RowId; count = r.ReceiveCount; break; }
                foreach (var c in entry.ItemCosts)
                    if (c.ItemCost.RowId == Planets.Cosmocredit) { price = c.CurrencyCost; break; }
                if (received == 0) continue;
                var at = index++;
                if (price == 0 || items.GetRowOrDefault(received) is not { } item) continue;
                var (param, grade) = materia.TryGetValue(item.RowId, out var mg) ? mg : (0u, -1);
                stock.Add(new CosmicShopItem(shopId, at, item.RowId, item.Name.ExtractText(), Math.Max(1, (int)count), (int)price, item.Icon, param, grade));
            }
        }
        Shop = stock;
        // Crafting (70 craftsmanship, 71 control, 11 CP) and gathering (72 gathering, 73 perception, 10 GP) materia, the top grade of each.
        var stats = new uint[] { 70, 71, 11, 72, 73, 10 };
        TopMateria = stats.Select(p => stock.Where(s => s.MateriaParam == p).MaxBy(s => s.MateriaGrade)).OfType<CosmicShopItem>().ToList();
    }
}
