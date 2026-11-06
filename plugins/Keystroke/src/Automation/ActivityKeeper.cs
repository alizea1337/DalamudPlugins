using System;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Keystroke.Automation;
using Keystroke.Game;

namespace Keystroke.Automation;

/// <summary>
/// Keeps the duty's inactivity kick away while automation plays by zeroing the client's own
/// activity timers (InputTimerModule). No keys or movement are sent; nothing is written into
/// agent memory.
/// </summary>
internal sealed unsafe class ActivityKeeper
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly Func<Configuration> config;
    private readonly IAutomationController controller;
    private DateTime lastReset = DateTime.MinValue;

    public ActivityKeeper(Func<Configuration> config, IAutomationController controller)
    {
        this.config = config;
        this.controller = controller;
    }

    public void Tick()
    {
        if (!config().Advanced.KeepActivityTimersAlive) return;
        if (controller.State is not (AutomationState.Playing or AutomationState.Finishing or AutomationState.Queued)) return;
        var now = DateTime.UtcNow;
        if (now - lastReset < Interval) return;
        lastReset = now;

        try
        {
            var module = InputTimerModule.Instance();
            if (module == null) return;
            // Only touch the timers while they belong to a Keybound area, as a guard against
            // resetting activity somewhere unexpected.
            if (!Keybound.IsKeyboundArea(module->TerritoryTypeId) && !Keybound.IsKeyboundArea((ushort)Services.ClientState.TerritoryType)) return;
            module->AfkTimer = 0;
            module->ContentInputTimer = 0;
            module->InputTimer = 0;
        }
        catch (Exception e)
        {
            Services.Log.Verbose(e, "Activity timer reset failed");
        }
    }
}
