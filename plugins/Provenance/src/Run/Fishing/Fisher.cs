using System;
using System.Collections.Generic;
using Provenance.Core.Run;

namespace Provenance.Run.Fishing;

/// <summary>What the Run page shows about fishing.</summary>
/// <param name="Fish">The fish being fished for (0 when none).</param>
/// <param name="Spot">The fishing hole or spearfishing spot's name, with its zone.</param>
/// <param name="Bait">The bait cast with (0 for spearfishing).</param>
/// <param name="BaitInBags">How much of that bait is left in the main bags.</param>
/// <param name="Window">"Bites for 12 more minutes", "Bites in 8 min (in Fog)", or "" when the fish has no window.</param>
/// <param name="IntuitionLeft">Fisher's Intuition time left, when it's up.</param>
/// <param name="Caught">Fish of this kind caught in the run so far.</param>
/// <param name="Casts">Casts (or spearfishing nodes) at this spot so far.</param>
internal sealed record FishingView(uint Fish, string Spot, uint Bait, int BaitInBags, string Window, TimeSpan? IntuitionLeft, int Caught, int Casts, bool Spear);

/// <summary>
/// Provenance's own fishing in a run (rod fishing and spearfishing): picks the fish to go for and its
/// spot (time and weather windows, level, reach), changes into the Fisher gearset, buys bait, travels to a
/// recorded stand (or a spearfishing node), lands, faces the water and checks that a cast works, then
/// lets the <see cref="Integrations.Fishing.Angler"/> (or the <see cref="Integrations.Fishing.SpearGame"/>)
/// catch. Moves on when the fish turn wary, leaves when they sense something amiss, buys more bait when
/// it runs out, and always reels in before teleporting. Framework thread; <see cref="Tick"/> every frame
/// while the fisher is the run's active worker. Follows GatherBuddy Reborn's AutoGather (Apache-2.0).
/// </summary>
internal sealed class Fisher : IDisposable
{
    public Fisher(Plugin plugin, Action<string, bool> log) => throw new NotImplementedException();

    public string Status { get; private set; } = "";
    /// <summary>Why the fisher gave up (no gearset, no bait anywhere, nothing reachable); the run stops on it.</summary>
    public string? Failure { get; private set; }
    /// <summary>Has fish to work on and isn't paused.</summary>
    public bool Running => throw new NotImplementedException();
    /// <summary>Paused, with the rod reeled in and the spearfishing window closed.</summary>
    public bool IsPaused => throw new NotImplementedException();
    /// <summary>Nothing can be fished now: every fish left waits for its time or weather window.</summary>
    public bool Waiting { get; private set; }
    /// <summary>Waiting at the stand for GP the catch can't do without.</summary>
    public bool WaitingForGp => throw new NotImplementedException();
    /// <summary>The rod is out, a spearfishing window is open, or a catch is under way: don't switch to gathering now.</summary>
    public bool Busy => throw new NotImplementedException();
    /// <summary>A fish left in the order bites now (or within the lead time), at a spot the fisher can use.</summary>
    public bool HasWorkNow => throw new NotImplementedException();
    /// <summary>When the next fish window opens (zero when one is open now); null when no fish left has a window.</summary>
    public TimeSpan? NextWindow => throw new NotImplementedException();
    /// <summary>Fish the fisher can't get (no usable spot, no bait, missing actions).</summary>
    public IReadOnlyCollection<uint> Unreachable => throw new NotImplementedException();
    /// <summary>Fish caught on the side that the order doesn't need (item id → count), for throwing away afterwards.</summary>
    public IReadOnlyDictionary<uint, int> ByCatch => throw new NotImplementedException();
    /// <summary>What the Run page shows; null when not fishing for anything.</summary>
    public FishingView? View => throw new NotImplementedException();

    /// <summary>
    /// Starts with the run's fish targets (other targets are ignored).
    /// </summary>
    /// <param name="left">How many of a target are still wanted (the run's count, including what mooching used up).</param>
    /// <param name="neededCollectables">Items the order needs, which must never be reduced or thrown away.</param>
    /// <param name="consumed">Called when fish the run counts leave the bags as bait (mooch, swimbait): item id, amount.</param>
    public void Begin(IEnumerable<GatherTarget> targets, Func<GatherTarget, int> left, Func<IReadOnlySet<uint>> neededCollectables, Action<uint, int> consumed)
        => throw new NotImplementedException();

    /// <summary>
    /// Real time until a fish can be fished at its best usable spot: zero when it bites now,
    /// <see cref="TimeSpan.MaxValue"/> when not within the weather forecast, null when it has no window.
    /// </summary>
    public TimeSpan? WaitFor(uint itemId) => throw new NotImplementedException();

    /// <summary>Every frame while fishing is the active work.</summary>
    public void Tick() => throw new NotImplementedException();

    /// <summary>Pause now: lets the bite go, reels in, stops moving. <see cref="IsPaused"/> once the rod is in.</summary>
    public void Pause() => throw new NotImplementedException();

    /// <summary>Pause after the current catch (or spearfishing node): <see cref="IsPaused"/> once the rod is in.</summary>
    public void RequestPause() => throw new NotImplementedException();

    public void Resume() => throw new NotImplementedException();

    /// <summary>Ends fishing: reels in first unless <paramref name="now"/> (then everything just stops).</summary>
    public void Stop(bool now = false) => throw new NotImplementedException();

    /// <summary>Removes the fishing hooks; always called on plugin unload.</summary>
    public void Dispose() => throw new NotImplementedException();
}
