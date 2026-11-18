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
    /// <summary>Pause asked for; waits for the current node or craft to finish.</summary>
    Pausing,
    Paused,
    /// <summary>Stop asked for during a craft; waits for it to finish so no materials are lost.</summary>
    Stopping,
    Finished,
    Stopped,
}

/// <summary>
/// Learn books, take items out of retainers, buy from vendors, gather (crafting while timed nodes are
/// closed), then craft the rest.
/// </summary>
internal enum RunStage { Retainers, Learn, Vendors, Hunt, Gather, Craft, TurnIn }

internal sealed record RunLogEntry(DateTime Time, string Text, bool Problem);

/// <summary>
/// Runs an order from start to finish. Books first; then gathering through GatherBuddy Reborn, and
/// when only timed nodes are left and none is up for a while, crafting what already can be crafted,
/// handing back to gathering shortly before the next window; then everything left is crafted.
/// Watches the stop rules throughout: knock-outs (with retries), a nearly full inventory, plugins
/// going away, duties.
/// </summary>
internal sealed class RunEngine : IDisposable
{
    private const int MinFreeSlots = 2;
    private const int MaxKnockOuts = 3;
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMilliseconds(500);

    private readonly Plugin plugin;
    private readonly GatherBuddyLists lists = new();
    private readonly List<RunLogEntry> log = new();
    private readonly Queue<uint> toLearn = new();
    /// <summary>Gathered materials used up by crafting during the run; they count as gathered.</summary>
    private readonly Dictionary<uint, int> consumed = new();

    private DateTime nextCheck = DateTime.MinValue;
    private DateTime gbrOffSince = DateTime.MinValue;
    private int reEnables;
    private bool pausedForDuty;
    private string? pendingStop;
    private bool pendingStopProblem;
    private double gatherActiveSeconds;
    private DateTime lastGatherTick;
    private DateTime learnDeadline;

    /// <summary>Crafting while waiting for timed nodes (still in the gather stage).</summary>
    private bool craftingBetween;
    /// <summary>A hand-back to gathering is under way (crafter finishing its craft and leaving the log).</summary>
    private bool yielding;

    /// <summary>The crafter got this run's recipes; never build them twice (ordered items would be crafted again).</summary>
    private bool craftQueueBuilt;

    /// <summary>Trip to the next timed node's spawn area while it's still closed.</summary>
    private readonly Travel nodeTrip = new();
    private TimedSpot? tripSpot;
    private uint tripItem;
    private DateTime nextTripCheck;
    private readonly HashSet<TimedSpot> tripFailed = new();
    /// <summary>Closer to the window than this, GatherBuddy Reborn goes on its own.</summary>
    private static readonly TimeSpan TripCutoff = TimeSpan.FromSeconds(20);

    private readonly ExtrasCleanup cleanup;
    private bool cleaning;

    private int knockOuts;
    private bool knockedOut;
    private DateTime knockedOutAt;
    private DateTime revivedAt;

