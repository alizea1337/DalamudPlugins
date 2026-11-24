using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Provenance.Data;
using Provenance.Integrations;

namespace Provenance.Run;

/// <summary>Something to buy at one exchange: a book or tome (one), or a material (as many as <paramref name="Amount"/>).</summary>
internal sealed record BookOrder(uint ItemId, ExchangeOffer Offer, int Amount = 1);

/// <summary>
/// Buys master books, folklore tomes and materials with scrips or tokens: travel to the exchange, talk, pick
/// the menu entry, then in the exchange window pick the page and subpage (scrip exchanges) or the row (plain
/// exchanges), confirm, and check the bags; for a material again until the amount is in (at most 99 at a time).
/// Window handling follows GatherBuddy Reborn's Vulcan vendor code (Apache-2.0). Ticked on the framework thread.
/// </summary>
internal sealed unsafe class BookBuyer
{
    private enum Step { Idle, Travel, Talk, Menu, Window, Closing, Done, Failed }

    private static readonly string[] Windows = { "InclusionShop", "ShopExchangeCurrency", "ShopExchangeItem" };
    private static readonly string[] Dialogs = { "ShopExchangeItemDialog", "ShopExchangeCurrencyDialog" };

    private readonly Plugin plugin;
    private readonly Action<string, bool> log;
    /// <summary>Vendors stand in shops and under roofs: the buildings rules are on.</summary>
    private readonly Travel travel = new() { RoofAware = true };
    private readonly Queue<BookOrder> orders = new();
    private Step step = Step.Idle;
    private DateTime waitUntil;
    private DateTime deadline;
    private int talkTries;
    private int menuTry;
    private int buyTries;
    /// <summary>0: pick the page next, 1: the subpage, 2: both picked.</summary>
    private int pageStep;
    private int subPage;
    private int startCount;
    /// <summary>Bought of the current order so far (bags now less bags at the start).</summary>
    private int got;
    /// <summary>The exchange window hands over at most this many in one go.</summary>
    private const int MaxPerExchange = 99;

    public BookBuyer(Plugin plugin, Action<string, bool> log)
    {
        this.plugin = plugin;
        this.log = log;
    }

    public string Status { get; private set; } = "";
    public string? Failure { get; private set; }
    public bool Finished => step is Step.Done or Step.Failed;

    public void Begin(IEnumerable<BookOrder> books)
    {
        orders.Clear();
        foreach (var b in books) orders.Enqueue(b);
        Failure = null;
        if (orders.Count == 0) { step = Step.Done; return; }
        StartOrder();
    }

    public void Cancel()
    {
        travel.Cancel();
        CloseWindows();
        if (!Finished) step = Step.Idle;
    }

    private BookOrder Current => orders.Peek();
    private string Name(uint item) => plugin.Plans.Catalog?.ItemName(item) ?? "item " + item;

    private void StartOrder()
    {
        var o = Current.Offer;
        travel.GoTo($"{o.NpcName} in {o.PlaceName}", o.TerritoryId, o.Position, 3.5f,
            () => Interact.Nearest(ObjectKind.EventNpc, id => id == o.NpcId)?.Position);
        talkTries = 0;
        menuTry = 0;
        buyTries = 0;
        pageStep = 0;
        subPage = o.SubPage;
        startCount = GameState.BagCount(Current.ItemId);
        got = 0;
        step = Step.Travel;
    }

    public void Tick()
    {
        if (Finished || step == Step.Idle || DateTime.UtcNow < waitUntil) return;
        switch (step)
        {
            case Step.Travel:
                travel.Tick();
                Status = travel.Status;
                if (travel.Current == Travel.State.Failed) { Fail(travel.Failure!); return; }
                if (travel.Current == Travel.State.Arrived) { step = Step.Talk; deadline = DateTime.UtcNow.AddSeconds(15); }
                return;
            case Step.Talk: Talk(); return;
            case Step.Menu: Menu(); return;
            case Step.Window: Window(); return;
            case Step.Closing:
                CloseWindows();
                if (Windows.Any(Interact.IsOpen)) { Wait(0.3); return; }
                orders.Dequeue();
                if (orders.Count == 0) { step = Step.Done; Status = "Bought everything"; return; }
                StartOrder();
                Wait(0.5);
                return;
        }
    }

    private bool WindowOpen => Windows.Any(w => Interact.Ready(w) != null);

    private void Talk()
    {
        var o = Current.Offer;
        if (WindowOpen) { step = Step.Window; deadline = DateTime.UtcNow.AddSeconds(30); return; }
        // A menu entry opened something else (a gil shop): close it, the next talk tries the next entry.
        if (Interact.Ready("Shop") != null) { Interact.Close("Shop"); menuTry = Math.Max(menuTry, 1); Wait(0.6); return; }
        if (Interact.Menu() != null) { step = Step.Menu; return; }
        if (Interact.SkipTalk()) { Wait(0.3); return; }
        if (DateTime.UtcNow > deadline) { Fail($"{o.NpcName} didn't answer."); return; }
        var npc = Interact.Nearest(ObjectKind.EventNpc, id => id == o.NpcId);
        if (npc == null) { Fail($"Couldn't find {o.NpcName} here."); return; }
        Status = "Talking to " + o.NpcName;
        if (++talkTries > 6) { Fail($"Couldn't talk to {o.NpcName}."); return; }
        Interact.With(npc);
        Wait(1);
    }

