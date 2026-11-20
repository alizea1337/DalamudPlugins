using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.ImGuiNotification;
using ECommons;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Provenance.Core.Catalog;
using Provenance.Core.Estimating;
using Provenance.Core.Planning;
using Provenance.Core.Run;
using Provenance.Data;
using Provenance.Integrations;

namespace Provenance.Run;

internal enum RunPhase
{
    Idle,
    Running,
    /// <summary>Pause asked for; waits for the current node, catch or craft to finish.</summary>
    Pausing,
    Paused,
    /// <summary>Stop asked for during a craft; waits for it to finish so no materials are lost.</summary>
    Stopping,
    Finished,
    Stopped,
}

/// <summary>
/// Learn books, take items out of retainers, buy from vendors, gather and fish (crafting while timed
/// nodes and fish windows are closed), then craft the rest.
/// </summary>
internal enum RunStage { Retainers, Learn, Vendors, Hunt, Gather, Craft, TurnIn, Return, ToSpot }

internal sealed record RunLogEntry(DateTime Time, string Text, bool Problem);

/// <summary>
/// A collectable an item from aetherial reduction comes from, as the Run page shows it: the job that gets it, where,
/// what's going on with it now ("up now", "opens in 12 min, at 16:00 ET", why it can't be had), and how many wait in
/// the bags to be reduced.
/// </summary>
internal sealed record ReductionSource(uint ItemId, uint ClassJobId, string Zone, string State, bool Problem, int Waiting);

/// <summary>
/// Runs an order from start to finish. Books first; then gathering and fishing with Provenance's own
/// gatherer and fisher, one at a time (a fish whose window is open first, then nodes, then fish that
/// always bite; a switch waits for the node or catch under way). When only timed nodes and fish windows
/// are left and none opens for a while, it crafts what already can be crafted, handing back shortly
/// before the next window; then everything left is crafted. Watches the stop rules throughout:
/// knock-outs (with retries), a nearly full inventory, plugins going away, duties.
/// </summary>
internal sealed class RunEngine : IDisposable
{
    /// <summary>The two workers of the gathering stage; only one works at a time (the fisher keeps its ticks while it reels in).</summary>
    private enum Worker { Land, Fish }

    private int MinFreeSlots => plugin.Config.Security.MinFreeSlots;
    private int MaxKnockOuts => plugin.Config.Security.MaxKnockOuts;
    /// <summary>Gil in the bags when the run started, for the Security tab's spending limit.</summary>
    private long startGil;

    /// <summary>Gil a vendor stop may still spend under the Security tab's limit; null when there's no limit.</summary>
    public long? GilLeft => plugin.Config.Security.MaxGilPerRun <= 0 ? null
        : Math.Max(0, plugin.Config.Security.MaxGilPerRun - (startGil - Integrations.GameState.BagCount(1)));

    /// <summary>A line in the run log from outside the run (the security checks).</summary>
    public void Note(string text) => Add(text, true);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMilliseconds(500);

    private readonly Plugin plugin;

    private readonly List<RunLogEntry> log = new();
    private readonly Queue<uint> toLearn = new();
    /// <summary>Gathered materials used up by crafting during the run; they count as gathered.</summary>
    private readonly Dictionary<uint, int> consumed = new();

    private DateTime nextCheck = DateTime.MinValue;
    private bool pausedForDuty;
    private string? pendingStop;
    private bool pendingStopProblem;
    /// <summary>Time actually spent gathering on land and fishing with the rod, for the learned rates.</summary>
    private double landActiveSeconds, fishActiveSeconds;
    private DateTime lastGatherTick;
    private DateTime learnDeadline;

    /// <summary>Crafting while waiting for timed nodes or fish windows (still in the gather stage).</summary>
    private bool craftingBetween;
    /// <summary>A hand-back to gathering is under way (crafter finishing its craft and leaving the log).</summary>
    private bool yielding;

    /// <summary>The crafter got this run's recipes; never build them twice (ordered items would be crafted again).</summary>
    private bool craftQueueBuilt;
    /// <summary>The crafting spot was already gone to (or tried) for this stretch of crafting.</summary>
    private bool spotTried;

    /// <summary>Trip to the next timed node's spawn area while it's still closed.</summary>
    private readonly Travel nodeTrip = new();
    private TimedSpot? tripSpot;
    private uint tripItem;
    private DateTime nextTripCheck;
    private readonly HashSet<TimedSpot> tripFailed = new();
    /// <summary>Closer to the window than this, the gatherer goes on its own.</summary>
    private static readonly TimeSpan TripCutoff = TimeSpan.FromSeconds(20);

    private readonly ExtrasCleanup cleanup;
    private bool cleaning;

    private int knockOuts;
    private bool knockedOut;
    private DateTime knockedOutAt;
    private DateTime revivedAt;

    // ------------------------------------------------------------------ gathering and fishing side by side

    /// <summary>Land → fish: after pausing the gatherer, wait this long in case a node it had just pressed opens after all.</summary>
    private static readonly TimeSpan Handover = TimeSpan.FromSeconds(1.5);
    /// <summary>The gatherer's "nothing to gather" counts after this long (it looks again every 3 s, after upkeep).</summary>
    private static readonly TimeSpan LandSettle = TimeSpan.FromSeconds(6);
    /// <summary>The gatherer found nothing although nodes should be up (given up on for a while): look again after this.</summary>
    private static readonly TimeSpan LandRecheck = TimeSpan.FromMinutes(2);
    /// <summary>A catch (or spearfishing node) still going after this long is let go.</summary>
    private static readonly TimeSpan ReelInPatience = TimeSpan.FromMinutes(2);
    /// <summary>After fishing stops with the rod out, the fisher keeps its ticks this long to reel in.</summary>
    private static readonly TimeSpan WindDown = TimeSpan.FromSeconds(30);
    /// <summary>The trip to a fishing hole, on top of the fisher's lead, when deciding to go for a fish window.</summary>
    private static readonly TimeSpan FishTrip = TimeSpan.FromMinutes(1.5);

    /// <summary>The worker ticked every frame in the gathering stage.</summary>
    private Worker worker = Worker.Land;
    /// <summary>A hand-over to the other worker is under way (the current one finishes its node or catch first).</summary>
    private Worker? switchingTo;
    /// <summary>Land → fish: the gatherer was paused for the hand-over; the fisher starts at this time.</summary>
    private DateTime handoverAt;
    /// <summary>When the gatherer last became the active worker (its "nothing to gather" is stale until it has looked).</summary>
    private DateTime landActiveSince;
    /// <summary>Since when the active gatherer has said nothing can be gathered.</summary>
    private DateTime? landWaitingSince;
    /// <summary>The gatherer found nothing it could do although targets are up: don't hand back to it before this.</summary>
    private DateTime landIdleUntil;
    /// <summary>The fisher was asked to land its catch and reel in (pause, hand-over, end of gathering) at this time.</summary>
    private DateTime? reelInSince;
    /// <summary>The catch took too long and the fisher was told to let it go.</summary>
    private bool reelInHard;
    /// <summary>Even that didn't bring the rod in or close the spearfishing window: the run stops.</summary>
    private bool fisherStuck;
    private DateTime nextQuitPress;
    /// <summary>After fishing stopped with the rod out: the fisher keeps its ticks until this time to reel in.</summary>
    private DateTime fishWindDownUntil;
    /// <summary>A gathering window opened while the fisher works (the gatherer had pressed the node just before).</summary>
    private DateTime? strayNodeSince;
    /// <summary>The worker's tick kept throwing; the run stops with this.</summary>
    private string? workerError;
    private int workerErrors;
    private DateTime workerErrorsSince;
    /// <summary>The fisher got this run's fish (it is only paused, stopped or asked about after that).</summary>
    private bool fishBegun;
    /// <summary>Since when everything still needed is something neither worker can get.</summary>
    private DateTime? onlyUnreachableSince;
    /// <summary>Per fish target still needed: real time until it bites (from the fisher, refreshed twice a second).</summary>
    private readonly Dictionary<uint, TimeSpan?> fishWaits = new();
    /// <summary>The fish can't all be in before this: the last fish window plus its catches.</summary>
    private DateTime fishWindowsEnd;
    /// <summary>Fish the run caught that went back into the water as bait (mooch, swimbait), for the log.</summary>
    private readonly Dictionary<uint, int> usedAsBait = new();
    /// <summary>
    /// Plain normal-quality counts in the main bags when gathering started (as <see cref="GameState.BagCount(uint, bool)"/>
    /// counts: no high-quality items, no collectables): by-catch is never thrown away below these.
    /// </summary>
    private Dictionary<uint, int> bagsAtStart = new();

    public RunEngine(Plugin plugin)
    {
        this.plugin = plugin;
        Crafter = new Crafter(plugin, Add);
        Crafter.Crafted += OnCrafted;
        Shopper = new Shopper(plugin, Add);
        Retainers = new RetainerRun(plugin, Add);
        Books = new BookBuyer(plugin, Add);
        TurnIn = new Appraiser(plugin, Add);
        Hunt = new Hunter(plugin, Add);
        Home = new Homecoming(plugin, Add);
        Gather = new Gathering.Gatherer(plugin, Add);
        Fish = new Fishing.Fisher(plugin, Add);
        cleanup = new ExtrasCleanup(Add, id => plugin.Plans.Catalog?.ItemName(id) ?? "item " + id);
    }

    public RunPhase Phase { get; private set; } = RunPhase.Idle;
    public RunStage Stage { get; private set; } = RunStage.Gather;
    public string Headline { get; private set; } = "Not running";
    public string Detail { get; private set; } = "";
    public DateTime? StartedAt { get; private set; }
    public DateTime? EndedAt { get; private set; }
    public IReadOnlyList<GatherTarget> Targets { get; private set; } = Array.Empty<GatherTarget>();
    public Crafter Crafter { get; }
    public Shopper Shopper { get; }
    public RetainerRun Retainers { get; }
    public BookBuyer Books { get; }
    public Hunter Hunt { get; }
    public Homecoming Home { get; }

    // ------------------------------------------------------------------ trying the crafting spot

    private Homecoming? spotTest;

    /// <summary>The trip to the crafting spot is being tried out (from the settings, outside a run).</summary>
    public bool TestingSpot => spotTest != null;

    /// <summary>What the try is doing, or how it ended.</summary>
    public string SpotTestStatus { get; private set; } = "";

    /// <summary>Whether the last try ended in a problem.</summary>
    public bool SpotTestFailed { get; private set; }

    /// <summary>Goes to the crafting spot now, the way a run would before crafting. Null when it set off, else why not.</summary>
    public string? TestSpot()
    {
        if (Active) return "A run is going; stop it first.";
        var spot = plugin.Config.CraftingSpot;
        if (!spot.IsSet) return "Save a spot first.";
        if (!Services.ClientState.IsLoggedIn || Services.Objects.LocalPlayer == null) return "Log in first.";
        Integrations.Travel.AdvancedUnstuck = plugin.Config.Security.AdvancedUnstuck;
        Integrations.Travel.StuckAfter = TimeSpan.FromSeconds(Math.Clamp(plugin.Config.Security.StuckSeconds, 3, 30));
        spotTest = new Homecoming(plugin, (_, _) => { });
        SpotTestFailed = false;
        if (!spotTest.BeginSpot(spot))
        {
            spotTest = null;
            SpotTestStatus = "Already there.";
            return null;
        }
        SpotTestStatus = "Setting off";
        return null;
    }

    public void StopSpotTest()
    {
        if (spotTest == null) return;
        spotTest.Cancel();
        Integrations.Vnav.Stop();
        spotTest = null;
        SpotTestStatus = "Stopped.";
    }

    private void TickSpotTest()
    {
        var trip = spotTest!;
        if (Services.Condition[ConditionFlag.InCombat]) { StopSpotTest(); SpotTestStatus = "Stopped: in combat."; SpotTestFailed = true; return; }
        trip.Tick();
        SpotTestStatus = trip.Status;
        if (!trip.Finished) return;
        spotTest = null;
        SpotTestFailed = trip.Failure != null;
        SpotTestStatus = trip.Failure is { } why ? "Couldn't get there: " + why : "Arrived.";
        if (SpotTestFailed) Services.Chat.PrintError("[Provenance] Crafting spot: " + SpotTestStatus);
        else Services.Chat.Print("[Provenance] At the crafting spot.");
    }
    /// <summary>Provenance's own gatherer (nodes, the gathering window, collectables, upkeep).</summary>
    public Gathering.Gatherer Gather { get; }
    /// <summary>Provenance's own fishing (rod fishing and spearfishing) for the fish targets of the gathering stage.</summary>
    public Fishing.Fisher Fish { get; }

    /// <summary>
    /// Collectables the order or the scrip earning needs: never aetherially reduced. Every ordered
    /// item counts, also when the bags already cover it (so it isn't gathered) or while the run earns
    /// scrips (when the gathering list is the scrip plan's).
    /// </summary>
    private IReadOnlySet<uint> NeededCollectables()
    {
        var set = plugin.Plans.ScripTasks.Select(t => t.Option.ItemId).ToHashSet();
        foreach (var t in Targets) set.Add(t.ItemId);
        foreach (var l in plugin.Config.Order.Lines) set.Add(l.ItemId);
        foreach (var l in plugin.Plans.Plan.Lines.Where(l => l.IsTarget)) set.Add(l.ItemId);
        return set;
    }
    /// <summary>Where the run was started (for "back to where the run started").</summary>
    private (uint Territory, System.Numerics.Vector3 Position)? startedAt;
    /// <summary>The finished order's result, shown once the trip home is over.</summary>
    private (string Headline, string Detail, bool Problem)? pendingEnd;
    /// <summary>The learn stage is buying books at exchanges before reading them.</summary>
    private bool buyingBooks;
    public IReadOnlyList<RunLogEntry> Log => log;
    /// <summary>When the next timed node opens or the next fish bites, while only those are left.</summary>
    public TimeSpan? NextWindow { get; private set; }

