using System;
using ECommons;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Provenance.Run;

/// <summary>
/// Security checks that run every frame, whether or not an order is running: cancels trade windows
/// someone else opened (a common scam is to trade a moment before a run moves items around).
/// </summary>
internal sealed unsafe class Guard
{
    private readonly Plugin plugin;
    private DateTime lastTrade;

    public Guard(Plugin plugin) => this.plugin = plugin;

    public void Tick()
    {
        var s = plugin.Config.Security;
        if (!s.DeclineTrades || (s.TradesOnlyDuringRuns && !plugin.Runs.Active)) return;
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("Trade", out var trade) || !trade->IsVisible) return;
        if (DateTime.UtcNow - lastTrade < TimeSpan.FromSeconds(1)) return;
        lastTrade = DateTime.UtcNow;
        Callback.Fire(trade, true, -1);
        trade->Close(true);
        plugin.Runs.Note("Declined a trade request.");
        Services.Chat.Print("[Provenance] Declined a trade request (Security tab).");
    }
}