    /// <summary>Picks the entry by name; when nothing matches, tries the entries one by one.</summary>
    private void Menu()
    {
        if (WindowOpen) { step = Step.Window; deadline = DateTime.UtcNow.AddSeconds(30); return; }
        var menu = Interact.Menu();
        if (menu == null) { step = Step.Talk; Wait(0.3); return; }
        if (menuTry == 0 && Interact.Choose(Current.Offer.MenuLabels)) { menuTry = 1; Wait(0.6); return; }
        var index = Math.Max(0, menuTry - 1);
        if (index >= menu.Count - 1) { Fail($"Couldn't find the exchange at {Current.Offer.NpcName}."); return; }
        menu[index].Select();
        menuTry = index + 2;
        Wait(0.6);
    }

    private void Window()
    {
        var order = Current;
        var o = order.Offer;
        var name = Name(order.ItemId);

        var now = Math.Max(0, GameState.BagCount(order.ItemId) - startCount);
        if (now > got) { got = now; buyTries = 0; }
        if (got >= order.Amount)
        {
            log(order.Amount == 1 ? $"Bought {name} for {o.Cost:N0} {o.CurrencyName}."
                : $"Bought {got:N0} {name} for {(long)got * o.Cost:N0} {plugin.Plans.Catalog?.ItemNamePlural(o.CurrencyItemId) ?? o.CurrencyName}.", false);
            step = Step.Closing;
            return;
        }
        // Confirmation dialogs after picking the row.
        foreach (var d in Dialogs)
            if (Interact.Ready(d) is var dialog && dialog != null) { Callback.Fire(dialog, true, 0); Wait(1); return; }
        if (Interact.Yes()) { Wait(1); return; }

        if (DateTime.UtcNow > deadline) { Fail($"Couldn't buy {name} at {o.NpcName}."); return; }
        var have = GameState.CurrencyCount(o.CurrencyItemId);
        if (have < o.Cost) { Fail($"{name} costs {o.Cost:N0} {o.CurrencyName}; you have {have:N0}."); return; }
        // As many as are still wanted, what the scrips at hand pay for, and the window takes at once.
        var amount = Math.Max(1, Math.Min(Math.Min(order.Amount - got, MaxPerExchange), have / Math.Max(1, o.Cost)));
        Status = order.Amount == 1 ? $"Buying {name} for {o.Cost:N0} {o.CurrencyName}" : $"Buying {name} ({got:N0} of {order.Amount:N0})";

        if (Interact.Ready("InclusionShop") is var shop && shop != null)
        {
            if (pageStep < 2)
            {
                if (pageStep == 0) Callback.Fire(shop, true, 12, (uint)Math.Max(0, o.Page));
                else Callback.Fire(shop, true, 13, (uint)Math.Max(1, subPage));
                pageStep++;
                Wait(0.8);
                return;
            }
            var row = InclusionRow(shop, order.ItemId);
            if (row < 0)
            {
                // Not on the expected subpage: look through the others of that page.
                subPage = subPage >= 12 ? 1 : subPage + 1;
                if (subPage == o.SubPage) { Fail($"Couldn't find {name} in the exchange at {o.NpcName}."); return; }
                Callback.Fire(shop, true, 13, (uint)subPage);
                Wait(0.8);
                return;
            }
            if (++buyTries > 4) { Fail($"The exchange didn't hand over {name}."); return; }
            Callback.Fire(shop, true, 14, (uint)row, (uint)amount);
            Wait(1.2);
            return;
        }

        foreach (var w in new[] { "ShopExchangeCurrency", "ShopExchangeItem" })
        {
            var exchange = Interact.Ready(w);
            if (exchange == null) continue;
            var row = AgentRow(order.ItemId);
            if (row < 0) { CloseWindows(); step = Step.Talk; menuTry = Math.Max(menuTry, 1); deadline = DateTime.UtcNow.AddSeconds(15); Wait(0.8); return; }
            if (++buyTries > 4) { Fail($"The exchange didn't hand over {name}."); return; }
            Callback.Fire(exchange, true, 0, row, amount, 0);
            Wait(1.2);
            return;
        }
        Wait(0.3);
    }

    /// <summary>Row of an item on the inclusion shop's current subpage (AtkValues: count at 298, item ids from 300 every 18).</summary>
    private static int InclusionRow(AtkUnitBase* addon, uint itemId)
    {
        var count = (int)ReadUInt(addon, 298);
        for (var i = 0; i < count && i < 200; i++)
            if (ReadUInt(addon, 300 + i * 18) == itemId) return i;
        return -1;
    }

    /// <summary>Row of an item in a plain exchange window, from the shop agent's list.</summary>
    private static int AgentRow(uint itemId)
    {
        var agent = (AgentShop*)AgentModule.Instance()->GetAgentByInternalId(AgentId.Shop);
        if (agent == null || agent->ItemReceive == null) return -1;
        var rows = agent->ItemReceiveSpan;
        for (var i = 0; i < rows.Length; i++)
            if (rows[i].ItemId == itemId) return i;
        return -1;
    }

    private static uint ReadUInt(AtkUnitBase* addon, int index)
    {
        if (index < 0 || index >= addon->AtkValuesCount) return 0;
        var v = addon->AtkValues[index];
        return v.Type switch
        {
            AtkValueType.UInt => v.UInt,
            AtkValueType.Int => (uint)Math.Max(0, v.Int),
            _ => 0,
        };
    }

    private static void CloseWindows()
    {
        foreach (var d in Dialogs) Interact.Close(d);
        foreach (var w in Windows) Interact.Close(w);
        Interact.Close("SelectIconString");
        Interact.Close("SelectString");
    }

    private void Fail(string message)
    {
        Failure = message;
        Status = message;
        step = Step.Failed;
        travel.Cancel();
        CloseWindows();
    }

    private void Wait(double seconds) => waitUntil = DateTime.UtcNow.AddSeconds(seconds);
}
