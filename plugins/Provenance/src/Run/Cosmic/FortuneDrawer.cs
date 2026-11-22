using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Provenance.Core.Cosmic;
using Provenance.Data;
using Provenance.Integrations;

namespace Provenance.Run.Cosmic;

/// <summary>
/// Spends planet credits at the Cosmic Fortunes counter: walk to Orbitingway, "Draw a cosmic fortune" (a Big Bang
/// fortune first when a ticket is there), pay, pick the wheel with the better prizes, spin, and again while 1,000
/// credits are left. The menu entries are found by the game's own text. Ticked on the framework thread.
/// </summary>
internal sealed unsafe class FortuneDrawer
{
    private enum Step { Idle, Travel, Talk, Paying, Wheel, Spinning, Done, Failed }

    /// <summary>What a cosmic fortune costs in planet credits.</summary>
    public const int Price = 1000;

    /// <summary>Big Bang tickets per planet (a fortune's prize; each is a free draw).</summary>
    private static uint Ticket(Planet p) => 48092u + (uint)p;

    private readonly Action<string, bool> log;
    private readonly Travel travel = new();
    private Step step = Step.Idle;
    private Planet planet;
    private DateTime waitUntil;
    private DateTime deadline;
    private int talkTries;
    /// <summary>Planet credits and Big Bang tickets before a draw was paid for: a draw has happened once they're fewer.</summary>
    private int creditsBefore;
    private int ticketsBefore;
    private DateTime giveUpAt;

    public FortuneDrawer(Action<string, bool> log) => this.log = log;

    public string Status { get; private set; } = "";
    public string? Failure { get; private set; }
    public bool Finished => step is Step.Done or Step.Failed;
    public int Draws { get; private set; }

    /// <summary>A draw can be paid for on the planet: enough of its credits, or a Big Bang ticket.</summary>
    public static bool CanDraw(Planet p) => GameState.CurrencyCount(p.CreditItem()) >= Price || GameState.BagCount(Ticket(p)) > 0;

    public void Begin(Planet p)
    {
        planet = p;
        Failure = null;
        Draws = 0;
        talkTries = 0;
        spun = false;
        giveUpAt = DateTime.UtcNow.AddMinutes(8);
        var npc = CosmicData.Fortunes(p);
        travel.GoTo("Orbitingway", npc.Territory, new System.Numerics.Vector2(npc.Position.X, npc.Position.Z), 3.5f,
            () => Interact.Nearest(ObjectKind.EventNpc, id => CosmicData.FortuneIds.Contains(id))?.Position);
        step = Step.Travel;
        Status = "Going to the Cosmic Fortunes counter";
    }

    public void Cancel()
    {
        travel.Cancel();
        spun = false;
        foreach (var w in new[] { "WKSLottery", "SelectYesno", "SelectString", "SelectIconString" })
            if (Interact.IsOpen(w)) Interact.Close(w);
        if (!Finished) step = Step.Idle;
    }

    public void Tick()
    {
        if (Finished || step == Step.Idle || DateTime.UtcNow < waitUntil) return;
        if (DateTime.UtcNow > giveUpAt) { Fail("Drawing took far too long."); return; }
        switch (step)
        {
            case Step.Travel:
                travel.Tick();
                Status = travel.Status;
                if (travel.Current == Travel.State.Failed) { Fail(travel.Failure ?? "Couldn't get to Orbitingway."); return; }
                if (travel.Current == Travel.State.Arrived) { step = Step.Talk; deadline = DateTime.UtcNow.AddSeconds(20); }
                return;
            case Step.Talk: Talk(); return;
            case Step.Paying: Paying(); return;
            case Step.Wheel: Wheel(); return;
            case Step.Spinning: Spinning(); return;
        }
    }