    public bool Active => Phase is RunPhase.Running or RunPhase.Pausing or RunPhase.Paused or RunPhase.Stopping;
    /// <summary>The crafter is the one working right now (craft stage, or crafting between timed nodes).</summary>
    public bool Crafting => Stage == RunStage.Craft || (Stage == RunStage.Gather && craftingBetween);
    /// <summary>The fisher is the one working in the gathering stage (fishing, on its way, or waiting at the water).</summary>
    public bool FishingNow => Active && Stage == RunStage.Gather && !craftingBetween && !cleaning && worker == Worker.Fish;

    /// <summary>
    /// Real time until a fish target bites at its best spot, as last checked: zero when it bites now,
    /// <see cref="TimeSpan.MaxValue"/> when not within the forecast; null without a window (or when done).
    /// </summary>
    public TimeSpan? FishWait(uint itemId) => fishWaits.GetValueOrDefault(itemId);

    /// <summary>"gathered", "caught" or "gathered or caught", for progress lines about these targets.</summary>
    public static string GotWord(IReadOnlyCollection<GatherTarget> targets)
    {
        var fish = targets.Count(t => t.Supply == Supply.Fish);
        return fish == 0 ? "gathered" : fish == targets.Count ? "caught" : "gathered or caught";
    }

    private RunSettings Settings => plugin.Config.Run;

    /// <summary>Earning scrips with collectables before the order itself.</summary>
    private bool scripPhase;
    private int scripRounds;
    /// <summary>
    /// Buying and reading the order's books that the scrips at hand pay for, between rounds of collectables
    /// (scrips stop at a cap, so a big batch of books is earned and bought in turns).
    /// </summary>
    private bool bookStop;
    /// <summary>Order books not bought yet when the current book stop began (to see whether a round got anywhere).</summary>
    private int booksBeforeStop;
    public bool EarningScrips => Active && scripPhase;
    public Appraiser TurnIn { get; private set; } = null!;

    /// <summary>The plan the stages work on: the collectables for scrips first, then the order.</summary>
    private Plan ActivePlan => scripPhase ? plugin.Plans.ScripPlan : plugin.Plans.Plan;

    /// <summary>Recipes to craft; ordered items marked HQ get a quality rotation, everything else the fastest.</summary>
    private List<CraftJob> CraftJobs()
    {
        var hqItems = plugin.Config.Order.Lines.Where(o => o.Hq).Select(o => o.ItemId).ToHashSet();
        var catalog = plugin.Plans.Catalog!;
        return ActivePlan.CraftSteps.Where(l => l.Recipe != null && l.Crafts > 0)
            .Select(l => scripPhase && l.IsTarget && catalog.Collectable(l.ItemId) is { } c
                // Collectables: quality for the top collectability (collectability is quality / 10).
                ? new CraftJob(l.Recipe!, l.Crafts, false, c.HighCollectability * 10)
                : new CraftJob(l.Recipe!, l.Crafts, !scripPhase && l.IsTarget && l.Recipe!.CanHq && hqItems.Contains(l.ItemId))).ToList();
    }

    /// <summary>Why a run can't start now, or null when it can.</summary>
    public string? StartBlocker()
    {
        var plan = plugin.Plans.Plan;
        if (Active) return "A run is already going.";
        if (plugin.Plans.Catalog == null) return "Still reading game data.";
        if (plugin.Config.Order.IsEmpty) return "Add items to the order first.";
        if (!plan.CanStart) return "Get the missing items first; they are listed at the top of the plan.";
        if (!plugin.Plans.Ready) return "Your character can't do everything in this order yet; the plan lists what's in the way.";
        if (!plugin.Plans.ScripPlan.CanStart && plugin.Plans.ScripTasks.Count > 0) return "The collectables for scrips need items you have to get yourself; they are listed in the plan.";
        if (!Services.ClientState.IsLoggedIn) return "Log in first.";
        if (Services.Condition[ConditionFlag.Fishing]) return "Put the fishing rod away first (Quit).";
        if (Stock.FreeSlots() <= MinFreeSlots) return $"Your inventory has {MinFreeSlots} or fewer free slots.";
        var gather = GatherTargets.Build(plan, BagNq).Count > 0;
        var scripGather = plugin.Plans.ScripTasks.Count > 0 && GatherTargets.Build(plugin.Plans.ScripPlan, BagNq).Count > 0;
        var craft = plan.CraftSteps.Any(l => l.Crafts > 0);
        if (!gather && !craft && plan.Learn.Count == 0 && plan.Count(Supply.Vendor) == 0 && plan.Count(Supply.Drop) == 0) return "Nothing to gather, buy or craft for this order.";

        if ((gather || scripGather) && !Vnav.Available) return "vnavmesh isn't loaded.";
        if (FishBlocker() is { } fish) return fish;
        if ((plan.Count(Supply.Vendor) > 0 || plan.Learn.Any(l => l.WillBuy) || (plugin.Config.Stock.Retainers && RetainerWork().Count > 0)) && !Vnav.Available)
            return "vnavmesh isn't loaded; it walks to vendors, exchanges and summoning bells.";
        if (HuntBlocker() is { } hunt) return hunt;
        if (UnreachableVendorItem() is { } unreachable)
            return $"No vendor you can reach sells {plugin.Plans.Catalog!.ItemName(unreachable)} under your price cap; get it yourself, or attune to more aetherytes and aethernet shards.";
        if (craft && Raphael.ExecutablePath() == null) return "raphael-cli.exe wasn't found; reinstall Provenance.";
        return null;
    }

    private DateTime fishCheckedAt;
    private string? fishResult;

    /// <summary>
    /// Why a fish in the order (or in the collectables for scrips) can't be caught by this character, naming
    /// the fish and the reason (level, reach, places to stand, actions, bait), or no Fisher gearset; null when
    /// every fish can be fished. Checked every few seconds: teleport data is slow.
    /// </summary>
    private string? FishBlocker()
    {
        if (DateTime.UtcNow - fishCheckedAt < TimeSpan.FromSeconds(3)) return fishResult;
        fishCheckedAt = DateTime.UtcNow;
        fishResult = null;
        var plans = plugin.Plans;
        var fish = plans.Plan.Lines.Concat(plans.ScripTasks.Count > 0 ? plans.ScripPlan.Lines : Enumerable.Empty<PlanLine>())
            .Where(l => l.Supply == Supply.Fish && l.Remaining > 0).Select(l => l.ItemId).Distinct().ToList();
        if (fish.Count == 0) return null;
        var catalog = plans.Catalog!;
        var level = PlanService.JobLevel(FishAccess.Fisher);
        foreach (var id in fish)
        {
            if (catalog.Gather.Fish(id) is not { } info)
                return fishResult = $"Provenance doesn't know where {catalog.ItemName(id)} bites; get it yourself.";
            if (FishAccess.Blocker(catalog.Gather, info, level) is { } why)
                return fishResult = $"Can't fish {catalog.ItemName(id)} yet ({why}).";
        }
        if (GameState.BestGearset(FishAccess.Fisher) < 0)
            return fishResult = "No gearset saved for Fisher; save one so the run can change into it.";
        return null;
    }

    // ------------------------------------------------------------------ start / stages

    public void Start()
    {
        if (StartBlocker() is { } why)
        {
            Notify("Can't start: " + why, true);
            return;
        }

        log.Clear();
        consumed.Clear();
        knockOuts = 0;
        knockedOut = false;
        pausedForDuty = false;
        pendingStop = null;
        craftingBetween = false;
        yielding = false;
        NextWindow = null;
        StartedAt = DateTime.Now;
        EndedAt = null;
        Detail = "";
        Targets = Array.Empty<GatherTarget>();
        Phase = RunPhase.Running;
        craftQueueBuilt = false;
        tripSpot = null;
        tripFailed.Clear();
        cleaning = false;
        cleanup.Cancel();
        nextTripCheck = default;
        ResetWorkers();
        fishWindDownUntil = default;
        Crafter.Begin(Array.Empty<CraftJob>());
        scripRounds = 0;
        extrasCrafted = false;
        pendingEnd = null;
        startedAt = Services.Objects.LocalPlayer is { } me ? (Services.ClientState.TerritoryType, me.Position) : null;
        spotTried = false;
        startGil = Integrations.GameState.BagCount(1);
        Integrations.Travel.AdvancedUnstuck = plugin.Config.Security.AdvancedUnstuck;
        Integrations.Travel.StuckAfter = TimeSpan.FromSeconds(Math.Clamp(plugin.Config.Security.StuckSeconds, 3, 30));
        scripPhase = plugin.Plans.ScripTasks.Count > 0;
        bookStop = false;
        if (scripPhase && BooksToBuyNow() > 0)
        {
            // The scrips at hand buy some books now; buying them first leaves room under the cap for the round.
            BeginBookStop();
            if (Active) Notify("Run started for " + plugin.Config.Order.Name + ".", false);
            return;
        }
        if (scripPhase) LogScripRound("Earning scrips for books first: ");

        // Retainers first (a book may be with a retainer), then books, vendors, gathering, crafting.
        StartRetainers();
        if (Active) Notify("Run started for " + plugin.Config.Order.Name + ".", false);
    }

    /// <summary>Books and tomes from the bags come before buying and gathering: recipes and legendary nodes need them.</summary>
    private void StartLearning()
    {
        plugin.Plans.Invalidate();
        // Books not in the bags that an exchange sells and the scrips at hand pay for: buy them first, then read
        // everything. Books still waiting for scrips are bought after the next round of collectables.
        var buy = LearnPlan.Learn.Where(l => l.WillBuy && plugin.Plans.BuyNow(l.ItemId))
            .Select(l => plugin.Plans.BookOffer(l.ItemId) is { } offer ? new BookOrder(l.ItemId, offer) : null)
            .OfType<BookOrder>().ToList();
        if (buy.Count > 0)
        {
            Stage = RunStage.Learn;
            Headline = "Buying books";
            buyingBooks = true;
            learnDeadline = default;
            Books.Begin(buy);
            Add($"Buying {buy.Count} {(buy.Count == 1 ? "book" : "books")} first: {string.Join(", ", buy.Select(b => plugin.Plans.Catalog!.ItemName(b.ItemId)))}.", false);
            return;
        }
        ReadBooks();
    }

    private void ReadBooks()
    {
        buyingBooks = false;
        toLearn.Clear();
        foreach (var l in LearnPlan.Learn.Where(l => GameState.BagCount(l.ItemId) > 0)) toLearn.Enqueue(l.ItemId);
        if (toLearn.Count == 0) { StartAfterLearning(); return; }
        Stage = RunStage.Learn;
        Headline = "Learning";
        learnDeadline = default;
        Add($"Reading {toLearn.Count} {(toLearn.Count == 1 ? "book" : "books")} first.", false);
    }

    private void StartAfterLearning()
    {
        if (bookStop) AfterBookStop();
        else StartVendors();
    }

    /// <summary>The plan whose books are bought and read now: the order's during a book stop, else the active one.</summary>
    private Plan LearnPlan => bookStop ? plugin.Plans.Plan : ActivePlan;

    /// <summary>Order books the scrips at hand pay for now.</summary>
    private int BooksToBuyNow() => plugin.Plans.Plan.Learn.Count(l => l.Remaining > 0 && l.WillBuy && plugin.Plans.BuyNow(l.ItemId));

    /// <summary>Order books still to buy (in the bags or not yet bought).</summary>
    private int BooksToBuy() => plugin.Plans.Plan.Learn.Count(l => l.Remaining > 0);

    private void BeginBookStop()
    {
        bookStop = true;
        booksBeforeStop = BooksToBuy();
        Add($"Buying the {BooksToBuyNow()} books your scrips pay for now, before earning more.", false);
        StartLearning();
    }

    /// <summary>The books are bought and read: earn more scrips for the rest, or go on with the order.</summary>
    private void AfterBookStop()
    {
        bookStop = false;
        scripPhase = false;
        var progress = BooksToBuy() < booksBeforeStop;
        plugin.Plans.Rebuild();
        if (plugin.Plans.ScripTasks.Count > 0)
        {
            if (progress) scripRounds = 0;
            scripPhase = true;
            LogScripRound($"{plugin.Plans.BooksForLater} books still need scrips: ");
        }
        else Add("Books bought; on to the order.", false);
        StartRetainers();
    }

    private void LogScripRound(string lead)
        => Add(lead + string.Join(", ", plugin.Plans.ScripTasks.Select(t =>
            $"{t.Count}× {plugin.Plans.Catalog!.ItemName(t.Option.ItemId)} for about {t.Expected:N0} {plugin.Plans.Catalog!.ItemNamePlural(t.Option.Currency)}")) + ".", false);

    private void StartRetainers()
    {
        var work = plugin.Config.Stock.Retainers ? RetainerWork() : new();
        if (work.Count == 0) { StartLearning(); return; }
        Stage = RunStage.Retainers;
        Headline = "Taking items from retainers";
        Retainers.Begin(work);
        Add($"Taking items from {work.Count} {(work.Count == 1 ? "retainer" : "retainers")}.", false);
    }

    private void StartVendors()
    {
        plugin.Plans.Invalidate();
        var purchases = Purchases();
        if (purchases.Count == 0) { StartHunt(); return; }
        Stage = RunStage.Vendors;
        Headline = "Buying from vendors";
        Shopper.Begin(purchases);
        var stops = purchases.Select(p => p.Offer.NpcId).Distinct().Count();
        Add($"Buying {purchases.Count} {(purchases.Count == 1 ? "item" : "items")} from {stops} {(stops == 1 ? "vendor" : "vendors")}.", false);
    }

    /// <summary>Monster drops still needed, with spawn areas the combat job can handle and the character can reach.</summary>
    private List<HuntOrder> HuntOrders(out uint? noSpot)
    {
        noSpot = null;
        var catalog = plugin.Plans.Catalog!;
        var job = plugin.Config.Combat.ClassJob;
        var level = job == 0 ? 0 : GameState.JobLevel(job);
        var reach = new Dictionary<uint, bool>();
        var orders = new List<HuntOrder>();
        foreach (var line in ActivePlan.Lines.Where(l => l.Supply == Supply.Drop && l.Remaining > 0))
        {
            var spots = catalog.Mobs.For(line.ItemId)
                .Where(s => s.Level <= level + 2)
                .Where(s => reach.TryGetValue(s.TerritoryId, out var r) ? r : reach[s.TerritoryId] = Teleporter.CanReach(s.TerritoryId))
                .ToList();
            if (spots.Count == 0) { noSpot ??= line.ItemId; continue; }
            orders.Add(new HuntOrder(line.ItemId, line.Remaining, spots));
        }
        return orders;
    }

