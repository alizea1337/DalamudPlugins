using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using Provenance.Core.Fishing;

namespace Provenance.Integrations.Fishing;

/// <summary>
/// Spearfishing: finding the spearfishing nodes of a spot in the object table, and working the
/// SpearFishing window once a node is open: reads the three lanes every frame, recognises fish by size
/// and speed, and gigs a wanted fish the moment it crosses the middle of its lane. Moving to a node and
/// opening it is the caller's job (with <see cref="Usable"/> and <see cref="NearestNode"/>).
/// Ported from AutoHook's AutoGig (BSD-3, Puni.sh) and GatherBuddy Reborn's spearfishing (Apache-2.0).
/// Framework thread; <see cref="Tick"/> every frame.
/// </summary>
internal sealed unsafe class SpearGame
{
    public string Status { get; private set; } = "";
    /// <summary>Gigging in an open window.</summary>
    public bool Active { get; private set; }
    /// <summary>The window has closed since <see cref="Begin"/> (the node is used up, or it was left).</summary>
    public bool Finished { get; private set; }

    /// <summary>Fish gigged (item id, amount), counted from the bags.</summary>
    public event Action<uint, int>? Caught;

    /// <summary>The SpearFishing window is open.</summary>
    public static bool WindowOpen => throw new NotImplementedException();

    /// <summary>
    /// Starts gigging in the open window: only fish whose size and speed match one of <paramref name="wanted"/>
    /// (every fish when it is empty). Fish that pass by unwanted are left alone.
    /// </summary>
    public void Begin(IReadOnlyList<FishInfo> wanted) => throw new NotImplementedException();

    /// <summary>Every frame.</summary>
    public void Tick() => throw new NotImplementedException();

    /// <summary>Stops gigging (the window stays open; it closes by itself when the node is used up).</summary>
    public void Stop() => throw new NotImplementedException();

    /// <summary>A spearfishing node object that can be opened now (targetable, attempts left).</summary>
    public static bool Usable(IGameObject node) => throw new NotImplementedException();

    /// <summary>
    /// The closest usable spearfishing node among these GatheringPoint rows (by data id), skipping
    /// <paramref name="skip"/> (game object ids); null when none is in the object table.
    /// </summary>
    public static IGameObject? NearestNode(IReadOnlyCollection<uint> points, IReadOnlySet<ulong>? skip = null) => throw new NotImplementedException();

    private void Raise(uint item, int amount) => Caught?.Invoke(item, amount);
}
