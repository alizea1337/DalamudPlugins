using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Provenance.Core.Catalog;
using Provenance.Core.Character;
using Provenance.Core.Deliveries;
using Provenance.Core.Planning;
using Provenance.Core.Run;
using Provenance.Data;
using Provenance.Integrations;
using Provenance.Run.Gathering;
using Deliver = Provenance.Integrations.Deliveries;

namespace Provenance.Run.Deliveries;

/// <summary>
/// The custom-delivery run: plan the week's deliveries, make what isn't in the bags yet (components bought next to the
/// client, crafted, gathered), then one tour through the clients handing everything in. A rank-up changes a client's
/// requests, so after the tour the run plans again while deliveries are left. Ticked on the framework thread.
/// </summary>
internal sealed unsafe class DeliveryRun
{
    private enum Step { Idle, Planning, Buy, Craft, Gather, Tour, Finished }
    private enum Visit { Travel, SwitchJob, Talk, Menu, Pick, Fill, After, Leave }

    /// <summary>Rounds of plan, make and tour (each after rank-ups changed what the clients ask for).</summary>
    private const int MaxRounds = 3;

    private readonly Plugin plugin;
    private readonly Crafter crafter;
    private readonly Gatherer gatherer;
    private readonly Shopper shopper;
    /// <summary>Clients stand in houses and under roofs: the buildings rules are on.</summary>
    private readonly Travel travel = new() { RoofAware = true };
    private readonly List<(DateTime At, string Text, bool Problem)> log = new();
    private readonly Dictionary<uint, int> aim = new();
    private readonly Dictionary<uint, int> want = new();

    private Step step = Step.Idle;
    private DateTime waitUntil;
    private DateTime deadline;
    private int tries;
    private long startGil;
    private int round;
    private int deliveredThisRound;
    private string? stopReason;
    private DeliveryPlan plan = DeliveryPlan.Empty;
    private DeliveryGoal goal;
    private List<Purchase> purchases = new();
    private List<CraftJob> crafts = new();
    private List<GatherTarget> gathers = new();
    private HashSet<uint> deliveryItems = new();

    // The tour.
    private List<(PlannedDelivery Delivery, int Count)> tour = new();
    private int tourIndex;
    private Visit visit;
    private int deliveredHere;
    private int usedBefore;
    private int rankBefore;
    /// <summary>Paused right now for a rank-up cutscene (Resume carries on in the hand-over's aftermath).</summary>
    private bool cutscenePaused;
    /// <summary>This hand-over already paused for its cutscene once.</summary>
    private bool cutsceneHandled;
    /// <summary>A rank-up was counted: wait out its cutscene, rewards and talk before going on.</summary>
    private bool rankedUp;
    private DateTime rankedAt;
    /// <summary>This hand-over's rank-up rewards were accepted.</summary>
    private bool rewardsAccepted;
    /// <summary>When the rewards window came up (accepted a moment later, as a person would).</summary>
    private DateTime rewardsSeen;
    /// <summary>This hand-over's cutscene was noted in the debug log.</summary>
    private bool cutsceneSeen;
    private DateTime calmSince;
    /// <summary>When the hand-over was sent: its aftermath gets a few minutes at most, whatever plays.</summary>
    private DateTime handedAt;
    /// <summary>Leaving a client: windows close once the game is between script steps.</summary>
    private DateTime leaveBy;

    public DeliveryRun(Plugin plugin)
    {
        this.plugin = plugin;
        crafter = new Crafter(plugin, Log);
        gatherer = new Gatherer(plugin, Log) { FallbackCollectability = id => aim.GetValueOrDefault(id) };
        shopper = new Shopper(plugin, Log) { GilBudget = GilLeft };
    }

    public bool Active => step is not (Step.Idle or Step.Finished);
    public bool Working => Active && !Paused;
    public bool Paused { get; private set; }
    public bool Pausing { get; private set; }
    public string Status { get; private set; } = "";
    public string? Problem { get; private set; }
    public IReadOnlyList<(DateTime At, string Text, bool Problem)> Lines => log;
    /// <summary>Deliveries handed in since the run started.</summary>
    public int Delivered { get; private set; }
    /// <summary>Deliveries the current round plans.</summary>
    public int Planned => plan.Count;
    /// <summary>Deliveries handed in this round.</summary>
    public int DeliveredThisRound => deliveredThisRound;
    /// <summary>
    /// Skip cutscenes now: the run is on its tour and isn't set to pause for them. The whole tour, not only right after
    /// a hand-over: the game counts a rank-up before its cutscene starts.
    /// </summary>
    public bool SkipCutscenesNow => Working && step == Step.Tour && !Settings.PauseForCutscenes;