    private DateTime huntCheckedAt;
    private string? huntResult;

    /// <summary>Why the monster drops can't be farmed (checked every few seconds), or null.</summary>
    private string? HuntBlocker()
    {
        if (DateTime.UtcNow - huntCheckedAt < TimeSpan.FromSeconds(3)) return huntResult;
        huntCheckedAt = DateTime.UtcNow;
        huntResult = null;
        var plan = plugin.Plans.Plan;
        var drops = plan.Lines.Where(l => l.Supply == Supply.Drop && l.Remaining > 0).ToList();
        if (drops.Count == 0) return null;
        var catalog = plugin.Plans.Catalog!;
        var job = plugin.Config.Combat.ClassJob;
        if (job == 0) return huntResult = "This order has monster drops; pick a combat job in Settings.";
        if (Combat.BasicCombo.For(job) == null) return huntResult = "Provenance has no combo for the combat job picked in Settings.";
        if (GameState.BestGearset(job) < 0) return huntResult = $"No gearset saved for {Core.Character.ClassJobs.Abbreviation(job)}, the combat job picked in Settings.";
        if (!Vnav.Available) return huntResult = "vnavmesh isn't loaded; it walks to the monsters.";
        var noMobs = drops.FirstOrDefault(l => catalog.Mobs.For(l.ItemId).Count == 0);
        if (noMobs != null) return huntResult = $"Provenance doesn't know where monsters that drop {catalog.ItemName(noMobs.ItemId)} spawn; get it yourself.";
        HuntOrders(out var noSpot);
        if (noSpot is { } item)
            return huntResult = $"The monsters that drop {catalog.ItemName(item)} are too strong for your {Core.Character.ClassJobs.Abbreviation(job)} or in zones you can't teleport to yet.";
        return null;
    }

    /// <summary>Monster drops after the vendors, before gathering.</summary>
    private void StartHunt()
    {
        var orders = HuntOrders(out _);
        if (orders.Count == 0) { StartGatherOrCraft(); return; }
        Stage = RunStage.Hunt;
        Headline = "Farming monster drops";
        Hunt.Begin(orders, plugin.Config.Combat.ClassJob);
        Add($"Farming {string.Join(", ", orders.Select(o => $"{o.Amount}× {plugin.Plans.Catalog!.ItemName(o.ItemId)}"))} as {Core.Character.ClassJobs.Abbreviation(plugin.Config.Combat.ClassJob)}.", false);
    }

    private void StartGatherOrCraft()
    {
        plugin.Plans.Invalidate();
        var targets = GatherTargets.Build(ActivePlan, BagNq);
        if (targets.Count > 0) StartGathering(targets);
        else StartCrafting();
    }

    /// <summary>
    /// What to take from which retainer: for every item the plan counts as owned, the part that isn't
    /// in the bags or saddlebag, taken from the retainers that hold it (as last seen).
    /// </summary>
    private List<(ulong Retainer, string Name, List<(uint Item, int Amount)> Items)> RetainerWork()
    {
        var result = new Dictionary<ulong, (string Name, List<(uint, int)> Items)>();
        if (!Services.PlayerState.IsLoaded || RetainerCache.For(plugin.Config, Services.PlayerState.ContentId) is not { } retainers) return new();
        var onHand = Stock.OnHand(plugin.Config);
        foreach (var line in ActivePlan.Lines)
        {
            var need = Math.Min(line.Needed, line.FromStock) - onHand.GetValueOrDefault(line.ItemId);
            foreach (var (id, stock) in retainers.OrderByDescending(r => r.Value.Items.GetValueOrDefault(line.ItemId)))
            {
                if (need <= 0) break;
                var have = stock.Items.GetValueOrDefault(line.ItemId);
                if (have <= 0) continue;
                var take = Math.Min(have, need);
                if (!result.TryGetValue(id, out var entry)) result[id] = entry = (stock.Name, new List<(uint, int)>());
                entry.Items.Add((line.ItemId, take));
                need -= take;
            }
        }
        return result.Select(kv => (kv.Key, kv.Value.Name, kv.Value.Items)).ToList();
    }

    private DateTime reachCheckedAt;
    private uint? reachResult;

    /// <summary>A vendor item none of whose vendors can be reached (checked every few seconds; teleport data is slow).</summary>
    private uint? UnreachableVendorItem()
    {
        if (DateTime.UtcNow - reachCheckedAt < TimeSpan.FromSeconds(3)) return reachResult;
        reachCheckedAt = DateTime.UtcNow;
        var cap = plugin.Config.Buying.VendorMaxPrice;
        var areas = new Dictionary<uint, bool>();
        reachResult = null;
        foreach (var line in ActivePlan.Lines.Where(l => l.Supply == Supply.Vendor && l.Remaining > 0))
        {
            var ok = plugin.Plans.Catalog!.Vendors.For(line.ItemId).Where(o => cap == 0 || o.Price <= cap)
                .Any(o => areas.TryGetValue(o.TerritoryId, out var r) ? r : areas[o.TerritoryId] = Teleporter.CanReach(o.TerritoryId));
            if (!ok) { reachResult = line.ItemId; break; }
        }
        return reachResult;
    }

    /// <summary>
    /// Vendor purchases: for each item, a vendor under the price cap in a zone the character can
    /// teleport to, closest to an aetheryte; a vendor already on the route is reused when it sells it.
    /// </summary>
    private List<Purchase> Purchases()
    {
        var catalog = plugin.Plans.Catalog!;
        var cap = plugin.Config.Buying.VendorMaxPrice;
        var unlocked = Teleporter.Unlocked();
        var here = Services.ClientState.TerritoryType;
        float Score(VendorOffer o)
        {
            if (o.TerritoryId == here && Services.Objects.LocalPlayer is { } me)
                return System.Numerics.Vector2.Distance(new System.Numerics.Vector2(me.Position.X, me.Position.Z), o.Position) - 10000;
            var best = float.MaxValue;
            foreach (var d in unlocked.Where(d => d.TerritoryId == o.TerritoryId))
                if (Teleporter.Position(d.AetheryteId) is { } a)
                    best = Math.Min(best, System.Numerics.Vector2.Distance(new System.Numerics.Vector2(a.X, a.Z), o.Position));
            // Areas reached through the aethernet: a bit further than a direct teleport.
            return best == float.MaxValue ? 5000 : best;
        }

        var chosen = new List<Purchase>();
        var reachable = new Dictionary<uint, bool>();
        foreach (var line in ActivePlan.Lines.Where(l => l.Supply == Supply.Vendor && l.Remaining > 0))
        {
            var offers = catalog.Vendors.For(line.ItemId)
                .Where(o => cap == 0 || o.Price <= cap)
                .Where(o => reachable.TryGetValue(o.TerritoryId, out var r) ? r : reachable[o.TerritoryId] = Teleporter.CanReach(o.TerritoryId))
                .ToList();
            if (offers.Count == 0)
            {
                Add($"No vendor you can reach sells {catalog.ItemName(line.ItemId)}; get it yourself.", true);
                continue;
            }
            var onRoute = offers.FirstOrDefault(o => chosen.Any(c => c.Offer.NpcId == o.NpcId));
            var offer = onRoute ?? offers.OrderBy(Score).First();
            chosen.Add(new Purchase(line.ItemId, line.Remaining, offer));
        }
        return chosen;
    }

    private void StartGathering(List<GatherTarget> targets)
    {
        Stage = RunStage.Gather;
        Targets = targets;
        landActiveSeconds = fishActiveSeconds = 0;
        lastGatherTick = DateTime.UtcNow;
        ResetWorkers();
        // From here on the stage ticks the fisher itself, also while it reels in.
        fishWindDownUntil = default;
        bagsAtStart = ExtrasCleanup.NormalCounts();

        // The crafter gets the whole order now, so it can work between timed nodes and fish windows.
        var jobs = CraftJobs();
        Crafter.Begin(jobs);
        craftQueueBuilt = true;
        Crafter.DeferMissing = true;
        Crafter.RequestPause();

        workTargets = GathererTargets(targets);
        Gather.Begin(workTargets, GathererLeft, NeededCollectables, ReduceNow);
        // Reduced only from collectables the order wants as themselves: nothing would ever gather for it.
        if (targets.FirstOrDefault(t => t.Supply == Supply.Reduce && !reduceFor.ContainsValue(t)) is { ItemId: not 0 } orphan)
        {
            Stop($"Can't get {ItemName(orphan.ItemId)}: the collectables it's reduced from are on the order as themselves. Get it yourself.", true, now: true);
            return;
        }
        Fish.Begin(workTargets.Where(t => t.Supply == Supply.Fish), GathererLeft, NeededCollectables, OnUsedAsBait, ReduceNow, reduceFor.ContainsKey);
        fishBegun = true;
        landActiveSince = DateTime.UtcNow;
        UpdateFishWaits();
        // Whoever has work now starts; the other waits paused until the run hands over.
        var land = workTargets.Count(t => t.Supply != Supply.Fish);
        var fish = workTargets.Count - land;
        var (want, _) = WantedWorker();
        worker = want ?? (land > 0 ? Worker.Land : Worker.Fish);
        if (worker == Worker.Fish) Gather.Pause();
        else Fish.Pause();
        Headline = worker == Worker.Fish ? "Fishing" : "Gathering";

        var fishText = fish == 1 ? $"fishing for {ItemName(workTargets.First(t => t.Supply == Supply.Fish).ItemId)}" : $"fishing for {fish} kinds of fish";
        Add(fish == 0 ? $"Gathering {land} {(land == 1 ? "item" : "items")}."
            : land == 0 ? char.ToUpperInvariant(fishText[0]) + fishText[1..] + "."
            : $"Gathering {land} {(land == 1 ? "item" : "items")} and {fishText}.", false);
        LogReductionSources(targets);
    }

    /// <summary>Everything gathered: craft whatever is left (or all of it when nothing was gathered).</summary>
    private void StartCrafting()
    {
        // To the crafting spot first, when one is saved and there's something to craft.
        if (!spotTried && plugin.Config.CraftingSpot is { Enabled: true, IsSet: true } spot && CraftingLeft())
        {
            spotTried = true;
            if (Home.BeginSpot(spot))
            {
                Stage = RunStage.ToSpot;
                Headline = "Going to the crafting spot";
                Add($"Going to the crafting spot ({spot.Label}).", false);
                return;
            }
        }
        Stage = RunStage.Craft;
        craftingBetween = false;
        yielding = false;
        if (!craftQueueBuilt)
        {
            var jobs = CraftJobs();
            if (jobs.Count == 0) { Finish(); return; }
            Crafter.Begin(jobs);
            craftQueueBuilt = true;
            Add($"Crafting {jobs.Sum(j => j.Crafts)} items across {jobs.Count} {(jobs.Count == 1 ? "recipe" : "recipes")}.", false);
        }
        else if (Crafter.Finished)
        {
            // Everything was already crafted between timed nodes.
            if (Crafter.Failure is { } failure) Stop(failure, true, now: true);
            else Finish();
            return;
        }
        else Add("Crafting what's left.", false);
        Crafter.FinalPass();
        if (Phase == RunPhase.Pausing) Crafter.RequestPause();
        Headline = "Crafting";
    }

    /// <summary>Crafts still to do: the queue's rest once it's built, else whether the plan has any.</summary>
    private bool CraftingLeft() => craftQueueBuilt ? !Crafter.Finished : CraftJobs().Count > 0;

    /// <summary>On the way to the crafting spot: once there (or once it turned out it can't get there), craft.</summary>
    private void TickToSpot()
    {
        Detail = Home.Status;
        if (Phase == RunPhase.Stopping) { Stop(pendingStop ?? "Stopped.", pendingStopProblem, now: true); return; }
        if (!Home.Finished) return;
        if (Home.Failure is { } why) Add($"Couldn't get to the crafting spot: {why} Crafting here instead.", true);
        StartCrafting();
    }

    // ------------------------------------------------------------------ controls

    /// <summary>Soft pause: finishes the node being gathered, the catch under way or the item being crafted, then stops.</summary>
    public void Pause()
    {
        if (Phase != RunPhase.Running) return;
        Phase = RunPhase.Pausing;
        if (Crafting)
        {
            Crafter.RequestPause();
            Headline = "Pausing after this craft";
        }
        else if (Stage == RunStage.Vendors) { Shopper.RequestPause(); Headline = "Pausing after this vendor"; }
        else if (Stage == RunStage.Retainers) { Retainers.RequestPause(); Headline = "Pausing after this retainer"; }
        else if (Stage == RunStage.Hunt) { Hunt.RequestPause(); Headline = "Pausing after this fight"; }
        else if (Stage == RunStage.ToSpot) Headline = "Pausing at the crafting spot";
        else if (FishingNow)
        {
            // The rod stays out between casts: the fisher lands the catch under way, then reels in.
            Fish.RequestPause();
            Headline = Fish.View is { Spear: true } ? "Pausing after this node" : "Pausing after this catch";
        }
        else Headline = "Pausing after this node";
        Add("Pause asked for; finishing what's under way first.", false);
    }

    public void Resume()
    {
        if (Phase is not (RunPhase.Paused or RunPhase.Pausing)) return;
        var wasPaused = Phase == RunPhase.Paused;
        Phase = RunPhase.Running;
        if (Stage == RunStage.Vendors) Shopper.Resume();
        else if (Stage == RunStage.Retainers) Retainers.Resume();
        else if (Stage == RunStage.Hunt) Hunt.Resume();
        else if (Stage == RunStage.Craft || craftingBetween) Crafter.Resume();
        else if (Stage == RunStage.Gather && wasPaused) HandBackToGathering(log: false);
        else if (Stage == RunStage.Gather && worker == Worker.Fish)
        {
            // "Keep going" before the catch was in: the fisher carries on fishing.
            Fish.Resume();
            reelInSince = null;
            reelInHard = false;
        }
        Headline = Crafting ? "Crafting" : Stage switch
        {
            RunStage.Learn => "Learning",
            RunStage.Retainers => "Taking items from retainers",
            RunStage.Vendors => "Buying from vendors",
            RunStage.Hunt => "Farming monster drops",
            RunStage.Return => "Heading back",
            RunStage.ToSpot => "Going to the crafting spot",
            RunStage.TurnIn => "Turning in collectables",
            RunStage.Gather when worker == Worker.Fish => "Fishing",
            _ => "Gathering",
        };
        Add("Resumed.", false);
    }

