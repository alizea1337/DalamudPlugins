using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Provenance.Core.Cosmic;
using Provenance.Data;
using Provenance.Integrations;

namespace Provenance.Run.Cosmic;

/// <summary>
/// The Cosmic Exploration loop: to the planet, then over and over: pick the class, open the mission board, take the
/// best-paying mission the class reliably golds (silver when none does), craft it, report it and note what it came
/// to. It stops at the goal or the time limit, buys the shopping list when the credits are there, and at the
/// Cosmocredit cap spends them (shopping list, then materia when that extra is on). Ticked on the framework thread.
/// </summary>
internal sealed unsafe class CosmicRun
{
    private enum Step { Idle, Travel, Choose, SwitchJob, OpenBoard, ReadBoard, Accept, Gather, Craft, Report, Buy, Fortunes, Home, Finished }

    /// <summary>A two-class mission starts another round only with this much of its time left (gathering, then crafting).</summary>
    private const int RoundSeconds = 300;
    /// <summary>Crafts beyond the ones the objective names, at most, while the score is short of gold.</summary>
    private const int MaxExtraCrafts = 8;
    private int extraCrafts;
    /// <summary>Stellar Steady Hand uses the mission still has (it lends them for all its crafts together).</summary>
    private int steadyHandLeft;

    /// <summary>At most this many rounds of a two-class mission.</summary>
    private const int MaxRounds = 2 * CosmicPlanner.TwoClassRounds;
    /// <summary>The mission's score after the last round, to see a round still adds to it.</summary>
    private uint roundScore;

    /// <summary>Keep this many Cosmocredits when buying materia at the cap.</summary>
    private const int MateriaFloor = 5000;
    /// <summary>This close to the Cosmocredit cap counts as at it: a mission would take it over.</summary>
    private const int CapMargin = 300;

    private readonly Plugin plugin;
    private readonly Crafter crafter;
    private readonly PlanetTrip trip = new();
    private readonly BookBuyer buyer;
    private readonly FortuneDrawer fortunes;
    private readonly CosmicGatherer gatherer;
    /// <summary>The trip to the "After the order" place, when that's set to follow the loop too.</summary>
    private readonly Homecoming home;
    /// <summary>Where the loop was started from (for "Where it started").</summary>
    private (uint Territory, Vector3 Position)? startedFrom;
    /// <summary>What the loop finishes with once the trip home is over.</summary>
    private string? finishAfterHome;
    /// <summary>What happens once the gearset change is done (a two-class mission's next gathering round).</summary>
    private Action? afterSwitch;
    private int rounds;
    /// <summary>The loop finishes with this once the fortunes are drawn (null: it goes on).</summary>
    private string? finishAfterFortunes;
    /// <summary>Drawing failed once this run: not tried again until the next start.</summary>
    private bool fortunesBroken;
    private readonly List<(DateTime At, string Text, bool Problem)> log = new();
    private readonly HashSet<string> saidOnce = new();

    private Step step = Step.Idle;
    private DateTime waitUntil;
    private DateTime deadline;
    private int tries;
    private DateTime startedAt;
    private int creditsAtStart;
    private uint classJob;
    private int classIndex;
    /// <summary>Classes whose board had nothing worth taking since the last finished mission.</summary>
    private readonly HashSet<uint> emptyBoards = new();
    private int tab;
    private readonly List<uint> offered = new();
    private DateTime missionStarted;
    private int creditsBefore;
    private Tier expected;
    private bool stopAfterCraft;
    private List<CosmicWish>? buying;
    /// <summary>The mission was already under way when the loop took it up: its time and credits aren't all known.</summary>
    private bool resumed;
    /// <summary>This loop sent the report for the mission (so the tier noted is its own).</summary>
    private bool reportSent;
    private Tier reportedTier;
    /// <summary>When the board's missions started being judged, and the latest the loop waits for them.</summary>
    private DateTime judgingSince;
    /// <summary>Cosmocredits spent at the trader during this run, so what was earned still adds up.</summary>
    private int spent;
    private int creditsBeforeBuying;
    /// <summary>Times the plan was worked out again while nothing could be taken (a stuck plan ends the run after a few).</summary>
    private int replans;

    public CosmicRun(Plugin plugin)
    {
        this.plugin = plugin;
        crafter = new Crafter(plugin, Log);
        buyer = new BookBuyer(plugin, Log);
        fortunes = new FortuneDrawer(Log);
        gatherer = new CosmicGatherer(plugin, Log);
        home = new Homecoming(plugin, Log);
    }

    public bool Active => step is not (Step.Idle or Step.Finished);
    /// <summary>The loop acts on its own right now (not paused).</summary>
    public bool Working => Active && !Paused;
    /// <summary>Paused: nothing moves until <see cref="Resume"/>.</summary>
    public bool Paused { get; private set; }
    /// <summary>Asked to pause while crafting: it pauses once the item under way is finished.</summary>
    public bool Pausing { get; private set; }