    /// <summary>The client being visited on the tour, when there is one.</summary>
    public DeliveryClient? Visiting => step == Step.Tour && tourIndex < tour.Count ? tour[tourIndex].Delivery.Client : null;

    private DeliverySettings Settings => plugin.Config.Deliveries;
    private GameCatalog? Catalog => plugin.Plans.Catalog;

    // ------------------------------------------------------------------ start, stop, pause

    /// <summary>Starts the run; a reason when it can't.</summary>
    public string? Start()
    {
        if (Active) return null;
        if (plugin.Runs.Active) return "An order run is going; stop it first.";
        if (plugin.CosmicLoop.Active) return "The Cosmic Exploration loop is going; stop it first.";
        if (plugin.TribeLoop.Active) return "The allied society run is going; stop it first.";
        if (OtherPlugins.StartBlocker(lookFirst: true) is { } inTheWay) return inTheWay;
        if (!Services.ClientState.IsLoggedIn || Services.Objects.LocalPlayer == null) return "Log in first.";
        if (!Vnav.Available) return "vnavmesh isn't loaded; it walks to the clients, the vendors and the nodes.";
        if (Catalog?.Deliveries is not { Clients.Count: > 0 }) return "Still reading the game data.";
        if (!Deliver.Loaded) return "The game hasn't sent your delivery data yet: open its client list once (Timers, then Custom Deliveries).";
        if (Deliver.Remaining == 0) return "No deliveries left this week.";
        if (Raphael.ExecutablePath() == null) return "Provenance's crafting solver (raphael-cli.exe) is missing next to it. Reinstalling Provenance brings it back.";

        plugin.Config.LastLoop = "deliveries";
        plugin.Config.Save();
        log.Clear();
        Problem = null;
        Delivered = 0;
        round = 0;
        Paused = Pausing = false;
        stopReason = null;
        startGil = GameState.BagCount(1);
        Travel.AdvancedUnstuck = plugin.Config.Security.AdvancedUnstuck;
        Travel.StuckAfter = TimeSpan.FromSeconds(Math.Clamp(plugin.Config.Security.StuckSeconds, 3, 30));
        DebugLog.BeginSession("deliveries");
        DebugLog.Write("deliveries", $"goal {Settings.Goal?.ToString() ?? "automatic"}, left out [{string.Join(",", Settings.LeftOut)}], prefer craft/gather {Settings.PreferCraftGather}, "
                                     + $"anyway {Settings.DeliverAnywayWhileRanking}, pause for cutscenes {Settings.PauseForCutscenes}, consumables {Settings.UseConsumables}, "
                                     + $"{Deliver.Remaining} left, seed {Deliver.Seed}, cutscene skipper hooked {SkipperText()}");
        Log("Started.", false);
        BeginRound();
        return null;
    }

    /// <summary>Stops: right away, except mid-craft, where the item under way is finished first.</summary>
    public void Stop(string reason = "Stopped.", bool problem = false)
    {
        if (!Active) return;
        if (step == Step.Craft && crafter.MidCraft && !problem && !Paused)
        {
            stopReason = reason;
            crafter.RequestPause();
            Status = "Stopping after this craft";
            return;
        }
        Finish(reason, problem);
    }

    /// <summary>Pauses after the item being crafted, otherwise right away.</summary>
    public void Pause()
    {
        if (!Active || Paused || Pausing) return;
        if (step == Step.Craft && crafter.Started && !crafter.Finished)
        {
            crafter.RequestPause();
            Pausing = true;
            Status = "Pausing after this craft";
            Log("Pause asked for; finishing the item being crafted first.", false);
            return;
        }
        if (step == Step.Tour && visit is Visit.Fill or Visit.After or Visit.Leave)
        {
            Pausing = true;
            Status = "Pausing after this delivery";
            Log("Pause asked for; finishing the delivery under way first.", false);
            return;
        }
        PauseNow();
    }

    private void PauseNow()
    {
        CancelWorkers();
        DeliveryWindow.HoldYesAlready(false);
        cutscenePaused = false;
        Pausing = false;
        Paused = true;
        Status = "Paused";
        Log("Paused.", false);
    }

