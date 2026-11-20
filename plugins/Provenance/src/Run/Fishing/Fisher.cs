using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using Provenance.Core.Fishing;
using Provenance.Core.Planning;
using Provenance.Core.Run;
using Provenance.Data;
using Provenance.Integrations;
using Provenance.Integrations.Fishing;
using Provenance.Run.Gathering;

namespace Provenance.Run.Fishing;

/// <summary>What the Run page shows about fishing.</summary>
/// <param name="Fish">The fish being fished for (0 when none).</param>
/// <param name="Spot">The fishing hole or spearfishing spot's name, with its zone.</param>
/// <param name="Bait">The bait cast with (0 for spearfishing).</param>
/// <param name="BaitInBags">How much of that bait is left in the main bags.</param>
/// <param name="Window">"Bites for 12 more minutes", "Bites in 8 min (in Fog)", or "" when the fish has no window.</param>
/// <param name="IntuitionLeft">Fisher's Intuition time left, when it's up.</param>
/// <param name="Caught">Fish of this kind caught in the run so far.</param>
/// <param name="Casts">Casts (or spearfishing nodes) at this spot so far.</param>
internal sealed record FishingView(uint Fish, string Spot, uint Bait, int BaitInBags, string Window, TimeSpan? IntuitionLeft, int Caught, int Casts, bool Spear);

/// <summary>
/// Provenance's own fishing in a run (rod fishing and spearfishing): picks the fish to go for and its
/// spot (time and weather windows, level, reach), changes into the Fisher gearset, buys bait, travels to a
/// recorded stand (or a spearfishing node), lands, faces the water and checks that a cast works, then
/// lets the <see cref="Integrations.Fishing.Angler"/> (or the <see cref="Integrations.Fishing.SpearGame"/>)
/// catch. Moves on when the fish turn wary, leaves when they sense something amiss, buys more bait when
/// it runs out, and always reels in before teleporting. Framework thread; <see cref="Tick"/> every frame
/// while the fisher is the run's active worker, and whenever it is <see cref="Busy"/> (pausing, stopping).
/// Follows GatherBuddy Reborn's AutoGather (Apache-2.0).
/// </summary>
internal sealed unsafe partial class Fisher : IDisposable
{
    private enum Step
    {
        Idle,
        Paused,
        /// <summary>Reeling in (and dealing with a spearfishing window nobody works on), then on to <see cref="afterSettle"/>.</summary>
        Settle,
        /// <summary>
        /// Stopped or failed with something still out: lets the bite go, reels in and puts the rod away, leaves an
        /// open spearfishing window, then Idle. Runs on the ticks the run keeps giving after it stopped.
        /// </summary>
        Ending,
        Choose,
        Upkeep,
        Gear,
        Bait,
        Shop,
        // Rod fishing
        Stand,
        Travel,
        Land,
        Face,
        CastCheck,
        Jiggle,
        Fish,
        Leave,
        Linger,
        // Spearfishing
        SpearFind,
        SpearTravel,
        SpearApproach,
        SpearOpen,
        Spear,
    }

    /// <summary>GeneralAction 23: lands when flying, gets off the mount on the ground (as the gatherer uses it).</summary>
    private const uint LandOrDismount = 23;
    /// <summary>
    /// Status 805: Collector's Glove. Fish for an order come in as normal items, so it stays off; fish caught for
    /// aetherial reduction come in as collectables, so it's on for those.
    /// </summary>
    private const uint CollectStatus = 805;

    private readonly Plugin plugin;
    private readonly Action<string, bool> log;
    private readonly Travel travel = new();
    private readonly Upkeep upkeep;
    private readonly Shopper shopper;
    private readonly BaitShop baitShop;
    private readonly Angler angler = new();
    private readonly SpearGame spear = new();

    private List<GatherTarget> targets = new();
    private Func<GatherTarget, int> left = _ => 0;
    private Func<IReadOnlySet<uint>> neededCollectables = () => new HashSet<uint>();
    private Func<IReadOnlySet<uint>> reduceNow = () => new HashSet<uint>();
    /// <summary>Fish caught as collectables (for aetherial reduction), with Collector's Glove on.</summary>
    private Func<uint, bool> collect = _ => false;
    private int collectTries;
    /// <summary>Kept from <see cref="Begin"/> for the contract; no mooch is reported through it (see <see cref="OnUsedAsBait"/> for why).</summary>
    private Action<uint, int> consumed = (_, _) => { };

