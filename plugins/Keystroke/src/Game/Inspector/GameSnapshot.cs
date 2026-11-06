using System;
using System.Collections.Generic;

namespace Keystroke.Game.Inspector;

/// <summary>
/// Immutable picture of the game state relevant to Keybound Brawler, captured on the framework
/// thread and rendered by the Inspector window. Also what "Copy snapshot" serialises.
/// </summary>
public sealed record GameSnapshot
{
    public DateTime CapturedAt { get; init; }
    public string PluginVersion { get; init; } = "";
    public bool IsLoggedIn { get; init; }
    public ushort TerritoryId { get; init; }
    public string TerritoryName { get; init; } = "";
    public string TerritoryPlaceName { get; init; } = "";
    public bool InKeyboundDuty { get; init; }
    public bool InKeyboundLobby { get; init; }
    public string ClientLanguage { get; init; } = "";
    public string Conditions { get; init; } = "";

    public List<AddonSnapshot> Addons { get; init; } = new();
    public List<AgentSnapshot> Agents { get; init; } = new();
    public ArraySnapshot? NumberArray { get; init; }
    public ArraySnapshot? StringArray { get; init; }
    public List<CastSnapshot> Casts { get; init; } = new();
    public CurrencySnapshot Currency { get; init; } = new();
    public QueueSnapshot Queue { get; init; } = new();
    public ObjectSnapshot? Target { get; init; }
    /// <summary>Event NPCs within 12 m of the player, nearest first.</summary>
    public List<ObjectSnapshot> NearbyNpcs { get; init; } = new();
    public List<string> Errors { get; init; } = new();
}

public sealed record ObjectSnapshot
{
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public uint EntityId { get; init; }
    /// <summary>Sheet id (ENpcResident / BNpcBase); stable across languages.</summary>
    public uint DataId { get; init; }
    public float Distance { get; init; }
}

public sealed record AddonSnapshot
{
    public string Name { get; init; } = "";
    public bool Present { get; init; }
    public string Address { get; init; } = "";
    public bool Visible { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Width { get; init; }
    public float Height { get; init; }
    public int NodeCount { get; init; }
    public List<NodeSnapshot> Nodes { get; init; } = new();
}

public sealed record NodeSnapshot
{
    public int Depth { get; init; }
    public uint NodeId { get; init; }
    public string Type { get; init; } = "";
    public bool Visible { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Width { get; init; }
    public float Height { get; init; }
    public string? Text { get; init; }
    public string? ComponentType { get; init; }
}

public sealed record AgentSnapshot
{
    public string Name { get; init; } = "";
    public uint Id { get; init; }
    public string Address { get; init; } = "";
    public bool Active { get; init; }
    public uint AddonId { get; init; }
}

public sealed record ArraySnapshot
{
    public int Index { get; init; }
    public bool Present { get; init; }
    public int Size { get; init; }
    public List<string> Values { get; init; } = new();
}

public sealed record CastSnapshot
{
    public uint EntityId { get; init; }
    public string Name { get; init; } = "";
    public uint ActionId { get; init; }
    public string ActionName { get; init; } = "";
    public float CurrentTime { get; init; }
    public float TotalTime { get; init; }
}

public sealed record CurrencySnapshot
{
    public int Mgp { get; init; }
    public int Mgc { get; init; }
    public uint MgcCap { get; init; }
    public int KeyboundCoffers { get; init; }
}

public sealed record QueueSnapshot
{
    public string State { get; init; } = "";
    public uint PoppedContentId { get; init; }
    public List<uint> QueuedContentIds { get; init; } = new();
    public int Position { get; init; }
}
