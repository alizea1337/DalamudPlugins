using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin;
using Provenance.Core.Run;

namespace Provenance.Integrations;

/// <summary>
/// Puts Provenance's gathering list into GatherBuddy Reborn. GatherBuddy Reborn has no IPC for lists,
/// so this reaches into its list manager by reflection, the same way its own Vulcan feature builds a
/// temporary list: other enabled lists are switched off for the run and switched back on afterwards.
/// Everything is checked up front; if GatherBuddy Reborn changed shape, <see cref="Error"/> says so.
/// </summary>
internal sealed class GatherBuddyLists
{
    public const string ListName = "Provenance";
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private object? manager;
    private Type? listType;
    private IDictionary? gatherables;
    private IDictionary? fishes;
    private object? ourList;
    private readonly List<object> disabledLists = new();

    public string? Error { get; private set; }
    public bool Installed => ourList != null;

    /// <summary>Creates (or replaces) the Provenance list and makes it the only active list.</summary>
    /// <returns>Items GatherBuddy Reborn doesn't know as gatherable or fish.</returns>
    public List<uint> Install(IReadOnlyList<GatherTarget> targets)
    {
        var unknown = new List<uint>();
        if (!Resolve()) return unknown;
        try
        {
            RemoveOurLists();
            disabledLists.Clear();
            foreach (var list in Lists())
            {
                if (Get<bool>(list, "Enabled") && !Get<bool>(list, "Fallback"))
                {
                    Set(list, "Enabled", false);
                    disabledLists.Add(list);
                }
            }

            var list2 = Activator.CreateInstance(listType!)!;
            Set(list2, "Name", ListName);
            Set(list2, "Description", "Made by Provenance for the current order. Removed when the run ends.");
            Set(list2, "UsesRetainerInventory", false);
            var add = listType!.GetMethod("Add", All) ?? throw new MissingMethodException("AutoGatherList.Add");
            foreach (var t in targets)
            {
                var item = Lookup(t.ItemId);
                if (item == null) { unknown.Add(t.ItemId); continue; }
                add.Invoke(list2, new[] { item, (object)t.Target });
            }
            Set(list2, "Enabled", true);

            Call(manager!, "AddList", list2, null);
            Call(manager!, "SetActiveItems", false);
            Call(manager!, "Save");
            ourList = list2;
            Services.Log.Information("GatherBuddy Reborn list installed with {Count} items, {Disabled} other lists paused", targets.Count - unknown.Count, disabledLists.Count);
        }
        catch (Exception e)
        {
            Fail("Couldn't create the gathering list in GatherBuddy Reborn: " + (e.InnerException ?? e).Message, e);
        }
        return unknown;
    }

    /// <summary>Removes the Provenance list and switches the user's lists back on.</summary>
    public void Uninstall()
    {
        if (manager == null) return;
        try
        {
            RemoveOurLists();
            foreach (var list in disabledLists) Set(list, "Enabled", true);
            disabledLists.Clear();
            Call(manager, "SetActiveItems", false);
            Call(manager, "Save");
        }
        catch (Exception e)
        {
            Services.Log.Warning(e, "Couldn't restore GatherBuddy Reborn lists");
        }
        ourList = null;
    }

    private bool Resolve()
    {
        Error = null;
        if (manager != null && listType != null && gatherables != null) return true;
        try
        {
            var plugin = PluginLookup.Instance(GatherBuddyIpc.InternalName)
                         ?? throw new InvalidOperationException("GatherBuddy Reborn isn't loaded.");
            manager = Get<object>(plugin, "AutoGatherListsManager") ?? throw new MissingMemberException("GatherBuddy.AutoGatherListsManager");
            var asm = plugin.GetType().Assembly;
            listType = asm.GetType("GatherBuddy.AutoGather.Lists.AutoGatherList") ?? throw new TypeLoadException("AutoGatherList");
            var gameData = asm.GetType("GatherBuddy.GatherBuddy")?.GetProperty("GameData", All)?.GetValue(null)
                           ?? throw new MissingMemberException("GatherBuddy.GameData");
            gatherables = Get<IDictionary>(gameData, "Gatherables") ?? throw new MissingMemberException("GameData.Gatherables");
            fishes = Get<IDictionary>(gameData, "Fishes");
            return true;
        }
        catch (Exception e)
        {
            manager = null;
            Fail(e is InvalidOperationException ? e.Message
                : "This version of GatherBuddy Reborn isn't supported yet (" + e.Message + "). Provenance needs an update.", e);
            return false;
        }
    }

    private object? Lookup(uint itemId)
    {
        if (gatherables!.Contains(itemId)) return gatherables[itemId];
        if (fishes != null && fishes.Contains(itemId)) return fishes[itemId];
        return null;
    }

    private IEnumerable<object> Lists() => (Get<IEnumerable>(manager!, "Lists") ?? Array.Empty<object>()).Cast<object>().ToList();

    private void RemoveOurLists()
    {
        foreach (var list in Lists().Where(l => Get<string>(l, "Name") == ListName).ToList())
            Call(manager!, "DeleteList", list);
    }

    private void Fail(string message, Exception e)
    {
        Error = message;
        Services.Log.Error(e, "GatherBuddy Reborn bridge: {Message}", message);
    }

    private static T? Get<T>(object obj, string name)
    {
        for (var t = obj.GetType(); t != null; t = t.BaseType)
        {
            if (t.GetProperty(name, All) is { } p) return (T?)p.GetValue(obj);
            if (t.GetField(name, All) is { } f) return (T?)f.GetValue(obj);
        }
        return default;
    }

    private static void Set(object obj, string name, object? value)
    {
        var p = obj.GetType().GetProperty(name, All) ?? throw new MissingMemberException(obj.GetType().Name + "." + name);
        p.SetValue(obj, value);
    }

    private static object? Call(object obj, string name, params object?[] args)
    {
        var m = obj.GetType().GetMethods(All).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == args.Length)
                ?? throw new MissingMethodException(obj.GetType().Name + "." + name);
        return m.Invoke(obj, args);
    }
}

/// <summary>Finds another loaded plugin's instance through Dalamud's plugin manager.</summary>
internal static class PluginLookup
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    public static IDalamudPlugin? Instance(string internalName)
    {
        var dalamud = typeof(IDalamudPlugin).Assembly;
        var managerType = dalamud.GetType("Dalamud.Plugin.Internal.PluginManager");
        var serviceType = dalamud.GetType("Dalamud.Service`1")?.MakeGenericType(managerType!);
        var manager = serviceType?.GetMethod("Get", All)?.Invoke(null, null);
        if (manager?.GetType().GetProperty("InstalledPlugins", All)?.GetValue(manager) is not IEnumerable plugins) return null;
        foreach (var entry in plugins)
        {
            var t = entry.GetType();
            if (!string.Equals(t.GetProperty("InternalName", All)?.GetValue(entry) as string, internalName, StringComparison.OrdinalIgnoreCase)) continue;
            if (t.GetProperty("IsLoaded", All)?.GetValue(entry) is not true) continue;
            for (var tt = t; tt != null; tt = tt.BaseType)
                foreach (var name in new[] { "instance", "Instance", "pluginInstance", "Plugin" })
                {
                    if (tt.GetField(name, All)?.GetValue(entry) is IDalamudPlugin p) return p;
                    if (tt.GetProperty(name, All)?.GetValue(entry) is IDalamudPlugin q) return q;
                }
        }
        return null;
    }
}