    private Step step = Step.Idle;
    private Step afterSettle = Step.Choose;
    private DateTime waitUntil;
    private DateTime stepSince;
    /// <summary>Settling saw the rod out (or a window open): stand up for a moment before moving on.</summary>
    private bool settleSawWork;
    /// <summary>A pause is asked for: go to <see cref="Step.Paused"/> once the catch, node or purchase under way is done.</summary>
    private bool pausing;

    // What the fisher works on.
    private GatherTarget? current;
    private FishInfo? fish;
    private FishingSpotInfo? spot;
    private uint bait;

    // The run so far.
    private readonly Dictionary<uint, int> caught = new();
    private readonly Dictionary<uint, int> byCatch = new();
    /// <summary>Casts of the sessions at this spot that are over (see <see cref="CountSession"/>).</summary>
    private int castsAtSpot;
    /// <summary>The last session's casts are in <see cref="castsAtSpot"/>.</summary>
    private bool sessionCounted = true;
    private int nodesAtSpot;

    public Fisher(Plugin plugin, Action<string, bool> log)
    {
        this.plugin = plugin;
        this.log = log;
        upkeep = new Upkeep(plugin, log);
        shopper = new Shopper(plugin, log);
        baitShop = new BaitShop(plugin);
        angler.Caught += OnCaught;
        angler.UsedAsBait += OnUsedAsBait;
        spear.Caught += OnSpearCaught;
    }

    public string Status { get; private set; } = "";
    /// <summary>Why the fisher gave up (no gearset, no bait anywhere, nothing reachable); the run stops on it.</summary>
    public string? Failure { get; private set; }
    /// <summary>Has fish to work on and isn't paused (nor stopped and only reeling in).</summary>
    public bool Running => step is not (Step.Idle or Step.Paused or Step.Ending) && targets.Any(t => left(t) > 0);
    /// <summary>Paused, with the rod reeled in and the spearfishing window closed.</summary>
    public bool IsPaused => step == Step.Paused;
    /// <summary>Nothing can be fished now: every fish left waits for its time or weather window.</summary>
    public bool Waiting { get; private set; }
    /// <summary>Waiting at the stand for GP the catch can't do without.</summary>
    public bool WaitingForGp => step == Step.Fish && angler.Active && angler.WaitingForGp;
    /// <summary>
    /// At the stand with the rod in, waiting for the fish's window to open (the fisher goes early by the lead), for
    /// the window of a fish Fisher's Intuition needs, or to catch the last of those; not fishing time. The session
    /// stays the active work (<see cref="Busy"/>).
    /// </summary>
    public bool WaitingForWindow => step == Step.Fish && angler.Active && angler.WaitingForWindow;
    /// <summary>
    /// The rod is out, a spearfishing window is open, or a catch, a node (until its last catch is counted), a bait
    /// trip, materia extraction or reduction, or reeling in after a stop is under way: don't switch to other work
    /// now, and keep ticking the fisher. False once paused with everything put away. A session left over from a
    /// stop that nobody ticks any more (<c>Stop(now: true)</c>) doesn't count; the rod itself still does.
    /// </summary>
    public bool Busy => StanceUp || (angler.Active && step != Step.Idle) || spear.Active || SpearGame.WindowOpen
                        || step is Step.Settle or Step.Upkeep or Step.Shop or Step.SpearOpen or Step.Spear or Step.Ending;
    /// <summary>A fish left in the order bites now (or within the lead time), at a spot the fisher can use.</summary>
    public bool HasWorkNow => targets.Count > 0 && (Ranked().Count > 0 || (current is { } t && FishingUnderIntuition(t.ItemId)));
    /// <summary>When the next fish window opens (zero when one is open now); null when no fish left has a window.</summary>
    /// <remarks>Fish that can't be fished, or don't bite within the forecast, have no window to wait for here (never MaxValue).</remarks>
    public TimeSpan? NextWindow
    {
        get
        {
            TimeSpan? next = null;
            foreach (var t in targets)
                if (left(t) > 0 && WaitFor(t.ItemId) is { } wait && wait != TimeSpan.MaxValue && (next == null || wait < next)) next = wait;
            return next;
        }
    }
    /// <summary>Why the collectables caught for a reduction can't be reduced now (see <see cref="Upkeep.ReduceBlocked"/>).</summary>
    public string? ReduceBlocked => upkeep.ReduceBlocked;
    /// <summary>Fish the fisher can't get (no usable spot, no bait, missing actions).</summary>
    public IReadOnlyCollection<uint> Unreachable => unreachable.Keys;
    /// <summary>Fish caught on the side that the order doesn't need (item id → count), for throwing away afterwards.</summary>
    public IReadOnlyDictionary<uint, int> ByCatch => byCatch;
    /// <summary>What the Run page shows; null when not fishing for anything.</summary>
    public FishingView? View
    {
        get
        {
            if (step is Step.Idle or Step.Ending || current is not { } t || fish is not { } f || spot is not { } s) return null;
            var inBags = !f.Spear && bait != 0 ? GameState.BagCount(bait) : 0;
            var intuition = angler.Active ? angler.IntuitionLeft : null;
            var casts = f.Spear ? nodesAtSpot : castsAtSpot + (sessionCounted ? 0 : angler.Casts);
            return new FishingView(t.ItemId, SpotLabel(f.Spear && pool != null ? pool : s), f.Spear ? 0 : bait, inBags,
                WindowText(f, s, false), intuition, caught.GetValueOrDefault(t.ItemId), casts, f.Spear);
        }
    }

