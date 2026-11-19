using System;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Provenance.Core.Fishing;

namespace Provenance.Integrations.Fishing;

/// <summary>
/// The three game functions rod fishing watches (rod-engine §3, AutoHook's HooksUpdateModule):
/// <list type="bullet">
/// <item><c>ActionManager.UseAction</c>: a Cast, Mooch or Mooch II going out. The bite timer starts here,
/// the reference point of the bite-time data.</item>
/// <item><c>FishingEventHandler.PlayAnimation</c> (vtable slot 276, +0x8A0): the bite animation tells the
/// tug (ActionTimeline 292 "!", 293 "!!", 294 "!!!").</item>
/// <item><c>AgentCatch.UpdateCatch</c>: a fish landed, with how many came in.</item>
/// </list>
/// Addresses come from FFXIVClientStructs; when its resolver missed one, the signature from the specs is
/// scanned instead (PlayAnimation: the live handler's vtable). Created once and kept for the plugin's
/// lifetime; every detour catches and logs its own errors and always calls the original.
/// Ported from AutoHook (BSD-3, Puni.sh).
/// </summary>
internal sealed unsafe class RodHooks : IDisposable
{
    /// <summary>ActionTimeline ids of the bite animations (fishing/hit_excite, hit_strike, hit_bite).</summary>
    private const ushort WeakTimeline = 292, StrongTimeline = 293, LegendaryTimeline = 294;
    // Fallback signatures (rod-engine §3; ClientStructs 7.57.0.9480). UseAction's is a call site: the scanner follows the E8.
    private const string UseActionSignature = "E8 ?? ?? ?? ?? B0 01 EB B6";
    private const string UpdateCatchSignature = "48 89 6C 24 ?? 56 41 56 41 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B 01";

    private Hook<ActionManager.Delegates.UseAction>? useAction;
    private Hook<FishingEventHandler.Delegates.PlayAnimation>? playAnimation;
    private Hook<AgentCatch.Delegates.UpdateCatch>? updateCatch;
    private bool disposed;

    /// <summary>Why the hooks couldn't be set up; null while they work (or haven't been tried).</summary>
    public string? Failure { get; private set; }

    public bool Working => useAction != null && playAnimation != null && updateCatch != null && !disposed;

    /// <summary>A Cast (289), Mooch (297) or Mooch II (268) is about to go out: the game accepts it (action status 0).</summary>
    public event Action<uint>? CastStarted;

    /// <summary>The fishing handler played an animation: a bite's tug, or <see cref="Tug.Unknown"/> for any other.</summary>
    public event Action<Tug>? Animation;

    /// <summary>A fish landed: base item id, how many, collectable, large, moochable.</summary>
    public event Action<uint, int, bool, bool, bool>? Landed;

    /// <summary>Creates and enables the hooks once. False (with <see cref="Failure"/>) when one can't be found.</summary>
    public bool Ensure()
    {
        if (disposed) return false;
        if (Working) return true;
        try
        {
            if (useAction == null && Resolve("ActionManager.UseAction", ActionManager.Addresses.UseAction.Value, UseActionSignature) is var ua and not 0)
                useAction = Create<ActionManager.Delegates.UseAction>(ua, UseActionDetour);
            if (updateCatch == null && Resolve("AgentCatch.UpdateCatch", AgentCatch.Addresses.UpdateCatch.Value, UpdateCatchSignature) is var uc and not 0)
                updateCatch = Create<AgentCatch.Delegates.UpdateCatch>(uc, UpdateCatchDetour);
            if (playAnimation == null && PlayAnimationAddress() is var pa and not 0)
                playAnimation = Create<FishingEventHandler.Delegates.PlayAnimation>(pa, PlayAnimationDetour);
        }
        catch (Exception e)
        {
            Services.Log.Error(e, "Couldn't set up the fishing hooks");
        }
        if (Working)
        {
            Failure = null;
            return true;
        }
        var missing = useAction == null ? "casting" : updateCatch == null ? "catching" : "the bite";
        Failure = $"Rod fishing can't run: the game function for {missing} wasn't found (the game may have been updated; wait for a plugin update).";
        return false;
    }