    private void Talk()
    {
        if (Interact.Ready("WKSLottery") != null) { step = Step.Wheel; deadline = DateTime.UtcNow.AddSeconds(15); return; }
        // "Pay 1,000 … for a cosmic fortune?" and "Trade … for a Big Bang fortune?": yes.
        if (Interact.Yes()) { Wait(0.8); return; }
        if (Interact.Menu() is { } menu && menu.Any(e => e.Text.Trim().TrimEnd('.').Equals(Text(26), StringComparison.CurrentCultureIgnoreCase)))
        {
            DebugLog.Write("fortune", "the pay question is up straight away: paying");
            // Straight to "Pay … for a cosmic fortune?" (the game asks again after a spin): that's the payment.
            creditsBefore = GameState.CurrencyCount(planet.CreditItem());
            ticketsBefore = GameState.BagCount(Ticket(planet));
            step = Step.Paying;
            deadline = DateTime.UtcNow.AddSeconds(15);
            return;
        }
        if (Interact.Menu() != null)
        {
            var ticket = GameState.BagCount(Ticket(planet)) > 0;
            var credits = GameState.CurrencyCount(planet.CreditItem()) >= Price;
            creditsBefore = GameState.CurrencyCount(planet.CreditItem());
            ticketsBefore = GameState.BagCount(Ticket(planet));
            DebugLog.Write("fortune", $"menu: [{string.Join(" | ", Interact.Menu()?.Select(e => e.Text) ?? Array.Empty<string>())}], ticket {ticket}, credits {creditsBefore}");
            if (ticket && Interact.Choose(new[] { Text(6) }) || credits && Interact.Choose(new[] { Text(7) }))
            {
                DebugLog.Write("fortune", "picked a draw; paying next");
                step = Step.Paying;
                deadline = DateTime.UtcNow.AddSeconds(15);
                Wait(1);
                return;
            }
            Interact.Choose(new[] { Text(9), "Nothing" });
            if (ticket || credits) { Fail("Orbitingway's menu had no fortune to draw."); return; }
            Done();
            return;
        }
        if (Interact.SkipTalk()) { Wait(0.3); return; }
        if (!CanDraw(planet)) { Done(); return; }
        if (DateTime.UtcNow > deadline || ++talkTries > 6) { Fail("Couldn't talk to Orbitingway."); return; }
        var npc = Interact.Nearest(ObjectKind.EventNpc, id => CosmicData.FortuneIds.Contains(id));
        if (npc == null) { Fail("Couldn't find Orbitingway here."); return; }
        Interact.With(npc);
        Status = "Talking to Orbitingway";
        Wait(1.2);
    }

    /// <summary>After a paid choice: yes to the price, then the wheels (whatever the credits now say).</summary>
    private void Paying()
    {
        if (Interact.Ready("WKSLottery") != null) { step = Step.Wheel; deadline = DateTime.UtcNow.AddSeconds(15); return; }
        // "Pay 1,000 … credits for a cosmic fortune?" comes as a menu with Yes and No (a Yes/No window on some clients).
        if (Interact.Menu() != null && Interact.Choose(new[] { Text(26), "Yes" })) { DebugLog.Write("fortune", "answered yes to the price"); Wait(0.8); return; }
        if (Interact.Yes()) { Wait(0.8); return; }
        if (Interact.SkipTalk()) { Wait(0.3); return; }
        if (DateTime.UtcNow > deadline) { Fail("The fortune wheels didn't show after paying."); return; }
        Wait(0.3);
    }

    /// <summary>The wheel window's prize slots (item id every 7th value from here; the left wheel's first at 89, the right's at 138).</summary>
    private const int LeftPrizes = 89, RightPrizes = 138;
    /// <summary>A wheel button's "checked" bit: the wheel picked for the spin.</summary>
    private const uint CheckedBit = 1u << 18;