    /// <summary>
    /// Ends the run. During a craft it waits for that craft to finish (materials are lost otherwise),
    /// unless <paramref name="now"/> is set because the game or a plugin went away. A rod that is out
    /// is reeled in either way (the fisher keeps its ticks for that after the run).
    /// </summary>
    public void Stop(string reason = "Stopped.", bool problem = false, bool now = false)
    {
        if (!Active) return;
        if (!now && Crafting && Crafter.MidCraft)
        {
            pendingStop = reason;
            pendingStopProblem = problem;
            Crafter.RequestPause();
            Phase = RunPhase.Stopping;
            Headline = "Stopping after this craft";
            Add("Stopping once the current craft is finished.", problem);
            return;
        }
        Gather.Pause();
        Gather.Stop();
        StopFisher();
        CancelNodeTrip();
        cleanup.Cancel();
        cleaning = false;
        Shopper.Cancel();
        Retainers.Cancel();
        Books.Cancel();
        TurnIn.Cancel();
        Hunt.Cancel();
        Home.Cancel();
        pendingEnd = null;
        buyingBooks = false;
        End(RunPhase.Stopped, problem ? "Stopped" : "Stopped by you", reason, problem);
    }

    private void Finish()
    {
        if (scripPhase && !extrasCrafted && ExtraCollectables() is { Count: > 0 } extra)
        {
            // Leftover materials make more collectables: craft them before going to the appraiser.
            extrasCrafted = true;
            Crafter.Begin(extra);
            Crafter.FinalPass();
            Stage = RunStage.Craft;
            Headline = "Crafting more collectables";
            Add($"Materials left for {extra.Sum(j => j.Crafts)} more {plugin.Plans.Catalog!.ItemName(extra[0].Recipe.ItemId)}; crafting them too.", false);
            return;
        }
        if (scripPhase) { StartTurnIn(); return; }
        CancelNodeTrip();
        Gather.Pause();
        Gather.Stop();
        StopFisher();
        var skipped = Crafter.History.Where(h => h.Skipped != null).ToList();
        var detail = skipped.Count == 0
            ? "Everything for this order is gathered and crafted."
            : $"Done, but {skipped.Count} {(skipped.Count == 1 ? "recipe was" : "recipes were")} skipped: " + string.Join("; ", skipped.Select(h => $"{plugin.Plans.Catalog!.ItemName(h.Job.Recipe.ItemId)} ({h.Skipped})")) + ".";
        var headline = skipped.Count > 0 ? "Order not finished" : "Order done";
        if (Home.Begin(plugin.Config.AfterOrder, startedAt))
        {
            pendingEnd = (headline, detail, skipped.Count > 0);
            Stage = RunStage.Return;
            Headline = headline + "; heading back";
            Add(detail, skipped.Count > 0);
            return;
        }
        End(RunPhase.Finished, headline, detail, skipped.Count > 0);
    }

    /// <summary>The trip after the order: when it's over (or failed), the run ends with the order's result.</summary>
    private void TickReturn()
    {
        Detail = Home.Status;
        if (!Home.Finished || pendingEnd is not { } end) return;
        if (Home.Failure is { } why) Add("Couldn't get back: " + why, true);
        pendingEnd = null;
        End(RunPhase.Finished, end.Headline, end.Detail, end.Problem);
    }

    private bool extrasCrafted;

    /// <summary>More of each scrip collectable the bags still hold every ingredient for (at most 20).</summary>
    private List<CraftJob> ExtraCollectables()
    {
        var catalog = plugin.Plans.Catalog!;
        var jobs = new List<CraftJob>();
        foreach (var task in plugin.Plans.ScripTasks.Where(t => t.Option.Crafted))
        {
            if (catalog.RecipesFor(task.Option.ItemId).FirstOrDefault() is not { } recipe) continue;
            var more = recipe.Ingredients.Min(i => GameState.BagCount(i.ItemId) / Math.Max(1, i.Amount));
            if (more > 0) jobs.Add(new CraftJob(recipe, Math.Min(20, more), false, task.Option.HighCollectability * 10));
        }
        return jobs;
    }

    /// <summary>Collectables made: take them to an appraiser.</summary>
    private void StartTurnIn()
    {
        CancelNodeTrip();
        Gather.Pause();
        Gather.Stop();
        StopFisher();
        Stage = RunStage.TurnIn;
        spotTried = false; // the next round's crafting goes back to the spot
        Headline = "Turning in collectables";
        TurnIn.Begin(plugin.Plans.ScripTasks.Select(t => t.Option.ItemId));
        Add("Collectables done; turning them in for scrips.", false);
    }

    /// <summary>Scrips earned: plan again; earn more if still short (up to three rounds), else on to the order.</summary>
    private void AfterTurnIn()
    {
        if (TurnIn.TurnedIn == 0)
        {
            // Another round would end the same way: say why and stop.
            Stop(TurnIn.Problem ?? "The appraiser took none of the collectables.", true, now: true);
            return;
        }
        scripPhase = false;
        plugin.Plans.Rebuild();
        ResetForNextPlan();
        extrasCrafted = false;
        if (plugin.Plans.ScripTasks.Count > 0)
        {
            // Rounds that buy no book in between go nowhere; a batch of books that needs several rounds is fine.
            if (++scripRounds >= 3) { Stop("Still short of scrips after three rounds of collectables without buying a book; the plan says how many are missing.", true, now: true); return; }
            if (BooksToBuyNow() > 0) { BeginBookStop(); return; }
            scripPhase = true;
            Add("Still short of scrips; another round of collectables.", true);
        }
        else Add("Scrips earned; on to the order.", false);
        StartRetainers();
    }

    /// <summary>Clears what one plan's stages left behind before the next plan starts.</summary>
    private void ResetForNextPlan()
    {
        consumed.Clear();
        craftingBetween = false;
        yielding = false;
        NextWindow = null;
        Targets = Array.Empty<GatherTarget>();
        craftQueueBuilt = false;
        tripSpot = null;
        cleaning = false;
        ResetWorkers();
        Crafter.Begin(Array.Empty<CraftJob>());
    }

    private void End(RunPhase phase, string headline, string detail, bool problem)
    {
        Phase = phase;
        EndedAt = DateTime.Now;
        Headline = headline;
        Detail = detail;
        NextWindow = null;
        Add(detail, problem);
        Notify(phase == RunPhase.Finished ? headline + " for " + plugin.Config.Order.Name + ". " + detail : detail, problem);
    }

    // ------------------------------------------------------------------ tick

    /// <summary>Framework thread, every frame.</summary>
    public void Tick()
    {
        if (fishWindDownUntil != default) TickFishWindDown();
        if (spotTest != null)
        {
            if (Active) StopSpotTest();
            else TickSpotTest();
        }
        if (!Active) return;

        // The crafter, the gatherer and the fisher need every frame; the checks below only twice a second.
        var working = !pausedForDuty && !knockedOut && Phase is RunPhase.Running or RunPhase.Pausing or RunPhase.Stopping;
        if (working && Crafting) Crafter.Tick();
        if (working && Stage == RunStage.Vendors) Shopper.Tick();
        if (working && Stage == RunStage.Retainers) Retainers.Tick();
        if (working && Stage == RunStage.TurnIn) TurnIn.Tick();
        if (working && Stage == RunStage.Hunt) Hunt.Tick();
        var workerTicked = working && Stage == RunStage.Gather && !craftingBetween && tripSpot == null;
        if (workerTicked) TickWorker(worker);
        // Whoever works (the gatherer, the crafter meanwhile, nobody while paused): a fisher with the rod out, a
        // catch, a spearfishing window or a bait trip still under way keeps its ticks until it has reeled in or
        // closed it (and then pauses, as Pause() asked). Never twice in one frame.
        if (Stage == RunStage.Gather && !pausedForDuty && !knockedOut && !(workerTicked && worker == Worker.Fish)
            && (Fish.Busy || Services.Condition[ConditionFlag.Fishing])) TickWorker(Worker.Fish);
        if (Stage is RunStage.Return or RunStage.ToSpot && !pausedForDuty && !knockedOut) Home.Tick();
        if (working && Stage == RunStage.Learn && buyingBooks) Books.Tick();
        if (DateTime.UtcNow < nextCheck) return;
        nextCheck = DateTime.UtcNow + CheckEvery;

        if (!Services.ClientState.IsLoggedIn) { Stop("You logged out.", true, now: true); return; }
        var player = Services.Objects.LocalPlayer;
        if (player == null) return; // zoning
        if (HandleKnockOut(player.IsDead)) return;
        if (Stock.FreeSlots() <= MinFreeSlots && Phase != RunPhase.Stopping) { Stop($"Only {MinFreeSlots} inventory slots left.", true); return; }
        if (HandleDuty()) return;
        var maxMinutes = plugin.Config.Security.MaxRunMinutes;
        if (maxMinutes > 0 && StartedAt is { } began && DateTime.Now - began > TimeSpan.FromMinutes(maxMinutes) && Phase == RunPhase.Running)
        {
            Stop($"The run reached its time limit of {maxMinutes} minutes (Security tab).", true);
            return;
        }

        switch (Stage)
        {
            case RunStage.Learn: TickLearning(); break;
            case RunStage.Retainers: TickErrand(Retainers.Finished, Retainers.Failure, Retainers.IsPaused, Retainers.Status, StartLearning); break;
            case RunStage.Vendors: TickErrand(Shopper.Finished, Shopper.Failure, Shopper.IsPaused, Shopper.Status, StartHunt); break;
            case RunStage.Hunt: TickErrand(Hunt.Finished, Hunt.Failure, Hunt.IsPaused, Hunt.Status, StartGatherOrCraft); break;
            case RunStage.Return: TickReturn(); break;
            case RunStage.ToSpot: TickToSpot(); break;
            case RunStage.Gather: TickGathering(); break;
            case RunStage.TurnIn: TickErrand(TurnIn.Finished, TurnIn.Failure, false, TurnIn.Status, AfterTurnIn); break;
            default: TickCrafting(); break;
        }
    }

    /// <summary>A worker's frame. A tick that keeps throwing stops the run (with the reason) instead of failing every frame.</summary>
    private void TickWorker(Worker which)
    {
        if (workerError != null) return;
        try
        {
            if (which == Worker.Fish) Fish.Tick();
            else Gather.Tick();
        }
        catch (Exception e)
        {
            Services.Log.Error(e, "The {Worker} tick failed", which);
            // A one-off glitch is logged; one that keeps coming back stops the run.
            var now = DateTime.UtcNow;
            if (now - workerErrorsSince > TimeSpan.FromSeconds(10)) { workerErrorsSince = now; workerErrors = 0; }
            if (++workerErrors >= 5) workerError ??= (which == Worker.Fish ? "Fishing hit an error: " : "Gathering hit an error: ") + e.Message;
        }
    }

