using System;
using System.Linq;
using System.Numerics;

namespace Provenance.Integrations;

/// <summary>Small helpers so a missing or unloaded plugin never throws into the run.</summary>
internal static class Ipc
{
    public static bool IsLoaded(string internalName)
        => Services.PluginInterface.InstalledPlugins.Any(p => p.InternalName == internalName && p.IsLoaded);

    public static T? Func<T>(string name, T? fallback = default)
    {
        try { return Services.PluginInterface.GetIpcSubscriber<T>(name).InvokeFunc(); }
        catch (Exception e)
        {
            Services.Log.Verbose("IPC {Name} failed: {Message}", name, e.Message);
            return fallback;
        }
    }

    public static TR? Func<T1, TR>(string name, T1 arg, TR? fallback = default)
    {
        try { return Services.PluginInterface.GetIpcSubscriber<T1, TR>(name).InvokeFunc(arg); }
        catch (Exception e)
        {
            Services.Log.Verbose("IPC {Name} failed: {Message}", name, e.Message);
            return fallback;
        }
    }

    public static TR? Func<T1, T2, TR>(string name, T1 a, T2 b, TR? fallback = default)
    {
        try { return Services.PluginInterface.GetIpcSubscriber<T1, T2, TR>(name).InvokeFunc(a, b); }
        catch (Exception e)
        {
            Services.Log.Verbose("IPC {Name} failed: {Message}", name, e.Message);
            return fallback;
        }
    }

    public static bool Action(string name)
    {
        try { Services.PluginInterface.GetIpcSubscriber<object>(name).InvokeAction(); return true; }
        catch (Exception e)
        {
            Services.Log.Warning("IPC {Name} failed: {Message}", name, e.Message);
            return false;
        }
    }

    public static bool Action<T1>(string name, T1 arg)
    {
        try { Services.PluginInterface.GetIpcSubscriber<T1, object>(name).InvokeAction(arg); return true; }
        catch (Exception e)
        {
            Services.Log.Warning("IPC {Name} failed: {Message}", name, e.Message);
            return false;
        }
    }
}

/// <summary>GatherBuddy Reborn's public IPC: switch auto-gather on and off, and read its state.</summary>
internal static class GatherBuddyIpc
{
    public const string InternalName = "GatherBuddyReborn";

    public static bool Available => Ipc.IsLoaded(InternalName);
    public static bool Enabled => Ipc.Func("GatherBuddyReborn.IsAutoGatherEnabled", false);
    public static bool Waiting => Ipc.Func("GatherBuddyReborn.IsAutoGatherWaiting", false);
    public static string Status => Ipc.Func("GatherBuddyReborn.GetAutoGatherStatusText", "") ?? "";

    public static bool SetEnabled(bool on) => Ipc.Action("GatherBuddyReborn.SetAutoGatherEnabled", on);
}

/// <summary>vnavmesh: pathfinding and movement.</summary>
internal static class Vnav
{
    public const string InternalName = "vnavmesh";

    public static bool Available => Ipc.IsLoaded(InternalName);
    public static bool Ready => Ipc.Func("vnavmesh.Nav.IsReady", false);
    public static float BuildProgress => Ipc.Func("vnavmesh.Nav.BuildProgress", -1f);

    /// <summary>True while a path is being computed or walked.</summary>
    public static bool Busy => Ipc.Func("vnavmesh.SimpleMove.PathfindInProgress", false) || Ipc.Func("vnavmesh.Path.IsRunning", false);

    public static bool MoveTo(Vector3 destination, bool fly)
        => Ipc.Func("vnavmesh.SimpleMove.PathfindAndMoveTo", destination, fly, false);

    public static Vector3? FlagPoint => Ipc.Func<Vector3?>("vnavmesh.Query.Mesh.FlagToPoint");

    public static void Stop() => Ipc.Action("vnavmesh.Path.Stop");
}
