using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Catalog;
using Provenance.Core.Crafting;
using Provenance.Core.Deliveries;
using Provenance.Integrations;
using Deliver = Provenance.Integrations.Deliveries;

namespace Provenance.Data;

/// <summary>
/// The week's custom-delivery plan from the game's state and the settings: which client gets which request how often,
/// at which tier, made by which class (crafted requests judged by Raphael with the class's stats, like the Cosmic
/// plan), and why the others get none. Rebuilt at most once a second for the page.
/// </summary>
internal sealed class DeliveryPlanning
{
    private readonly Plugin plugin;
    private DeliveryPlan cached = DeliveryPlan.Empty;
    private DateTime builtAt;

    public DeliveryPlanning(Plugin plugin) => this.plugin = plugin;

    /// <summary>Crafted requests still being worked out by Raphael in the last plan.</summary>
    public int Pending { get; private set; }

    /// <summary>Each client's crafting-component vendor (ENpc), next to the client (game data: the GilShop selling the recipe's component).</summary>
    public static readonly IReadOnlyDictionary<uint, uint> Vendors = new Dictionary<uint, uint>
    {
        [1] = 1017188, [2] = 1022232, [3] = 1019175, [4] = 1017188, [5] = 1027545, [6] = 1033785,
        [7] = 1035213, [8] = 1037054, [9] = 1044548, [10] = 1046406, [11] = 1048606, [12] = 1058168,
    };

    private DeliverySettings Settings => plugin.Config.Deliveries;

    /// <summary>
    /// The goal in effect: the one picked, else "Rank up" while a client that takes deliveries is below the top level,
    /// else "Scrips". "Rank up" picked with every client at the top level has nothing to rank up: Scrips then.
    /// </summary>
    public DeliveryGoal Goal(IReadOnlyList<ClientWeek> weeks)
    {
        var canRank = CanRankUp(weeks);
        if (Settings.Goal is { } picked && (picked != DeliveryGoal.RankUp || canRank)) return picked;
        return canRank ? DeliveryGoal.RankUp : DeliveryGoal.Scrips;
    }

    /// <summary>A client that takes deliveries is below the top level: "Rank up" has something to do.</summary>
    public static bool CanRankUp(IReadOnlyList<ClientWeek> weeks)
        => weeks.Any(w => w.Rank is > 0 and < DeliveryClient.TopLevel && Deliver.QuestDone(w.Client.UnlockQuest));

    /// <summary>The plan, rebuilt at most once a second.</summary>
    public DeliveryPlan Current
    {
        get
        {
            if (DateTime.UtcNow - builtAt < TimeSpan.FromSeconds(1)) return cached;
            builtAt = DateTime.UtcNow;
            cached = Build();
            return cached;
        }
    }

    public List<ClientWeek> Weeks()
        => plugin.Plans.Catalog?.Deliveries is { } data ? data.Clients.Select(c => Deliver.Week(data, c)).ToList() : new List<ClientWeek>();

    public DeliveryPlan Build()
    {
        Pending = 0;
        if (plugin.Plans.Catalog is not { } catalog || !Deliver.Loaded) return DeliveryPlan.Empty;
        var weeks = Weeks();
        var goal = Goal(weeks);
        var options = new DeliveryOptions(goal,
            weeks.Select(w => w.Client.Id).Where(id => !Settings.LeftOut.Contains(id)).ToHashSet(),
            new HashSet<DeliveryKind> { DeliveryKind.Craft, DeliveryKind.Gather },
            Settings.PreferCraftGather, Settings.DeliverAnywayWhileRanking);
        var scrips = new Dictionary<byte, int>
        {
            [2] = GameState.CurrencyCount(Deliver.CurrencyItem(2)),
            [4] = GameState.CurrencyCount(Deliver.CurrencyItem(4)),
        };
        var pending = 0;
        var plan = DeliveryPlanner.Plan(weeks, Deliverable, (w, r) =>
        {
            var a = Judge(catalog, w, r, goal);
            if (a.Why == StillWorking) pending++;
            return a;
        }, Deliver.Remaining, options, scrips);
        Pending = pending;
        return plan;
    }

    private const string StillWorking = "still working out what the gear reaches";

    /// <summary>Null when the client takes deliveries now; otherwise why not.</summary>
    public string? Deliverable(ClientWeek week)
    {
        var c = week.Client;
        if (!Deliver.QuestDone(c.UnlockQuest)) return $"locked (quest \"{c.UnlockQuestName}\")";
        if (!Deliver.Known(c.Id) || week.Rank == 0) return "no data from the game";
        if (c.Quests.FirstOrDefault(q => q.Gates && q.Level == week.Rank && !Deliver.QuestDone(q.QuestId)) is { } quest)
            return $"the story quest \"{quest.Name}\" first";
        if (c.Territory == Travel.Firmament && !OnHomeWorld) return "the Firmament is only open on your home world";
        if (BestLevel < c.LevelUnlock) return $"needs a crafter or gatherer at level {c.LevelUnlock}";
        return null;
    }

    private static bool OnHomeWorld => Services.Objects.LocalPlayer is { } me && me.CurrentWorld.RowId == me.HomeWorld.RowId;

    private static int BestLevel => Core.Character.ClassJobs.Crafters.Concat(Core.Character.ClassJobs.Gatherers).Max(j => PlanService.JobLevel(j));