    private GameCatalog Catalog => plugin.Plans.Catalog!;
    private GatherData Data => Catalog.Gather;
    private FishingSettings Settings => plugin.Config.Fishing;
    private static bool RodOut => Services.Condition[ConditionFlag.Fishing];

    /// <summary>
    /// The fishing stance is up: the rod is out, or (as Fisher, no spearfishing window) the game still counts the
    /// player as gathering, which GBR reads as a rod session being open. Either one refuses a teleport.
    /// </summary>
    private static bool StanceUp
        => RodOut || Services.Condition[ConditionFlag.Gathering] && GameState.CurrentJob == FishAccess.Fisher && !SpearGame.WindowOpen;

    /// <summary>Something to reel in or close first: the rod (or the stance), a session, a spearfishing window or its abandon question.</summary>
    private bool SomethingOut => StanceUp || angler.Active || spear.Active || SpearGame.WindowOpen || SpearGame.AbandonAsked;

    /// <summary>
    /// Starts with the run's fish targets (other targets are ignored).
    /// </summary>
    /// <param name="left">How many of a target are still wanted (the run's count, including what mooching used up).</param>
    /// <param name="neededCollectables">Items the order needs, which must never be reduced or thrown away.</param>
    /// <param name="consumed">Called when fish the run counts leave the bags as bait (mooch, swimbait): item id, amount.</param>
    /// <param name="reduceNow">Collectables caught to be reduced; reduced between catches as soon as they're in the bags.</param>
    /// <param name="collect">Fish to catch as collectables (for aetherial reduction): Collector's Glove goes on for them.</param>
    public void Begin(IEnumerable<GatherTarget> targets, Func<GatherTarget, int> left, Func<IReadOnlySet<uint>> neededCollectables, Action<uint, int> consumed,
        Func<IReadOnlySet<uint>>? reduceNow = null, Func<uint, bool>? collect = null)
    {
        this.targets = targets.Where(t => t.Supply == Supply.Fish).ToList();
        this.left = left;
        this.neededCollectables = neededCollectables;
        this.consumed = consumed;
        this.reduceNow = reduceNow ?? (() => new HashSet<uint>());
        this.collect = collect ?? (_ => false);
        collectTries = 0;
        travel.Cancel();
        shopper.Cancel();
        if (spear.Active) spear.Stop();
        if (angler.Active) angler.Stop();
        ResetRun();
        if (this.targets.Count == 0) { step = Step.Idle; return; }
        // Anything still out from before (the rod, a spearfishing window) is put away first.
        Settle(Step.Choose);
    }

    /// <summary>Forgets everything one run's fishing learned along the way.</summary>
    private void ResetRun()
    {
        Failure = null;
        Status = "";
        Waiting = false;
        pausing = false;
        current = null;
        fish = null;
        spot = null;
        bait = 0;
        caught.Clear();
        byCatch.Clear();
        castsAtSpot = nodesAtSpot = 0;
        sessionCounted = true;
        ResetChoice();
        ResetRod();
        ResetSpear();
    }