    private static nint Resolve(string name, nint known, string signature)
    {
        if (known != 0) return known;
        if (Services.SigScanner.TryScanText(signature, out var found) && found != 0)
        {
            Services.Log.Warning($"{name}: ClientStructs had no address, found it by signature at {found:X}");
            return found;
        }
        Services.Log.Error($"{name}: no address (signature {signature})");
        return 0;
    }

    /// <summary>
    /// PlayAnimation from ClientStructs' FishingEventHandler vtable; when that wasn't resolved, the vtable of
    /// the live fishing handler (EventFramework → EventHandlerModule + 0x70).
    /// </summary>
    private static nint PlayAnimationAddress()
    {
        var table = FishingEventHandler.StaticVirtualTablePointer;
        if (table != null && table->PlayAnimation != null) return (nint)table->PlayAnimation;
        var framework = EventFramework.Instance();
        var handler = framework != null ? framework->EventHandlerModule.FishingEventHandler : null;
        if (handler != null && handler->VirtualTable != null && handler->VirtualTable->PlayAnimation != null)
        {
            Services.Log.Warning("FishingEventHandler.PlayAnimation: ClientStructs had no vtable, took the live handler's");
            return (nint)handler->VirtualTable->PlayAnimation;
        }
        Services.Log.Error($"FishingEventHandler.PlayAnimation: no address (vtable signature {FishingEventHandler.Addresses.StaticVirtualTable.String})");
        return 0;
    }

    private static Hook<T> Create<T>(nint address, T detour) where T : Delegate
    {
        var hook = Services.GameInterop.HookFromAddress(address, detour);
        hook.Enable();
        return hook;
    }

    private bool UseActionDetour(ActionManager* manager, ActionType type, uint id, ulong target, uint extra, ActionManager.UseActionMode mode, uint combo, bool* areaTargeted)
    {
        try
        {
            // Checked before the original runs, as AutoHook does: the status says the game will take it.
            if (type == ActionType.Action && id is FishingIds.Cast or FishingIds.Mooch or FishingIds.MoochII && manager != null
                && manager->GetActionStatus(type, id) == 0)
                CastStarted?.Invoke(id);
        }
        catch (Exception e)
        {
            Services.Log.Warning(e, "Fishing UseAction detour");
        }
        return useAction!.Original(manager, type, id, target, extra, mode, combo, areaTargeted);
    }

    private bool PlayAnimationDetour(FishingEventHandler* handler, Character* character, ushort timeline, ulong callback)
    {
        try
        {
            Animation?.Invoke(timeline switch
            {
                WeakTimeline => Tug.Weak,
                StrongTimeline => Tug.Strong,
                LegendaryTimeline => Tug.Legendary,
                _ => Tug.Unknown,
            });
        }
        catch (Exception e)
        {
            Services.Log.Warning(e, "Fishing PlayAnimation detour");
        }
        return playAnimation!.Original(handler, character, timeline, callback);
    }

    private void UpdateCatchDetour(AgentCatch* agent, uint itemId, bool isLarge, ushort size, byte amount, byte level, byte stars, byte oceanStars,
        bool isMoochable, bool isFirstTimeCatch, byte a11, byte a12)
    {
        updateCatch!.Original(agent, itemId, isLarge, size, amount, level, stars, oceanStars, isMoochable, isFirstTimeCatch, a11, a12);
        try
        {
            var (id, collectable) = BaseId(itemId);
            if (id != 0) Landed?.Invoke(id, amount, collectable, isLarge, isMoochable);
        }
        catch (Exception e)
        {
            Services.Log.Warning(e, "Fishing UpdateCatch detour");
        }
    }

    /// <summary>The base item id: collectables come as id + 500,000, high quality as id + 1,000,000; event items (2,000,000 and up) are none.</summary>
    public static (uint Id, bool Collectable) BaseId(uint itemId) => itemId switch
    {
        >= 2_000_000 => (0, false),
        >= 1_000_000 => (itemId - 1_000_000, false),
        >= 500_000 => (itemId - 500_000, true),
        _ => (itemId, false),
    };

    public void Dispose()
    {
        disposed = true;
        useAction?.Dispose();
        playAnimation?.Dispose();
        updateCatch?.Dispose();
        useAction = null;
        playAnimation = null;
        updateCatch = null;
    }
}