    public RunEngine(Plugin plugin)
    {
        this.plugin = plugin;
        Crafter = new Crafter(plugin, Add);
        Crafter.Crafted += OnCrafted;
        Shopper = new Shopper(plugin, Add);
        Retainers = new RetainerRun(plugin, Add);
        Books = new BookBuyer(plugin, Add);
        TurnIn = new Appraiser(plugin, Add);
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
    /// <summary>The learn stage is buying books at exchanges before reading them.</summary>
    private bool buyingBooks;
    public IReadOnlyList<RunLogEntry> Log => log;
    /// <summary>When the next timed node opens, while only timed nodes are left.</summary>
    public TimeSpan? NextWindow { get; private set; }

    public bool Active => Phase is RunPhase.Running or RunPhase.Pausing or RunPhase.Paused or RunPhase.Stopping;
    /// <summary>The crafter is the one working right now (craft stage, or crafting between timed nodes).</summary>
    public bool Crafting => Stage == RunStage.Craft || (Stage == RunStage.Gather && craftingBetween);

    private RunSettings Settings => plugin.Config.Run;

    /// <summary>Earning scrips with collectables before the order itself.</summary>
    private bool scripPhase;
    private int scripRounds;
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
        if (Stock.FreeSlots() <= MinFreeSlots) return $"Your inventory has {MinFreeSlots} or fewer free slots.";
        var gather = GatherTargets.Build(plan, BagNq).Count > 0;
        var craft = plan.CraftSteps.Any(l => l.Crafts > 0);
        if (!gather && !craft && plan.Learn.Count == 0 && plan.Count(Supply.Vendor) == 0) return "Nothing to gather, buy or craft for this order.";
        if (gather && !GatherBuddyIpc.Available) return "GatherBuddy Reborn isn't loaded.";
        if (gather && !Vnav.Available) return "vnavmesh isn't loaded.";
        if (gather && plan.Count(Supply.Fish) > 0 && !Ipc.IsLoaded("AutoHook")) return "AutoHook isn't loaded; this order has fish to catch.";
        if ((plan.Count(Supply.Vendor) > 0 || plan.Learn.Any(l => l.WillBuy) || (plugin.Config.Stock.Retainers && RetainerWork().Count > 0)) && !Vnav.Available)
            return "vnavmesh isn't loaded; it walks to vendors, exchanges and summoning bells.";
        if (UnreachableVendorItem() is { } unreachable)
            return $"No vendor you can reach sells {plugin.Plans.Catalog!.ItemName(unreachable)} under your price cap; get it yourself, or attune to more aetherytes and aethernet shards.";
        if (craft && Raphael.ExecutablePath() == null) return "raphael-cli.exe wasn't found; reinstall Provenance (or install GatherBuddy Reborn, which ships it).";
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
        reEnables = 0;
        knockOuts = 0;
        knockedOut = false;
        pausedForDuty = false;
        pendingStop = null;
        craftingBetween = false;
        yielding = false;
        NextWindow = null;
        gbrOffSince = DateTime.MinValue;
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
        Crafter.Begin(Array.Empty<CraftJob>());
        scripRounds = 0;
        scripPhase = plugin.Plans.ScripTasks.Count > 0;
        if (scripPhase)
            Add("Earning scrips for books first: " + string.Join(", ", plugin.Plans.ScripTasks.Select(t =>
                $"{t.Count}× {plugin.Plans.Catalog!.ItemName(t.Option.ItemId)} for about {t.Expected:N0} {plugin.Plans.Catalog!.ItemName(t.Option.Currency)}")) + ".", false);

        // Retainers first (a book may be with a retainer), then books, vendors, gathering, crafting.
        StartRetainers();
        if (Active) Notify("Run started for " + plugin.Config.Order.Name + ".", false);
    }

    /// <summary>Books and tomes from the bags come before buying and gathering: recipes and legendary nodes need them.</summary>
    private void StartLearning()
    {
        plugin.Plans.Invalidate();
        // Books not in the bags that an exchange sells: buy them first, then read everything.
        var buy = ActivePlan.Learn.Where(l => l.WillBuy)
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
        foreach (var l in ActivePlan.Learn.Where(l => GameState.BagCount(l.ItemId) > 0)) toLearn.Enqueue(l.ItemId);
        if (toLearn.Count == 0) { StartVendors(); return; }
        Stage = RunStage.Learn;
        Headline = "Learning";
        learnDeadline = default;
        Add($"Reading {toLearn.Count} {(toLearn.Count == 1 ? "book" : "books")} first.", false);
    }

