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

    public static TR? Func<T1, T2, T3, TR>(string name, T1 a, T2 b, T3 c, TR? fallback = default)
    {
        try { return Services.PluginInterface.GetIpcSubscriber<T1, T2, T3, TR>(name).InvokeFunc(a, b, c); }
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

    /// <summary>Walks until within <paramref name="range"/> of the destination.</summary>
    public static bool MoveCloseTo(Vector3 destination, bool fly, float range)
        => Ipc.Func<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo", destination, fly, range, false);

    /// <summary>Closest point on the walkable mesh to a spot (height guessed from where the player stands).</summary>
    public static Vector3? NearestPoint(Vector3 near, float halfXz, float halfY)
        => Ipc.Func<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint", near, halfXz, halfY);

    public static void Stop() => Ipc.Action("vnavmesh.Path.Stop");
}