    /// <summary>Carries on: a craft where it paused; anything else is planned again from what's in the bags now.</summary>
    public void Resume()
    {
        if (!Active || !(Paused || Pausing)) return;
        var was = Paused;
        var afterCutscene = cutscenePaused;
        cutscenePaused = false;
        Paused = Pausing = false;
        Log("Resumed.", false);
        // After the cutscene pause: carry on with the hand-over's aftermath (rewards, talk), nothing was cancelled.
        if (afterCutscene) return;
        if (step == Step.Craft) { crafter.Resume(); return; }
        if (!was) return;
        round--;
        BeginRound();
    }

    private void CancelWorkers()
    {
        travel.Cancel();
        shopper.Cancel();
        if (step == Step.Gather) { gatherer.Pause(); gatherer.Stop(); }
        if (step == Step.Tour && !DeliveryWindow.SceneBusy)
        {
            Interact.Close("Request");
            Interact.Close("SatisfactionSupply");
            Interact.Close("SelectString");
        }
    }

    private void Finish(string reason, bool problem)
    {
        Paused = Pausing = false;
        crafter.Release("the run ended");
        CancelWorkers();
        gatherer.Pause();
        gatherer.Stop();
        DeliveryWindow.HoldYesAlready(false);
        if (problem) Problem = reason;
        Log(reason, problem);
        Status = problem ? "Stopped: " + reason : reason;
        step = Step.Finished;
    }

    public void Tick()
    {
        if (!Active || Paused) return;
        if (stopReason != null || Pausing && step == Step.Craft)
        {
            crafter.Tick();
            if (!(crafter.IsPaused || crafter.Finished)) return;
            if (stopReason is { } reason) { stopReason = null; Finish(reason, false); return; }
            Pausing = false;
            Paused = true;
            Status = "Paused";
            return;
        }
        // A pause asked for mid-delivery lands once the hand-over is over.
        if (Pausing && !(step == Step.Tour && visit is Visit.Fill or Visit.After or Visit.Leave)) { PauseNow(); return; }
        try
        {
            // The crafter and the gatherer need every frame.
            if (step == Step.Craft) { TickCraft(); return; }
            if (step == Step.Gather) { TickGather(); return; }
            if (DateTime.UtcNow < waitUntil) return;
            switch (step)
            {
                case Step.Planning: TickPlanning(); break;
                case Step.Buy: TickBuy(); break;
                case Step.Tour: TickTour(); break;
            }
        }
        catch (Exception e)
        {
            Services.Log.Error(e, "Custom deliveries tick failed");
            Finish("Something went wrong: " + e.Message, true);
        }
    }

    // ------------------------------------------------------------------ planning and making

    private void BeginRound()
    {
        round++;
        deliveredThisRound = 0;
        plan = DeliveryPlan.Empty;
        Go(Step.Planning);
        deadline = DateTime.UtcNow.AddSeconds(Data.CosmicPlanner.ThinkSeconds);
        Status = "Working out the week's deliveries";
    }

    private void TickPlanning()
    {
        var planning = plugin.DeliveryPlanning;
        plan = planning.Build();
        if (planning.Pending > 0 && DateTime.UtcNow < deadline)
        {
            Status = $"Working out what the gear reaches ({planning.Pending} left)";
            Wait(1);
            return;
        }
        goal = planning.Goal(planning.Weeks());
        foreach (var (client, why) in plan.Skipped) DebugLog.Write("deliveries", $"skip {client.Name}: {why}");
        if (plan.Count == 0)
        {
            var reasons = plan.Skipped.Select(s => $"{s.Client.Name}: {s.Why}").Take(4).ToList();
            Finish(round == 1
                ? "Nothing to deliver" + (reasons.Count > 0 ? " (" + string.Join("; ", reasons) + ")." : ".")
                : $"Done: {Delivered} {(Delivered == 1 ? "delivery" : "deliveries")}.", false);
            return;
        }
        Log($"Round {round}, goal {GoalName(goal)}: " + string.Join(", ", plan.Deliveries.Select(d => $"{d.Count}x {Catalog!.ItemName(d.Request.Item.ItemId)} for {d.Client.Name}")) + ".", false);
        PrepareMaking();
    }