    /// <summary>
    /// Knocked out: accept a raise or return to the home point, then carry on where the run was. After
    /// three knock-outs the run stops.
    /// </summary>
    private unsafe bool HandleKnockOut(bool dead)
    {
        if (dead)
        {
            if (!knockedOut)
            {
                knockedOut = true;
                knockedOutAt = DateTime.UtcNow;
                knockOuts++;
                Gather.Pause();
                if (Stage == RunStage.Gather) Fish.Pause();
                CancelNodeTrip();
                Vnav.Stop();
                if (knockOuts > MaxKnockOuts)
                {
                    Stop($"Knocked out {knockOuts} times; stopping.", true, now: true);
                    return true;
                }
                Headline = "Knocked out";
                Add($"Knocked out ({knockOuts} of {MaxKnockOuts}); getting back up, then carrying on.", true);
            }
            // Give a party member a moment to raise, then take whatever the game offers (raise or home point).
            if (DateTime.UtcNow - knockedOutAt > TimeSpan.FromSeconds(4)
                && GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectYesno", out var yesno) && GenericHelpers.IsAddonReady(yesno))
                new AddonMaster.SelectYesno((nint)yesno).Yes();
            return true;
        }
        if (!knockedOut) return false;
        if (revivedAt == default) revivedAt = DateTime.UtcNow;
        if (Services.Condition[ConditionFlag.BetweenAreas] || DateTime.UtcNow - revivedAt < TimeSpan.FromSeconds(4)) return true;
        knockedOut = false;
        revivedAt = default;
        Add("Back up; carrying on.", false);
        if (Phase == RunPhase.Running && Stage == RunStage.Gather && !craftingBetween) HandBackToGathering(log: false);
        else if (Stage == RunStage.ToSpot && !Home.BeginSpot(plugin.Config.CraftingSpot)) StartCrafting(); // up again: the trip starts over from here
        return false;
    }

    /// <summary>A duty pop takes over: pause gathering, fishing or crafting, carry on once the duty is over.</summary>
    private bool HandleDuty()
    {
        var inDuty = Services.Condition[ConditionFlag.BoundByDuty] || Services.Condition[ConditionFlag.BoundByDuty56];
        if (inDuty && !pausedForDuty)
        {
            pausedForDuty = true;
            Gather.Pause();
            if (Stage == RunStage.Gather) Fish.Pause();
            CancelNodeTrip();
            Headline = "Paused for a duty";
            Add("Duty started; the run waits until it ends.", false);
            return true;
        }
        if (!pausedForDuty) return false;
        if (inDuty) return true;
        pausedForDuty = false;
        Add("Duty over; carrying on.", false);
        if (Phase == RunPhase.Running && Stage == RunStage.Gather && !craftingBetween) HandBackToGathering(log: false);
        else if (Stage == RunStage.ToSpot && !Home.BeginSpot(plugin.Config.CraftingSpot)) StartCrafting(); // back from the duty: the trip starts over from here
        return false;
    }

    /// <summary>Retainers and vendors: move on when done, stop on failure, honour pause and stop.</summary>
    private void TickErrand(bool finished, string? failure, bool paused, string status, Action next)
    {
        Detail = status;
        if (failure != null) { Stop(failure, true, now: true); return; }
        if (Phase == RunPhase.Stopping) { Stop(pendingStop ?? "Stopped.", pendingStopProblem, now: true); return; }
        if (paused && Phase == RunPhase.Pausing) { Phase = RunPhase.Paused; Headline = "Paused"; Add("Paused.", false); return; }
        if (finished && Phase == RunPhase.Running) next();
    }

    /// <summary>Uses each book or tome from the bags and waits until the game counts it as learned.</summary>
    private unsafe void TickLearning()
    {
        if (buyingBooks)
        {
            Detail = Books.Status;
            if (Books.Failure is { } why) { Stop(why, true, now: true); return; }
            if (Phase == RunPhase.Stopping) { Stop(pendingStop ?? "Stopped.", pendingStopProblem, now: true); return; }
            if (!Books.Finished) return; // a pause waits until the books are bought
            plugin.Plans.Invalidate();
            ReadBooks();
            return;
        }
        if (Phase is RunPhase.Pausing) { Phase = RunPhase.Paused; Headline = "Paused"; return; }
        if (Phase != RunPhase.Running) return;
        if (!toLearn.TryPeek(out var item)) { plugin.Plans.Invalidate(); StartAfterLearning(); return; }
        var name = plugin.Plans.Catalog!.ItemName(item);
        Headline = "Learning";
        Detail = "Reading " + name;

        if (Learned(item))
        {
            Add($"Learned {name}.", false);
            toLearn.Dequeue();
            learnDeadline = default;
            plugin.Plans.Invalidate();
            return;
        }
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectYesno", out var yesno) && GenericHelpers.IsAddonReady(yesno))
        {
            new AddonMaster.SelectYesno((nint)yesno).Yes();
            return;
        }
        if (learnDeadline == default)
        {
            if (GameState.InCraftingStance || Services.Condition[ConditionFlag.Gathering]) return;
            learnDeadline = DateTime.UtcNow.AddSeconds(12);
            GameState.UseItem(item, false);
            return;
        }
        if (DateTime.UtcNow > learnDeadline) Stop($"Couldn't read {name}. Use it from your bags yourself, then start again.", true);
    }

    /// <summary>Master books by their recipe book flag, folklore tomes by the item's learned state.</summary>
    private bool Learned(uint item)
    {
        var book = plugin.Plans.Catalog!.MasterBooks.FirstOrDefault(b => b.ItemId == item);
        return book != null ? PlanService.IsBookLearned(book.Id) : ItemUnlocks.IsLearned(item);
    }

    private void TickGathering()
    {
        if (Gather.Failure is { } failure) { Stop(failure, true, now: true); return; }
        // The collectables for a reduction are in, but reducing them would take something else too: say what.
        if (((Gather.Waiting && Gather.ReduceBlocked is { } landBlocked ? landBlocked : null)
             ?? (!Fish.Running && Fish.ReduceBlocked is { } fishBlocked ? fishBlocked : null)) is { } blocked
            && reduceFor.Values.Any(t => Left(t) > 0))
        {
            Stop($"Can't reduce the collectables for {string.Join(", ", reduceFor.Values.Where(t => Left(t) > 0).Select(t => ItemName(t.ItemId)).Distinct())}: {blocked}.", true, now: true);
            return;
        }
        if (Fish.Failure is { } fishFailure) { Stop(fishFailure, true, now: true); return; }
        if (workerError is { } error) { workerError = null; Stop(error, true, now: true); return; }
        if (fisherStuck)
        {
            fisherStuck = false;
            Stop("The fisher couldn't reel in or close the spearfishing window; do that yourself, then start again.", true, now: true);
            return;
        }
        if (!Vnav.Available) { Stop("vnavmesh was unloaded.", true, now: true); return; }

        UpdateFishWaits();
        CountActiveTime();

        if (cleaning)
        {
            cleanup.Tick();
            Headline = "Throwing away extras";
            Detail = cleanup.Status;
            if (!cleanup.Done) return;
            cleaning = false;
            StartCrafting();
            return;
        }

        if (GatherTargets.AllDone(Targets, CountedAsGathered))
        {
            if (craftingBetween && Crafter.MidCraft) return; // let the craft finish first
            // The amount is in, but the fish on the line is landed and the rod comes in first.
            if (!craftingBetween && !FisherIn())
            {
                Headline = "Finishing this catch";
                Detail = Fish.View is { Spear: true }
                    ? "The order's amount is in; finishing this spearfishing node."
                    : "The order's amount is in; landing the fish on the line, then reeling in.";
                return;
            }
            // The amount is in, but a node is gathered to the end.
            if (!craftingBetween && Services.Condition[ConditionFlag.Gathering])
            {
                Headline = "Finishing this node";
                Detail = "The order's amount is in; gathering what's left on the node.";
                return;
            }
            // The fisher's by-catch tally is read before it stops.
            var byCatch = fishBegun ? ByCatchToDiscard().ToList() : new List<(uint, int)>();
            Gather.Pause();
            Gather.Stop();
            StopFisher();
            CancelNodeTrip();
            LearnRates();
            NextWindow = null;
            Add(workTargets.All(t => t.Supply == Supply.Fish) ? "Fishing done."
                : workTargets.Any(t => t.Supply == Supply.Fish) ? "Gathering and fishing done." : "Gathering done.", false);
            if (usedAsBait.Count > 0)
                Add("Caught and used as bait along the way: " + string.Join(", ", usedAsBait.Select(kv => $"{kv.Value}× {ItemName(kv.Key)}")) + ".", false);
            fishWaits.Clear();
            // While earning scrips the extras stay: they become more collectables. By-catch goes either way.
            var extras = Settings.DiscardExtras && !scripPhase ? TargetExtras() : new List<(uint, int)>();
            cleanup.Begin(extras, byCatch);
            if (!cleanup.Done)
            {
                cleaning = true;
                Headline = "Throwing away extras";
                return;
            }
            StartCrafting();
            return;
        }

        if (craftingBetween)
        {
            TickCraftingBetween();
            return;
        }

        var busy = Services.Condition[ConditionFlag.Gathering] || Services.Condition[ConditionFlag.ExecutingGatheringAction]
                   || Services.Condition[ConditionFlag.Fishing] || (worker == Worker.Fish ? Fish.Busy : UpkeepOpen);
        WatchStrayNode();
        switch (Phase)
        {
            case RunPhase.Pausing when busy:
                // The node is gathered to the end; the fisher lands its catch and reels in.
                if (worker == Worker.Fish) FisherIn();
                return;
            case RunPhase.Pausing:
                CancelNodeTrip();
                Gather.Pause();
                Fish.Pause();
                switchingTo = null;
                handoverAt = default;
                Phase = RunPhase.Paused;
                Headline = "Paused";
                Detail = "Resume to carry on.";
                Add("Paused.", false);
                return;
            case RunPhase.Running:
                break;
            default:
                return;
        }

        var decision = Decide();
        NextWindow = decision.NextWindow;
        if (StuckOnUnreachable()) return;
        var (want, work) = WantedWorker();
        if (TickSwitch(want, busy)) return;
        KeepWorkerGoing();
        if (TickNodeTrip(decision, busy)) return;
        if (decision.Mode == GatherMode.CraftMeanwhile && !work && !busy && !Fish.Busy)
        {
            // Only closed windows left, none opening for a while: craft what can be crafted until shortly
            // before. The rod is in (busy covers it), so the crafter can change jobs.
            Gather.Pause();
            Fish.Pause();
            craftingBetween = true;
            yielding = false;
            Crafter.DeferMissing = true;
            Crafter.Resume();
            Headline = CraftingWhile();
            Add(CraftMeanwhileReason(decision.NextWindow ?? TimeSpan.Zero) + "; crafting in the meantime.", false);
            return;
        }

        if (worker == Worker.Fish)
        {
            Detail = Fish.Status;
            Headline = FishingHeadline();
            return;
        }
        Detail = Gather.Status;
        Headline = decision.Mode == GatherMode.Wait || Gather.Waiting ? WaitingHeadline() : "Gathering";
    }

    /// <summary>Crafting between timed windows: hand back in time, or when nothing more can be crafted.</summary>
    private void TickCraftingBetween()
    {
        var decision = Decide();
        NextWindow = decision.NextWindow;
        Detail = Crafter.Status;

        if (Crafter.Failure is { } failure) { Stop(failure, true, now: true); return; }
        if (Crafter.Finished) { if (Phase == RunPhase.Running) HandBackToGathering(log: true); return; }

        if (Crafter.IsPaused)
        {
            if (Phase == RunPhase.Stopping) { Stop(pendingStop ?? "Stopped.", pendingStopProblem, now: true); return; }
            if (Phase == RunPhase.Pausing) { Phase = RunPhase.Paused; Headline = "Paused"; Detail = "Resume to carry on."; Add("Paused.", false); return; }
            if (yielding) { HandBackToGathering(log: true); return; }
        }

        if (Phase != RunPhase.Running || yielding) return;
        // The fisher wants to be at the water early (its lead, the predators for Fisher's Intuition).
        var fishCalls = Fish.HasWorkNow;
        if (decision.Mode != GatherMode.CraftMeanwhile || fishCalls || Crafter.WaitingForMaterials || Crafter.Finished)
        {
            yielding = true;
            Crafter.RequestYield();
            var back = fishCalls && (DueFish() != null || !LandWorkByTargets()) ? "fishing" : "gathering";
            Headline = Crafter.MidCraft ? $"Back to {back} after this craft" : $"Back to {back}";
            return;
        }
        Headline = CraftingWhile();
    }

    /// <summary>
    /// While only closed timed nodes are left: go to where the next one spawns, so crafting and waiting
    /// happen there and gathering starts the moment it opens. True while the trip is under way. Only
    /// while the gatherer is the worker and no fish window comes first (the fisher goes ahead by itself).
    /// </summary>
    private bool TickNodeTrip(GatherDecision decision, bool busy)
    {
        var catalog = plugin.Plans.Catalog!;
        if (tripSpot is { } spot)
        {
            var name = catalog.ItemName(tripItem);
            // The node's own window: other windows (fish) don't end this trip early.
            if (Core.Timing.Eorzea.UntilOpen(spot.Mask, DateTimeOffset.UtcNow) is not { } left || left <= TripCutoff
                || worker != Worker.Land || Fish.HasWorkNow)
            {
                EndNodeTrip();
                return false;
            }
            if (Gather.Running) Gather.Pause();
            nodeTrip.Tick();
            Headline = $"Heading to the {name} node (opens in {Short(left)})";
            Detail = nodeTrip.Status;
            if (nodeTrip.Current == Travel.State.Failed)
            {
                tripFailed.Add(spot);
                Add($"Couldn't get to the {name} node ahead of time ({nodeTrip.Failure}); the gatherer goes there when it opens.", true);
                EndNodeTrip();
                return false;
            }
            if (nodeTrip.Current != Travel.State.Arrived) return true;
            Add($"At the {name} node; it opens in {Short(left)}.", false);
            EndNodeTrip();
            return false;
        }

        if (busy || worker != Worker.Land || Fish.Busy || Fish.HasWorkNow || !Settings.TravelAhead || DateTime.UtcNow < nextTripCheck) return false;
        // Only while nothing but closed windows is left.
        if (decision.NextWindow is not { } soonest || soonest <= TripCutoff * 3) return false;
        nextTripCheck = DateTime.UtcNow.AddSeconds(10);
        if (NextTimedSpot() is not { } next || next.Wait <= TripCutoff * 3) return false;
        // A fish window comes first: the fisher goes there in time by itself.
        if (SoonestFishWindow() is { } fish && fish.Wait < next.Wait) return false;
        if (Near(next.Spot)) return false;

        Gather.Pause();
        tripSpot = next.Spot;
        tripItem = next.ItemId;
        // Aim for the middle of the spawn area; anywhere well inside it is close enough.
        nodeTrip.GoTo($"the {catalog.ItemName(next.ItemId)} node", next.Spot.TerritoryId, next.Spot.Position, Math.Clamp(next.Spot.Radius * 0.4f, 15f, 40f), null);
        Add($"Heading to where {catalog.ItemName(next.ItemId)} spawns while its node is closed.", false);
        return true;
    }

    /// <summary>The spawn area of the soonest timed node still needed, among zones the character can reach.</summary>
    private (uint ItemId, TimedSpot Spot, TimeSpan Wait)? NextTimedSpot()
    {
        var catalog = plugin.Plans.Catalog!;
        var now = DateTimeOffset.UtcNow;
        var reach = new Dictionary<uint, bool>();
        (uint, TimedSpot, TimeSpan)? best = null;
        var bestWait = TimeSpan.MaxValue;
        foreach (var t in workTargets.Where(t => Timed(t) && WorkLeft(t) > 0 && !Gather.Unreachable.Contains(t.ItemId)))
            foreach (var s in catalog.TimedSpots(t.ItemId))
            {
                if (tripFailed.Contains(s) || Core.Timing.Eorzea.UntilOpen(s.Mask, now) is not { } wait) continue;
                if (wait > bestWait || (wait == bestWait && s.TerritoryId != Services.ClientState.TerritoryType)) continue;
                if (!(reach.TryGetValue(s.TerritoryId, out var ok) ? ok : reach[s.TerritoryId] = Teleporter.CanReach(s.TerritoryId))) continue;
                best = (t.ItemId, s, wait);
                bestWait = wait;
            }
        return best;
    }

    private static bool Near(TimedSpot spot)
    {
        if (Services.ClientState.TerritoryType != spot.TerritoryId || Services.Objects.LocalPlayer is not { } me) return false;
        return System.Numerics.Vector2.Distance(new System.Numerics.Vector2(me.Position.X, me.Position.Z), spot.Position) <= Math.Max(spot.Radius, 40f) + 20f;
    }

    /// <summary>Trip over (arrived, gave up, or the window is near): the gatherer carries on.</summary>
    private void EndNodeTrip()
    {
        CancelNodeTrip();
        if (worker == Worker.Land) ResumeGatherer();
    }

    private void CancelNodeTrip()
    {
        if (tripSpot == null) return;
        nodeTrip.Cancel();
        tripSpot = null;
    }

    /// <summary>Back from crafting between windows (or a pause, a duty, a knock-out): whoever has work carries on.</summary>
    private void HandBackToGathering(bool log)
    {
        craftingBetween = false;
        yielding = false;
        var (want, _) = WantedWorker();
        var next = want ?? worker;
        Activate(next);
        Headline = next == Worker.Fish ? "Fishing" : "Gathering";
        if (log) Add(next == Worker.Fish ? "Back to fishing." : "Back to gathering for the timed nodes.", false);
    }

    private GatherDecision Decide()
    {
        var catalog = plugin.Plans.Catalog!;
        // Targets neither worker can get don't hold the stage in gathering (their worker reports them).
        var targets = workTargets.Select(t => new ScheduledTarget(t.ItemId,
            t.Supply == Supply.Fish ? Supply.Fish : Timed(t) ? Supply.GatherTimed : Supply.Gather,
            Reachable(t) ? WorkLeft(t) : 0, Timed(t) ? catalog.TimedMask(t.ItemId) : 0,
            t.Supply == Supply.Fish ? FishWait(t.ItemId) : null));
        var allowed = Settings.CraftWhileWaiting && Crafter.HasReadyWork();
        return Interleave.Decide(targets, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(Settings.TimedLeadMinutes), allowed);
    }

    private bool Reachable(GatherTarget t)
        => t.Supply switch
        {
            Supply.Fish => !Fish.Unreachable.Contains(t.ItemId),
            // An item from reduction: any of its collectables that a worker can still get.
            Supply.Reduce => workTargets.Any(w => reduceFor.TryGetValue(w.ItemId, out var r) && r.ItemId == t.ItemId && Reachable(w)),
            _ => !Gather.Unreachable.Contains(t.ItemId),
        };

    private void TickCrafting()
    {
        Detail = Crafter.Status;
        if (Crafter.Finished)
        {
            if (Crafter.Failure is { } failure) Stop(failure, true, now: true);
            else if (pendingStop != null) Stop(pendingStop, pendingStopProblem, now: true);
            else Finish();
            return;
        }
        if (Crafter.IsPaused)
        {
            if (Phase == RunPhase.Stopping) { Stop(pendingStop ?? "Stopped.", pendingStopProblem, now: true); return; }
            if (Phase == RunPhase.Pausing)
            {
                Phase = RunPhase.Paused;
                Headline = "Paused";
                Detail = "Resume to carry on crafting.";
                Add("Paused.", false);
            }
        }
    }

    /// <summary>Remembers gathered materials a craft used, so they still count as gathered.</summary>
    private void OnCrafted(RecipeInfo recipe)
    {
        if (Stage != RunStage.Gather) return;
        foreach (var ing in recipe.Ingredients)
            if (Targets.Any(t => t.ItemId == ing.ItemId))
                consumed[ing.ItemId] = consumed.GetValueOrDefault(ing.ItemId) + ing.Amount;
    }

    /// <summary>
    /// The fisher put a fish it caught back in the water as bait (mooch, swimbait). Only tallied for the
    /// log: unlike materials a craft used, such a fish never reaches the order, so it doesn't count as
    /// gathered. The target's count goes back up and the fisher catches another one; counting it here
    /// would leave the order short and the extras cleanup would throw the order's own fish away.
    /// </summary>
    private void OnUsedAsBait(uint itemId, int amount)
    {
        if (amount > 0) usedAsBait[itemId] = usedAsBait.GetValueOrDefault(itemId) + amount;
    }

    /// <summary>
    /// Learns the gathering rate from time spent gathering on land, and the fishing rate from time spent
    /// rod fishing (waiting for a window or GP left out). Collectables and spearfishing go at their own pace.
    /// </summary>
    private void LearnRates()
    {
        var t = plugin.Config.Timings;
        var changed = false;
        var land = Targets.Where(x => x.Supply != Supply.Fish).ToList();
        var gathered = land.Sum(x => x.Remaining);
        if (landActiveSeconds >= 60 && gathered >= 10 && !land.Any(x => GameState.GatheredAsCollectable(x.ItemId)))
        {
            t.GatheredPerMinute = Math.Clamp(Timings.Blend(t.GatheredPerMinute, gathered / (landActiveSeconds / 60)), 1, 120);
            changed = true;
        }
        var catalog = plugin.Plans.Catalog;
        var caught = Targets.Where(x => x.Supply == Supply.Fish && catalog?.Gather.Fish(x.ItemId) is { Spear: false }).Sum(x => x.Remaining);
        if (fishActiveSeconds >= 120 && caught >= 3)
        {
            t.FishPerMinute = Math.Clamp(Timings.Blend(t.FishPerMinute, caught / (fishActiveSeconds / 60)), 0.1, 20);
            changed = true;
        }
        if (changed) plugin.Config.Save();
    }

    /// <summary>Estimated time until the run is done, or null when not running.</summary>
    public TimeSpan? TimeLeft()
    {
        if (!Active) return null;
        var plans = plugin.Plans;
        var crafting = TimeSpan.Zero;
        if (Crafter.Current is { } cur) crafting += plans.PerCraft(cur.Recipe, cur.Hq) * Math.Max(0, cur.Crafts - Crafter.DoneThisRecipe);
        foreach (var job in Crafter.Upcoming) crafting += plans.PerCraft(job.Recipe, job.Hq) * job.Crafts;
        if (Stage == RunStage.Craft || !Crafter.Started) return Stage == RunStage.Craft ? crafting : crafting + plans.Estimate.Crafting;
        var t = plugin.Config.Timings;
        var gather = Targets.Where(x => x.Supply != Supply.Fish).ToList();
        var left = gather.Where(x => !GameState.GatheredAsCollectable(x.ItemId)).Sum(Left);
        var fish = Targets.Where(x => x.Supply == Supply.Fish).Sum(Left);
        // Collectables go by node visits, with the wait for GP before the regular ones.
        var collect = gather.Where(x => GameState.GatheredAsCollectable(x.ItemId)).ToList();
        var visits = collect.Sum(x => Estimator.Visits(Left(x), t));
        var gpWait = CollectableGpWait(collect.Where(x => x.Supply == Supply.Gather).Sum(x => Estimator.Visits(Left(x), t)), t);
        // Only closed timed nodes and fish windows left: the wait for the next window comes on top.
        var wait = NextWindow is { } w && w > TimeSpan.Zero ? w : TimeSpan.Zero;
        var gathering = TimeSpan.FromMinutes(left / Math.Max(0.1, t.GatheredPerMinute) + fish / Math.Max(0.05, t.FishPerMinute))
                        + TimeSpan.FromSeconds(visits * t.SecondsPerCollectableVisit) + gpWait + wait;
        // Fish that only bite at some hours or in some weather can't be in before their window (plus their catches).
        var windows = fishWindowsEnd - DateTime.UtcNow;
        if (windows > gathering) gathering = windows;
        return gathering + crafting;
    }

    /// <summary>Waiting for GP before the collectable nodes still to visit, from the GP there is now.</summary>
    private TimeSpan CollectableGpWait(int visits, Timings t)
    {
        if (visits == 0 || !Settings.WaitForCollectableGp) return TimeSpan.Zero;
        var job = GameState.CurrentJob;
        var gatherer = job is 16 or 17;
        var p = Services.Objects.LocalPlayer;
        var maxGp = gatherer && p != null ? (int)p.MaxGp
            : new uint[] { 16, 17 }.Max(j => plugin.Plans.Profile?.Job(j) is { Stats.Length: >= 3 } js ? js.Stats[2] : 0);
        if (maxGp <= 0) maxGp = Core.Gathering.CollectableRotation.FullRotationGp;
        // A collectable node open right now counts as a normal visit, from the GP it was opened with.
        var gp = Gather.VisitStartGp is { } opened ? (int)opened : gatherer && p != null ? (int)p.CurrentGp : maxGp;
        var cordials = Settings.UseCordials ? Cordials.InBags() : new List<Core.Gathering.Cordial>();
        var readyIn = Settings.UseCordials ? Cordials.SecondsUntilReady() : 0;
        return Core.Gathering.GpBudget.Simulate(visits, gp, maxGp, cordials, (int)t.GpPerCollectableVisit, t.SecondsPerCollectableVisit,
            t.SecondsInCollectableNode, readyIn).Wait;
    }

    /// <summary>Still to gather for one target: bags plus what crafting already used, against the target.</summary>
    public int Left(GatherTarget t) => GatherTargets.Left(t, CountedAsGathered);

    private int CountedAsGathered(uint itemId) => BagNq(itemId) + consumed.GetValueOrDefault(itemId);

    /// <summary>What counts as gathered: normal-quality items in the main bags, and the collectables of items that only come as collectables.</summary>
    private static int BagNq(uint itemId) => GameState.GatheredInBags(itemId);

    // ------------------------------------------------------------------ choosing the worker

    /// <summary>Clears the worker hand-over state for a new gathering stage.</summary>
    private void ResetWorkers()
    {
        worker = Worker.Land;
        switchingTo = null;
        handoverAt = default;
        landWaitingSince = null;
        landIdleUntil = default;
        reelInSince = null;
        reelInHard = false;
        fisherStuck = false;
        strayNodeSince = null;
        workerError = null;
        onlyUnreachableSince = null;
        fishWaits.Clear();
        fishWindowsEnd = default;
        usedAsBait.Clear();
    }

    /// <summary>
    /// Which worker should work now, and whether either has something to do right now: a fish whose
    /// window is open (or about to) first, then nodes that are up, then fish that always bite. With
    /// nothing to do, whoever has the next window waits for it (the gatherer goes ahead to timed nodes).
    /// </summary>
    private (Worker? Want, bool Work) WantedWorker()
    {
        var fishNow = Fish.HasWorkNow;
        if (fishNow && DueFish() != null) return (Worker.Fish, true);
        if (LandHasWork()) return (Worker.Land, true);
        if (fishNow) return (Worker.Fish, true);

        var landLeft = workTargets.Any(t => t.Supply != Supply.Fish && WorkLeft(t) > 0 && !Gather.Unreachable.Contains(t.ItemId));
        var fishLeft = fishWaits.Keys.Any(id => !Fish.Unreachable.Contains(id));
        if (landLeft != fishLeft) return (landLeft ? Worker.Land : Worker.Fish, false);
        if (!landLeft) return (null, false);
        var landNext = LandNextWindow();
        var fishNext = SoonestFishWindow()?.Wait;
        if (landNext is { } l && (fishNext is not { } f || l <= f)) return (Worker.Land, false);
        return (fishNext != null ? Worker.Fish : null, false);
    }

    /// <summary>
    /// The gatherer has something to do now. While it works (and has had a moment to look), its own view
    /// counts: busy with a node, a trip or upkeep is work, so is waiting for a timed node that opens within
    /// the lead, and "nothing to gather" for a few seconds isn't. Otherwise it goes by the targets: a regular
    /// item, or a timed node that is up or opens within the lead.
    /// </summary>
    private bool LandHasWork()
    {
        var now = DateTime.UtcNow;
        if (worker == Worker.Land && switchingTo == null && Gather.Running && now - landActiveSince >= TimeSpan.FromSeconds(1))
        {
            if (!Gather.Waiting)
            {
                landWaitingSince = null;
                landIdleUntil = default;
                return true;
            }
            // A timed node that isn't up yet but opens within the lead: the gatherer only takes nodes that are up,
            // so it waits for this one and sets out the moment it opens. That's its own wait, not "nothing to
            // gather" (handing over would bring it back late, after the catch under way).
            if (!Gather.WaitingToRetry && LandNextWindow(closed: true) is { } opens && opens <= TimeSpan.FromMinutes(Settings.TimedLeadMinutes))
            {
                landWaitingSince = null;
                landIdleUntil = default;
                return true;
            }
            landWaitingSince ??= now;
            if (now - landWaitingSince < LandSettle) return LandWorkByTargets();
            // It finds nothing although the targets say otherwise (nodes given up on for a while): fish meanwhile,
            // and look again after a while, or as the next timed node opens if that comes first.
            if (LandWorkByTargets())
                landIdleUntil = now + (LandNextWindow(closed: true) is { } next && next < LandRecheck ? next : LandRecheck);
            return false;
        }
        return LandWorkByTargets() && now >= landIdleUntil;
    }

    /// <summary>Materia extraction or aetherial reduction is open (the gatherer's upkeep between nodes): not a moment to hand over.</summary>
    private static bool UpkeepOpen => Interact.IsOpen("Materialize") || Interact.IsOpen("MaterializeDialog")
        || Interact.IsOpen("PurifyItemSelector") || Interact.IsOpen("PurifyResult");

    /// <summary>A land target is up now or opens within the lead, going by the targets alone.</summary>
    private bool LandWorkByTargets()
    {
        var catalog = plugin.Plans.Catalog!;
        var now = DateTimeOffset.UtcNow;
        var lead = TimeSpan.FromMinutes(Settings.TimedLeadMinutes);
        foreach (var t in workTargets)
        {
            if (t.Supply == Supply.Fish || WorkLeft(t) <= 0 || Gather.Unreachable.Contains(t.ItemId)) continue;
            if (!Timed(t)) return true;
            var mask = catalog.TimedMask(t.ItemId);
            if (mask == 0 || Core.Timing.Eorzea.UntilOpen(mask, now) is { } w && w <= lead) return true;
        }
        return false;
    }

    /// <summary>
    /// The timed node still needed that opens soonest (zero when one is up), or null. With <paramref name="closed"/>,
    /// only nodes that aren't up yet count (one that is up may be one the gatherer can't use at this hour).
    /// </summary>
    private TimeSpan? LandNextWindow(bool closed = false)
    {
        var catalog = plugin.Plans.Catalog!;
        var now = DateTimeOffset.UtcNow;
        TimeSpan? best = null;
        foreach (var t in workTargets.Where(t => Timed(t) && WorkLeft(t) > 0 && !Gather.Unreachable.Contains(t.ItemId)))
            if (catalog.TimedMask(t.ItemId) is var mask and not 0 && Core.Timing.Eorzea.UntilOpen(mask, now) is { } w
                && (!closed || w > TimeSpan.Zero) && (best == null || w < best))
                best = w;
        return best;
    }

    /// <summary>The fish target with a window that bites soonest (zero: now), or null when none bites within the forecast.</summary>
    private (uint ItemId, TimeSpan Wait)? SoonestFishWindow()
    {
        (uint ItemId, TimeSpan Wait)? best = null;
        foreach (var (id, wait) in fishWaits)
            if (wait is { } w && w != TimeSpan.MaxValue && !Fish.Unreachable.Contains(id) && (best is not { } b || w < b.Wait))
                best = (id, w);
        return best;
    }

    /// <summary>A fish with a window that bites now, or soon enough that the fisher should take over (<see cref="FishLead"/>).</summary>
    private (uint ItemId, TimeSpan Wait)? DueFish()
    {
        (uint ItemId, TimeSpan Wait)? best = null;
        foreach (var (id, wait) in fishWaits)
            if (wait is { } w && w != TimeSpan.MaxValue && w <= FishLead(id) && !Fish.Unreachable.Contains(id) && (best is not { } b || w < b.Wait))
                best = (id, w);
        return best;
    }

    /// <summary>
    /// How early the fisher takes over for a fish with a window: its own lead (the minutes to be at the spot
    /// before it opens, and catching the fish for Fisher's Intuition first, as early as the angler starts on them;
    /// <see cref="Fishing.Fisher.LeadFor"/>) and the trip there.
    /// </summary>
    private TimeSpan FishLead(uint itemId) => Fish.LeadFor(itemId) + FishTrip;

    /// <summary>Asks the fisher when each fish still needed bites, and when the last of them can be in.</summary>
    private void UpdateFishWaits()
    {
        fishWaits.Clear();
        var perMinute = Math.Max(0.05, plugin.Config.Timings.FishPerMinute);
        var end = TimeSpan.Zero;
        foreach (var t in workTargets)
        {
            if (t.Supply != Supply.Fish) continue;
            var left = WorkLeft(t);
            if (left <= 0) continue;
            // Collectables waiting to be reduced: that's work now, whatever the fish's window.
            var wait = GathererLeft(t) <= 0 ? TimeSpan.Zero : Fish.WaitFor(t.ItemId);
            fishWaits[t.ItemId] = wait;
            if (wait is { } w && w != TimeSpan.MaxValue && w + TimeSpan.FromMinutes(left / perMinute) is var done && done > end) end = done;
        }
        fishWindowsEnd = end > TimeSpan.Zero ? DateTime.UtcNow + end : default;
    }

    /// <summary>
    /// Hands the gathering stage to the wanted worker once the current one is between nodes or catches:
    /// the fisher lands its catch and reels in first; the gatherer finishes the node it's on, and a short
    /// pause catches a node it had just pressed. True while a hand-over is under way.
    /// </summary>
    private bool TickSwitch(Worker? want, bool busy)
    {
        var next = want ?? switchingTo;
        if (next is not { } target || target == worker)
        {
            if (switchingTo != null) CancelSwitch();
            return false;
        }
        switchingTo = target;
        if (worker == Worker.Fish)
        {
            if (!FisherIn())
            {
                Headline = Fish.View is { Spear: true } ? "Gathering after this spearfishing node" : "Gathering after this catch";
                Detail = Fish.Status;
                return true;
            }
        }
        else if (busy)
        {
            // On a node (or one that opened as the gatherer was paused): it's gathered to the end first.
            if (handoverAt != default) { Gather.Resume(); handoverAt = default; }
            Headline = "Fishing after this node";
            Detail = Gather.Status;
            return true;
        }
        else if (handoverAt == default)
        {
            CancelNodeTrip();
            Gather.Pause();
            handoverAt = DateTime.UtcNow + Handover;
            return true;
        }
        else if (DateTime.UtcNow < handoverAt) return true;

        string? why = null;
        if (target == Worker.Fish)
            why = DueFish() is { } due ? $"{ItemName(due.ItemId)} {(due.Wait > TimeSpan.Zero ? "bites in " + Short(due.Wait) : "bites now")}; fishing first."
                : !Targets.Any(t => t.Supply != Supply.Fish && Left(t) > 0) ? "Gathering done; on to fishing."
                : Fish.HasWorkNow ? "Nothing to gather right now; fishing meanwhile." : null;
        else if (LandWorkByTargets()) why = "Back to gathering.";
        Activate(target, why);
        return false;
    }

    /// <summary>The hand-over isn't wanted any more: the current worker carries on.</summary>
    private void CancelSwitch()
    {
        if (worker == Worker.Fish) Fish.Resume(); // withdraws the pause request; the fishing goes on
        else if (handoverAt != default) ResumeGatherer();
        switchingTo = null;
        handoverAt = default;
        reelInSince = null;
        reelInHard = false;
    }

    /// <summary>Pauses the current worker and starts the other one (both may be paused, after crafting or a pause).</summary>
    private void Activate(Worker next, string? why = null)
    {
        switchingTo = null;
        handoverAt = default;
        reelInSince = null;
        reelInHard = false;
        strayNodeSince = null;
        if (next == Worker.Fish)
        {
            CancelNodeTrip();
            Gather.Pause();
            Fish.Resume();
        }
        else
        {
            Fish.Pause();
            ResumeGatherer();
        }
        worker = next;
        if (why != null) Add(why, false);
    }

    /// <summary>The gatherer carries on (a hand-over, a trip ahead that ended): its "nothing to gather" counts afresh once it has looked again.</summary>
    private void ResumeGatherer()
    {
        Gather.Resume();
        landWaitingSince = null;
        landActiveSince = DateTime.UtcNow;
    }

    /// <summary>The active worker runs: a "Keep going" before the catch was in, or a resume that came early, can leave it paused.</summary>
    private void KeepWorkerGoing()
    {
        if (switchingTo != null || tripSpot != null) return;
        if (worker == Worker.Fish) { if (Fish.IsPaused) Fish.Resume(); }
        else if (!Gather.Running) ResumeGatherer();
    }

    /// <summary>
    /// True once the rod is in and no catch or spearfishing node is under way. Until then the fisher is
    /// asked to finish the catch and reel in; after a long while it is told to let the bite go, then Quit
    /// is pressed here, and if even that fails the run stops (next check).
    /// </summary>
    private bool FisherIn()
    {
        if (!Fish.Busy && !Services.Condition[ConditionFlag.Fishing])
        {
            reelInSince = null;
            reelInHard = false;
            return true;
        }
        var now = DateTime.UtcNow;
        if (reelInSince is not { } since)
        {
            reelInSince = now;
            Fish.RequestPause();
            return false;
        }
        if (!reelInHard && now - since > ReelInPatience)
        {
            reelInHard = true;
            Fish.Pause();
            Add("The catch under way took too long; letting it go and reeling in.", true);
        }
        else if (reelInHard && now - since > ReelInPatience + TimeSpan.FromSeconds(20))
        {
            LastResortQuit();
            if (now - since > ReelInPatience + TimeSpan.FromMinutes(1)) fisherStuck = true;
        }
        return false;
    }

    /// <summary>
    /// A gathering window that opened as the fisher took over (the gatherer had just pressed the node):
    /// the gatherer works it to the end first. Spearfishing opens its own window, so this never mistakes it.
    /// </summary>
    private void WatchStrayNode()
    {
        if (worker != Worker.Fish || (!Interact.IsOpen("Gathering") && !Interact.IsOpen("GatheringMasterpiece")))
        {
            strayNodeSince = null;
            return;
        }
        strayNodeSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - strayNodeSince < TimeSpan.FromSeconds(2)) return;
        Activate(Worker.Land, "A node opened just as fishing took over; gathering it first.");
    }