    /// <summary>
    /// Real time until a fish can be fished at its best usable spot: zero when it bites now,
    /// <see cref="TimeSpan.MaxValue"/> when not within the weather forecast, null when it has no window.
    /// </summary>
    /// <remarks>
    /// The raw wait until it bites; the fisher sets out earlier by <see cref="LeadFor"/>. MaxValue also when the
    /// fish can't be fished at all (see <see cref="Unreachable"/>). The window is the one it can be caught in (see
    /// <see cref="Windowed"/>): a fish mooched from one that bites at some hours waits for those, and one caught
    /// under Fisher's Intuition with no window of its own waits for the soonest of its predators' windows.
    /// </remarks>
    public TimeSpan? WaitFor(uint itemId) => WindowWait(itemId);

    /// <summary>
    /// The fish only bites in a window: at some hours or in some weather, its own or that of the mooch chain it's
    /// caught along, or a predator's (caught first for Fisher's Intuition, or gigged first for its swimming
    /// shadows). False for an item that isn't a known fish.
    /// </summary>
    public bool Windowed(uint itemId) => plugin.Plans.Catalog != null && Data.Fish(itemId) != null && Places(itemId).Windowed;

    /// <summary>
    /// Every frame while fishing is the active work, and whenever <see cref="Busy"/> (a pause or a stop reels in
    /// and closes what's open through these ticks).
    /// </summary>
    public void Tick()
    {
        if (step is Step.Idle or Step.Paused) return;
        var c = Services.Condition;
        if (c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51])
        {
            // Only the trips keep track of the loading screen (they wait for it to end); nothing acts.
            if (step is Step.Travel or Step.SpearTravel or Step.Leave) travel.Tick();
            else if (step == Step.Shop) shopper.Tick();
            return;
        }
        var me = Services.Objects.LocalPlayer;
        if (me == null || me.IsDead) return;
        if (c[ConditionFlag.OccupiedInCutSceneEvent] || c[ConditionFlag.WatchingCutscene] || c[ConditionFlag.WatchingCutscene78]) return;

        // The angler and the spearfishing game need every frame while they work (and from the frame they start).
        if (angler.Active || step == Step.Fish) angler.Tick();
        if (spear.Active || step == Step.Spear) spear.Tick();

