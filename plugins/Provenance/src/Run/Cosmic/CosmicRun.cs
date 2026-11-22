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
    private enum Step { Idle, Travel, Choose, SwitchJob, OpenBoard, ReadBoard, Accept, Craft, Report, Buy, Finished }

    /// <summary>Keep this many Cosmocredits when buying materia at the cap.</summary>
    private const int MateriaFloor = 5000;
    /// <summary>This close to the Cosmocredit cap counts as at it: a mission would take it over.</summary>
    private const int CapMargin = 300;

    private readonly Plugin plugin;
    private readonly Crafter crafter;
    private readonly PlanetTrip trip = new();
    private readonly BookBuyer buyer;
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

    public CosmicRun(Plugin plugin)
    {
        this.plugin = plugin;
        crafter = new Crafter(plugin, Log);
        buyer = new BookBuyer(plugin, Log);
    }

    public bool Active => step is not (Step.Idle or Step.Finished);
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
        if (!Services.ClientState.IsLoggedIn || Services.Objects.LocalPlayer == null) return "Log in first.";
        if (!Vnav.Available) return "vnavmesh isn't loaded; it walks to the aetheryte, Cruisingway and the trader.";
        if (Data == null) return "Still reading the game data.";
        if (!Settings.Classes.Any(c => c is >= 8 and <= 15)) return "Tick at least one crafter to work with.";
        if (CosmicPlanner.TargetPlanet(Settings) is not { } planet)
            return $"Cosmic Exploration isn't open yet: it starts with the quest \"{Integrations.Cosmic.UnlockQuestName(Planet.SinusArdorum)}\".";
        if (!Integrations.Cosmic.Unlocked(planet)) return $"{planet.Name()} isn't open yet: it opens with the quest \"{Integrations.Cosmic.UnlockQuestName(planet)}\".";
        if (Raphael.ExecutablePath() == null) return "raphael-cli.exe wasn't found next to Provenance, so rotations can't be worked out.";
        if (Integrations.Cosmic.ActiveMission is var active and not 0 && Data.Mission(active) is { IsCraft: false } other)
            return $"The mission \"{other.Name}\" is under way and isn't a crafting one; finish or abandon it first.";

        log.Clear();
        saidOnce.Clear();
        emptyBoards.Clear();
        Problem = null;
        Missions = Golds = 0;
        spent = 0;
        classIndex = 0;
        startedAt = DateTime.UtcNow;
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
        if (step == Step.Craft && crafter.MidCraft && !problem)
        {
            stopAfterCraft = true;
            crafter.RequestPause();
            Status = "Stopping after this craft";
            return;
        }
        Finish(reason, problem);
    }

    private void Finish(string reason, bool problem, bool missionLeft = true)
    {
        trip.Cancel();
        buyer.Cancel();
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
        if (!Active || DateTime.UtcNow < waitUntil) return;
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
                case Step.Craft: TickCraft(); break;
                case Step.Report: Report(); break;
                case Step.Buy: TickBuy(); break;
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

        if (TimeUp()) { Finish($"Stopped after {Settings.TimeLimitMinutes} minutes, as set.", false); return; }
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
                Finish($"Goal reached: {credits:N0} Cosmocredits.", false);
                return;
            case CosmicGoal.ShoppingList when Settings.Shopping.Count == 0:
                Finish("The shopping list is empty.", false);
                return;
            case CosmicGoal.ShoppingList when credits >= CosmicPlanner.ShoppingCost(Settings, data):
                StartBuying(Settings.Shopping.ToList(), "Buying the shopping list");
                return;
        }
        // Near the cap more credits would be lost: spend some first (materia only with nothing on the shopping list).
        if (credits >= Planets.CosmocreditCap - CapMargin)
        {
            var affordable = Affordable(credits);
            if (affordable.Count > 0) { StartBuying(affordable, "At the Cosmocredit cap: buying from the shopping list"); return; }
            if (Settings.Shopping.Count == 0 && Settings.Has(CosmicExtra.MateriaAtCap) && MateriaOrders(credits) is { Count: > 0 } materia)
            {
                StartBuying(materia, "At the Cosmocredit cap: buying materia");
                return;
            }
            if (saidOnce.Add("cap"))
                Log("Cosmocredits are at the cap, so what missions pay beyond it is lost. Put something on the shopping list, or turn on \"Buy materia at the Cosmocredit cap\".", true);
        }

        var plan = plugin.CosmicPlanner.Plan(fresh: true);
        var usable = plan.Classes.Where(c => c.Problem == null && c.Ranking.Best != null).Select(c => c.ClassJob).ToList();
        if (usable.Count == 0)
        {
            var why = plan.Problem ?? string.Join(" ", plan.Classes.Select(c => c.Problem ?? $"{CosmicPlanner.JobName(c.ClassJob)}: no mission it can open reaches silver with this gear."));
            if (plan.Problem == null && plan.Classes.Any(c => c.Pending)) { Status = "Working out rotations"; Wait(1); return; }
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
        var ranking = CosmicPicker.Rank(candidates, m => plugin.CosmicPlanner.Judge(m, job.Level, job.Stats, job.Manipulation, consumables), plugin.CosmicPlanner.Record);
        // Every offered mission judged before picking, so a better one still being worked out isn't passed over; after
        // three minutes the best of those judged, and with none judged at all, more waiting (up to ten).
        var solving = ranking.Skipped.Count(x => x.Reason == CosmicPlanner.StillWorking);
        if (solving > 0)
        {
            var waited = DateTime.UtcNow - judgingSince;
            if (waited < TimeSpan.FromMinutes(3) || ranking.Best == null && waited < TimeSpan.FromMinutes(10))
            {
                Status = $"Working out rotations for the board's missions ({candidates.Count - solving} of {candidates.Count})";
                Wait(0.5);
                return;
            }
            if (ranking.Best == null) { Finish("Working out the rotations for the board's missions took too long.", true); return; }
        }
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

    /// <summary>Which special missions the extras allow (critical and two-class missions come with gathering, later).</summary>
    private bool Allowed(CosmicMission m)
    {
        if (m.Is(MissionKind.Critical) || m.Is(MissionKind.TwoClass)) return false;
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
        missionStarted = DateTime.UtcNow;
        creditsBefore = Integrations.Cosmic.Credits;
        reportSent = false;
        reportedTier = Tier.None;
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
        Go(Step.Report, 1.5);
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

    /// <summary>The shopping list's items the credits pay for now, in list order.</summary>
    private List<CosmicWish> Affordable(int credits)
    {
        var data = Data!;
        var list = new List<CosmicWish>();
        foreach (var w in Settings.Shopping)
        {
            if (data.Shop.FirstOrDefault(x => x.ItemId == w.ItemId) is not { } item) continue;
            var cost = w.Count * item.Cost;
            if (cost > credits) continue;
            credits -= cost;
            list.Add(w);
        }
        return list;
    }

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
            var labels = item.ShopId == 1770977
                ? (IReadOnlyList<string>)new[] { "Materials/Materia/Items", "Materia" }
                : new[] { "Cosmocredit Exchange" };
            var offer = new ExchangeOffer(item.ItemId, npcId, npcName, npc.Territory, new Vector2(npc.Position.X, npc.Position.Z),
                Planets.Cosmocredit, "Cosmocredits", item.Cost, item.ShopId, 0, 0, 0, labels);
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
                Settings.Shopping.RemoveAll(x => x.ItemId == w.ItemId && x.Count <= w.Count);
        buying = null;
        plugin.Config.Save();
        if (Settings.Goal == CosmicGoal.ShoppingList && Settings.Shopping.Count == 0) { Finish("Bought everything on the shopping list.", false); return; }
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
    }

    private void Go(Step next, double wait = 0, bool keepDeadline = false)
    {
        if (next != step) tries = 0;
        step = next;
        if (!keepDeadline) deadline = default;
        if (wait > 0) Wait(wait);
    }

    private void Wait(double seconds) => waitUntil = DateTime.UtcNow.AddSeconds(seconds);
}