    /// <summary>Everything still needed is something neither worker can get: the run stops (after a moment, so a worker can say why first).</summary>
    private bool StuckOnUnreachable()
    {
        var left = Targets.Where(t => Left(t) > 0).ToList();
        if (left.Count == 0 || left.Any(Reachable))
        {
            onlyUnreachableSince = null;
            return false;
        }
        onlyUnreachableSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - onlyUnreachableSince < TimeSpan.FromSeconds(10)) return false;
        Stop($"Nothing left that the run can get: {string.Join(", ", left.Select(t => ItemName(t.ItemId)))}. Get {(left.Count == 1 ? "it" : "them")} yourself, then start again.", true, now: true);
        return true;
    }

    /// <summary>Time the active worker really works (not paused, waiting for a window or for GP, crafting or handing over).</summary>
    private void CountActiveTime()
    {
        var now = DateTime.UtcNow;
        // Capped: checks pause while knocked out or in a duty, and that time isn't gathering.
        var seconds = Math.Min(2, (now - lastGatherTick).TotalSeconds);
        lastGatherTick = now;
        if (Phase != RunPhase.Running || craftingBetween || cleaning || switchingTo != null) return;
        if (worker == Worker.Land)
        {
            if (tripSpot == null && Gather.Running && !Gather.Waiting && !Gather.WaitingForGp) landActiveSeconds += seconds;
            return;
        }
        // Rod fishing only; standing at the water before the window opens (the fish's, or that of a fish Fisher's
        // Intuition needs) or holding back the last of those isn't fishing time.
        if (Fish.Waiting || Fish.WaitingForGp || Fish.WaitingForWindow || Fish.View is not { Spear: false, Fish: not 0 } view) return;
        if (FishWait(view.Fish) is not { } wait || wait == TimeSpan.Zero) fishActiveSeconds += seconds;
    }

    // ------------------------------------------------------------------ fishing: stopping, by-catch, headlines

    /// <summary>
    /// Ends fishing. With the rod still out (or a catch or spearfishing window under way), the fisher keeps
    /// its ticks for a while after this until it has reeled in: <see cref="TickFishWindDown"/>.
    /// </summary>
    private void StopFisher()
    {
        if (!fishBegun) return;
        var gone = !Services.ClientState.IsLoggedIn || Services.Objects.LocalPlayer is not { IsDead: false };
        var rodOut = Fish.Busy || Services.Condition[ConditionFlag.Fishing];
        Fish.Stop(now: gone);
        if (!rodOut || gone) return;
        fishWindDownUntil = DateTime.UtcNow + WindDown;
        windDownTickFailed = false;
    }

    /// <summary>The fisher's tick threw during the wind-down: only Quit is pressed from then on.</summary>
    private bool windDownTickFailed;

    /// <summary>
    /// Fishing stopped with something still out (the run ended, or went on past gathering): the fisher keeps its
    /// ticks while it is busy, as it reels in, lets the bite go and closes a spearfishing window; Quit is pressed
    /// here too in the last seconds. A gathering stage ticks the fisher itself.
    /// </summary>
    private void TickFishWindDown()
    {
        if (!Fish.Busy && !Services.Condition[ConditionFlag.Fishing])
        {
            fishWindDownUntil = default;
            return;
        }
        if (Active && Stage == RunStage.Gather) return;
        var now = DateTime.UtcNow;
        if (now > fishWindDownUntil)
        {
            fishWindDownUntil = default;
            if (Services.Condition[ConditionFlag.Fishing]) Add("The fishing rod is still out; use Quit to put it away.", true);
            return;
        }
        if (Services.Objects.LocalPlayer is not { IsDead: false } || Services.Condition[ConditionFlag.BetweenAreas]) return;
        if (!windDownTickFailed)
        {
            try { Fish.Tick(); }
            catch (Exception e)
            {
                Services.Log.Error(e, "The fisher's tick failed while reeling in");
                windDownTickFailed = true;
                if (fishWindDownUntil - now > TimeSpan.FromSeconds(10)) fishWindDownUntil = now + TimeSpan.FromSeconds(10);
            }
        }
        // The fisher should have reeled in by now: Quit as a last resort.
        if (fishWindDownUntil - now < TimeSpan.FromSeconds(10)) LastResortQuit();
    }

    /// <summary>The fisher didn't reel in: Quit, at most every 3 s, never while dead, zoning or in a cutscene.</summary>
    private void LastResortQuit()
    {
        var c = Services.Condition;
        if (!c[ConditionFlag.Fishing] || DateTime.UtcNow < nextQuitPress) return;
        if (Services.Objects.LocalPlayer is not { IsDead: false } || c[ConditionFlag.BetweenAreas]
            || c[ConditionFlag.OccupiedInCutSceneEvent] || c[ConditionFlag.WatchingCutscene] || c[ConditionFlag.WatchingCutscene78]) return;
        nextQuitPress = DateTime.UtcNow.AddSeconds(3);
        const FFXIVClientStructs.FFXIV.Client.Game.ActionType action = FFXIVClientStructs.FFXIV.Client.Game.ActionType.Action;
        if (GameState.CanUse(action, FishAccess.Quit)) GameState.Use(action, FishAccess.Quit);
    }

    /// <summary>
    /// Timed nodes, and fish that only bite at some hours or in some weather: their own, their mooch chain's, or a
    /// predator's (<see cref="Fishing.Fisher.Windowed"/>; "Also from timed nodes and rare fish").
    /// </summary>
    private bool Windowed(GatherTarget t)
        => t.Supply == Supply.GatherTimed || (t.Supply == Supply.Fish && Fish.Windowed(t.ItemId));

    /// <summary>
    /// What the gatherer and the fisher go for: the targets, with each item from aetherial reduction swapped for the
    /// collectables it comes from (Levin Quartz and Calamus Root on ephemeral nodes, Purple Palate fished, for
    /// Levinchrome Aethersand). The run's scheduling goes by these; the order's progress by <see cref="Targets"/>.
    /// </summary>
    private List<GatherTarget> workTargets = new();

    /// <summary>
    /// The collectables an item from aetherial reduction comes from, with their state now (for the Run page): those
    /// this run gathers or fishes for it, or before gathering starts every one a run can get.
    /// </summary>
    public IReadOnlyList<ReductionSource> SourcesOf(GatherTarget target)
    {
        var catalog = plugin.Plans.Catalog;
        if (catalog == null || target.Supply != Supply.Reduce) return Array.Empty<ReductionSource>();
        var ids = reduceFor.Where(kv => kv.Value.ItemId == target.ItemId).Select(kv => kv.Key).ToList();
        if (ids.Count == 0) ids = catalog.ReducedFrom(target.ItemId).ToList();
        var now = DateTimeOffset.UtcNow;
        var list = new List<ReductionSource>();
        foreach (var id in ids)
        {
            var waiting = GameState.Collectables(id).Count;
            if (catalog.Sources(id).Has(Obtain.Fish))
            {
                var spot = catalog.Gather.Fish(id)?.Spots.Select(catalog.Gather.Spot).OfType<Core.Fishing.FishingSpotInfo>().FirstOrDefault();
                var zone = spot != null ? FishAccess.Zone(spot.TerritoryId) : "";
                if (Fish.Unreachable.Contains(id)) { list.Add(new ReductionSource(id, FishAccess.Fisher, zone, "the fisher can't get it (see the log)", true, waiting)); continue; }
                var wait = fishWaits.TryGetValue(id, out var w) ? w : Fish.WaitFor(id);
                var state = wait switch
                {
                    null => "bites any time",
                    { } x when x == TimeSpan.MaxValue => "doesn't bite within the weather forecast",
                    { } x when x <= TimeSpan.Zero => "bites now",
                    { } x => $"bites in {Short(x)}",
                };
                list.Add(new ReductionSource(id, FishAccess.Fisher, zone, state, false, waiting));
                continue;
            }
            var points = catalog.GatherPoints(id);
            var job = points.Select(p => p.ClassJobId).FirstOrDefault();
            var where = points.Count > 0 ? FishAccess.Zone(points[0].TerritoryId) : "";
            if (Gather.Unreachable.Contains(id)) { list.Add(new ReductionSource(id, job, where, "no node the run can reach (see the log)", true, waiting)); continue; }
            var mask = catalog.TimedMask(id);
            if (mask == 0 || catalog.Sources(id).Has(Obtain.Gather)) { list.Add(new ReductionSource(id, job, where, "regular nodes", false, waiting)); continue; }
            var opens = Core.Timing.Eorzea.UntilOpen(mask, now);
            var text = opens is not { } o ? "never up" : o <= TimeSpan.Zero ? "node up now" : $"node opens in {Short(o)}";
            list.Add(new ReductionSource(id, job, where, $"{text} ({Hours(mask)} ET)", opens == null, waiting));
        }
        return list;
    }

    /// <summary>"00:00–04:00", "04:00–08:00 and 20:00–24:00": the Eorzea hours of an uptime mask.</summary>
    private static string Hours(uint mask)
    {
        var spans = new List<string>();
        for (var h = 0; h < 24; h++)
        {
            if ((mask & (1u << h)) == 0 || (h > 0 && (mask & (1u << (h - 1))) != 0)) continue;
            var end = h;
            while (end < 24 && (mask & (1u << end)) != 0) end++;
            spans.Add($"{h:00}:00–{end:00}:00");
        }
        // A window across midnight reads as one: 20:00–04:00.
        if (spans.Count > 1 && spans[0].StartsWith("00:00") && spans[^1].EndsWith("24:00"))
        {
            spans[^1] = spans[^1][..5] + spans[0][5..];
            spans.RemoveAt(0);
        }
        return spans.Count <= 1 ? spans.FirstOrDefault() ?? "" : string.Join(", ", spans.Take(spans.Count - 1)) + " and " + spans[^1];
    }

    /// <summary>"Levinchrome Aethersand comes from reducing Levin Quartz (MIN, Living Memory, node 00:00–04:00 ET), ...".</summary>
    private void LogReductionSources(IEnumerable<GatherTarget> targets)
    {
        foreach (var t in targets.Where(t => t.Supply == Supply.Reduce))
        {
            var parts = SourcesOf(t).Select(src =>
            {
                var job = Core.Character.ClassJobs.Abbreviation(src.ClassJobId);
                var where = src.Zone.Length > 0 ? ", " + src.Zone : "";
                var mask = plugin.Plans.Catalog!.TimedMask(src.ItemId);
                var how = src.ClassJobId == FishAccess.Fisher ? "fished"
                    : mask != 0 && !plugin.Plans.Catalog!.Sources(src.ItemId).Has(Obtain.Gather) ? $"node {Hours(mask)} ET" : "regular nodes";
                return $"{ItemName(src.ItemId)} ({job}{where}, {how})";
            }).ToList();
            if (parts.Count > 0)
                Add($"{ItemName(t.ItemId)} comes from reducing {string.Join(", ", parts)}. Whichever is up is gathered or fished as a collectable and reduced.", false);
        }
    }

    /// <summary>Collectables gathered or fished for a reduction wait in the bags while the item still lacks.</summary>
    private bool ReducePending(GatherTarget t)
        => reduceFor.TryGetValue(t.ItemId, out var target) && Left(target) > 0 && GameState.Collectables(t.ItemId).Count > 0;

    /// <summary>
    /// What's left of a work target for the scheduling: what its worker still has to get, and at least one while its
    /// collectables wait to be reduced, so that worker stays on and reduces them (between nodes or catches).
    /// </summary>
    private int WorkLeft(GatherTarget t) => Math.Max(GathererLeft(t), ReducePending(t) ? 1 : 0);

    /// <summary>A timed node's item to wait for; not while its collectables are there to reduce now.</summary>
    private bool Timed(GatherTarget t) => t.Supply == Supply.GatherTimed && !(GathererLeft(t) <= 0 && ReducePending(t));

    /// <summary>
    /// The gathered targets whose extras the clean-up throws away (Settings), each with how many to keep.
    /// Counts are plain normal-quality items in the main bags (<see cref="GameState.BagCount(uint, bool)"/>;
    /// high-quality items and collectables are never touched). Keep what the order still needs there: the
    /// target (what the bags held when gathering started plus what the order still needed) less what crafting
    /// meanwhile already used up. The crafter takes normal-quality materials before high-quality ones, so this
    /// never drops below what the bags held before gathering, less what crafting took of it: only what this
    /// gathering stage brought in over the order's amount goes. Crystals always stay.
    /// </summary>
    /// <summary>
    /// For the gatherer, a reduction target is the collectables it comes from (source item → the target). A
    /// collectable the order needs as itself is never gathered for reduction, and two targets don't share one.
    /// </summary>
    private readonly Dictionary<uint, GatherTarget> reduceFor = new();

    /// <summary>The workers' targets: reduction targets swapped for the collectables they come from (gathered or fished).</summary>
    private List<GatherTarget> GathererTargets(List<GatherTarget> targets)
    {
        reduceFor.Clear();
        var catalog = plugin.Plans.Catalog!;
        var direct = targets.Where(t => t.Supply != Supply.Reduce).Select(t => t.ItemId).ToHashSet();
        var list = new List<GatherTarget>();
        foreach (var t in targets)
        {
            if (t.Supply != Supply.Reduce) { list.Add(t); continue; }
            foreach (var source in catalog.ReducedFrom(t.ItemId))
            {
                if (direct.Contains(source) || reduceFor.ContainsKey(source)) continue;
                reduceFor[source] = t;
                // Gathered (regular or timed nodes) or fished (rod or spearfishing), as a collectable.
                var src = catalog.Sources(source);
                var supply = src.Has(Obtain.Gather) ? Supply.Gather : src.Has(Obtain.GatherTimed) ? Supply.GatherTimed : Supply.Fish;
                list.Add(new GatherTarget(source, supply, t.Remaining, 0));
            }
        }
        return list;
    }

    /// <summary>
    /// How many more the gatherer should gather: for a reduction's collectables, what the target still lacks less
    /// the collectables already waiting to be reduced (each gives at least one); otherwise the run's own count.
    /// </summary>
    private int GathererLeft(GatherTarget t)
    {
        if (!reduceFor.TryGetValue(t.ItemId, out var target)) return Left(t);
        var lacking = Left(target);
        if (lacking <= 0) return 0;
        var waiting = reduceFor.Where(kv => kv.Value.ItemId == target.ItemId).Sum(kv => GameState.Collectables(kv.Key).Count);
        return Math.Max(0, lacking - waiting);
    }

    /// <summary>Collectables to reduce now: those gathered for reduction targets that still lack items.</summary>
    private IReadOnlySet<uint> ReduceNow() => reduceFor.Where(kv => Left(kv.Value) > 0).Select(kv => kv.Key).ToHashSet();

    private List<(uint ItemId, int Keep)> TargetExtras()
        // Reduction results stay: how many a reduction gives is up to chance, and they're worth keeping.
        => Targets.Where(t => !Items.IsCrystal(t.ItemId) && t.Supply != Supply.Reduce && (Settings.DiscardTimedExtras || !Windowed(t)))
            .Select(t => (t.ItemId, Math.Max(0, (int)t.Target - consumed.GetValueOrDefault(t.ItemId)))).ToList();

    /// <summary>
    /// Fish caught on the side to throw away (Settings), each with how many to keep, in the same count as
    /// <see cref="TargetExtras"/>. Never a fish the order, a plan, the scrip earning or a needed collectable
    /// includes. Keep at least what the bags held when gathering started (the player's own) and everything
    /// the fisher didn't count as caught on the side, so only this run's by-catch goes.
    /// </summary>
    private IEnumerable<(uint ItemId, int Keep)> ByCatchToDiscard()
    {
        if (!plugin.Config.Fishing.DiscardByCatch) yield break;
        var needed = NeededCollectables();
        var plans = plugin.Plans;
        foreach (var (id, caught) in Fish.ByCatch)
        {
            if (caught <= 0 || needed.Contains(id) || plans.Plan[id] != null || plans.ScripPlan[id] != null) continue;
            var have = GameState.BagCount(id, false);
            var before = bagsAtStart.GetValueOrDefault(id);
            var keep = Math.Max(before, have - caught);
            if (have > keep) yield return (id, keep);
        }
    }

    /// <summary>While the fisher works: "Fishing", "Spearfishing", or "Waiting for Sculptor (bites in 12 min)".</summary>
    private string FishingHeadline()
    {
        var view = Fish.View;
        if (!Fish.Busy && view is { Fish: not 0 } v && FishWait(v.Fish) is { } w && w > TimeSpan.Zero && w != TimeSpan.MaxValue)
            return $"Waiting for {ItemName(v.Fish)} (bites in {Short(w)})";
        if (Fish.Waiting) return WaitingHeadline();
        return view is { Spear: true } ? "Spearfishing" : "Fishing";
    }

    /// <summary>Nothing to do right now: the window that comes first, a fish's or a timed node's.</summary>
    private string WaitingHeadline()
    {
        var land = LandNextWindow();
        if (SoonestFishWindow() is { } fish && (land is not { } l || fish.Wait <= l))
            return $"Waiting for {ItemName(fish.ItemId)}" + (fish.Wait > TimeSpan.Zero ? $" (bites in {Short(fish.Wait)})" : "");
        if (land is { } next)
            return "Waiting for a timed node" + (next > TimeSpan.Zero ? $" (opens in {Short(next)})" : "");
        // A node that couldn't be landed at or opened, given up on for a few minutes.
        if (Gather.WaitingToRetry) return "Waiting to try a node again";
        // Only fish whose weather isn't in the forecast yet (fish the fisher can't get at all are reported, not waited for).
        foreach (var (id, wait) in fishWaits)
            if (wait == TimeSpan.MaxValue && !Fish.Unreachable.Contains(id)) return $"Waiting for {ItemName(id)}'s weather";
        if (worker == Worker.Fish)
            return "Waiting for a fish window" + (Fish.NextWindow is { } n && n > TimeSpan.Zero ? $" (opens in {Short(n)})" : "");
        return "Waiting for a timed node";
    }

    /// <summary>"Crafting while timed nodes are closed (next opens in 12 min)" or "Crafting while waiting for Sculptor (bites in 12 min)".</summary>
    private string CraftingWhile()
    {
        var land = LandNextWindow();
        if (SoonestFishWindow() is { } fish && (land is not { } l || fish.Wait <= l))
            return $"Crafting while waiting for {ItemName(fish.ItemId)} (bites in {Short(fish.Wait)})";
        return "Crafting while timed nodes are closed" + (land is { } w ? $" (next opens in {Short(w)})" : "");
    }

    /// <summary>The log line's first part when crafting starts between windows.</summary>
    private string CraftMeanwhileReason(TimeSpan next)
    {
        var timed = LandNextWindow() != null;
        var fish = SoonestFishWindow();
        if (fish is { } f && !timed) return $"Only fish with time or weather windows left and {ItemName(f.ItemId)} bites in {Short(f.Wait)}";
        return (fish != null ? "Only timed nodes and fish with time or weather windows left" : "Only timed nodes left") + $" and the next opens in {Short(next)}";
    }

    private string ItemName(uint itemId) => plugin.Plans.Catalog?.ItemName(itemId) ?? "item " + itemId;

    /// <summary>"12 min", "under a minute": <see cref="Estimator.Describe"/> without "about".</summary>
    private static string Short(TimeSpan span) => Estimator.Describe(span).Replace("about ", "");

    private void Add(string text, bool problem)
    {
        log.Insert(0, new RunLogEntry(DateTime.Now, text, problem));
        if (log.Count > 80) log.RemoveAt(log.Count - 1);
    }

    private static void Notify(string text, bool problem)
    {
        Services.Notifications.AddNotification(new Notification
        {
            Title = "Provenance",
            Content = text,
            Type = problem ? NotificationType.Warning : NotificationType.Info,
        });
        Services.Chat.Print(new SeStringBuilder().AddUiForeground("[Provenance] ", 62).AddText(text).Build());
    }

    /// <summary>Plugin unload: stops a run under way, and always removes the fisher's hooks.</summary>
    public void Dispose()
    {
        if (Active)
        {
            try
            {
                CancelNodeTrip();
                Gather.Pause();
                Gather.Stop();
                if (fishBegun) Fish.Stop(now: true);
            }
            catch (Exception e)
            {
                Services.Log.Error(e, "Stopping the run on unload failed");
            }
        }
        try { Fish.Dispose(); }
        catch (Exception e) { Services.Log.Error(e, "Removing the fishing hooks failed"); }
    }
}
