using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.UIHelpers.AddonMasterImplementations;
using Provenance.Data;
using Provenance.Integrations;

namespace Provenance.Run;

/// <summary>One item to buy at one vendor.</summary>
internal sealed record Purchase(uint ItemId, int Amount, VendorOffer Offer);

/// <summary>
/// Buys from gil vendors, one NPC at a time: travel there, talk, pick the shop from the menu, buy,
/// check the bags, close. Ticked on the framework thread.
/// </summary>
internal sealed unsafe class Shopper
{
    private enum Step { Idle, Travel, Talk, Menu, Buying, Closing, Done, Failed }

    private readonly Plugin plugin;
    private readonly Action<string, bool> log;
    private readonly Travel travel = new();
    private readonly List<IGrouping<uint, Purchase>> stops = new();
    private int stopIndex;
    private Dictionary<uint, int> startCounts = new();
    private Step step = Step.Idle;
    private DateTime waitUntil;
    private DateTime deadline;
    private int menuTry;
    private int talkTries;
    private bool pauseRequested;

    public Shopper(Plugin plugin, Action<string, bool> log)
    {
        this.plugin = plugin;
        this.log = log;
    }

    public string Status { get; private set; } = "";
    public string? Failure { get; private set; }
    public bool Finished => step is Step.Done or Step.Failed;
    public bool IsPaused { get; private set; }
    public int StopsTotal => stops.Count;
    public int StopNumber => Math.Min(stopIndex + 1, stops.Count);

    public void Begin(IEnumerable<Purchase> purchases)
    {
        stops.Clear();
        stops.AddRange(purchases.Where(p => p.Amount > 0).GroupBy(p => p.Offer.NpcId));
        stopIndex = 0;
        Failure = null;
        IsPaused = false;
        pauseRequested = false;
        startCounts = stops.SelectMany(g => g).Select(p => p.ItemId).Distinct().ToDictionary(id => id, GameState.BagCount);
        if (stops.Count == 0) { step = Step.Done; return; }
        StartStop();
    }

    public void RequestPause() => pauseRequested = true;

    public void Resume()
    {
        pauseRequested = false;
        if (!IsPaused) return;
        IsPaused = false;
        StartStop();
    }

    private IGrouping<uint, Purchase> Stop => stops[stopIndex];

    private void StartStop()
    {
        var offer = Stop.First().Offer;
        travel.GoTo($"{offer.NpcName} in {offer.PlaceName}", offer.TerritoryId, offer.Position, 3.5f,
            () => Interact.Nearest(ObjectKind.EventNpc, id => id == offer.NpcId)?.Position);
        menuTry = 0;
        talkTries = 0;
        step = Step.Travel;
    }

    private int Bought(uint item) => GameState.BagCount(item) - startCounts.GetValueOrDefault(item);

    private Purchase? NextToBuy() => Stop.FirstOrDefault(p => Bought(p.ItemId) < p.Amount);

    public void Tick()
    {
        if (Finished || IsPaused || DateTime.UtcNow < waitUntil) return;
        switch (step)
        {
            case Step.Travel:
                travel.Tick();
                Status = travel.Status;
                if (travel.Current == Travel.State.Failed) { Fail(travel.Failure!); return; }
                if (travel.Current == Travel.State.Arrived) { step = Step.Talk; deadline = DateTime.UtcNow.AddSeconds(15); }
                return;

            case Step.Talk:
                Talk();
                return;

            case Step.Menu:
                Menu();
                return;

            case Step.Buying:
                Buy();
                return;

            case Step.Closing:
                Interact.Close("Shop");
                Interact.Close("SelectIconString");
                Interact.Close("SelectString");
                if (Interact.IsOpen("Shop")) { Wait(0.3); return; }
                stopIndex++;
                if (pauseRequested) { IsPaused = true; Status = "Paused"; pauseRequested = false; if (stopIndex >= stops.Count) step = Step.Done; return; }
                if (stopIndex >= stops.Count) { step = Step.Done; Status = "Bought everything"; return; }
                StartStop();
                Wait(0.5);
                return;
        }
    }