    /// <summary>
    /// Pauses: right away (the trip, buying, fortunes and gathering stop where they are; fishing reels in), or after the
    /// item being crafted. A mission's own time keeps running meanwhile.
    /// </summary>
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
        trip.Cancel();
        buyer.Cancel();
        fortunes.Cancel();
        gatherer.Cancel();
        if (step == Step.Home) home.Cancel();
        Paused = true;
        Status = "Paused";
        Log("Paused.", false);
    }

    /// <summary>Carries on with the step it paused in (a trip, a purchase or a gathering round starts that step again).</summary>
    public void Resume()
    {
        if (!Active || !(Paused || Pausing)) return;
        var was = Paused;
        Paused = Pausing = false;
        Log("Resumed.", false);
        if (step == Step.Craft) { crafter.Resume(); return; }
        if (!was) return;
        switch (step)
        {
            case Step.Travel:
                if (CosmicPlanner.TargetPlanet(Settings) is { } planet) trip.Begin(planet);
                break;
            case Step.Gather:
                if (Mission is { } m) StartRound2(m);
                break;
            case Step.Home:
                if (!home.Begin(plugin.Config.AfterOrder, startedFrom)) { var r = finishAfterHome ?? "Done."; finishAfterHome = null; Finish(r, false); }
                break;
            case Step.Fortunes:
            case Step.Buy:
                // Worked out again from what's at hand (the cap and goal checks start buying or drawing again).
                if (finishAfterFortunes is { } reason) { finishAfterFortunes = null; FinishAfterFortunes(reason); }
                else Go(Step.Choose, 0.5);
                break;
        }
    }
    public string Status { get; private set; } = "";
    public string? Problem { get; private set; }
    public IReadOnlyList<(DateTime At, string Text, bool Problem)> Lines => log;
    public CosmicMission? Mission { get; private set; }
    public int Missions { get; private set; }
    public int Golds { get; private set; }
    public int Earned => Math.Max(0, Integrations.Cosmic.Credits - creditsAtStart + spent);
    public TimeSpan Elapsed => Active ? DateTime.UtcNow - startedAt : TimeSpan.Zero;
    /// <summary>What the crafter is doing right now, for the page.</summary>
    public string CraftStatus => step == Step.Craft ? crafter.Status : "";

    private CosmicSettings Settings => plugin.Config.Cosmic;
    private CosmicData? Data => plugin.Plans.Catalog?.Cosmic;

    /// <summary>Why the loop can't start, or null when it did.</summary>
    public string? Start()
    {
        if (Active) return null;
        if (plugin.Runs.Active) return "An order run is going; stop it first.";
        if (plugin.DeliveryLoop.Active) return "The custom deliveries are going; stop them first.";
        if (plugin.TribeLoop.Active) return "The allied society run is going; stop it first.";
        if (OtherPlugins.StartBlocker(lookFirst: true) is { } inTheWay) return inTheWay;
        if (!Services.ClientState.IsLoggedIn || Services.Objects.LocalPlayer == null) return "Log in first.";
        if (!Vnav.Available) return "vnavmesh isn't loaded; it walks to the aetheryte, Cruisingway and the trader.";
        if (Data == null) return "Still reading the game data.";
        if (!Settings.Classes.Any(c => c is >= 8 and <= 15)) return "Tick at least one crafter to work with.";
        if (CosmicPlanner.TargetPlanet(Settings) is not { } planet)
            return $"Cosmic Exploration isn't open yet: it starts with the quest \"{Integrations.Cosmic.UnlockQuestName(Planet.SinusArdorum)}\".";
        if (!Integrations.Cosmic.Unlocked(planet)) return $"{planet.Name()} isn't open yet: it opens with the quest \"{Integrations.Cosmic.UnlockQuestName(planet)}\".";
        if (Raphael.ExecutablePath() == null) return "Provenance's crafting solver (raphael-cli.exe) is missing next to it, so missions can't be worked out. Reinstalling Provenance brings it back.";
        if (Integrations.Cosmic.ActiveMission is var active and not 0 && Data.Mission(active) is { IsCraft: false } other)
            return $"The mission \"{other.Name}\" is under way and isn't a crafting one; finish or abandon it first.";

        plugin.Config.LastLoop = "cosmic";
        log.Clear();
        saidOnce.Clear();
        emptyBoards.Clear();
        DebugLog.BeginSession("cosmic");
        DebugLog.Write("cosmic", $"planet {planet}, classes {string.Join("/", Settings.Classes)}, goal {Settings.Goal} {Settings.CreditsTarget}, time limit {Settings.TimeLimitMinutes}, "
                                 + $"extras {Settings.Extras}, no EX {Settings.NoExMissions}, shopping {Settings.Shopping.Count}, consumables {Settings.UseConsumables}, credits {Integrations.Cosmic.Credits}");
        Problem = null;
        Missions = Golds = 0;
        spent = 0;
        fortunesBroken = false;
        classIndex = 0;
        startedAt = DateTime.UtcNow;
        startedFrom = Services.Objects.LocalPlayer is { } me ? (Services.ClientState.TerritoryType, me.Position) : null;
        finishAfterHome = null;
        creditsAtStart = Integrations.Cosmic.Credits;
        stopAfterCraft = false;
        Log($"Started on {planet.Name()}: {GoalText()}.", false);
        trip.Begin(planet);
        Go(Step.Travel);
        return null;
    }

    /// <summary>Stops the loop. A craft under way is finished first; a mission under way stays as it is.</summary>
    public void Stop(string reason = "Stopped.", bool problem = false)
    {
        if (!Active) return;
        if (step == Step.Home)
        {
            home.Cancel();
            finishAfterHome = null;
            Finish(reason, problem);
            return;
        }
        if (step == Step.Craft && crafter.MidCraft && !problem && !Paused)
        {
            stopAfterCraft = true;
            crafter.RequestPause();
            Status = "Stopping after this craft";
            return;
        }
        Finish(reason, problem);
    }

    /// <param name="goHome">Finished on its own (goal, time limit, shopping list): the "After the order" trip follows when it's set for the loop too.</param>
    private void Finish(string reason, bool problem, bool missionLeft = true, bool goHome = false)
    {
        Paused = Pausing = false;
        crafter.Release("the run ended");
        if (goHome && !problem && plugin.Config.AfterOrder.AfterCosmic && Integrations.Cosmic.ActiveMission == 0)
        {
            trip.Cancel();
            buyer.Cancel();
            fortunes.Cancel();
            gatherer.Cancel();
            Integrations.Cosmic.CloseBoard();
            Interact.Close("WKSRecipeNotebook");
            if (home.Begin(plugin.Config.AfterOrder, startedFrom))
            {
                finishAfterHome = reason;
                Log(reason + " Heading back.", false);
                Status = "Heading back";
                step = Step.Home;
                return;
            }
        }
        trip.Cancel();
        buyer.Cancel();
        fortunes.Cancel();
        gatherer.Cancel();
        afterSwitch = null;
        Integrations.Cosmic.CloseBoard();
        Interact.Close("WKSRecipeNotebook");
        if (problem) Problem = reason;
        if (missionLeft && Integrations.Cosmic.ActiveMission is var m and not 0 && Data?.Mission(m) is { } left)
            reason += $" The mission \"{left.Name}\" is still under way: report or abandon it in its window.";
        Log(reason, problem);
        Status = problem ? "Stopped: " + reason : reason;
        step = Step.Finished;
        Mission = null;
        plugin.Config.Save();
    }

    public void Tick()
    {
        // Stopped (or paused) mid-fishing: the rod is reeled in and stowed even though nothing else moves.
        if (!Active || Paused) { gatherer.WindDown(); return; }
        if (Pausing)
        {
            crafter.Tick();
            if (crafter.IsPaused || crafter.Finished) { Pausing = false; Paused = true; Status = "Paused"; }
            return;
        }
        if (DateTime.UtcNow < waitUntil) return;
        try
        {
            switch (step)
            {
                case Step.Travel: TickTravel(); break;
                case Step.Choose: Choose(); break;
                case Step.SwitchJob: SwitchJob(); break;
                case Step.OpenBoard: OpenBoard(); break;
                case Step.ReadBoard: ReadBoard(); break;
                case Step.Accept: Accept(); break;
                case Step.Gather: TickGather(); break;
                case Step.Craft: TickCraft(); break;
                case Step.Report: Report(); break;
                case Step.Buy: TickBuy(); break;
                case Step.Fortunes: TickFortunes(); break;
                case Step.Home: TickHome(); break;
            }
        }
        catch (Exception e)
        {
            Services.Log.Error(e, "Cosmic loop step {Step} failed", step);
            Finish("The Cosmic Exploration loop hit an error: " + e.Message, true);
        }
    }

    // ------------------------------------------------------------------ steps

    private void TickTravel()
    {
        trip.Tick();
        Status = trip.Status;
        if (!trip.Finished) return;
        if (trip.Failed) { Finish(trip.Failure ?? "Couldn't get to the planet.", true); return; }
        Go(Step.Choose, 1.5);
    }

    private void Choose()
    {
        var data = Data!;
        // A mission already under way (taken by hand, or before a restart): craft it, when it's a crafting one.
        if (Integrations.Cosmic.ActiveMission is var active and not 0)
        {
            if (data.Mission(active) is { IsCraft: true } tc && tc.Is(MissionKind.TwoClass))
            {
                classJob = tc.ClassJob;
                Mission = tc;
                expected = Tier.Bronze;
                resumed = true;
                rounds = 0;
                roundScore = Integrations.Cosmic.Score;
                missionStarted = DateTime.UtcNow;
                creditsBefore = Integrations.Cosmic.Credits;
                reportSent = false;
                Log($"Carrying on with \"{tc.Name}\", already under way.", false);
                StartRound(tc);
                return;
            }
            if (data.Mission(active) is { IsCraft: true } m && m.ClassJob is >= 8 and <= 15)
            {
                classJob = m.ClassJob;
                Mission = m;
                expected = Tier.Bronze;
                resumed = true;
                Log($"Carrying on with \"{m.Name}\", already under way.", false);
                if (GameState.CurrentJob != classJob) { EquipFor(classJob); return; }
                BeginCraft(m);
                return;
            }
            Finish($"The mission \"{data.Mission(active)?.Name ?? "?"}\" is under way and isn't a crafting one; finish or abandon it first.", true);
            return;
        }

        if (TimeUp()) { FinishAfterFortunes($"Stopped after {Settings.TimeLimitMinutes} minutes, as set."); return; }
        var credits = Integrations.Cosmic.Credits;
        if (Settings.Shopping.Count > 0)
        {
            if (data.Shop.Count == 0) { Finish("The Cosmocredit trader's stock couldn't be read from the game data, so the shopping list can't be bought.", true); return; }
            var unknown = Settings.Shopping.Where(w => data.Shop.All(x => x.ItemId != w.ItemId)).ToList();
            foreach (var w in unknown)
            {
                Settings.Shopping.Remove(w);
                Log($"Took {plugin.Plans.Catalog?.ItemName(w.ItemId) ?? "an item"} off the shopping list: the trader doesn't sell it (any more).", true);
            }
            if (unknown.Count > 0) plugin.Config.Save();
        }
        switch (Settings.Goal)
        {
            // Close to the cap counts: the missions' credits beyond it would be lost anyway.
            case CosmicGoal.Cosmocredits when credits >= Math.Min(Settings.CreditsTarget, Planets.CosmocreditCap - CapMargin):
                FinishAfterFortunes($"Goal reached: {credits:N0} Cosmocredits.");
                return;
            case CosmicGoal.ShoppingList when Settings.Shopping.Count == 0:
                FinishAfterFortunes("The shopping list is empty.");
                return;
            case CosmicGoal.ShoppingList:
            {
                // Everything that can be bought here is paid for: buy it. Drone Modules of another planet wait for that planet.
                var here = Settings.Shopping.Where(BuyableHere).ToList();
                foreach (var w in Settings.Shopping.Except(here))
                    if (saidOnce.Add("elsewhere" + w.ItemId))
                        Log($"{plugin.Plans.Catalog?.ItemName(w.ItemId)} can only be bought on its own planet; it stays on the list.", true);
                var affordable = Affordable();
                if (here.Count > 0 && affordable.Count == here.Count && affordable.All(a => here.Any(h => h.ItemId == a.ItemId && h.Count == a.Count)))
                {
                    StartBuying(here, "Buying the shopping list");
                    return;
                }
                var parts = affordable.Where(a => Item(a) is { } it && GameState.CurrencyCount(it.Currency) >= Cap(it) - CapMargin
                                                  && here.Any(h => h.ItemId == a.ItemId && h.Count * it.Cost > Cap(it))).ToList();
                if (parts.Count > 0) { StartBuying(parts, "Buying part of the shopping list (it costs more than the currency holds at once)"); return; }
                if (here.Count == 0) { FinishAfterFortunes("Nothing left on the shopping list can be bought on this planet."); return; }
                break;
            }
        }
        // Near the cap more credits would be lost: spend some first (materia only with nothing on the shopping list).
        if (credits >= Planets.CosmocreditCap - CapMargin)
        {
            var affordable = Affordable().Where(w => Item(w)?.Dronebits == false).ToList();
            if (affordable.Count > 0) { StartBuying(affordable, "At the Cosmocredit cap: buying from the shopping list"); return; }
            if (Settings.Shopping.Count == 0 && Settings.Has(CosmicExtra.MateriaAtCap) && MateriaOrders(credits) is { Count: > 0 } materia)
            {
                StartBuying(materia, "At the Cosmocredit cap: buying materia");
                return;
            }
            if (saidOnce.Add("cap"))
                Log("Cosmocredits are at the cap, so what missions pay beyond it is lost. Put something on the shopping list, or turn on \"Buy materia at the Cosmocredit cap\".", true);
        }

        if (Integrations.Cosmic.CurrentPlanet is { } planetHere)
        {
            var planetCredits = GameState.CurrencyCount(planetHere.CreditItem());
            if (planetCredits >= Planets.PlanetCreditCap - FortuneDrawer.Price)
            {
                if (Settings.Has(CosmicExtra.CosmicFortunes) && !fortunesBroken && FortuneDrawer.CanDraw(planetHere)) { StartFortunes(planetHere, null); return; }
                if (saidOnce.Add("planetcap"))
                    Log($"{planetCredits:N0} of {Planets.PlanetCreditCap:N0} {planetHere.Name()} credits: what missions pay beyond the cap is lost. "
                        + "Turn on Cosmic Fortunes (extras) to spend them.", true);
            }
            if (planetHere.Dronebit() is var bit and not 0 && GameState.CurrencyCount(bit) >= Planets.DronebitCap - 500)
            {
                var modules = Affordable().Where(w => Item(w)?.Dronebits == true).ToList();
                if (modules.Count > 0) { StartBuying(modules, "Near the dronebit cap: buying Drone Modules from the shopping list"); return; }
                if (saidOnce.Add("dronecap"))
                    Log($"{GameState.CurrencyCount(bit):N0} of {Planets.DronebitCap:N0} dronebits: what missions pay beyond the cap is lost. "
                        + (Settings.Shopping.Any(w => Item(w) is { Dronebits: true } it && it.Planet == planetHere)
                            ? "The Drone Modules on the shopping list can't be paid for yet."
                            : $"Put {planetHere.Name()} Drone Modules on the shopping list to spend them."), true);
            }
        }

        var plan = plugin.CosmicPlanner.Plan(fresh: true);
        var usable = plan.Classes.Where(c => c.Problem == null && c.Ranking.Best != null).Select(c => c.ClassJob).ToList();
        if (usable.Count > 0) replans = 0;
        if (usable.Count == 0)
        {
            var why = plan.Problem ?? string.Join(" ", plan.Classes.Select(c => c.Problem ?? $"{CosmicPlanner.JobName(c.ClassJob)}: no mission it can open reaches silver with this gear."));
            if (plan.Problem == null && plan.Classes.Any(c => c.Pending))
            {
                if (plan.Settled)
                {
                    if (++replans > 4) { Finish("Working out the missions took too long; try again in a while (what was worked out is kept).", true); return; }
                    plugin.CosmicPlanner.Replan();
                }
                Status = "Working out rotations";
                Wait(1);
                return;
            }
            Finish(why.Length > 0 ? why : "None of the ticked crafters can take a mission.", true);
            return;
        }
        // The best class first; the next one when its board had nothing worth taking.
        var left = usable.Where(c => !emptyBoards.Contains(c)).ToList();
        if (left.Count == 0)
        {
            Finish("The boards had nothing the ticked crafters reliably reach silver on right now. Try again in a while; the basic missions change as missions are done.", true);
            return;
        }
        classJob = left[Math.Min(classIndex, left.Count - 1)];
        if (GameState.CurrentJob != classJob) { EquipFor(classJob); return; }
        Go(Step.OpenBoard);
    }

    private void EquipFor(uint job)
    {
        var gearset = GameState.BestGearset(job);
        if (gearset < 0) { Finish($"There's no gearset saved for {CosmicPlanner.JobName(job)}.", true); return; }
        Integrations.Cosmic.CloseBoard();
        GameState.EquipGearset(gearset);
        Status = "Switching to " + CosmicPlanner.JobName(job);
        deadline = DateTime.UtcNow.AddSeconds(10);
        Go(Step.SwitchJob, 0.8, keepDeadline: true);
    }

    private void SwitchJob()
    {
        if (GameState.CurrentJob == classJob && !Services.Condition[ConditionFlag.Casting])
        {
            if (afterSwitch is { } then) { afterSwitch = null; Wait(0.8); then(); return; }
            Go(Mission != null && Integrations.Cosmic.ActiveMission == Mission.Id ? Step.Craft : Step.OpenBoard, 0.8);
            if (step == Step.Craft) BeginCraft(Mission!);
            return;
        }
        if (DateTime.UtcNow > deadline) { Finish($"Couldn't switch to {CosmicPlanner.JobName(classJob)}.", true); return; }
        Wait(0.3);
    }

    private void OpenBoard()
    {
        if (Integrations.Cosmic.BoardOpen) { tab = 0; offered.Clear(); Go(Step.ReadBoard, 0.6); return; }
        if (deadline == default) deadline = DateTime.UtcNow.AddSeconds(12);
        if (DateTime.UtcNow > deadline) { Finish("The mission board didn't open. Is the planet's mission panel showing?", true); return; }
        Status = "Opening the mission board";
        Integrations.Cosmic.OpenBoard();
        Wait(1.2);
    }

    /// <summary>Reads the basic tab, then the provisional one when its missions are wanted, then takes the best mission.</summary>
    private void ReadBoard()
    {
        if (!Integrations.Cosmic.BoardOpen) { Go(Step.OpenBoard); return; }
        var wantProvisional = Settings.Has(CosmicExtra.ChainedMissions) || Settings.Has(CosmicExtra.WeatherTimeMissions) || Settings.Has(CosmicExtra.MasterMissions);
        if (tab < 2)
        {
            var provisional = tab == 1;
            if (provisional && !wantProvisional) { tab = 2; return; }
            Integrations.Cosmic.ShowTab(provisional);
            Status = "Reading the mission board";
            tab += 10; // read on the next tick, once the tab shows
            Wait(0.7);
            return;
        }
        if (tab >= 10)
        {
            // The tab asked for must be showing; a slow switch otherwise reads the other one twice.
            var want = (uint)(tab - 10);
            if (Integrations.Cosmic.BoardTab != want)
            {
                // Give the switch a moment; then read what shows (the same missions twice count once).
                if (deadline == default) deadline = DateTime.UtcNow.AddSeconds(3);
                if (DateTime.UtcNow < deadline) { Wait(0.3); return; }
                if (saidOnce.Add("tab" + want)) Services.Log.Debug("Board tab {Want} asked for, {Shown} showing", want, Integrations.Cosmic.BoardTab);
            }
            deadline = default;
            offered.AddRange(Integrations.Cosmic.Board() ?? new List<uint>());
            tab = tab - 10 + 1;
            judgingSince = DateTime.UtcNow;
            return;
        }

        var data = Data!;
        var job = plugin.Plans.Profile?.Job(classJob);
        if (job == null) { Finish($"{CosmicPlanner.JobName(classJob)}'s stats aren't known. Check your character on the Character page.", true); return; }
        var candidates = offered.Distinct().Select(data.Mission).OfType<CosmicMission>().Where(m => m.IsCraft && m.ClassJob == classJob && Allowed(m)).ToList();
        var consumables = CosmicPlanner.InUse(plugin);
        var ranking = CosmicPicker.Rank(candidates,
            m => m.Is(MissionKind.TwoClass) ? plugin.CosmicPlanner.JudgeTwoClass(m) : plugin.CosmicPlanner.Judge(m, job.Level, job.Stats, job.Manipulation, consumables),
            plugin.CosmicPlanner.Record);
        // Every offered mission judged before picking, so a better one still being worked out isn't passed over; after
        // a minute the best of those judged, and with none judged at all, more waiting (up to five minutes).
        var solving = ranking.Skipped.Count(x => x.Reason == CosmicPlanner.StillWorking);
        if (solving > 0)
        {
            var waited = DateTime.UtcNow - judgingSince;
            if (waited < TimeSpan.FromSeconds(CosmicPlanner.ThinkSeconds) || ranking.Best == null && waited < TimeSpan.FromMinutes(5))
            {
                Status = $"Working out rotations for the board's missions ({candidates.Count - solving} of {candidates.Count})";
                Wait(0.5);
                return;
            }
            if (ranking.Best == null) { Finish("Working out the rotations for the board's missions took too long.", true); return; }
        }
        DebugLog.Write("cosmic", $"board for {classJob}: [{string.Join(",", offered.Distinct())}]; ranked: "
                                 + string.Join("; ", ranking.Choices.Take(6).Select(c => $"{c.Mission.Id} {c.Mission.Label} {c.Judgement.Tier} {c.CreditsPerHour:0}/h ({c.Judgement.Reason})"))
                                 + "; skipped: " + string.Join("; ", ranking.Skipped.Take(6).Select(x => $"{x.Mission.Id} {x.Reason}")));
        if (ranking.Best is not { } best)
        {
            Log($"{CosmicPlanner.JobName(classJob)}: nothing on the board it reliably reaches silver on with this gear; trying another crafter.", false);
            emptyBoards.Add(classJob);
            classIndex = 0;
            Go(Step.Choose, 0.5);
            return;
        }
        Mission = best.Mission;
        expected = best.Judgement.Tier;
        Status = $"Taking {best.Mission.Label}";
        Go(Step.Accept);
    }

    /// <summary>Which special missions the extras allow (critical missions come with gathering, later).</summary>
    private bool Allowed(CosmicMission m)
    {
        if (m.Is(MissionKind.Critical)) return false;
        if (Settings.NoExMissions && m.Rank > MissionRank.A) return false;
        if (m.Is(MissionKind.TwoClass) && !(Settings.Has(CosmicExtra.TwoClassMissions) && Settings.Classes.Contains(m.SecondClassJob))) return false;
        if (m.Is(MissionKind.Master) && !Settings.Has(CosmicExtra.MasterMissions)) return false;
        if (m.Is(MissionKind.Chained) && !Settings.Has(CosmicExtra.ChainedMissions)) return false;
        if ((m.Is(MissionKind.Weather) || m.Is(MissionKind.Time)) && !Settings.Has(CosmicExtra.WeatherTimeMissions)) return false;
        return true;
    }

    private void Accept()
    {
        var m = Mission!;
        if (Integrations.Cosmic.ActiveMission == m.Id)
        {
            Integrations.Cosmic.CloseBoard();
            Log($"Took {m.Label} (expecting {expected.ToString().ToLowerInvariant()}).", false);
            if (m.Is(MissionKind.TwoClass))
            {
                missionStarted = DateTime.UtcNow;
                creditsBefore = Integrations.Cosmic.Credits;
                reportSent = false;
                reportedTier = Tier.None;
                rounds = 0;
                roundScore = 0;
                StartRound(m);
                return;
            }
            BeginCraft(m);
            return;
        }
        if (deadline != default && DateTime.UtcNow < deadline) { Wait(0.3); return; }
        if (++tries > 3) { Finish($"Couldn't take the mission \"{m.Name}\".", true); return; }
        Integrations.Cosmic.Accept(m.Id);
        deadline = DateTime.UtcNow.AddSeconds(4);
        Go(Step.Accept, 0.5, keepDeadline: true);
    }

    private void BeginCraft(CosmicMission m)
    {
        var catalog = plugin.Plans.Catalog!;
        var crafts = CosmicPicker.Crafts(m);
        var jobs = new List<CraftJob>();
        for (var i = 0; i < m.Steps.Count; i++)
        {
            if (crafts[i] <= 0 || (catalog.CosmicRecipe(m.Steps[i].RecipeId) ?? catalog.Recipe(m.Steps[i].RecipeId)) is not { } recipe) continue;
            jobs.Add(new CraftJob(recipe, crafts[i], true, null, Cosmic: true,
                SteadyHand: m.Action == StellarAction.SteadyHand ? m.ActionCharges : 0, UseConsumables: Settings.UseConsumables));
        }
        if (jobs.Count == 0) { Finish($"The recipes of \"{m.Name}\" weren't found.", true); return; }
        if (!m.Is(MissionKind.TwoClass))
        {
            extraCrafts = 0;
            roundScore = 0;
            steadyHandLeft = m.Action == StellarAction.SteadyHand ? m.ActionCharges : 0;
            jobs = jobs.Select(j => j with { SteadyHand = steadyHandLeft }).ToList();
        }
        if (!m.Is(MissionKind.TwoClass))
        {
            missionStarted = DateTime.UtcNow;
            creditsBefore = Integrations.Cosmic.Credits;
            reportSent = false;
            reportedTier = Tier.None;
        }
        crafter.Begin(jobs);
        Go(Step.Craft);
    }

    private void TickCraft()
    {
        crafter.Tick();
        Status = crafter.Status.Length > 0 ? crafter.Status : "Crafting";
        if (crafter.IsPaused && stopAfterCraft) { Finish("Stopped.", false); return; }
        if (!crafter.Finished) return;
        if (crafter.Failure is { } why)
        {
            // Something to show for it: report that much; otherwise give the mission up.
            if (Integrations.Cosmic.Rank > Tier.None)
            {
                Log("Crafting stopped early: " + why + " Reporting what's done.", true);
                craftFailed = true;
                Go(Step.Report, 1);
                return;
            }
            Integrations.Cosmic.Abandon();
            Finish("Crafting stopped: " + why + " The mission was abandoned.", true, missionLeft: false);
            return;
        }
        steadyHandLeft = Math.Max(0, steadyHandLeft - crafter.SteadyHandUsed);
        // Short of gold with supplies left: one more of the final item (score adds up per item), while it still adds.
        if (Mission is { } more && !more.Is(MissionKind.TwoClass) && Integrations.Cosmic.ActiveMission == more.Id && Integrations.Cosmic.Rank < Tier.Gold)
        {
            var score = Integrations.Cosmic.Score;
            var rose = score > roundScore;
            DebugLog.Write("cosmic", $"after the crafts: rank {Integrations.Cosmic.Rank}, score {score} (was {roundScore}), extra crafts {extraCrafts}, time left {TimeLeft(more):0} s, Steady Hand left {steadyHandLeft}");
            roundScore = score;
            if (rose && extraCrafts < MaxExtraCrafts && TimeLeft(more) > 60 && OneMore(more) is { Count: > 0 } jobs)
            {
                extraCrafts++;
                Log($"{Integrations.Cosmic.Rank} so far ({score:N0}); supplies are left, so crafting another for gold.", false);
                crafter.Begin(jobs);
                return;
            }
        }
        if (Mission is { } tc && tc.Is(MissionKind.TwoClass))
        {
            if (Integrations.Cosmic.ActiveMission != tc.Id) { Go(Step.Report, 0.5); return; }
            var score = Integrations.Cosmic.Score;
            var rose = score > roundScore;
            roundScore = score;
            if (Integrations.Cosmic.Rank < Tier.Gold && rose && rounds < MaxRounds && TimeLeft(tc) > RoundSeconds && !TimeUp())
            {
                Log($"Round {rounds} done: {Integrations.Cosmic.Rank.ToString().ToLowerInvariant()} so far ({score:N0}), going round again for gold.", false);
                StartRound(tc);
                return;
            }
        }
        Go(Step.Report, 1.5);
    }

    /// <summary>
    /// The crafts for one more of a mission's final item (with the steps in between), when the Cosmopouch still holds
    /// the supplies for it; empty otherwise (and for missions of several different items).
    /// </summary>
    private List<CraftJob> OneMore(CosmicMission m)
    {
        if (m.Final is not { } final || m.Targets.Select(t => t.ItemId).Distinct().Count() > 1 || plugin.Plans.Catalog is not { } catalog) return new List<CraftJob>();
        var single = m with { Targets = new[] { new MissionTarget(final.ItemId, 1) } };
        var crafts = CosmicPicker.Crafts(single);
        var supplies = 0;
        for (var i = 0; i < m.Steps.Count; i++)
            if (m.Steps[i].IngredientId == m.SuppliedItem) supplies += crafts[i] * m.Steps[i].IngredientAmount;
        // The supplies can sit in the bags or the Cosmopouch: both count.
        var have = m.SuppliedItem == 0 ? 0 : GameState.BagCount(m.SuppliedItem);
        DebugLog.Write("cosmic", $"one more {final.ItemId}: {supplies} of {m.SuppliedItem} needed, {have} at hand (pouch {GameState.PouchCount(m.SuppliedItem)})");
        if (m.SuppliedItem == 0 || have < supplies) return new List<CraftJob>();
        var jobs = new List<CraftJob>();
        for (var i = 0; i < m.Steps.Count; i++)
            if (crafts[i] > 0 && (catalog.CosmicRecipe(m.Steps[i].RecipeId) ?? catalog.Recipe(m.Steps[i].RecipeId)) is { } recipe)
                jobs.Add(new CraftJob(recipe, crafts[i], true, null, Cosmic: true, SteadyHand: steadyHandLeft, UseConsumables: Settings.UseConsumables));
        return jobs;
    }

    /// <summary>Seconds a mission still has (its time limit since it was taken; plenty when it has none).</summary>
    private double TimeLeft(CosmicMission m)
        => m.TimeLimitSeconds <= 0 ? double.MaxValue : m.TimeLimitSeconds - (DateTime.UtcNow - missionStarted).TotalSeconds;

    /// <summary>A two-class round: the gatherer's gearset, the material for one craft, then the crafter crafts it.</summary>
    private void StartRound(CosmicMission m)
    {
        rounds++;
        if (GameState.CurrentJob != m.ClassJob)
        {
            classJob = m.ClassJob;
            afterSwitch = () => StartRound2(m);
            EquipFor(m.ClassJob);
            return;
        }
        StartRound2(m);
    }

    private void StartRound2(CosmicMission m)
    {
        var first = m.Steps[0];
        var crafts = Math.Max(1, CosmicPicker.Crafts(m)[0]);
        gatherer.Begin(m, first.IngredientId, crafts * first.IngredientAmount);
        Go(Step.Gather);
    }

    private void TickGather()
    {
        gatherer.Tick();
        Status = gatherer.Status;
        if (!gatherer.Finished) return;
        if (gatherer.Failure is { } why)
        {
            if (Integrations.Cosmic.ActiveMission != Mission!.Id) { Go(Step.Report, 0.5); return; }
            if (Integrations.Cosmic.Rank > Tier.None) { Log(why + " Reporting what's done.", true); craftFailed = true; Go(Step.Report, 1); return; }
            Integrations.Cosmic.Abandon();
            Finish(why + " The mission was abandoned.", true, missionLeft: false);
            return;
        }
        BeginCraft(Mission!);
    }

    private void Report()
    {
        var m = Mission!;
        if (Integrations.Cosmic.ActiveMission != m.Id)
        {
            if (!reportSent)
            {
                // Gone before the loop reported it (its time ran out, or it was ended by hand): nothing to note.
                Log($"\"{m.Name}\" ended before it was reported.", true);
                Mission = null;
                resumed = craftFailed = false;
                Go(Step.Choose, 1.5);
                return;
            }
            // Reported: note what it came to.
            var gained = Integrations.Cosmic.Credits - creditsBefore;
            var seconds = (DateTime.UtcNow - missionStarted).TotalSeconds;
            var tier = reportedTier;
            if (!resumed)
            {
                if (!Settings.Records.TryGetValue(m.Id, out var record)) Settings.Records[m.Id] = record = new MissionRecord();
                record.Add(tier, Math.Max(0, gained), seconds, craftFailed ? 0 : crafter.LastQuality);
            }
            resumed = craftFailed = false;
            Missions++;
            if (tier == Tier.Gold) Golds++;
            var note = tier < expected ? $" (expected {expected.ToString().ToLowerInvariant()}; noted for next time)" : "";
            Log($"{tier}: {m.Label}, +{Math.Max(0, gained):N0} Cosmocredits in {seconds / 60:0.0} min{note}.", tier < expected);
            plugin.Config.Save();
            emptyBoards.Clear();
            Mission = null;
            if (stopAfterCraft) { Finish("Stopped.", false); return; }
            Go(Step.Choose, 1.5);
            return;
        }
        if (deadline != default && DateTime.UtcNow < deadline) { Wait(0.3); return; }
        if (++tries > 4) { Finish($"Couldn't report the mission \"{m.Name}\".", true); return; }
        reportedTier = Integrations.Cosmic.Rank;
        Status = "Reporting";
        Integrations.Cosmic.Report();
        reportSent = true;
        deadline = DateTime.UtcNow.AddSeconds(5);
        Go(Step.Report, 0.5, keepDeadline: true);
    }

    private bool craftFailed;

    // ------------------------------------------------------------------ buying

    private CosmicShopItem? Item(CosmicWish w) => Data?.Shop.FirstOrDefault(x => x.ItemId == w.ItemId);

    /// <summary>Sold where the loop is: the Cosmocredit trader's stock anywhere, Drone Modules only on their planet.</summary>
    private bool BuyableHere(CosmicWish w)
        => Item(w) is { } item && (item.Planet == null || item.Planet == Integrations.Cosmic.CurrentPlanet);

    /// <summary>
    /// The shopping list's items that what's at hand pays for now (each in its own currency), in list order: whole,
    /// and, for one whose whole count costs more than its currency holds at its cap, as many as it pays for.
    /// </summary>
    private List<CosmicWish> Affordable()
    {
        var budget = new Dictionary<uint, int>();
        var list = new List<CosmicWish>();
        foreach (var w in Settings.Shopping)
        {
            if (!BuyableHere(w) || Item(w) is not { } item) continue;
            if (!budget.TryGetValue(item.Currency, out var have)) have = GameState.CurrencyCount(item.Currency);
            var cost = w.Count * item.Cost;
            if (cost <= have)
            {
                budget[item.Currency] = have - cost;
                list.Add(w);
                continue;
            }
            var units = have / Math.Max(1, item.Cost);
            if (cost > Cap(item) && units > 0)
            {
                budget[item.Currency] = have - units * item.Cost;
                list.Add(new CosmicWish { ItemId = w.ItemId, Count = units });
                continue;
            }
            budget[item.Currency] = have;
        }
        return list;
    }

    /// <summary>Most of an item's currency there can be at once.</summary>
    private static int Cap(CosmicShopItem item) => item.Dronebits ? Planets.DronebitCap : Planets.CosmocreditCap;

    /// <summary>The top crafter and gatherer materia, one of each in turn, down to <see cref="MateriaFloor"/> Cosmocredits.</summary>
    private List<CosmicWish> MateriaOrders(int credits)
    {
        var materia = Data!.TopMateria;
        if (materia.Count == 0) return new List<CosmicWish>();
        var budget = credits - MateriaFloor;
        var counts = new int[materia.Count];
        for (var i = 0; budget >= materia[i % materia.Count].Cost; i++)
        {
            budget -= materia[i % materia.Count].Cost;
            counts[i % materia.Count]++;
        }
        return materia.Select((m, i) => new CosmicWish { ItemId = m.ItemId, Count = counts[i] }).Where(w => w.Count > 0).ToList();
    }

    private void StartBuying(List<CosmicWish> wishes, string why)
    {
        var data = Data!;
        var planet = Integrations.Cosmic.CurrentPlanet ?? CosmicPlanner.TargetPlanet(Settings) ?? Planet.SinusArdorum;
        var npc = CosmicData.Trader(planet);
        var here = Interact.Nearest(ObjectKind.EventNpc, id => CosmicData.TraderIds.Contains(id));
        var npcId = here?.BaseId ?? npc.Id;
        var npcName = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.ENpcResident>().GetRowOrDefault(npcId)?.Singular.ExtractText() ?? "the Cosmocredit trader";
        var orders = new List<BookOrder>();
        foreach (var w in wishes)
        {
            if (data.Shop.FirstOrDefault(x => x.ItemId == w.ItemId) is not { } item) continue;
            ExchangeOffer offer;
            if (item.Dronebits)
            {
                // Drone Modules: Kaede, on their own planet, for its dronebits.
                if (item.Planet is not { } droneplanet || CosmicData.DroneTrader(droneplanet) is not { } kaede) continue;
                var currencyName = plugin.Plans.Catalog?.ItemNamePlural(item.Currency) ?? "dronebits";
                offer = new ExchangeOffer(item.ItemId, kaede.Id, "Kaede", kaede.Territory, new Vector2(kaede.Position.X, kaede.Position.Z),
                    item.Currency, currencyName, item.Cost, item.ShopId, 0, 0, 0, new[] { DroneMenuText, "drone modules" });
            }
            else
            {
                var labels = item.ShopId == 1770977
                    ? (IReadOnlyList<string>)new[] { "Materials/Materia/Items", "Materia" }
                    : new[] { "Cosmocredit Exchange" };
                offer = new ExchangeOffer(item.ItemId, npcId, npcName, npc.Territory, new Vector2(npc.Position.X, npc.Position.Z),
                    Planets.Cosmocredit, "Cosmocredits", item.Cost, item.ShopId, 0, 0, 0, labels);
            }
            orders.Add(new BookOrder(item.ItemId, offer, Math.Max(1, w.Count)));
        }
        if (orders.Count == 0) { Finish("Nothing on the list could be bought from the Cosmocredit trader.", true); return; }
        Integrations.Cosmic.CloseBoard();
        Log(why + ".", false);
        buying = wishes;
        creditsBeforeBuying = Integrations.Cosmic.Credits;
        buyer.Begin(orders);
        Go(Step.Buy);
    }

    private void TickBuy()
    {
        buyer.Tick();
        Status = buyer.Status;
        if (!buyer.Finished) return;
        spent += Math.Max(0, creditsBeforeBuying - Integrations.Cosmic.Credits);
        if (buyer.Failure is { } why) { Finish(why, true); return; }
        // Bought: off the shopping list.
        if (buying != null)
            foreach (var w in buying)
                if (Settings.Shopping.FirstOrDefault(x => x.ItemId == w.ItemId) is { } wish)
                {
                    wish.Count -= w.Count;
                    if (wish.Count <= 0) Settings.Shopping.Remove(wish);
                }
        buying = null;
        plugin.Config.Save();
        if (Settings.Goal == CosmicGoal.ShoppingList && Settings.Shopping.Count == 0) { FinishAfterFortunes("Bought everything on the shopping list."); return; }
        Go(Step.Choose, 1);
    }

    /// <summary>Kaede's "Acquire drone modules".</summary>
    private static string DroneMenuText
    {
        get
        {
            try
            {
                var s = Services.Data.Excel.GetSheet<Lumina.Excel.RawRow>(Dalamud.Utility.ClientLanguageExtensions.ToLumina(Services.Data.Language), "custom/009/CtsWksTreasure_00966");
                return s.GetRowOrDefault(10)?.ReadStringColumn(1).ExtractText().Trim().TrimEnd('.') ?? "Acquire drone modules";
            }
            catch
            {
                return "Acquire drone modules";
            }
        }
    }

    // ------------------------------------------------------------------ fortunes

    /// <summary>Draws the planet's credits; then goes on (<paramref name="thenFinish"/> null) or finishes with that.</summary>
    private void StartFortunes(Planet planet, string? thenFinish)
    {
        Integrations.Cosmic.CloseBoard();
        finishAfterFortunes = thenFinish;
        Log($"Drawing cosmic fortunes with {planet.Name()} credits.", false);
        fortunes.Begin(planet);
        Go(Step.Fortunes);
    }

    /// <summary>Finishes, drawing the planet's credits first when Cosmic Fortunes is on and there's enough for a draw.</summary>
    private void FinishAfterFortunes(string reason)
    {
        if (Settings.Has(CosmicExtra.CosmicFortunes) && !fortunesBroken && Integrations.Cosmic.CurrentPlanet is { } p && FortuneDrawer.CanDraw(p))
        {
            StartFortunes(p, reason);
            return;
        }
        Finish(reason, false, goHome: true);
    }

    private void TickHome()
    {
        home.Tick();
        Status = home.Status.Length > 0 ? home.Status : "Heading back";
        if (!home.Finished) return;
        if (home.Failure is { } why) Log("Couldn't get back: " + why, true);
        var reason = finishAfterHome ?? "Done.";
        finishAfterHome = null;
        Finish(reason, false);
    }

    private void TickFortunes()
    {
        fortunes.Tick();
        Status = fortunes.Status;
        if (!fortunes.Finished) return;
        if (fortunes.Failure == null && fortunes.Draws == 0 && Integrations.Cosmic.CurrentPlanet is { } p
            && GameState.CurrencyCount(p.CreditItem()) >= Planets.PlanetCreditCap - FortuneDrawer.Price)
        {
            Log("Cosmic Fortunes: no fortune could be drawn; leaving them for this run.", true);
            fortunesBroken = true;
        }
        if (fortunes.Failure is { } why)
        {
            Log("Cosmic Fortunes: " + why, true);
            // Without draws the credits would go over the cap again right away: don't try again this run.
            fortunesBroken = true;
        }
        if (finishAfterFortunes is { } reason) { finishAfterFortunes = null; Finish(reason, false, goHome: true); return; }
        Go(Step.Choose, 1);
    }

    // ------------------------------------------------------------------ helpers

    private bool TimeUp() => Settings.TimeLimitMinutes > 0 && DateTime.UtcNow - startedAt >= TimeSpan.FromMinutes(Settings.TimeLimitMinutes);

    private string GoalText() => Settings.Goal switch
    {
        CosmicGoal.Cosmocredits => $"up to {Settings.CreditsTarget:N0} Cosmocredits",
        CosmicGoal.ShoppingList => "the shopping list",
        _ => "until stopped",
    } + (Settings.TimeLimitMinutes > 0 ? $", for at most {Settings.TimeLimitMinutes} minutes" : "");

    private void Log(string text, bool problem)
    {
        log.Add((DateTime.Now, text, problem));
        if (log.Count > 200) log.RemoveAt(0);
        Services.Log.Information("[Cosmic] {Text}", text);
        DebugLog.Write("cosmic", (problem ? "[!] " : "") + text);
    }

    private void Go(Step next, double wait = 0, bool keepDeadline = false)
    {
        if (next != step)
        {
            tries = 0;
            DebugLog.Write("cosmic", $"step {step} -> {next}; mission {Integrations.Cosmic.ActiveMission}, score {Integrations.Cosmic.Score}, rank {Integrations.Cosmic.Rank}, job {GameState.CurrentJob}");
        }
        step = next;
        if (!keepDeadline) deadline = default;
        if (wait > 0) Wait(wait);
    }

    private void Wait(double seconds) => waitUntil = DateTime.UtcNow.AddSeconds(seconds);
}