    private void StartAfterLearning() => StartVendors();

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
        if (purchases.Count == 0) { StartGatherOrCraft(); return; }
        Stage = RunStage.Vendors;
        Headline = "Buying from vendors";
        Shopper.Begin(purchases);
        var stops = purchases.Select(p => p.Offer.NpcId).Distinct().Count();
        Add($"Buying {purchases.Count} {(purchases.Count == 1 ? "item" : "items")} from {stops} {(stops == 1 ? "vendor" : "vendors")}.", false);
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
        var unknown = lists.Install(targets);
        if (lists.Error != null)
        {
            End(RunPhase.Stopped, "Couldn't start", lists.Error, true);
            return;
        }
        if (unknown.Count > 0)
        {
            var catalog = plugin.Plans.Catalog!;
            Add($"GatherBuddy Reborn doesn't list {string.Join(", ", unknown.Select(catalog.ItemName))} as gatherable; get them yourself.", true);
            targets = targets.Where(t => !unknown.Contains(t.ItemId)).ToList();
        }
        Targets = targets;
        gatherActiveSeconds = 0;
        lastGatherTick = DateTime.UtcNow;

        // The crafter gets the whole order now, so it can work between timed nodes.
        var jobs = CraftJobs();
        Crafter.Begin(jobs);
        craftQueueBuilt = true;
        Crafter.DeferMissing = true;
        Crafter.RequestPause();