    /// <summary>
    /// Picks the wheel whose prizes are worth more (unlockables and rarer items first), then spins. Nothing here sends
    /// a click to a wheel: the pick is the wheel button's own checked state (as the game keeps it), set only on
    /// buttons that are there and enabled, and the prizes are read only from values that are there and hold numbers.
    /// A click sent to a wheel with the wrong event data took the game down before (0.19.8).
    /// </summary>
    private void Wheel()
    {
        var addon = Interact.Ready("WKSLottery");
        if (addon == null)
        {
            if (DateTime.UtcNow > deadline) Fail("The fortune wheels didn't show.");
            else Wait(0.3);
            return;
        }
        var lottery = new AddonMaster.WKSLottery((nint)addon);
        var leftButton = lottery.WheelLeftButton;
        var rightButton = lottery.WheelRightButton;
        var leftOn = Usable(leftButton);
        var rightOn = Usable(rightButton);
        if (!leftOn && !rightOn)
        {
            // Nothing to pick (a single wheel, or the window still setting up): spin once Spin comes on.
            if (Usable(lottery.SpinWheelButton)) { DebugLog.Write("fortune", "no wheel to pick; spinning what's there"); ToSpin(); return; }
            if (DateTime.UtcNow > deadline) Fail("The fortune wheels didn't come on.");
            else Wait(0.3);
            return;
        }
        var leftItems = Prizes(addon, LeftPrizes);
        var rightItems = Prizes(addon, RightPrizes);
        if (leftItems.Count == 0 && rightItems.Count == 0)
        {
            if (DateTime.UtcNow > deadline) Fail("The fortune wheels showed no prizes.");
            else Wait(0.3);
            return;
        }
        var left = leftItems.Sum(Worth);
        var right = rightItems.Sum(Worth);
        DebugLog.Write("fortune", $"wheels ({addon->AtkValuesCount} values, buttons {(leftOn ? "on" : "off")}/{(rightOn ? "on" : "off")}): "
                                  + $"left {left} [{string.Join(", ", leftItems.Select(i => $"{i} {ItemName(i)}"))}], right {right} [{string.Join(", ", rightItems.Select(i => $"{i} {ItemName(i)}"))}]");
        // A wheel with no prizes (a draw can come with just one) isn't one to pick.
        var pickRight = rightOn && rightItems.Count > 0 && (leftItems.Count == 0 || !leftOn || right > left);
        var pick = pickRight ? rightButton : leftButton;
        var other = pickRight ? leftButton : rightButton;
        if (Usable(pick))
        {
            pick->Flags |= CheckedBit;
            if (Usable(other)) other->Flags &= ~CheckedBit;
            DebugLog.Write("fortune", $"picked the {(pickRight ? "right" : "left")} wheel");
        }
        ToSpin();
    }

    private void ToSpin()
    {
        Status = "Spinning a cosmic fortune";
        step = Step.Spinning;
        spun = false;
        deadline = DateTime.UtcNow.AddSeconds(10);
        Wait(0.5);
    }

    /// <summary>A button that's there, shown and enabled (each pointer checked before it's followed).</summary>
    private static bool Usable(AtkComponentButton* b)
        => b != null && b->AtkComponentBase.OwnerNode != null && b->AtkResNode != null && b->IsEnabled && b->AtkResNode->IsVisible();

    /// <summary>The Spin button can take a click: usable, with the event the click goes through.</summary>
    private static bool Clickable(AtkComponentButton* b)
        => Usable(b) && b->AtkComponentBase.OwnerNode->AtkResNode.AtkEventManager.Event != null;

    /// <summary>A wheel's prize item ids, read only from values that are there and hold numbers.</summary>
    private static List<uint> Prizes(AtkUnitBase* addon, int first)
    {
        var list = new List<uint>();
        for (var i = 0; i < 7; i++)
        {
            var at = first + i * 7;
            if (at >= addon->AtkValuesCount) break;
            var v = addon->AtkValues[at];
            var id = v.Type switch { AtkValueType.UInt => v.UInt, AtkValueType.Int => (uint)Math.Max(0, v.Int), _ => 0u };
            if (id != 0) list.Add(id);
        }
        return list;
    }

    private static string ItemName(uint id)
        => Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(id)?.Name.ExtractText() ?? "?";

    private bool spun;