    /// <summary>What's missing for the plan: components to buy, crafts, gathering; items already in the bags count.</summary>
    private void PrepareMaking()
    {
        var catalog = Catalog!;
        purchases = new List<Purchase>();
        crafts = new List<CraftJob>();
        gathers = new List<GatherTarget>();
        aim.Clear();
        want.Clear();
        var crystalsTaken = new Dictionary<uint, int>();
        deliveryItems = plan.Deliveries.Select(d => d.Request.Item.ItemId).ToHashSet();
        foreach (var d in plan.Deliveries)
        {
            var item = d.Request.Item;
            var need = Math.Max(0, d.Count - Usable(item));
            want[item.ItemId] = d.Count;
            if (need == 0) continue;
            if (d.Kind == DeliveryKind.Craft)
            {
                if (DeliveryPlanning.Recipe(catalog, d.Client, item.ItemId) is not { } recipe) { Log($"No crafter can make {catalog.ItemName(item.ItemId)} now.", true); continue; }
                // Crystals from the bags limit how many can be made.
                var possible = need;
                foreach (var ing in recipe.Ingredients.Where(i => Items.IsCrystal(i.ItemId)))
                    possible = Math.Min(possible, Math.Max(0, GameState.BagCount(ing.ItemId) - crystalsTaken.GetValueOrDefault(ing.ItemId)) / Math.Max(1, ing.Amount));
                if (possible < need) Log($"Crystals for only {possible} of {need} {catalog.ItemName(item.ItemId)}.", true);
                if (possible <= 0) continue;
                foreach (var ing in recipe.Ingredients.Where(i => Items.IsCrystal(i.ItemId)))
                    crystalsTaken[ing.ItemId] = crystalsTaken.GetValueOrDefault(ing.ItemId) + ing.Amount * possible;
                foreach (var ing in recipe.Ingredients.Where(i => !Items.IsCrystal(i.ItemId)))
                {
                    var missing = ing.Amount * possible - GameState.BagCount(ing.ItemId);
                    if (missing > 0 && Offer(catalog, d.Client, ing.ItemId) is { } offer) purchases.Add(new Purchase(ing.ItemId, missing, offer));
                    else if (missing > 0) Log($"No vendor known for {catalog.ItemName(ing.ItemId)}.", true);
                }
                var useFood = goal != DeliveryGoal.RankUp && Settings.UseConsumables;
                crafts.Add(new CraftJob(recipe, possible, false, d.Collectability * 10, UseConsumables: useFood));
            }
            else if (d.Kind == DeliveryKind.Gather)
            {
                aim[item.ItemId] = d.Collectability;
                gathers.Add(new GatherTarget(item.ItemId, Supply.Gather, need, 0));
            }
        }
        if (purchases.Count > 0) { shopper.Begin(purchases); Go(Step.Buy); Status = "Buying components"; }
        else NextAfterBuying();
    }

    /// <summary>The vendor of a component next to the client: from the vendor index when it knows one in the client's zone, else the known vendor.</summary>
    private static VendorOffer? Offer(GameCatalog catalog, DeliveryClient client, uint itemId)
    {
        var known = catalog.Vendors.For(itemId);
        if (known.FirstOrDefault(o => o.TerritoryId == client.Territory) is { } here) return here;
        if (!DeliveryPlanning.Vendors.TryGetValue(client.Id, out var npc)) return known.FirstOrDefault();
        var name = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.ENpcResident>().GetRowOrDefault(npc)?.Singular.ExtractText() ?? "the vendor";
        var price = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(itemId)?.PriceMid ?? 0;
        // The vendor stands next to the client: walk there, then to the vendor once in sight.
        return new VendorOffer(npc, name, 0, client.Territory, new Vector2(client.Position.X, client.Position.Z), price, Array.Empty<string>());
    }

    private void TickBuy()
    {
        shopper.Tick();
        Status = shopper.Status;
        if (!shopper.Finished) return;
        if (shopper.Failure is { } why) Log("Buying: " + why, true);
        NextAfterBuying();
    }

    private void NextAfterBuying()
    {
        if (crafts.Count > 0)
        {
            crafter.Begin(crafts);
            Go(Step.Craft);
            Status = "Crafting";
            return;
        }
        NextAfterCrafting();
    }

    private void TickCraft()
    {
        crafter.Tick();
        Status = crafter.Status;
        if (!crafter.Finished) return;
        if (crafter.Failure is { } why) Log("Crafting: " + why, true);
        foreach (var h in crafter.History.Where(h => h.Skipped != null)) Log($"Not crafted: {Catalog!.ItemName(h.Job.Recipe.ItemId)} ({h.Skipped}).", true);
        Interact.Close("RecipeNote");
        NextAfterCrafting();
    }

    private void NextAfterCrafting()
    {
        if (gathers.Count > 0)
        {
            gatherer.Begin(gathers, t => Math.Max(0, want.GetValueOrDefault(t.ItemId) - UsableById(t.ItemId)), () => deliveryItems);
            Go(Step.Gather);
            Status = "Gathering";
            return;
        }
        StartTour();
    }