        GatherBuddyIpc.SetEnabled(true);
        Headline = "Gathering";
        Add($"Gathering {targets.Count} {(targets.Count == 1 ? "item" : "items")} with GatherBuddy Reborn.", false);
    }

    /// <summary>Everything gathered: craft whatever is left (or all of it when nothing was gathered).</summary>
    private void StartCrafting()
    {
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

    // ------------------------------------------------------------------ controls

    /// <summary>Soft pause: finishes the node being gathered or the item being crafted, then stops.</summary>
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
        else Headline = "Pausing after this node";
        Add("Pause asked for; finishing what's under way first.", false);
    }

    public void Resume()
    {
        if (Phase is not (RunPhase.Paused or RunPhase.Pausing)) return;
        var wasPaused = Phase == RunPhase.Paused;
        Phase = RunPhase.Running;
        gbrOffSince = DateTime.MinValue;
        if (Stage == RunStage.Vendors) Shopper.Resume();
        else if (Stage == RunStage.Retainers) Retainers.Resume();
        else if (Stage == RunStage.Craft || craftingBetween) Crafter.Resume();
        else if (Stage == RunStage.Gather && wasPaused) HandBackToGathering(log: false);
        Headline = Crafting ? "Crafting" : Stage switch
        {
            RunStage.Learn => "Learning",
            RunStage.Retainers => "Taking items from retainers",
            RunStage.Vendors => "Buying from vendors",
            _ => "Gathering",
        };
        Add("Resumed.", false);
    }

    /// <summary>
    /// Ends the run. During a craft it waits for that craft to finish (materials are lost otherwise),
    /// unless <paramref name="now"/> is set because the game or a plugin went away.
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
        GatherBuddyIpc.SetEnabled(false);
        lists.Uninstall();
        CancelNodeTrip();
        cleanup.Cancel();
        cleaning = false;
        Shopper.Cancel();
        Retainers.Cancel();
        Books.Cancel();
        TurnIn.Cancel();
        buyingBooks = false;
        End(RunPhase.Stopped, problem ? "Stopped" : "Stopped by you", reason, problem);
    }

    private void Finish()
    {
        if (scripPhase) { StartTurnIn(); return; }
        CancelNodeTrip();
        GatherBuddyIpc.SetEnabled(false);
        lists.Uninstall();
        var skipped = Crafter.History.Where(h => h.Skipped != null).ToList();
        var detail = skipped.Count == 0
            ? "Everything for this order is gathered and crafted."
            : $"Done, but {skipped.Count} {(skipped.Count == 1 ? "recipe was" : "recipes were")} skipped: " + string.Join("; ", skipped.Select(h => $"{plugin.Plans.Catalog!.ItemName(h.Job.Recipe.ItemId)} ({h.Skipped})")) + ".";
        End(RunPhase.Finished, skipped.Count > 0 ? "Order not finished" : "Order done", detail, skipped.Count > 0);
    }

    /// <summary>Collectables made: take them to an appraiser.</summary>
    private void StartTurnIn()
    {
        CancelNodeTrip();
        GatherBuddyIpc.SetEnabled(false);
        lists.Uninstall();
        Stage = RunStage.TurnIn;
        Headline = "Turning in collectables";
        TurnIn.Begin(plugin.Plans.ScripTasks.Select(t => t.Option.ItemId));
        Add("Collectables done; turning them in for scrips.", false);
    }

    /// <summary>Scrips earned: plan again; earn more if still short (up to three rounds), else on to the order.</summary>
    private void AfterTurnIn()
    {
        scripPhase = false;
        plugin.Plans.Rebuild();
        ResetForNextPlan();
        if (plugin.Plans.ScripTasks.Count > 0)
        {
            if (++scripRounds >= 3) { Stop("Still short of scrips after three rounds of collectables; the plan says how many are missing.", true, now: true); return; }
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
        if (!Active) return;

        // The crafter needs every frame; the checks below only twice a second.
        var working = !pausedForDuty && !knockedOut && Phase is RunPhase.Running or RunPhase.Pausing or RunPhase.Stopping;
        if (working && Crafting) Crafter.Tick();
        if (working && Stage == RunStage.Vendors) Shopper.Tick();
        if (working && Stage == RunStage.Retainers) Retainers.Tick();
        if (working && Stage == RunStage.TurnIn) TurnIn.Tick();
        if (working && Stage == RunStage.Learn && buyingBooks) Books.Tick();
        if (DateTime.UtcNow < nextCheck) return;
        nextCheck = DateTime.UtcNow + CheckEvery;

        if (!Services.ClientState.IsLoggedIn) { Stop("You logged out.", true, now: true); return; }
        var player = Services.Objects.LocalPlayer;
        if (player == null) return; // zoning
        if (HandleKnockOut(player.IsDead)) return;
        if (Stock.FreeSlots() <= MinFreeSlots && Phase != RunPhase.Stopping) { Stop($"Only {MinFreeSlots} inventory slots left.", true); return; }
        if (HandleDuty()) return;

        switch (Stage)
        {
            case RunStage.Learn: TickLearning(); break;
            case RunStage.Retainers: TickErrand(Retainers.Finished, Retainers.Failure, Retainers.IsPaused, Retainers.Status, StartLearning); break;
            case RunStage.Vendors: TickErrand(Shopper.Finished, Shopper.Failure, Shopper.IsPaused, Shopper.Status, StartGatherOrCraft); break;
            case RunStage.Gather: TickGathering(); break;
            case RunStage.TurnIn: TickErrand(TurnIn.Finished, TurnIn.Failure, false, TurnIn.Status, AfterTurnIn); break;
            default: TickCrafting(); break;
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
                GatherBuddyIpc.SetEnabled(false);
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
        if (Phase == RunPhase.Running && Stage == RunStage.Gather && !craftingBetween) GatherBuddyIpc.SetEnabled(true);
        return false;
    }

    /// <summary>A duty pop takes over: pause gathering or crafting, carry on once the duty is over.</summary>
    private bool HandleDuty()
    {
        var inDuty = Services.Condition[ConditionFlag.BoundByDuty] || Services.Condition[ConditionFlag.BoundByDuty56];
        if (inDuty && !pausedForDuty)
        {
            pausedForDuty = true;
            GatherBuddyIpc.SetEnabled(false);
            Headline = "Paused for a duty";
            Add("Duty started; the run waits until it ends.", false);
            return true;
        }
        if (!pausedForDuty) return false;
        if (inDuty) return true;
        pausedForDuty = false;
        Add("Duty over; carrying on.", false);
        if (Phase == RunPhase.Running && Stage == RunStage.Gather && !craftingBetween) { GatherBuddyIpc.SetEnabled(true); Headline = "Gathering"; }
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
        if (!GatherBuddyIpc.Available) { Stop("GatherBuddy Reborn was unloaded.", true, now: true); return; }
        if (!Vnav.Available) { Stop("vnavmesh was unloaded.", true, now: true); return; }

        // Time actually spent gathering (not paused, not waiting, not crafting), for the gathering rate.
        var now = DateTime.UtcNow;
        if (Phase == RunPhase.Running && !craftingBetween && !GatherBuddyIpc.Waiting) gatherActiveSeconds += (now - lastGatherTick).TotalSeconds;
        lastGatherTick = now;

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
            // The amount is in, but a node is gathered to the end; GatherBuddy Reborn takes the rest.
            if (!craftingBetween && Services.Condition[ConditionFlag.Gathering])
            {
                Headline = "Finishing this node";
                Detail = "The order's amount is in; gathering what's left on the node.";
                return;
            }
            GatherBuddyIpc.SetEnabled(false);
            lists.Uninstall();
            CancelNodeTrip();
            LearnGatheringRate();
            NextWindow = null;
            Add("Gathering done.", false);
            if (Settings.DiscardExtras)
            {
                cleanup.Begin(Targets.Where(t => !Items.IsCrystal(t.ItemId))
                    .Select(t => (t.ItemId, Math.Max(0, (int)t.Target - consumed.GetValueOrDefault(t.ItemId)))));
                if (!cleanup.Done)
                {
                    cleaning = true;
                    Headline = "Throwing away extras";
                    return;
                }
            }
            StartCrafting();
            return;
        }

        if (craftingBetween)
        {
            TickCraftingBetween();
            return;
        }

        var busy = Services.Condition[ConditionFlag.Gathering] || Services.Condition[ConditionFlag.ExecutingGatheringAction] || Services.Condition[ConditionFlag.Fishing];
        switch (Phase)
        {
            case RunPhase.Pausing when !busy:
                CancelNodeTrip();
                GatherBuddyIpc.SetEnabled(false);
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
        if (TickNodeTrip(decision, busy)) return;
        if (decision.Mode == GatherMode.CraftMeanwhile && !busy)
        {
            // Only timed nodes left, none up for a while: craft what can be crafted until shortly before.
            GatherBuddyIpc.SetEnabled(false);
            craftingBetween = true;
            yielding = false;
            Crafter.DeferMissing = true;
            Crafter.Resume();
            Headline = "Crafting while timed nodes are closed";
            Add($"Only timed nodes left and the next opens in {Estimator.Describe(decision.NextWindow ?? TimeSpan.Zero).Replace("about ", "")}; crafting in the meantime.", false);
            return;
        }

        Detail = GatherBuddyIpc.Status;
        Headline = decision.Mode == GatherMode.Wait || GatherBuddyIpc.Waiting
            ? "Waiting for a timed node" + (decision.NextWindow is { } w && w > TimeSpan.Zero ? $" (opens in {Estimator.Describe(w).Replace("about ", "")})" : "")
            : "Gathering";
        WatchGatherBuddy();
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
        if (decision.Mode != GatherMode.CraftMeanwhile || Crafter.WaitingForMaterials || Crafter.Finished)
        {
            yielding = true;
            Crafter.RequestYield();
            Headline = Crafter.MidCraft ? "Back to gathering after this craft" : "Back to gathering";
            return;
        }
        Headline = "Crafting while timed nodes are closed" + (decision.NextWindow is { } w ? $" (next opens in {Estimator.Describe(w).Replace("about ", "")})" : "");
    }

    /// <summary>
    /// While only closed timed nodes are left: go to where the next one spawns, so crafting and waiting
    /// happen there and gathering starts the moment it opens. True while the trip is under way.
    /// </summary>
    private bool TickNodeTrip(GatherDecision decision, bool busy)
    {
        var catalog = plugin.Plans.Catalog!;
        if (tripSpot is { } spot)
        {
            var name = catalog.ItemName(tripItem);
            if (decision.NextWindow is not { } left || left <= TripCutoff)
            {
                EndNodeTrip();
                return false;
            }
            if (GatherBuddyIpc.Enabled) GatherBuddyIpc.SetEnabled(false);
            nodeTrip.Tick();
            Headline = $"Heading to the {name} node (opens in {Estimator.Describe(left).Replace("about ", "")})";
            Detail = nodeTrip.Status;
            if (nodeTrip.Current == Travel.State.Failed)
            {
                tripFailed.Add(spot);
                Add($"Couldn't get to the {name} node ahead of time ({nodeTrip.Failure}); GatherBuddy Reborn goes there when it opens.", true);
                EndNodeTrip();
                return false;
            }
            if (nodeTrip.Current != Travel.State.Arrived) return true;
            Add($"At the {name} node; it opens in {Estimator.Describe(left).Replace("about ", "")}.", false);
            EndNodeTrip();
            return false;
        }

        if (busy || !Settings.TravelAhead || DateTime.UtcNow < nextTripCheck) return false;
        if (decision.NextWindow is not { } wait || wait <= TripCutoff * 3) return false;
        nextTripCheck = DateTime.UtcNow.AddSeconds(10);
        if (NextTimedSpot() is not { } next) return false;
        if (Near(next.Spot)) return false;

        GatherBuddyIpc.SetEnabled(false);
        tripSpot = next.Spot;
        tripItem = next.ItemId;
        // Aim for the middle of the spawn area; anywhere well inside it is close enough.
        nodeTrip.GoTo($"the {catalog.ItemName(next.ItemId)} node", next.Spot.TerritoryId, next.Spot.Position, Math.Clamp(next.Spot.Radius * 0.4f, 15f, 40f), null);
        Add($"Heading to where {catalog.ItemName(next.ItemId)} spawns while its node is closed.", false);
        return true;
    }

    /// <summary>The spawn area of the soonest timed node still needed, among zones the character can reach.</summary>
    private (uint ItemId, TimedSpot Spot)? NextTimedSpot()
    {
        var catalog = plugin.Plans.Catalog!;
        var now = DateTimeOffset.UtcNow;
        var reach = new Dictionary<uint, bool>();
        (uint, TimedSpot)? best = null;
        var bestWait = TimeSpan.MaxValue;
        foreach (var t in Targets.Where(t => t.Supply == Supply.GatherTimed && Left(t) > 0))
            foreach (var s in catalog.TimedSpots(t.ItemId))
            {
                if (tripFailed.Contains(s) || Core.Timing.Eorzea.UntilOpen(s.Mask, now) is not { } wait) continue;
                if (wait > bestWait || (wait == bestWait && s.TerritoryId != Services.ClientState.TerritoryType)) continue;
                if (!(reach.TryGetValue(s.TerritoryId, out var ok) ? ok : reach[s.TerritoryId] = Teleporter.CanReach(s.TerritoryId))) continue;
                best = (t.ItemId, s);
                bestWait = wait;
            }
        return best;
    }

    private static bool Near(TimedSpot spot)
    {
        if (Services.ClientState.TerritoryType != spot.TerritoryId || Services.Objects.LocalPlayer is not { } me) return false;
        return System.Numerics.Vector2.Distance(new System.Numerics.Vector2(me.Position.X, me.Position.Z), spot.Position) <= Math.Max(spot.Radius, 40f) + 20f;
    }

    /// <summary>Trip over (arrived, gave up, or the window is near): GatherBuddy Reborn carries on.</summary>
    private void EndNodeTrip()
    {
        CancelNodeTrip();
        GatherBuddyIpc.SetEnabled(true);
        gbrOffSince = DateTime.MinValue;
    }

    private void CancelNodeTrip()
    {
        if (tripSpot == null) return;
        nodeTrip.Cancel();
        tripSpot = null;
    }

    private void HandBackToGathering(bool log)
    {
        craftingBetween = false;
        yielding = false;
        // Crafting used some gathered materials: lower GatherBuddy Reborn's targets by that much.
        lists.UpdateTargets(Targets.Select(t => (t.ItemId, (uint)Math.Max(1, (int)t.Target - consumed.GetValueOrDefault(t.ItemId)))));
        GatherBuddyIpc.SetEnabled(true);
        gbrOffSince = DateTime.MinValue;
        Headline = "Gathering";
        if (log) Add("Back to gathering for the timed nodes.", false);
    }

    private GatherDecision Decide()
    {
        var catalog = plugin.Plans.Catalog!;
        var targets = Targets.Select(t => new ScheduledTarget(t.ItemId, t.Supply, Left(t), t.Supply == Supply.GatherTimed ? catalog.TimedMask(t.ItemId) : 0));
        var allowed = Settings.CraftWhileWaiting && Crafter.HasReadyWork();
        return Interleave.Decide(targets, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(Settings.TimedLeadMinutes), allowed);
    }

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

    private void LearnGatheringRate()
    {
        var gathered = Targets.Where(t => t.Supply != Supply.Fish).Sum(t => t.Remaining);
        if (gatherActiveSeconds < 60 || gathered < 10 || Targets.Any(t => t.Supply == Supply.Fish)) return;
        var t = plugin.Config.Timings;
        t.GatheredPerMinute = Math.Clamp(Timings.Blend(t.GatheredPerMinute, gathered / (gatherActiveSeconds / 60)), 1, 120);
        plugin.Config.Save();
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
        var left = Targets.Where(x => x.Supply != Supply.Fish).Sum(Left);
        var fish = Targets.Where(x => x.Supply == Supply.Fish).Sum(Left);
        // Only closed timed nodes left: the wait for the window comes on top.
        var wait = NextWindow is { } w && w > TimeSpan.Zero ? w : TimeSpan.Zero;
        return TimeSpan.FromMinutes(left / Math.Max(0.1, t.GatheredPerMinute) + fish / Math.Max(0.05, t.FishPerMinute)) + wait + crafting;
    }

    /// <summary>GatherBuddy Reborn can switch itself off; turn it back on a few times before giving up.</summary>
    private void WatchGatherBuddy()
    {
        if (GatherBuddyIpc.Enabled)
        {
            gbrOffSince = DateTime.MinValue;
            return;
        }
        if (gbrOffSince == DateTime.MinValue) { gbrOffSince = DateTime.UtcNow; return; }
        if (DateTime.UtcNow - gbrOffSince < TimeSpan.FromSeconds(5)) return;

        var status = GatherBuddyIpc.Status;
        if (reEnables >= 3)
        {
            Stop("GatherBuddy Reborn keeps switching auto-gather off" + (status.Length > 0 ? ": " + status : "."), true);
            return;
        }
        reEnables++;
        gbrOffSince = DateTime.MinValue;
        Add("GatherBuddy Reborn switched auto-gather off" + (status.Length > 0 ? $" ({status})" : "") + "; switching it back on.", true);
        GatherBuddyIpc.SetEnabled(true);
    }

    /// <summary>Still to gather for one target: bags plus what crafting already used, against the target.</summary>
    public int Left(GatherTarget t) => GatherTargets.Left(t, CountedAsGathered);

    private int CountedAsGathered(uint itemId) => BagNq(itemId) + consumed.GetValueOrDefault(itemId);

    /// <summary>What GatherBuddy Reborn counts: normal-quality items in the main bags.</summary>
    private static int BagNq(uint itemId) => GameState.BagCount(itemId, false);

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

    public void Dispose()
    {
        if (!Active) return;
        GatherBuddyIpc.SetEnabled(false);
        lists.Uninstall();
    }
}