    private void Talk()
    {
        var offer = Stop.First().Offer;
        if (Interact.Ready("Shop") != null) { step = Step.Buying; return; }
        if (Interact.Menu() != null) { step = Step.Menu; return; }
        if (Interact.SkipTalk()) { Wait(0.3); return; }
        if (DateTime.UtcNow > deadline) { Fail($"{offer.NpcName} didn't answer."); return; }
        var npc = Interact.Nearest(ObjectKind.EventNpc, id => id == offer.NpcId);
        if (npc == null) { Fail($"Couldn't find {offer.NpcName} here."); return; }
        Status = "Talking to " + offer.NpcName;
        if (++talkTries > 6) { Fail($"Couldn't talk to {offer.NpcName}."); return; }
        Interact.With(npc);
        Wait(1);
    }

    /// <summary>Picks the shop by name; when the names don't match, tries the entries one by one.</summary>
    private void Menu()
    {
        if (Interact.Ready("Shop") != null) { step = Step.Buying; return; }
        var menu = Interact.Menu();
        if (menu == null) { step = Step.Talk; Wait(0.3); return; }
        var labels = Stop.First().Offer.MenuLabels;
        if (menuTry == 0 && Interact.Choose(labels)) { menuTry = 1; Wait(0.6); return; }
        // Name didn't match: try each entry (the last is usually "Cancel").
        var index = Math.Max(0, menuTry - 1);
        if (index >= menu.Count - 1) { Fail($"Couldn't find the right shop at {Stop.First().Offer.NpcName}."); return; }
        menu[index].Select();
        menuTry = index + 2;
        Wait(0.6);
    }

    private void Buy()
    {
        var shopAddon = Interact.Ready("Shop");
        if (shopAddon == null)
        {
            if (Interact.Yes()) { Wait(0.5); return; }
            if (Interact.Menu() != null) { step = Step.Menu; return; }
            Wait(0.2);
            return;
        }
        if (Interact.Yes()) { Wait(0.5); return; }

        var next = NextToBuy();
        if (next == null) { step = Step.Closing; return; }

        var shop = new AddonMaster.Shop((nint)shopAddon);
        var items = shop.ShopItems;
        var index = Array.FindIndex(items, i => i.ItemId == next.ItemId);
        if (index < 0)
        {
            // Wrong shop of this NPC: close it and try the next menu entry.
            Interact.Close("Shop");
            step = Step.Talk;
            deadline = DateTime.UtcNow.AddSeconds(15);
            Wait(0.8);
            return;
        }

        var left = next.Amount - Bought(next.ItemId);
        var amount = Math.Min(99, left);
        long gil = GameState.BagCount(1);
        if (plugin.Runs.GilLeft is { } budget) gil = Math.Min(gil, budget);
        var cost = (long)items[index].CostAmount * amount;
        if (cost > gil)
        {
            amount = (int)Math.Min(amount, gil / Math.Max(1, items[index].CostAmount));
            if (amount <= 0)
            {
                Fail(plugin.Runs.GilLeft is { } budgetLeft && budgetLeft < GameState.BagCount(1)
                    ? $"The run reached its gil limit (Security tab) before buying {plugin.Plans.Catalog!.ItemName(next.ItemId)}."
                    : $"Not enough gil for {plugin.Plans.Catalog!.ItemName(next.ItemId)}.");
                return;
            }
        }
        Status = $"Buying {amount}× {plugin.Plans.Catalog!.ItemName(next.ItemId)}";
        var before = GameState.BagCount(next.ItemId);
        items[index].Select(amount);
        log($"Bought {amount}× {plugin.Plans.Catalog!.ItemName(next.ItemId)} from {next.Offer.NpcName}.", false);
        deadline = DateTime.UtcNow.AddSeconds(6);
        Wait(1.2);
    }

    public void Cancel()
    {
        travel.Cancel();
        Interact.Close("Shop");
    }

    private void Fail(string message)
    {
        Failure = message;
        Status = message;
        step = Step.Failed;
        travel.Cancel();
        Interact.Close("Shop");
    }

    private void Wait(double seconds) => waitUntil = DateTime.UtcNow.AddSeconds(seconds);
}
