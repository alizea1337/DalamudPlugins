using System;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using Keystroke.Game;

namespace Keystroke.Automation;

/// <summary>
/// Opens carried Keybound Coffers one at a time: use the item, wait for the inventory count to
/// drop, repeat. Stops on combat, duty, loading, or a full inventory. Never starts on its own.
/// </summary>
internal sealed unsafe class CofferOpener
{
    private static readonly TimeSpan UseTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan BetweenUses = TimeSpan.FromMilliseconds(900);

    private bool running;
    private int expected;
    private DateTime usedAt;
    private DateTime nextAt;

    public bool Running => running;
    public string Status { get; private set; } = "";
    public int Opened { get; private set; }

    public int Count()
    {
        var inv = InventoryManager.Instance();
        return inv == null ? 0 : inv->GetInventoryItemCount(Keybound.CofferItemId);
    }

    public void Start()
    {
        Opened = 0;
        expected = -1;
        running = true;
        nextAt = DateTime.MinValue;
        Status = "Opening coffers";
        Services.Log.Information("Coffer opener started ({Count} carried)", Count());
    }

    public void Stop(string? reason = null)
    {
        if (!running) return;
        running = false;
        Status = reason ?? $"Opened {Opened}";
        Services.Log.Information("Coffer opener stopped: {Status}", Status);
    }

    public void Tick()
    {
        if (!running) return;
        var now = DateTime.UtcNow;
        if (now < nextAt) return;

        try
        {
            var count = Count();
            if (count <= 0) { Stop(Opened > 0 ? $"Opened {Opened}; none left" : "No Keybound Coffers to open"); return; }
            if (Services.Condition[ConditionFlag.InCombat]) { Stop("Stopped: in combat"); return; }
            if (Services.Condition[ConditionFlag.BoundByDuty] || Services.Condition[ConditionFlag.BetweenAreas]) { Stop("Stopped: in a duty or loading"); return; }

            // Waiting for the previous use to settle.
            if (expected >= 0)
            {
                if (count <= expected)
                {
                    Opened++;
                    expected = -1;
                    nextAt = now + BetweenUses;
                    Status = $"Opened {Opened}, {count} left";
                    return;
                }
                if (now - usedAt > UseTimeout) { Stop("Stopped: the coffer did not open (inventory full?)"); return; }
                return;
            }

            if (Services.Condition[ConditionFlag.Casting] || Services.Condition[ConditionFlag.OccupiedInQuestEvent]) return;

            var inv = InventoryManager.Instance();
            if (inv != null && inv->GetEmptySlotsInBag() == 0) { Stop("Stopped: inventory is full"); return; }

            var am = ActionManager.Instance();
            if (am == null) { Stop("Stopped: action manager unavailable"); return; }
            var ok = am->UseAction(ActionType.Item, Keybound.CofferItemId, 0xE0000000, 65535);
            if (!ok)
            {
                if (now - usedAt > UseTimeout) { Stop("Stopped: the game refused to use the coffer"); }
                nextAt = now + TimeSpan.FromMilliseconds(500);
                return;
            }
            expected = count - 1;
            usedAt = now;
            Status = $"Opening ({count} left)";
        }
        catch (Exception e)
        {
            Services.Log.Error(e, "Coffer opener failed");
            Stop("Stopped: " + e.Message);
        }
    }
}