    private void TickGather()
    {
        gatherer.Tick();
        Status = gatherer.Status;
        if (gatherer.Failure is { } why)
        {
            Log("Gathering: " + why, true);
            gatherer.Pause();
            gatherer.Stop();
            StartTour();
            return;
        }
        var done = gathers.All(t => UsableById(t.ItemId) >= want.GetValueOrDefault(t.ItemId));
        if (!done || gatherer.OnNode || gatherer.UpkeepBusy || Services.Condition[ConditionFlag.Gathering]) return;
        gatherer.Pause();
        gatherer.Stop();
        StartTour();
    }

    // ------------------------------------------------------------------ the tour

    private void StartTour()
    {
        var here = Services.ClientState.TerritoryType;
        tour = plan.Deliveries
            .Select(d => (Delivery: d, Count: Math.Min(d.Count, Usable(d.Request.Item))))
            .Where(x =>
            {
                if (x.Count > 0) return true;
                Log($"Nothing to hand {x.Delivery.Client.Name}: no {Catalog!.ItemName(x.Delivery.Request.Item.ItemId)} with collectability {x.Delivery.Request.Item.Low} or more.", true);
                return false;
            })
            // The zone you're in first, then zone by zone (the two in Idyllshire, the two in the Firmament together).
            .OrderBy(x => x.Delivery.Client.Territory == here ? 0 : 1)
            .ThenBy(x => x.Delivery.Client.Territory)
            .ThenBy(x => x.Delivery.Client.Id)
            .ToList();
        tourIndex = 0;
        if (tour.Count == 0) { EndRound(); return; }
        DeliveryWindow.HoldYesAlready(true);
        Go(Step.Tour);
        BeginVisit();
    }

    private void BeginVisit()
    {
        if (tourIndex >= tour.Count) { EndRound(); return; }
        var (d, count) = tour[tourIndex];
        deliveredHere = 0;
        tries = 0;
        Log($"To {d.Client.Name}: {count}x {Catalog!.ItemName(d.Request.Item.ItemId)}.", false);
        travel.GoTo(d.Client.Name, d.Client.Territory, new Vector2(d.Client.Position.X, d.Client.Position.Z), 3.5f,
            () => Interact.Nearest(ObjectKind.EventNpc, id => id == d.Client.NpcId)?.Position);
        visit = Visit.Travel;
    }

    /// <summary>Done with this client: close its windows once the game is between script steps, then the next.</summary>
    private void NextVisit()
    {
        visit = Visit.Leave;
        leaveBy = DateTime.UtcNow.AddSeconds(30);
        Wait(0.3);
    }

    /// <summary>
    /// Leaving a client the way a player does: the hand-over and the delivery window closed, then "Nothing" in the
    /// client's menu, so the conversation ends (closing windows alone leaves the game counting you as busy for a while,
    /// and the next teleport is refused).
    /// </summary>
    private void TickLeave()
    {
        if (Interact.SkipTalk()) { Wait(0.3); return; }
        var timedOut = DateTime.UtcNow > leaveBy;
        if (!timedOut && (DeliveryWindow.SceneBusy || Interact.Ready("SelectYesno") != null)) { Wait(0.3); return; }
        if (Interact.Ready("Request") != null) { Interact.Close("Request"); Wait(0.5); return; }
        if (Interact.Ready("SatisfactionSupply") != null) { Interact.Close("SatisfactionSupply"); Wait(0.8); return; }
        if (Interact.Menu() is { Count: > 0 } menu)
        {
            // The client's menu: its last entry is "Nothing."
            menu[^1].Select();
            Wait(0.8);
            return;
        }
        if (!timedOut && (Services.Condition[ConditionFlag.OccupiedInEvent] || Services.Condition[ConditionFlag.OccupiedInQuestEvent]))
        {
            Wait(0.3);
            return;
        }
        tourIndex++;
        Wait(0.5);
        BeginVisit();
    }

    private void FailVisit(string why)
    {
        var d = tour[tourIndex].Delivery;
        Log($"{d.Client.Name}: {why}", true);
        NextVisit();
    }

    private void EndRound()
    {
        DeliveryWindow.HoldYesAlready(false);
        if (deliveredThisRound > 0 && round < MaxRounds && Deliver.Remaining > 0) { BeginRound(); return; }
        Finish($"Done: {Delivered} {(Delivered == 1 ? "delivery" : "deliveries")}" + (Deliver.Remaining > 0 ? $", {Deliver.Remaining} left this week." : "."), false);
    }

