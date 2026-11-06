using System;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Keystroke.Game;

/// <summary>
/// Presses buttons in game windows by delivering the button's own ButtonClick event to the
/// addon, exactly as the UI does when the mouse releases over it. Framework thread only.
/// </summary>
internal static unsafe class AtkClicker
{
    public static bool IsVisible(string addonName)
    {
        var ptr = Services.GameGui.GetAddonByName(addonName);
        return !ptr.IsNull && ptr.IsVisible;
    }

    public static bool IsReady(string addonName)
    {
        var ptr = Services.GameGui.GetAddonByName(addonName);
        return !ptr.IsNull && ptr.IsVisible && ptr.IsReady;
    }

    /// <summary>Clicks the button component with the given node id. Returns false if anything is missing.</summary>
    public static bool ClickButton(string addonName, uint componentNodeId)
    {
        try
        {
            var ptr = Services.GameGui.GetAddonByName(addonName);
            if (ptr.IsNull || !ptr.IsVisible) return false;
            var addon = (AtkUnitBase*)ptr.Address;
            var node = addon->GetComponentNodeById(componentNodeId);
            if (node == null || node->Component == null) return false;
            var component = node->Component;
            if (component->GetComponentType() != ComponentType.Button) return false;
            var button = (AtkComponentButton*)component;
            if (!button->IsEnabled) return false;

            var owner = component->OwnerNode;
            if (owner == null) return false;

            // Walk the owner node's registered events for the click handler and raise it.
            for (var evt = owner->AtkEventManager.Event; evt != null; evt = evt->NextEvent)
            {
                if (evt->State.EventType != AtkEventType.ButtonClick) continue;
                var listener = evt->Listener != null ? evt->Listener : (AtkEventListener*)addon;
                listener->ReceiveEvent(AtkEventType.ButtonClick, (int)evt->Param, evt, null);
                Services.Log.Debug("Clicked {Addon} button {Node}", addonName, componentNodeId);
                return true;
            }
            Services.Log.Warning("{Addon} button {Node} has no ButtonClick event", addonName, componentNodeId);
            return false;
        }
        catch (Exception e)
        {
            Services.Log.Error(e, "ClickButton {Addon}/{Node} failed", addonName, componentNodeId);
            return false;
        }
    }
}
