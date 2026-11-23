using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.Automation;
using Lumina.Excel;
using Provenance.Core.Cosmic;
using Provenance.Data;
using Provenance.Integrations;

namespace Provenance.Run.Cosmic;

/// <summary>
/// Gets to a Cosmic Exploration planet: from anywhere through Mare Lamentorum's Bestways Burrow aetheryte ("Travel to
/// …"), and between planets with Cruisingway and the planet picker. The menu entries are found by the game's own
/// text; the picker's reply follows what Lifestream does (facts only). Ticked on the framework thread.
/// </summary>
internal sealed unsafe class PlanetTrip
{
    private enum Step { Idle, ToBurrow, BurrowTalk, Landing, ToCruiser, CruiserTalk, Warping, Done, Failed }

    private readonly Travel travel = new();
    private Step step = Step.Idle;
    private Step noted = Step.Idle;
    private Planet target;
    private DateTime waitUntil;
    private DateTime deadline;
    private int tries;

    public string Status { get; private set; } = "";
    public string? Failure { get; private set; }
    public bool Finished => step is Step.Done or Step.Failed;
    public bool Failed => step == Step.Failed;

    public void Begin(Planet planet)
    {
        target = planet;
        Failure = null;
        tries = 0;
        noted = Step.Idle;
        DebugLog.Write("travel", $"planet trip to {planet.Name()} from {(Integrations.Cosmic.CurrentPlanet is { } from ? from.Name() : "zone " + Services.ClientState.TerritoryType)}");
        if (Integrations.Cosmic.CurrentPlanet == planet) { step = Step.Done; return; }
        if (Integrations.Cosmic.CurrentPlanet is { } here) { ToCruiser(here); return; }
        var crystal = Teleporter.Position(CosmicData.BestwaysBurrow);
        travel.GoTo("Bestways Burrow", CosmicData.MareLamentorum, new System.Numerics.Vector2(crystal?.X ?? 0, crystal?.Z ?? 0), 7f,
            () => Interact.Crystal(CosmicData.BestwaysBurrow)?.Position);
        step = Step.ToBurrow;
        Status = "Going to Bestways Burrow";
    }

    public void Cancel()
    {
        travel.Cancel();
        if (!Finished) step = Step.Idle;
    }

    public void Tick()
    {
        NoteStep();
        TickStep();
        NoteStep();
    }

    /// <summary>Each step change goes into the debug log, with the status it shows.</summary>
    private void NoteStep()
    {
        if (step == noted) return;
        DebugLog.Write("travel", $"planet trip to {target.Name()}: {noted} -> {step}{(Status.Length > 0 ? " (" + Status + ")" : "")}");
        noted = step;
    }

    private void TickStep()
    {
        if (Finished || step == Step.Idle || DateTime.UtcNow < waitUntil) return;
        switch (step)
        {
            case Step.ToBurrow:
            case Step.ToCruiser:
                travel.Tick();
                Status = travel.Status;
                if (travel.Current == Travel.State.Failed) { Fail(travel.Failure ?? "Couldn't get there."); return; }
                if (travel.Current != Travel.State.Arrived) return;
                step = step == Step.ToBurrow ? Step.BurrowTalk : Step.CruiserTalk;
                deadline = DateTime.UtcNow.AddSeconds(25);
                tries = 0;
                return;
            case Step.BurrowTalk: BurrowTalk(); return;
            case Step.Landing: Landing(); return;
            case Step.CruiserTalk: CruiserTalk(); return;
            case Step.Warping: Warping(); return;
        }
    }

    /// <summary>The aetheryte's menu offers "Travel to …" for the newest planet; the target's own entry when it's there.</summary>
    private void BurrowTalk()
    {
        if (Integrations.Cosmic.CurrentPlanet != null || Services.Condition[ConditionFlag.BetweenAreas]) { Landed(); return; }
        if (Interact.Yes()) { Wait(1); return; }
        if (Interact.Menu() is { } menu)
        {
            // The target's entry first, then any planet's (that's the newest; Cruisingway takes it from there).
            var labels = new System.Collections.Generic.List<string> { BurrowText(target) };
            foreach (var p in Planets.All) labels.Add(BurrowText(p));
            if (Interact.Choose(labels)) { Status = "Travelling to the planet"; Wait(1.5); return; }
            Fail("The aetheryte's menu had no way to the planets (is the quest \"" + Integrations.Cosmic.UnlockQuestName(Planet.SinusArdorum) + "\" done?).");
            return;
        }
        if (Interact.SkipTalk()) { Wait(0.3); return; }
        if (DateTime.UtcNow > deadline || ++tries > 6) { Fail("Couldn't use the Bestways Burrow aetheryte."); return; }
        if (Interact.Crystal(CosmicData.BestwaysBurrow) is { } crystal) Interact.With(crystal, travels: true);
        Status = "Using the aetheryte";
        Wait(1.5);
    }