    private void TickTour()
    {
        if (tourIndex >= tour.Count) { EndRound(); return; }
        var (d, count) = tour[tourIndex];
        var client = d.Client;
        if (TickRankUpScene(client)) return;
        switch (visit)
        {
            case Visit.Travel:
                travel.Tick();
                Status = travel.Status;
                if (travel.Current == Travel.State.Failed) { FailVisit("couldn't get there: " + travel.Failure); return; }
                if (travel.Current != Travel.State.Arrived) return;
                visit = Visit.SwitchJob;
                return;

            case Visit.SwitchJob:
            {
                // Deliver as a crafter or gatherer at the client's level: the class that made the item, if need be.
                var job = GameState.CurrentJob;
                if ((ClassJobs.IsCrafter(job) || ClassJobs.IsGatherer(job)) && PlanService.JobLevel(job) >= client.LevelUnlock) { visit = Visit.Talk; return; }
                var set = GameState.BestGearset(d.ClassJob);
                if (set < 0 || ++tries > 3) { FailVisit($"no gearset to deliver as {ClassJobs.Abbreviation(d.ClassJob)}."); return; }
                GameState.EquipGearset(set);
                Wait(1.5);
                return;
            }

            case Visit.Talk:
                Status = $"Talking to {client.Name}";
                if (DeliveryWindow.SupplyReady(client.Id)) { visit = Visit.Pick; return; }
                if (Interact.Menu() is { Count: > 0 }) { visit = Visit.Menu; return; }
                if (Interact.SkipTalk()) { Wait(0.3); return; }
                if (DeliveryWindow.SceneBusy) { Wait(0.3); return; }
                if (++tries > 6) { FailVisit("the delivery window didn't open."); return; }
                if (Interact.Nearest(ObjectKind.EventNpc, id => id == client.NpcId) is not { } npc) { FailVisit("couldn't find them here."); return; }
                Interact.With(npc);
                Wait(1.2);
                return;

            case Visit.Menu:
            {
                if (DeliveryWindow.SupplyReady(client.Id)) { visit = Visit.Pick; return; }
                if (DeliveryWindow.SceneBusy) { Wait(0.2); return; }
                var menu = Interact.Menu();
                if (menu is not { Count: > 0 }) { visit = Visit.Talk; Wait(0.5); return; }
                // "Make a delivery (N remaining)." comes first for every client; it alone carries the count.
                if (!menu[0].Text.Any(char.IsDigit))
                {
                    DebugLog.Write("deliveries", $"{client.Name}'s menu: {string.Join(" / ", menu.Select(e => e.Text.Trim()))}");
                    FailVisit("their menu has no delivery entry (a story quest or the weekly limit?).");
                    return;
                }
                menu[0].Select();
                visit = Visit.Talk;
                Wait(1);
                return;
            }

            case Visit.Pick:
            {
                if (!DeliveryWindow.SupplyReady(client.Id))
                {
                    if (Interact.SkipTalk()) { Wait(0.3); return; }
                    if (++tries > 30) FailVisit("the delivery window closed."); else Wait(0.3);
                    return;
                }
                var slot = (int)d.Kind - 1;
                if (Deliver.Window() is { } window && window.Items[slot] != d.Request.Item.ItemId)
                {
                    FailVisit($"the window asks for item {window.Items[slot]} instead of {Catalog!.ItemName(d.Request.Item.ItemId)}.");
                    return;
                }
                usedBefore = Deliver.Used(client.Id);
                rankBefore = Deliver.Rank(client.Id);
                rankedUp = false;
                cutsceneHandled = false;
                cutsceneSeen = false;
                rewardsAccepted = false;
                rewardsSeen = default;
                calmSince = default;
                if (!DeliveryWindow.PickRequest(slot)) { Wait(0.3); return; }
                tries = 0;
                deadline = DateTime.UtcNow.AddSeconds(15);
                visit = Visit.Fill;
                Wait(0.6);
                return;
            }

            case Visit.Fill:
                Status = $"Handing {Catalog!.ItemName(d.Request.Item.ItemId)} to {client.Name}";
                if (DeliveryWindow.CanHandOver)
                {
                    DeliveryWindow.HandOver();
                    handedAt = DateTime.UtcNow;
                    deadline = DateTime.UtcNow.AddSeconds(30);
                    visit = Visit.After;
                    Wait(0.5);
                    return;
                }
                if (DeliveryWindow.RequestOpen && !DeliveryWindow.Fill(d.Request.Item.ItemId, d.Request.Item.Low, goal != DeliveryGoal.RankUp, out var noneFits) && noneFits)
                {
                    FailVisit($"no {Catalog!.ItemName(d.Request.Item.ItemId)} with collectability {d.Request.Item.Low} or more in the bags.");
                    return;
                }
                if (DateTime.UtcNow > deadline) { FailVisit("the hand-over window didn't take the item."); return; }
                Wait(0.4);
                return;

            case Visit.After:
                TickAfter(d, count);
                return;

            case Visit.Leave:
                TickLeave();
                return;
        }
    }