    private void Spinning()
    {
        var addon = Interact.Ready("WKSLottery");
        if (addon != null && !spun)
        {
            var lottery = new AddonMaster.WKSLottery((nint)addon);
            if (!Clickable(lottery.SpinWheelButton))
            {
                if (DateTime.UtcNow > deadline) Fail("The fortune wheel's Spin button didn't come on.");
                else Wait(0.3);
                return;
            }
            DebugLog.Write("fortune", "spinning");
            lottery.ConfirmButton();
            spun = true;
            deadline = DateTime.UtcNow.AddSeconds(30);
            Wait(1.5);
            return;
        }
        if (Interact.Ready("SelectYesno") != null)
        {
            // After a spin the game can offer another draw on the spot: yes while one can be paid for, else no.
            if (spun && !CanDraw(planet)) { DebugLog.Write("fortune", "asked for another draw: no (nothing left to pay with)"); Interact.No(); Wait(1); return; }
            DebugLog.Write("fortune", $"yes/no: yes (spun {spun}, credits {GameState.CurrencyCount(planet.CreditItem())})");
            Interact.Yes();
            if (spun)
            {
                // Another round on the same window: count the one just drawn and pick again.
                Draws++;
                creditsBefore = GameState.CurrencyCount(planet.CreditItem());
                ticketsBefore = GameState.BagCount(Ticket(planet));
                spun = false;
                step = Step.Wheel;
                deadline = DateTime.UtcNow.AddSeconds(15);
            }
            Wait(1);
            return;
        }
        if (addon != null && DateTime.UtcNow < deadline) { Wait(0.5); return; }
        // The wheel is done (the prize is in the bags): the first draw must have cost something, else nothing was drawn.
        spun = false;
        if (Draws == 0 && GameState.CurrencyCount(planet.CreditItem()) >= creditsBefore && GameState.BagCount(Ticket(planet)) >= ticketsBefore)
        {
            Fail("The fortune didn't go through.");
            return;
        }
        Draws++;
        DebugLog.Write("fortune", $"draw {Draws} done; credits {GameState.CurrencyCount(planet.CreditItem())} (were {creditsBefore}), tickets {GameState.BagCount(Ticket(planet))}");
        if (CanDraw(planet))
        {
            step = Step.Talk;
            talkTries = 0;
            deadline = DateTime.UtcNow.AddSeconds(20);
            Wait(1.5);
            return;
        }
        Done();
    }

    /// <summary>How much a prize counts: unlockables (mounts, minions, emotes, orchestrion rolls, outfits) most, then rarity.</summary>
    private static int Worth(uint itemId)
    {
        if (Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(itemId) is not { } item) return 0;
        var unlock = item.ItemAction.ValueNullable?.Action.RowId is 1322 or 853 or 2633 or 25183 or 20086 or 1013 ? 50 : 0;
        return unlock + item.Rarity * 10;
    }

    private void Done()
    {
        if (Draws > 0) log($"Drew {Draws} cosmic {(Draws == 1 ? "fortune" : "fortunes")} with {planet.Name()} credits.", false);
        Status = "Fortunes drawn";
        step = Step.Done;
    }

    private void Fail(string why)
    {
        DebugLog.Write("fortune", "[!] " + why);
        travel.Cancel();
        Failure = why;
        step = Step.Failed;
    }

    private void Wait(double seconds) => waitUntil = DateTime.UtcNow.AddSeconds(seconds);

    /// <summary>Orbitingway's menu text (row 6 Big Bang fortune, 7 cosmic fortune, 9 nothing, 26 yes), up to its first placeholder.</summary>
    private static string Text(uint row)
    {
        try
        {
            var s = Services.Data.Excel.GetSheet<RawRow>(Dalamud.Utility.ClientLanguageExtensions.ToLumina(Services.Data.Language), "custom/009/CtsWksLot_00918");
            var text = s.GetRowOrDefault(row)?.ReadStringColumn(1).ExtractText() ?? "";
            var cut = text.IndexOf('(');
            return (cut > 0 ? text[..cut] : text).Trim().TrimEnd('.');
        }
        catch (Exception e)
        {
            Services.Log.Debug("Couldn't read Orbitingway's menu: {Message}", e.Message);
            return row switch { 6 => "Big Bang fortune", 7 => "cosmic fortune", 26 => "Yes", _ => "Nothing" };
        }
    }
}
