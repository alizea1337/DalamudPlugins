using System;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.Automation.UIInput;
using ECommons.UIHelpers.AddonMasterImplementations;
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
        if (Interact.Yes()) { Wait(1); return; }
        if (Interact.Menu() != null)
        {
            var ticket = GameState.BagCount(Ticket(planet)) > 0;
            var credits = GameState.CurrencyCount(planet.CreditItem()) >= Price;
            creditsBefore = GameState.CurrencyCount(planet.CreditItem());
            ticketsBefore = GameState.BagCount(Ticket(planet));
            if (ticket && Interact.Choose(new[] { Text(6) }) || credits && Interact.Choose(new[] { Text(7) }))
            {
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
        if (Interact.Yes()) { Wait(1); return; }
        if (Interact.SkipTalk()) { Wait(0.3); return; }
        if (DateTime.UtcNow > deadline) { Fail("The fortune wheels didn't show after paying."); return; }
        Wait(0.3);
    }

    /// <summary>Picks the wheel whose prizes are worth more (unlockables and rarer items first), then spins.</summary>
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
        var left = lottery.LeftWheelItems.Sum(i => Worth(i.itemId));
        var right = lottery.RightWheelItems.Sum(i => Worth(i.itemId));
        if (right > left && lottery.WheelRightButton != null) (*lottery.WheelRightButton).ClickAddonButton(addon);
        else lottery.SelectWheelLeft();
        Status = "Spinning a cosmic fortune";
        step = Step.Spinning;
        deadline = DateTime.UtcNow.AddSeconds(30);
        Wait(0.8);
    }

    private bool spun;

    private void Spinning()
    {
        var addon = Interact.Ready("WKSLottery");
        if (addon != null && !spun)
        {
            new AddonMaster.WKSLottery((nint)addon).ConfirmButton();
            spun = true;
            Wait(1.5);
            return;
        }
        if (Interact.Yes()) { Wait(1); return; }
        if (addon != null && DateTime.UtcNow < deadline) { Wait(0.5); return; }
        // The wheel is done (the prize is in the bags): it must have cost something, else nothing was drawn.
        spun = false;
        if (GameState.CurrencyCount(planet.CreditItem()) >= creditsBefore && GameState.BagCount(Ticket(planet)) >= ticketsBefore)
        {
            Fail("The fortune didn't go through.");
            return;
        }
        Draws++;
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
        travel.Cancel();
        Failure = why;
        step = Step.Failed;
    }

    private void Wait(double seconds) => waitUntil = DateTime.UtcNow.AddSeconds(seconds);

    /// <summary>Orbitingway's menu text (row 6 Big Bang fortune, 7 cosmic fortune, 9 nothing), up to its first placeholder.</summary>
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
            return row switch { 6 => "Big Bang fortune", 7 => "cosmic fortune", _ => "Nothing" };
        }
    }
}
