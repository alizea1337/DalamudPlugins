using System;
using System.Collections.Generic;
using System.Numerics;

namespace Provenance.Core.Run;

/// <summary>Another player in the object table this frame.</summary>
/// <param name="Hidden">The game doesn't draw its model.</param>
/// <param name="GameMaster">Its online status is one of the game master ones.</param>
/// <param name="Party">In the player's party (never counted).</param>
/// <param name="AtAetheryte">Stands at an aetheryte or aethernet shard: where a teleport arrives.</param>
public readonly record struct SeenPlayer(ulong Id, Vector3 Position, bool Hidden, bool GameMaster, bool Party, bool AtAetheryte);

public enum WatcherKind { GameMaster, Appeared, Hidden }

/// <summary>Someone who may be watching: who (the object id), what about them, and how far away (on the ground).</summary>
public sealed record Watcher(WatcherKind Kind, ulong Id, float Yalms);

/// <summary>What to look for, and what counts as close by.</summary>
public readonly record struct WatchFor(bool GameMasters, bool Appearing, bool Hidden, float NearYalms);

/// <summary>
/// Watches the players around while a run works: a game master (by online status), a player who appears close by
/// without walking there (players come into range far away; arrivals at an aetheryte don't count), and a player
/// close by the game doesn't draw. Party members never count. Fed every frame.
/// </summary>
public sealed class PlayerWatch
{
    /// <summary>A player close by has to stay undrawn this long (a player loading in is undrawn for a moment).</summary>
    public static readonly TimeSpan HiddenFor = TimeSpan.FromSeconds(3);
    /// <summary>After a zone change or a trip, the players around stream in for a while without having appeared.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(6);

    private HashSet<ulong> known = new();
    private readonly Dictionary<ulong, DateTime> hiddenSince = new();
    private DateTime judgeFrom = DateTime.MinValue;
    private bool primed;

    /// <summary>Starts afresh (a run starts or resumes): whoever is around now was already there.</summary>
    public void Reset()
    {
        known.Clear();
        hiddenSince.Clear();
        primed = false;
        judgeFrom = DateTime.MinValue;
    }

    /// <summary>The surroundings change for a reason (loading, a teleport, an aethernet hop): no one "appears" for a while.</summary>
    public void Unsettle(DateTime now)
    {
        var until = now + Settle;
        if (until > judgeFrom) judgeFrom = until;
        hiddenSince.Clear();
    }

    public Watcher? Feed(DateTime now, Vector3 me, IReadOnlyList<SeenPlayer> players, WatchFor look)
    {
        var judging = primed && now >= judgeFrom;
        var present = new HashSet<ulong>();
        Watcher? gm = null, other = null;
        foreach (var p in players)
        {
            present.Add(p.Id);
            var d = Flat(me, p.Position);
            if (look.GameMasters && p.GameMaster) gm ??= new Watcher(WatcherKind.GameMaster, p.Id, d);
            if (p.Party) continue;
            var near = d <= look.NearYalms;
            if (judging && look.Appearing && near && !p.AtAetheryte && !known.Contains(p.Id))
                other ??= new Watcher(WatcherKind.Appeared, p.Id, d);
            if (judging && look.Hidden && near && p.Hidden)
            {
                if (!hiddenSince.TryGetValue(p.Id, out var since)) hiddenSince[p.Id] = now;
                else if (now - since >= HiddenFor) other ??= new Watcher(WatcherKind.Hidden, p.Id, d);
            }
            else hiddenSince.Remove(p.Id);
        }
        foreach (var id in new List<ulong>(hiddenSince.Keys))
            if (!present.Contains(id)) hiddenSince.Remove(id);
        known = present;
        primed = true;
        return gm ?? other;
    }

    private static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();
}