    /// <summary>
    /// What a rank-up brings, at any point of the tour (the game counts the delivery before its cutscene starts): the
    /// rewards window is accepted and the "Skip cutscene?" question answered; while a cutscene plays, the run doesn't
    /// travel or leave. True when it took the tick.
    /// </summary>
    private bool TickRankUpScene(DeliveryClient client)
    {
        if (DeliveryWindow.RankUpOpen)
        {
            if (rewardsSeen == default)
            {
                rewardsSeen = DateTime.UtcNow;
                DebugLog.Write("deliveries", $"{client.Name}'s rewards window ({visit}): accept button ready {DeliveryWindow.RankUpButtonReady}, scene busy {DeliveryWindow.SceneBusy}");
            }
            Status = $"Accepting {client.Name}'s rewards";
            var open = DateTime.UtcNow - rewardsSeen;
            if (open < TimeSpan.FromSeconds(0.8)) { Wait(0.3); return true; }
            // The button, as a click; a few seconds without one it can press, the window's confirm.
            if (DeliveryWindow.AcceptRankUp(open > TimeSpan.FromSeconds(4), out var how))
            {
                DebugLog.Write("deliveries", $"accepted {client.Name}'s rewards ({how})");
                if (!rewardsAccepted) Log($"Accepted {client.Name}'s level-up rewards.", false);
                rewardsAccepted = true;
                Wait(1.5);
                return true;
            }
            Wait(0.3);
            return true;
        }
        rewardsSeen = default;

        var inCutscene = Services.Condition[ConditionFlag.OccupiedInCutSceneEvent] || Services.Condition[ConditionFlag.WatchingCutscene]
                         || Services.Condition[ConditionFlag.WatchingCutscene78];
        if (!inCutscene) return false;
        if (!cutsceneSeen)
        {
            cutsceneSeen = true;
            DebugLog.Write("deliveries", $"{client.Name}'s cutscene ({visit}): skip {SkipCutscenesNow}, cutscene skipper hooked {SkipperText()}");
        }
        // The skipped cutscene asks "Skip cutscene?" (and a rank-up scene can ask a question of its own): the first answer.
        if (!Settings.PauseForCutscenes && Interact.Menu() is { Count: >= 2 } prompt)
        {
            DebugLog.Write("deliveries", $"cutscene question: [{string.Join(" / ", prompt.Select(e => e.Text.Trim()))}] -> \"{prompt[0].Text.Trim()}\"");
            if (!cutsceneHandled) Log($"Skipped {client.Name}'s cutscene.", false);
            cutsceneHandled = true;
            prompt[0].Select();
            Wait(1);
            return true;
        }
        // Not on the move while it plays (a teleport is refused, and leaving would answer the scene's menus). The
        // hand-over steps go on as before: the aftermath may pause for it.
        if (visit is not (Visit.Travel or Visit.SwitchJob or Visit.Leave)) return false;
        Status = $"{client.Name}'s cutscene";
        Wait(0.5);
        return true;
    }

    private static string SkipperText() => DeliveryWindow.CutsceneSkipperHooked?.ToString() ?? "unknown";