        switch (step)
        {
            case Step.Fish: FishTick(me); return;
            case Step.Spear: SpearTick(); return;
        }
        if (DateTime.UtcNow < waitUntil) return;
        // A pause asked for while a catch, node or purchase was under way: that's done now (or never came), so pause.
        if (pausing && step is not (Step.Settle or Step.Ending or Step.Upkeep or Step.Shop or Step.SpearOpen))
        {
            travel.Cancel();
            Vnav.Stop();
            PauseOnceIn();
            return;
        }
        switch (step)
        {
            case Step.Settle: SettleTick(); return;
            case Step.Ending: EndingTick(); return;
            case Step.Upkeep: UpkeepTick(); return;
            case Step.Choose: Choose(); return;
            case Step.Gear: Gear(); return;
            case Step.Bait: BaitStep(); return;
            case Step.Shop: ShopTick(); return;
            case Step.Stand: StartStand(me); return;
            case Step.Travel: TravelTick(me); return;
            case Step.Land: LandTick(me); return;
            case Step.Face: FaceTick(me); return;
            case Step.CastCheck: CastCheckTick(me); return;
            case Step.Jiggle: JiggleTick(); return;
            case Step.Leave: LeaveTick(); return;
            case Step.Linger: LingerTick(); return;
            case Step.SpearFind: SpearFind(me); return;
            case Step.SpearTravel: SpearTravelTick(me); return;
            case Step.SpearApproach: SpearApproachTick(me); return;
            case Step.SpearOpen: SpearOpenTick(me); return;
        }
    }

    // ------------------------------------------------------------------ pause, resume, stop

    /// <summary>
    /// Pause now: lets the bite go, reels in, leaves an open spearfishing node, stops moving. With nothing out
    /// (rod away, no session, no spearfishing window) <see cref="IsPaused"/> at once, without a tick; otherwise
    /// once the ticks that follow have put it away (<see cref="Busy"/> until then). Materia extraction or
    /// reduction under way finishes first (quick; its windows mustn't stay open).
    /// </summary>
    public void Pause()
    {
        if (step is Step.Idle or Step.Paused or Step.Ending) return;
        pausing = true;
        travel.Cancel();
        Vnav.Stop();
        if (step == Step.Shop) shopper.Cancel();
        if (angler.Active) angler.Stop();
        switch (step)
        {
            case Step.Upkeep:
                Status = "Pausing after this";
                return;
            case Step.Spear:
                // A node only ends by itself once its fish are gigged: leave it now (SpearTick).
                leaveSince ??= DateTime.UtcNow;
                Status = "Leaving the spearfishing node";
                return;
            case Step.SpearOpen when openTries > 0:
                // The node may be opening as the pause comes: give its window a moment to show, then leave it.
                Settle(Step.Paused, 2);
                Status = "Pausing";
                return;
        }
        PauseOnceIn();
    }

    /// <summary>Paused at once when nothing is out; otherwise reels in (and leaves an open window) first.</summary>
    private void PauseOnceIn()
    {
        if (SomethingOut)
        {
            Settle(Step.Paused);
            Status = "Pausing";
            return;
        }
        CountSession();
        pausing = false;
        Go(Step.Paused);
        Status = "Paused";
    }

    /// <summary>Pause after the current catch (or spearfishing node): <see cref="IsPaused"/> once the rod is in.</summary>
    public void RequestPause()
    {
        if (step is Step.Idle or Step.Paused or Step.Ending) return;
        if (angler.Active)
        {
            pausing = true;
            angler.RequestStop();
            Status = "Pausing after this catch";
            return;
        }
        if (step is Step.Spear or Step.SpearOpen || spear.Active)
        {
            // The node is gigged to its end (or left when its fish can't come any more).
            pausing = true;
            Status = "Pausing after this node";
            return;
        }
        if (step == Step.Upkeep)
        {
            pausing = true;
            Status = "Pausing after this";
            return;
        }
        if (step == Step.Shop)
        {
            pausing = true;
            shopper.RequestPause();
            Status = "Pausing after this vendor";
            return;
        }
        if (step == Step.Settle)
        {
            // Already reeling in: stay paused afterwards.
            pausing = true;
            afterSettle = Step.Paused;
            return;
        }
        // Nothing under way to finish: stop moving now.
        Pause();
    }

    public void Resume()
    {
        if (step == Step.Paused)
        {
            pausing = false;
            Go(Step.Choose);
            return;
        }
        if (!pausing) return;
        pausing = false;
        if (step == Step.Settle && afterSettle == Step.Paused) afterSettle = Step.Choose;
        if (step == Step.Shop) shopper.Resume();
        // A catch already asked to stop still ends, and a node being left is left; the fisher then chooses again.
    }

    /// <summary>
    /// Ends fishing. Unless <paramref name="now"/>, what the fisher has out is put away on the ticks the run keeps
    /// giving (<see cref="Busy"/> meanwhile, not <see cref="Running"/>): the bite is let go, the rod reeled in and
    /// stowed, an open spearfishing window left; then Idle. With <paramref name="now"/> (logged out, knocked out)
    /// everything just stops.
    /// </summary>
    public void Stop(bool now = false)
    {
        var wasWorking = step != Step.Idle;
        // Only what the fisher itself has out is put away (not a rod the player took out while the run was paused).
        var reelIn = !now && step is not (Step.Idle or Step.Paused);
        pausing = false;
        leaveSince = null;
        travel.Cancel();
        shopper.Cancel();
        if (wasWorking) Vnav.Stop();
        if (spear.Active) spear.Stop();
        if (angler.Active) angler.Stop();
        // Quit right away when the game allows it; the ticks that follow finish the rest.
        if (reelIn && StanceUp) QuitRod();
        Waiting = false;
        current = null;
        Status = "";
        if (reelIn && SomethingOut)
        {
            Go(Step.Ending);
            Status = "Reeling in";
            return;
        }
        step = Step.Idle;
    }

    /// <summary>Removes the fishing hooks; always called on plugin unload.</summary>
    public void Dispose()
    {
        angler.Caught -= OnCaught;
        angler.UsedAsBait -= OnUsedAsBait;
        spear.Caught -= OnSpearCaught;
        if (step != Step.Idle)
        {
            travel.Cancel();
            shopper.Cancel();
        }
        step = Step.Idle;
        try
        {
            if (spear.Active) spear.Stop();
        }
        catch (Exception e)
        {
            Services.Log.Error(e, "Couldn't stop spearfishing on unload");
        }
        try
        {
            angler.Dispose();
        }
        catch (Exception e)
        {
            Services.Log.Error(e, "Couldn't remove the fishing hooks");
        }
    }

    // ------------------------------------------------------------------ reeling in

    /// <summary>Reels in (and deals with an open spearfishing window), then goes on with <paramref name="next"/>.</summary>
    private void Settle(Step next, double wait = 0)
    {
        afterSettle = next;
        settleSawWork = false;
        leaveSince = null;
        CountSession();
        Go(Step.Settle, wait);
    }

    /// <summary>Adds the last session's casts to the spot's count (once).</summary>
    private void CountSession()
    {
        if (sessionCounted) return;
        castsAtSpot += angler.Casts;
        sessionCounted = true;
    }

    private void SettleTick()
    {
        var since = DateTime.UtcNow - stepSince;
        if (angler.Active)
        {
            settleSawWork = true;
            Status = "Reeling in";
            if (since > TimeSpan.FromSeconds(45)) { Fail("The fishing didn't end; reel in and start again."); return; }
            return;
        }
        if (SpearGame.WindowOpen || SpearGame.AbandonAsked)
        {
            settleSawWork = true;
            // A window nobody works on (opened by hand, or as a pause came). Spearfishing for a fish still wanted:
            // work it like any node. Otherwise leave it, since nothing else can happen while it's open.
            if (leaveSince == null && SpearGame.WindowOpen && !pausing && afterSettle != Step.Paused
                && fish is { Spear: true } && current is { } t && left(t) > 0)
            {
                StartSpear();
                return;
            }
            LeaveNode();
            return;
        }
        if (StanceUp)
        {
            settleSawWork = true;
            Status = "Putting the rod away";
            if (since > TimeSpan.FromSeconds(30)) { Fail("Couldn't put the rod away; reel in and start again."); return; }
            // The angler leaves the rod in when its session ends; when it doesn't, Quit here.
            if (since > TimeSpan.FromSeconds(2)) QuitRod();
            Wait(1.5);
            return;
        }
        if (afterSettle == Step.Paused || pausing)
        {
            pausing = false;
            Go(Step.Paused);
            Status = "Paused";
            return;
        }
        // Standing up takes a moment; moving or teleporting right away is refused (GBR waits after Quit).
        Go(afterSettle, settleSawWork ? 1.5 : 0);
    }

    /// <summary>Reeling in after a stop or a failure never takes longer (the angler ends itself after 30 s).</summary>
    private static readonly TimeSpan EndingLimit = TimeSpan.FromSeconds(45);

    /// <summary>Stopped or failed: lets the session end, leaves an open spearfishing window, puts the rod away, then Idle.</summary>
    private void EndingTick()
    {
        var since = DateTime.UtcNow - stepSince;
        if (since > EndingLimit) { step = Step.Idle; return; }
        // The angler lets the bite go, reels in and stows the rod itself (ticked above while it's Active).
        if (angler.Active) return;
        if (SpearGame.WindowOpen || SpearGame.AbandonAsked)
        {
            spear.Leave();
            return;
        }
        if (StanceUp)
        {
            // The angler leaves the rod in when its session ends; when it doesn't, Quit here.
            if (since > TimeSpan.FromSeconds(2)) QuitRod();
            Wait(1.5);
            return;
        }
        step = Step.Idle;
    }

    /// <summary>The Quit action (puts the rod away), when the game allows it now.</summary>
    private static void QuitRod()
    {
        var am = ActionManager.Instance();
        if (am != null && am->GetActionStatus(ActionType.Action, FishAccess.Quit) == 0) am->UseAction(ActionType.Action, FishAccess.Quit);
    }

    /// <summary>Collector's Glove is on: fish come in as collectables.</summary>
    private static bool CollectOn => GatherActions.HasStatus(CollectStatus);

    /// <summary>
    /// Collector's Glove on for a fish caught to be reduced, off for anything else (an order counts normal fish).
    /// True while switching it (wait), or when the fisher gave up because it wouldn't come on.
    /// </summary>
    private bool SetCollect(uint itemId)
    {
        var on = collect(itemId);
        if (CollectOn == on) { collectTries = 0; return false; }
        if (++collectTries > 8)
        {
            collectTries = 0;
            if (!on) return false; // left on: the angler keeps whatever the collectable prompt asks about
            Fail("Couldn't turn Collector's Glove on, which fish caught for aetherial reduction need.");
            return true;
        }
        if (!GatherActions.Use(FishAccess.Collect)) { Wait(0.5); return true; }
        Status = on ? "Turning Collector's Glove on" : "Turning Collector's Glove off";
        Wait(1);
        return true;
    }

    // ------------------------------------------------------------------ upkeep and gear

    private void UpkeepTick()
    {
        upkeep.Tick();
        Status = upkeep.Status;
        if (upkeep.Busy) return;
        if (pausing) { PauseOnceIn(); return; }
        Go(Step.Choose);
    }

    private void Gear()
    {
        Status = "Changing to Fisher";
        if (GameState.CurrentJob == FishAccess.Fisher)
        {
            Go(fish!.Spear ? Step.SpearFind : Step.Bait);
            return;
        }
        var set = GameState.BestGearset(FishAccess.Fisher);
        if (set < 0) { Fail("No gearset saved for Fisher."); return; }
        var c = Services.Condition;
        if (c[ConditionFlag.InCombat] || c[ConditionFlag.Casting])
        {
            // The gearset can't change now; the fight doesn't count against the time to change.
            Status = "Changing to Fisher after the fight";
            stepSince = DateTime.UtcNow;
            Wait(1);
            return;
        }
        if (DateTime.UtcNow - stepSince > TimeSpan.FromSeconds(10)) { Fail("Couldn't change to Fisher."); return; }
        GameState.EquipGearset(set);
        Wait(1.5);
    }

    // ------------------------------------------------------------------ catches

    /// <summary>Items the run must keep: its targets and anything the order or the scrip earning uses.</summary>
    private bool Needed(uint itemId)
    {
        if (targets.Any(t => t.ItemId == itemId) || neededCollectables().Contains(itemId)) return true;
        return plugin.Plans.Plan.Lines.Any(l => l.ItemId == itemId) || plugin.Plans.ScripPlan.Lines.Any(l => l.ItemId == itemId);
    }

    private void OnCaught(FishCatch c) => Landed(c.ItemId, c.Amount);

    private void OnSpearCaught(uint itemId, int amount) => Landed(itemId, amount);

    private void Landed(uint itemId, int amount)
    {
        if (itemId == 0 || amount <= 0) return;
        caught[itemId] = caught.GetValueOrDefault(itemId) + amount;
        // A catch shows the fish still bite here, and that the bait works.
        amissLeaves = 0;
        noBaitStreak = 0;
        if (!Needed(itemId)) byCatch[itemId] = byCatch.GetValueOrDefault(itemId) + amount;
    }

    /// <summary>
    /// A caught fish left the bags as bait (mooched, or banked as swimbait). By-catch used that way isn't
    /// there to throw away any more. A target fish isn't reported through <see cref="consumed"/>: the game
    /// only mooches the catch that just came in, so the bags are back where they were before it and
    /// counting it as caught would leave the order short (and the clean-up would throw needed fish away).
    /// </summary>
    private void OnUsedAsBait(uint itemId)
    {
        if (!byCatch.TryGetValue(itemId, out var n)) return;
        if (n <= 1) byCatch.Remove(itemId);
        else byCatch[itemId] = n - 1;
    }

    // ------------------------------------------------------------------ names and texts

    private string Name(uint itemId) => Catalog.ItemName(itemId);

    private readonly Dictionary<uint, string> spotLabels = new();

    /// <summary>"Mistbeard Cove, Western La Noscea" (or just the zone).</summary>
    private string SpotLabel(FishingSpotInfo s)
    {
        if (spotLabels.TryGetValue(s.Id, out var known)) return known;
        var zone = FishAccess.Zone(s.TerritoryId);
        var label = s.Name.Length == 0 ? zone : zone.Length == 0 || zone == s.Name ? s.Name : $"{s.Name}, {zone}";
        return spotLabels[s.Id] = label.Length > 0 ? label : "the fishing spot";
    }

    private static string SpotName(FishingSpotInfo s) => s.Name.Length > 0 ? s.Name : "the fishing spot";

    private static readonly Dictionary<uint, string> WeatherNames = new();

    private static string WeatherName(uint id)
    {
        if (WeatherNames.TryGetValue(id, out var known)) return known;
        var name = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Weather>().GetRowOrDefault(id)?.Name.ExtractText() ?? "";
        return WeatherNames[id] = name.Length > 0 ? name : "weather " + id;
    }

    /// <summary>"Fog", "Fog or Rain", "Fog, Rain or Clouds".</summary>
    private static string Either(IReadOnlyList<uint> weathers)
    {
        var names = weathers.Select(WeatherName).Distinct().ToList();
        return names.Count <= 1 ? names.FirstOrDefault() ?? "" : string.Join(", ", names.Take(names.Count - 1)) + " or " + names[^1];
    }

    /// <summary>"in Fog after Rain", or "" for a fish that only keeps Eorzea hours.</summary>
    private static string WeatherRule(FishInfo f)
    {
        var parts = new List<string>();
        if (f.Weather.Count > 0) parts.Add("in " + Either(f.Weather));
        if (f.PreviousWeather.Count > 0) parts.Add("after " + Either(f.PreviousWeather));
        return string.Join(" ", parts);
    }

    /// <summary>" (in Fog after Rain)", or "" for a fish that only keeps Eorzea hours.</summary>
    private static string WeatherNote(FishInfo f) => WeatherRule(f) is { Length: > 0 } rule ? $" ({rule})" : "";

    /// <summary>"8 min", "under a minute", "2 h 05 min".</summary>
    private static string Span(TimeSpan t)
    {
        if (t.TotalSeconds < 60) return "under a minute";
        var minutes = (int)Math.Round(t.TotalMinutes);
        return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60:00} min";
    }

    /// <summary>The fish whose weather a window waits for: the fish, or (without a rule of its own) the first link of its mooch chain with one.</summary>
    private FishInfo WeatherFish(FishInfo f)
        => f.Weather.Count > 0 || f.PreviousWeather.Count > 0 ? f
            : CatchWindow.For(f, Data.Fish, AnyWeather).Links.FirstOrDefault(l => l.Weather.Count > 0 || l.PreviousWeather.Count > 0) ?? f;

    /// <summary>
    /// "Bites for 12 more minutes", "Bites in 8 min (in Fog)", or "" for a fish without a window. Waiting for a
    /// predator's window: "Ichimonji bites in 40 min, for the swimming shadows", "Giant Takitaro bites in 40 min (in
    /// Thunder), for Fisher's Intuition". <paramref name="lower"/> starts it in lower case, for the middle of a sentence.
    /// </summary>
    private string WindowText(FishInfo f, FishingSpotInfo s, bool lower)
    {
        var place = PlaceAt(f, s.Id);
        if (!(place?.Timed ?? Places(f.ItemId).Windowed)) return "";
        var predator = place?.Predator ?? 0;
        var now = DateTimeOffset.UtcNow;
        string text;
        if (place is not { Opens: { } opens }) text = "doesn't bite within the forecast";
        else if (opens <= now)
        {
            var open = place.Closes - now;
            var minutes = (int)Math.Round(open.TotalMinutes);
            text = open.TotalSeconds < 60 ? "bites for under a minute more"
                : minutes < 60 ? $"bites for {minutes} more {(minutes == 1 ? "minute" : "minutes")}"
                : $"bites for {Span(open)} more";
        }
        else text = $"bites in {Span(opens - now)}{WeatherNote(WeatherFish(predator != 0 && Data.Fish(predator) is { } p ? p : f))}";
        // The predator's name starts it, whatever the case asked for.
        if (predator != 0) return $"{Name(predator)} {text}, {(f.Spear ? "for the swimming shadows" : "for Fisher's Intuition")}";
        return lower ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    // ------------------------------------------------------------------ step machine

    /// <summary>
    /// Gives up: the run stops on <see cref="Failure"/> (the first reason is kept). What the fisher has out is put
    /// away on the ticks the run keeps giving (<see cref="Step.Ending"/>, <see cref="Busy"/> meanwhile), then Idle.
    /// </summary>
    private void Fail(string message)
    {
        Failure ??= message;
        Status = Failure;
        travel.Cancel();
        shopper.Cancel();
        Vnav.Stop();
        if (angler.Active) angler.Stop();
        if (spear.Active) spear.Stop();
        if (StanceUp) QuitRod();
        Waiting = false;
        pausing = false;
        leaveSince = null;
        if (SomethingOut) Go(Step.Ending);
        else step = Step.Idle;
    }

    private void Go(Step next, double wait = 0)
    {
        step = next;
        stepSince = DateTime.UtcNow;
        Wait(wait);
    }

    private void Wait(double seconds) => waitUntil = DateTime.UtcNow.AddSeconds(seconds);

    /// <summary>XZ position of a point, for the travel target.</summary>
    private static Vector2 Xz(Vector3 p) => new(p.X, p.Z);
}
