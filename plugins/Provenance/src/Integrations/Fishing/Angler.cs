using System;
using System.Collections.Generic;
using Provenance.Core.Fishing;

namespace Provenance.Integrations.Fishing;

/// <summary>Why a fishing session ended (the rod is reeled in by then).</summary>
internal enum AnglerSignal
{
    /// <summary>Still fishing, or never started.</summary>
    None,
    /// <summary>Enough of the target caught (<see cref="AnglerJob.Left"/> reached 0).</summary>
    Done,
    /// <summary>"The fish have become wary of your presence": cast from a stand at least 10 yalms away.</summary>
    Wary,
    /// <summary>"The fish sense something amiss": this fishing hole is done for a while.</summary>
    Amiss,
    /// <summary>The bait ran out, or the game says no bait is selected.</summary>
    NoBait,
    /// <summary>The line can't be cast from here (no water in front, a bad stand).</summary>
    CantCast,
    /// <summary>The target's time or weather window closed before enough were caught.</summary>
    WindowClosed,
    /// <summary>Stopped on request (<see cref="Angler.RequestStop"/> or <see cref="Angler.Stop"/>).</summary>
    Stopped,
}

/// <summary>One fish landed, as the game reports it after the catch.</summary>
/// <param name="ItemId">The fish's item id (collectables already mapped back from their +500,000 id).</param>
/// <param name="Amount">How many came in (Double and Triple Hook land several).</param>
/// <param name="Moochable">The game offers Mooch with it.</param>
internal readonly record struct FishCatch(uint ItemId, int Amount, bool Collectable, bool Large, bool Moochable);

/// <summary>A fishing session at the stand the player is on: catch <see cref="Target"/> until <see cref="Left"/> is 0.</summary>
/// <param name="Target">The fish the run wants. Its mooch chain and the fish needed for Fisher's Intuition are caught along the way.</param>
/// <param name="Spot">The fishing hole the player stands at.</param>
/// <param name="WeatherTable">The spot zone's weather table, for the target's window.</param>
/// <param name="Bait">Tackle to cast with: the chain's own bait, or Versatile Lure when that's all there is.</param>
/// <param name="FishData">The fish database (for predators and chain fish).</param>
/// <param name="Left">How many more of the target the run wants; the session ends at 0.</param>
/// <param name="UseBuffs">Chum, Patience, Surface Slap, Identical Cast and lures when the catch plan calls for them.</param>
/// <param name="UseCordials">Drink cordials for GP between casts.</param>
internal sealed record AnglerJob(
    FishInfo Target,
    FishingSpotInfo Spot,
    IReadOnlyList<(uint Weather, int Rate)> WeatherTable,
    uint Bait,
    Func<uint, FishInfo?> FishData,
    Func<int> Left,
    bool UseBuffs,
    bool UseCordials);

/// <summary>
/// Rod fishing at one stand, at AutoHook's level: selects the bait, casts, reads the bite (tug and time
/// since the cast), hooks only what the catch plan wants (the target, its mooch chain, the fish for
/// Fisher's Intuition) with the right hookset and Rests on everything else, mooches, keeps buffs up and
/// drinks cordials between casts, and watches the fishing hole's warnings. It never moves the player:
/// standing somewhere castable, on foot, as Fisher, with the bait in the bags is the caller's job.
/// Ported from AutoHook (BSD-3, Puni.sh). Framework thread; <see cref="Tick"/> every frame.
/// </summary>
internal sealed unsafe class Angler : IDisposable
{
    public string Status { get; private set; } = "";
    /// <summary>Something that a retry won't fix (hooks unavailable, the job has no usable plan); the run stops on it.</summary>
    public string? Failure { get; private set; }
    /// <summary>Why the last session ended; <see cref="AnglerSignal.None"/> while one runs.</summary>
    public AnglerSignal Signal { get; private set; }
    /// <summary>A session runs (including reeling in at its end).</summary>
    public bool Active { get; private set; }
    /// <summary>The fisher stance is up (the rod is out: <c>ConditionFlag.Fishing</c>).</summary>
    public bool RodOut => throw new NotImplementedException();
    /// <summary>Waiting at the stand with the rod in: the target's window opens soon and nothing has to be caught first.</summary>
    public bool WaitingForWindow { get; private set; }
    /// <summary>Waiting at the stand for GP the catch plan can't do without.</summary>
    public bool WaitingForGp { get; private set; }
    /// <summary>Casts in the current session.</summary>
    public int Casts { get; private set; }
    /// <summary>Fisher's Intuition is up, and how long it lasts.</summary>
    public TimeSpan? IntuitionLeft => throw new NotImplementedException();

    /// <summary>Every fish landed (the target or anything else).</summary>
    public event Action<FishCatch>? Caught;
    /// <summary>A caught fish (item id) left the bags as bait: mooched, or banked as swimbait.</summary>
    public event Action<uint>? UsedAsBait;

    /// <summary>Starts a session (ends any running one). Signal goes back to None.</summary>
    public void Begin(AnglerJob job) => throw new NotImplementedException();

    /// <summary>Every frame.</summary>
    public void Tick() => throw new NotImplementedException();

    /// <summary>Lands the fish on the line if one is hooked, then reels in; the session ends as <see cref="AnglerSignal.Stopped"/>.</summary>
    public void RequestStop() => throw new NotImplementedException();

    /// <summary>Reels in now (a bite is let go) and ends the session as <see cref="AnglerSignal.Stopped"/>.</summary>
    public void Stop() => throw new NotImplementedException();

    /// <summary>The Cast action can be used where the player stands.</summary>
    public static bool CanCastHere() => throw new NotImplementedException();

    /// <summary>Removes the hooks. Always called on plugin unload, whether or not a run is active.</summary>
    public void Dispose() => throw new NotImplementedException();

    private void Raise(FishCatch c) => Caught?.Invoke(c);
    private void RaiseBait(uint id) => UsedAsBait?.Invoke(id);
}