    /// <summary>Whether a request can be made and delivered, by which class, and the best tier reached reliably.</summary>
    public DeliveryAbility Judge(GameCatalog catalog, ClientWeek week, DeliveryRequest request, DeliveryGoal goal)
        => request.Item.Kind switch
        {
            DeliveryKind.Craft => Craft(catalog, week, request, goal),
            DeliveryKind.Gather => Gather(catalog, week, request),
            _ => DeliveryAbility.No("fish come in a later version"),
        };

    /// <summary>The recipe a crafted request is made with: the best-levelled crafter that can make it and deliver it.</summary>
    public static RecipeInfo? Recipe(GameCatalog catalog, DeliveryClient client, uint itemId)
        => catalog.RecipesFor(itemId)
            .Where(r => PlanService.JobLevel(r.Job) >= Math.Max(r.Level, client.LevelUnlock) && GameState.BestGearset(r.Job.ClassJobId()) >= 0)
            .OrderByDescending(r => PlanService.JobLevel(r.Job)).ThenBy(r => r.RecipeId)
            .FirstOrDefault();

    private DeliveryAbility Craft(GameCatalog catalog, ClientWeek week, DeliveryRequest request, DeliveryGoal goal)
    {
        var item = request.Item;
        var recipes = catalog.RecipesFor(item.ItemId);
        if (recipes.Count == 0) return DeliveryAbility.No("no recipe for the crafted request");
        if (Recipe(catalog, week.Client, item.ItemId) is not { } recipe)
        {
            var level = Math.Max(recipes.Min(r => r.Level), week.Client.LevelUnlock);
            return DeliveryAbility.No($"needs a crafter at level {level} with a saved gearset");
        }
        // Crystals come from the bags (the vendor next to the client sells the other component).
        foreach (var ing in recipe.Ingredients.Where(i => Items.IsCrystal(i.ItemId)))
            if (GameState.BagCount(ing.ItemId) < ing.Amount)
                return DeliveryAbility.No($"needs {ing.Amount}x {catalog.ItemName(ing.ItemId)} a craft");

        var classJob = recipe.Job.ClassJobId();
        var stats = plugin.Plans.Profile?.Job(classJob);
        if (stats == null || stats.Source == Core.Character.StatSource.None || stats.Stats.Length < 3 || stats.Level <= 0)
            return DeliveryAbility.No($"{recipe.Job.Abbreviation()}'s stats aren't known yet (Character page)");
        var consumables = goal != DeliveryGoal.RankUp && Settings.UseConsumables ? Consumables() : null;
        var req = new RaphaelRequest(recipe.RecipeId, stats.Level, stats.Stats[0], stats.Stats[1], stats.Stats[2], stats.Manipulation,
            plugin.Config.Crafting.UseSpecialistActions && stats.Specialist, null, null, 0,
            consumables?.Food ?? 0, consumables?.FoodHq ?? false, consumables?.Potion ?? 0, consumables?.PotionHq ?? false);
        var task = plugin.Raphael.Solve(req);
        if (!task.IsCompleted) return new DeliveryAbility(false, 0, classJob, StillWorking);
        if (task.IsFaulted || task.IsCanceled) return DeliveryAbility.No($"no rotation for {catalog.ItemName(item.ItemId)} with {recipe.Job.Abbreviation()}'s gear");
        var solution = task.Result;
        var reached = solution.FinalQuality >= 0 ? solution.FinalQuality / 10 : solution.ReachesMax ? item.High : item.Low;
        var tier = reached >= item.High ? 2 : reached >= item.Mid ? 1 : reached >= item.Low ? 0 : -1;
        if (tier < 0) return DeliveryAbility.No($"{recipe.Job.Abbreviation()}'s gear reaches collectability {reached}, the client takes {item.Low} or more");
        return new DeliveryAbility(true, tier, classJob);
    }

    /// <summary>Crafting food and medicine from the Character page that are in the bags.</summary>
    private (uint Food, bool FoodHq, uint Potion, bool PotionHq)? Consumables()
    {
        var (food, medicine) = plugin.Config.Consumables.For(Core.Character.Discipline.Hand);
        var f = food.ItemId != 0 && GameState.BagCount(food.ItemId) > 0 ? food.ItemId : 0;
        var p = medicine.ItemId != 0 && GameState.BagCount(medicine.ItemId) > 0 ? medicine.ItemId : 0;
        if (f == 0 && p == 0) return null;
        return (f, food.Hq && GameState.BagCount(food.ItemId, true) > 0, p, medicine.Hq && GameState.BagCount(medicine.ItemId, true) > 0);
    }

    /// <summary>Gathered requests: a miner or botanist at the node's level (and the client's), with a gearset, at a node that can be reached.</summary>
    private static DeliveryAbility Gather(GameCatalog catalog, ClientWeek week, DeliveryRequest request)
    {
        var points = catalog.GatherPoints(request.Item.ItemId);
        if (points.Count == 0) return DeliveryAbility.No("no gathering point known for the gathered request");
        var usable = points
            .Where(p => PlanService.JobLevel(p.ClassJobId) >= Math.Max(p.Level, week.Client.LevelUnlock)
                        && GameState.BestGearset(p.ClassJobId) >= 0 && Teleporter.CanReach(p.TerritoryId))
            .OrderByDescending(p => PlanService.JobLevel(p.ClassJobId))
            .FirstOrDefault();
        if (usable == null)
            return DeliveryAbility.No($"needs a miner or botanist at level {Math.Max(points.Min(p => p.Level), week.Client.LevelUnlock)} with a saved gearset");
        // The collectable rotation aims for the top tier the window shows.
        return new DeliveryAbility(true, 2, usable.ClassJobId);
    }
}
