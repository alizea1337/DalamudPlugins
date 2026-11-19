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
/// while the fisher is the run's active worker. Follows GatherBuddy Reborn's AutoGather (Apache-2.0).
/// </summary>
internal sealed unsafe partial class Fisher : IDisposable
{
    private enum Step
    {
        Idle,
        Paused,
        /// <summary>Reeling in (and letting a spearfishing window close), then on to <see cref="afterSettle"/>.</summary>
        Settle,
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
    /// <summary>Status 805: Collector's Glove. Fish for an order come in as normal items, so it stays off.</summary>
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
    /// <summary>Kept from <see cref="Begin"/> for the contract; no mooch is reported through it (see <see cref="OnUsedAsBait"/> for why).</summary>
    private Action<uint, int> consumed = (_, _) => { };

    private Step step = Step.Idle;
    private Step afterSettle = Step.Choose;
    private DateTime waitUntil;
    private DateTime stepSince;
    /// <summary>Settling saw the rod out (or a window open): stand up for a moment before moving on.</summary>
    private bool settleSawWork;
    /// <summary>Settling started gigging in a window nobody worked on (once).</summary>
    private bool settleGigged;
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
    /// <summary>Has fish to work on and isn't paused.</summary>
    public bool Running => step is not (Step.Idle or Step.Paused) && targets.Any(t => left(t) > 0);
    /// <summary>Paused, with the rod reeled in and the spearfishing window closed.</summary>
    public bool IsPaused => step == Step.Paused;
    /// <summary>Nothing can be fished now: every fish left waits for its time or weather window.</summary>
    public bool Waiting { get; private set; }
    /// <summary>Waiting at the stand for GP the catch can't do without.</summary>
    public bool WaitingForGp => step == Step.Fish && angler.Active && angler.WaitingForGp;
    /// <summary>
    /// At the stand with the rod in, waiting for the fish's window to open (the fisher goes early by the lead);
    /// not fishing time. Stays the active work: <see cref="HasWorkNow"/> is true then.
    /// </summary>
    public bool WaitingForWindow => step == Step.Fish && angler.Active && angler.WaitingForWindow;
    /// <summary>The rod is out, a spearfishing window is open, or a catch is under way: don't switch to gathering now.</summary>
    public bool Busy => StanceUp || angler.Active || spear.Active || SpearGame.WindowOpen || step is Step.Settle or Step.Shop or Step.SpearOpen;
    /// <summary>A fish left in the order bites now (or within the lead time), at a spot the fisher can use.</summary>
    public bool HasWorkNow => targets.Count > 0 && Ranked().Count > 0;
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
    /// <summary>Fish the fisher can't get (no usable spot, no bait, missing actions).</summary>
    public IReadOnlyCollection<uint> Unreachable => unreachable.Keys;
    /// <summary>Fish caught on the side that the order doesn't need (item id → count), for throwing away afterwards.</summary>
    public IReadOnlyDictionary<uint, int> ByCatch => byCatch;
    /// <summary>What the Run page shows; null when not fishing for anything.</summary>
    public FishingView? View
    {
        get
        {
            if (step == Step.Idle || current is not { } t || fish is not { } f || spot is not { } s) return null;
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

    /// <summary>
    /// Starts with the run's fish targets (other targets are ignored).
    /// </summary>
    /// <param name="left">How many of a target are still wanted (the run's count, including what mooching used up).</param>
    /// <param name="neededCollectables">Items the order needs, which must never be reduced or thrown away.</param>
    /// <param name="consumed">Called when fish the run counts leave the bags as bait (mooch, swimbait): item id, amount.</param>
    public void Begin(IEnumerable<GatherTarget> targets, Func<GatherTarget, int> left, Func<IReadOnlySet<uint>> neededCollectables, Action<uint, int> consumed)
    {
        this.targets = targets.Where(t => t.Supply == Supply.Fish).ToList();
        this.left = left;
        this.neededCollectables = neededCollectables;
        this.consumed = consumed;
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
    /// fish can't be fished at all (see <see cref="Unreachable"/>).
    /// </remarks>
    public TimeSpan? WaitFor(uint itemId) => WindowWait(itemId);

    /// <summary>Every frame while fishing is the active work.</summary>
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
        if (pausing && step is not (Step.Settle or Step.Upkeep or Step.Shop or Step.SpearOpen))
        {
            travel.Cancel();
            Vnav.Stop();
            Settle(Step.Paused);
            return;
        }
        switch (step)
        {
            case Step.Settle: SettleTick(); return;
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

    /// <summary>Pause now: lets the bite go, reels in, stops moving. <see cref="IsPaused"/> once the rod is in.</summary>
    public void Pause()
    {
        if (step is Step.Idle or Step.Paused) return;
        pausing = true;
        travel.Cancel();
        Vnav.Stop();
        if (step == Step.Shop) shopper.Cancel();
        if (angler.Active) angler.Stop();
        // A spearfishing node can't be left halfway: it finishes by itself (a node is over in about a minute).
        // Materia extraction and reduction finish too; both are quick.
        if (step is Step.Spear or Step.Upkeep || spear.Active)
        {
            Status = step == Step.Upkeep ? "Pausing after this" : "Pausing after this node";
            return;
        }
        Settle(Step.Paused);
        Status = "Pausing";
    }

    /// <summary>Pause after the current catch (or spearfishing node): <see cref="IsPaused"/> once the rod is in.</summary>
    public void RequestPause()
    {
        if (step is Step.Idle or Step.Paused) return;
        if (angler.Active)
        {
            pausing = true;
            angler.RequestStop();
            Status = "Pausing after this catch";
            return;
        }
        if (step is Step.Spear or Step.Upkeep or Step.SpearOpen || spear.Active)
        {
            pausing = true;
            Status = "Pausing after this node";
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
        // A catch already asked to stop still ends; the fisher then chooses again.
    }

    /// <summary>Ends fishing: reels in first unless <paramref name="now"/> (then everything just stops).</summary>
    public void Stop(bool now = false)
    {
        var wasWorking = step != Step.Idle;
        pausing = false;
        travel.Cancel();
        shopper.Cancel();
        if (wasWorking) Vnav.Stop();
        if (spear.Active) spear.Stop();
        if (angler.Active) angler.Stop();
        // The run stops ticking the fisher from here on: put the rod away right away.
        if (!now && StanceUp) QuitRod();
        Waiting = false;
        current = null;
        step = Step.Idle;
        Status = "";
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

    /// <summary>Reels in (and lets an open spearfishing window close), then goes on with <paramref name="next"/>.</summary>
    private void Settle(Step next, double wait = 0)
    {
        afterSettle = next;
        settleSawWork = false;
        settleGigged = false;
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
        if (SpearGame.WindowOpen)
        {
            settleSawWork = true;
            // A window nobody works on (opened by hand, or just as a pause came): gig what's wanted so the node
            // runs out, since it can't be left halfway.
            if (!spear.Active && !settleGigged && fish is { Spear: true })
            {
                settleGigged = true;
                spear.Begin(wanted.Count > 0 ? wanted : new List<FishInfo> { fish });
            }
            Status = spear.Active ? spear.Status : "Waiting for the spearfishing window to close";
            if (since > TimeSpan.FromMinutes(3)) { Fail("The spearfishing window stayed open; close it and start again."); return; }
            Wait(0.5);
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

    /// <summary>The Quit action (puts the rod away), when the game allows it now.</summary>
    private static void QuitRod()
    {
        var am = ActionManager.Instance();
        if (am != null && am->GetActionStatus(ActionType.Action, FishAccess.Quit) == 0) am->UseAction(ActionType.Action, FishAccess.Quit);
    }

    /// <summary>Collector's Glove is on: fish would come in as collectables, which the run doesn't count.</summary>
    private static bool CollectOn => GatherActions.HasStatus(CollectStatus);

    /// <summary>Turns Collector's Glove off when it's on. True when the action went off (wait for it).</summary>
    private bool CollectOff()
    {
        if (!CollectOn || !GatherActions.Use(FishAccess.Collect)) return false;
        Status = "Turning Collector's Glove off";
        Wait(1);
        return true;
    }

    // ------------------------------------------------------------------ upkeep and gear

    private void UpkeepTick()
    {
        upkeep.Tick();
        Status = upkeep.Status;
        if (upkeep.Busy) return;
        if (pausing) { Settle(Step.Paused); return; }
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

    /// <summary>" (in Fog after Rain)", or "" for a fish that only keeps Eorzea hours.</summary>
    private static string WeatherNote(FishInfo f)
    {
        var parts = new List<string>();
        if (f.Weather.Count > 0) parts.Add("in " + Either(f.Weather));
        if (f.PreviousWeather.Count > 0) parts.Add("after " + Either(f.PreviousWeather));
        return parts.Count == 0 ? "" : $" ({string.Join(" ", parts)})";
    }

    /// <summary>"8 min", "under a minute", "2 h 05 min".</summary>
    private static string Span(TimeSpan t)
    {
        if (t.TotalSeconds < 60) return "under a minute";
        var minutes = (int)Math.Round(t.TotalMinutes);
        return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60:00} min";
    }

    /// <summary>
    /// "Bites for 12 more minutes", "Bites in 8 min (in Fog)", or "" for a fish without a window.
    /// <paramref name="lower"/> starts it in lower case, for the middle of a sentence.
    /// </summary>
    private string WindowText(FishInfo f, FishingSpotInfo s, bool lower)
    {
        if (!f.Restricted) return "";
        var now = DateTimeOffset.UtcNow;
        string text;
        if (WindowState(f.ItemId, s.Id) is not { } w) text = "Doesn't bite within the forecast";
        else if (w.Opens <= now)
        {
            var open = w.Closes - now;
            var minutes = (int)Math.Round(open.TotalMinutes);
            text = open.TotalSeconds < 60 ? "Bites for under a minute more"
                : minutes < 60 ? $"Bites for {minutes} more {(minutes == 1 ? "minute" : "minutes")}"
                : $"Bites for {Span(open)} more";
        }
        else text = $"Bites in {Span(w.Opens - now)}{WeatherNote(f)}";
        return lower ? char.ToLowerInvariant(text[0]) + text[1..] : text;
    }

    // ------------------------------------------------------------------ step machine

    /// <summary>Gives up: the run stops on <see cref="Failure"/> (and stops ticking the fisher), so the rod is put away now.</summary>
    private void Fail(string message)
    {
        Failure = message;
        Status = message;
        travel.Cancel();
        shopper.Cancel();
        Vnav.Stop();
        if (angler.Active) angler.Stop();
        if (spear.Active) spear.Stop();
        if (StanceUp) QuitRod();
        Waiting = false;
        pausing = false;
        step = Step.Idle;
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