    /// <summary>After the hand-over: the scrip-cap question, a rank-up (cutscene, rewards), the client's thanks; done once the game counts it.</summary>
    private void TickAfter(PlannedDelivery d, int count)
    {
        var client = d.Client;
        if (DeliveryWindow.SceneBusy) { Wait(0.2); return; }
        if (Interact.Ready("SelectYesno") != null)
        {
            // "Unable to receive the following items... Proceed?": the scrips would go over the cap.
            var anyway = goal == DeliveryGoal.RankUp && Settings.DeliverAnywayWhileRanking && rankBefore < DeliveryClient.TopLevel;
            if (anyway) { Interact.Yes(); Log($"{client.Name}: delivered past the scrip cap (ranking up).", false); }
            else { Interact.No(); FailVisit("the scrips would go over the cap, so no more for them."); Wait(1); return; }
            Wait(1);
            return;
        }
        if (Interact.SkipTalk()) { Wait(0.3); return; }
        // Whatever plays, the aftermath of one hand-over gets a few minutes at most.
        if (DateTime.UtcNow - handedAt > TimeSpan.FromMinutes(4)) { FailVisit("the delivery's aftermath didn't end."); return; }

        var watching = Services.Condition[ConditionFlag.WatchingCutscene] || Services.Condition[ConditionFlag.WatchingCutscene78];
        var cutscene = watching || Services.Condition[ConditionFlag.OccupiedInCutSceneEvent];
        if (cutscene)
        {
            // Pausing is for the rank-up cutscene only (the flags can come up around an ordinary hand-in too).
            var rankUp = watching || Deliver.Rank(client.Id) != rankBefore;
            if (Settings.PauseForCutscenes && rankUp && !cutsceneHandled)
            {
                cutsceneHandled = true;
                cutscenePaused = true;
                Paused = true;
                Status = $"Paused for {client.Name}'s cutscene; Resume carries on";
                Log($"{client.Name} reached a new level: paused for the cutscene.", false);
                return;
            }
            Status = $"{client.Name}'s cutscene";
            Wait(0.5);
            return;
        }

        if (rankedUp)
        {
            // The count comes before the cutscene and its rewards: go on once the rewards are accepted (or a good while
            // has passed without a rewards window) and two quiet seconds follow (the delivery window back counts as quiet).
            var rewardsDone = rewardsAccepted || DateTime.UtcNow - rankedAt > TimeSpan.FromSeconds(45);
            var calm = rewardsDone && !Interact.IsOpen("SatisfactionSupplyResult") && Interact.Ready("Talk") == null
                       && (!Services.Condition[ConditionFlag.OccupiedInEvent] || DeliveryWindow.SupplyReady(client.Id));
            if (!calm) { calmSince = default; }
            else if (calmSince == default) calmSince = DateTime.UtcNow;
            else if (DateTime.UtcNow - calmSince > TimeSpan.FromSeconds(2)) { NextVisit(); return; }
            if (DateTime.UtcNow > deadline) { NextVisit(); return; }
            Wait(0.3);
            return;
        }

        if (Deliver.Used(client.Id) > usedBefore)
        {
            Delivered++;
            deliveredThisRound++;
            deliveredHere++;
            Log($"Delivered {Catalog!.ItemName(d.Request.Item.ItemId)} to {client.Name} ({deliveredHere} of {count}).", false);
            if (Deliver.Rank(client.Id) != rankBefore)
            {
                Log($"{client.Name} reached level {Deliver.Rank(client.Id)}: their requests change, so the run plans again after this round.", false);
                rankedUp = true;
                rankedAt = DateTime.UtcNow;
                calmSince = default;
                deadline = DateTime.UtcNow.AddSeconds(90);
                Wait(1);
                return;
            }
            if (deliveredHere < count && Deliver.Remaining > 0 && Usable(d.Request.Item) > 0)
            {
                tries = 0;
                visit = Visit.Talk;
                Wait(0.8);
                return;
            }
            NextVisit();
            return;
        }
        if (DateTime.UtcNow > deadline) { FailVisit("the game didn't count the delivery."); return; }
        Wait(0.3);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Collectables of the item in the bags the client takes (at least the lowest tier).</summary>
    private static int Usable(DeliveryCandidate item) => GameState.Collectables(item.ItemId).Count(c => c >= item.Low);

    private int UsableById(uint itemId)
        => plan.Deliveries.FirstOrDefault(d => d.Request.Item.ItemId == itemId) is { } d ? Usable(d.Request.Item) : 0;

    private long? GilLeft()
        => plugin.Config.Security.MaxGilPerRun <= 0 ? null : Math.Max(0, plugin.Config.Security.MaxGilPerRun - (startGil - GameState.BagCount(1)));

    public static string GoalName(DeliveryGoal goal) => goal switch
    {
        DeliveryGoal.RankUp => "Rank up",
        DeliveryGoal.Scrips => "Scrips",
        _ => "Gil",
    };

    private void Log(string text, bool problem)
    {
        log.Add((DateTime.Now, text, problem));
        if (log.Count > 200) log.RemoveAt(0);
        Services.Log.Information("[Deliveries] {Text}", text);
        DebugLog.Write("deliveries", (problem ? "[!] " : "") + text);
    }

    private void Go(Step next)
    {
        if (next != step)
        {
            tries = 0;
            DebugLog.Write("deliveries", $"step {step} -> {next}; delivered {Delivered}, {Deliver.Remaining} left this week, job {GameState.CurrentJob}");
        }
        step = next;
    }

    private void Wait(double seconds) => waitUntil = DateTime.UtcNow.AddSeconds(seconds);
}