    private void Landed()
    {
        step = Step.Landing;
        deadline = DateTime.UtcNow.AddSeconds(90);
        Status = "Landing";
    }

    private void Landing()
    {
        if (Services.Condition[ConditionFlag.BetweenAreas] || Services.Objects.LocalPlayer == null) { Wait(0.5); return; }
        if (Integrations.Cosmic.CurrentPlanet is not { } here)
        {
            if (DateTime.UtcNow > deadline) Fail("Didn't arrive on a planet.");
            else Wait(0.5);
            return;
        }
        if (here == target) { step = Step.Done; Status = "On " + target.Name(); return; }
        Wait(2); // the planet's objects come in a moment after landing
        ToCruiser(here);
    }

    private void ToCruiser(Planet here)
    {
        var npc = CosmicData.Cruiser(here);
        travel.GoTo("Cruisingway", npc.Territory, new System.Numerics.Vector2(npc.Position.X, npc.Position.Z), 4f,
            () => Interact.Nearest(ObjectKind.EventNpc, id => id == npc.Id)?.Position);
        step = Step.ToCruiser;
        Status = "Going to Cruisingway";
    }

    /// <summary>"Travel to another cosmic exploration area", then the planet in the picker.</summary>
    private void CruiserTalk()
    {
        if (Interact.Ready("WKSPlanetSelect") is var picker && picker != null)
        {
            Callback.Fire(picker, true, 11, (int)target, 1);
            step = Step.Warping;
            deadline = DateTime.UtcNow.AddSeconds(60);
            Status = "Flying to " + target.Name();
            Wait(1);
            return;
        }
        if (Interact.Menu() != null)
        {
            if (!Interact.Choose(new[] { CruiserText })) { Fail("Cruisingway's menu had no way to another planet."); return; }
            Wait(1);
            return;
        }
        if (Interact.SkipTalk()) { Wait(0.3); return; }
        if (DateTime.UtcNow > deadline || ++tries > 6) { Fail("Couldn't talk to Cruisingway."); return; }
        var here = Integrations.Cosmic.CurrentPlanet;
        var npc = here == null ? null : Interact.Nearest(ObjectKind.EventNpc, id => id == CosmicData.Cruiser(here.Value).Id);
        if (npc == null) { Fail("Couldn't find Cruisingway here."); return; }
        Interact.With(npc, travels: true);
        Status = "Talking to Cruisingway";
        Wait(1.2);
    }

    private void Warping()
    {
        if (Interact.Yes()) { Wait(1); return; }
        if (Services.Condition[ConditionFlag.BetweenAreas]) { Landed(); return; }
        if (Integrations.Cosmic.CurrentPlanet == target) { step = Step.Done; Status = "On " + target.Name(); return; }
        if (DateTime.UtcNow > deadline) Fail("The flight to " + target.Name() + " didn't start.");
        else Wait(0.5);
    }

    private void Fail(string why)
    {
        DebugLog.Write("travel", $"[!] planet trip to {target.Name()}: {why}");
        travel.Cancel();
        Failure = why;
        step = Step.Failed;
    }

    private void Wait(double seconds) => waitUntil = DateTime.UtcNow.AddSeconds(seconds);

    /// <summary>"Travel to Phaenna" (the aetheryte's entry for a planet, in the client language).</summary>
    private static string BurrowText(Planet p) => Text("transport/AetheryteBestwaysBurrow", 1 + (uint)p) is { Length: > 0 } t ? t : "Travel to " + p.Name();

    /// <summary>Cruisingway's "Travel to another cosmic exploration area".</summary>
    private static string CruiserText => Text("custom/009/CtsWksEntrance_00921", 18) is { Length: > 0 } t ? t : "another cosmic exploration area";

    private static string Text(string sheet, uint row)
    {
        try
        {
            var s = Services.Data.Excel.GetSheet<RawRow>(Dalamud.Utility.ClientLanguageExtensions.ToLumina(Services.Data.Language), sheet);
            return s.GetRowOrDefault(row)?.ReadStringColumn(1).ExtractText().Trim().TrimEnd('.') ?? "";
        }
        catch (Exception e)
        {
            Services.Log.Debug("Couldn't read {Sheet}: {Message}", sheet, e.Message);
            return "";
        }
    }
}
