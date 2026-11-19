using System;
using System.Collections.Generic;

namespace Provenance.Core.Fishing;

/// <summary>
/// How much bait, and how much fishing time, one catch of a fish takes, by rule of thumb: the fish data says
/// what a fish bites on, its tug and bite time and when it bites, but not how often it bites. The numbers
/// below are deliberate guesses, kept in one place so they are easy to tune.
/// <para>
/// Bait is used up only when something is hooked (a cast that nothing bites, or a bite that is let go,
/// keeps it), and a run hooks only the wanted fish's tug inside its bite time. So the bait per catch is the
/// hooks per catch: the fish itself, other fish with the same tug and bite time, and a hook that fails now
/// and then. A mooched fish needs the chain's first fish caught again (on the tackle) whenever a mooch
/// brings up something else, and a fish behind Fisher's Intuition needs its predators caught first, again
/// each time the intuition runs out before the fish bites. While the intuition is up such a fish bites far
/// more readily, so it counts as a restricted fish then, even a big one.
/// </para>
/// <para>
/// Checked against the shipped fish data (1,941 line fish): half need about 1.4 bait and a minute and a
/// quarter a catch, one in ten more than 4 bait or 10 minutes, and the hardest (big fish at the end of a
/// mooch chain or behind intuition) around 100 bait and two hours.
/// </para>
/// Pure: other fish (the chain, the predators) come through a lookup.
/// </summary>
public static class BaitNeeds
{
    // Casts per catch: casts that end in landing the fish, the others end at another fish's bite or after
    // the fish's latest bite time. A common fish bites about one cast in three; a fish that only bites at
    // some hours or in some weather is usually a rarer bite even then; a big fish is rarer still (the
    // observed rate of the Bombardfish is about one cast in a hundred, Patience, Chum and lures help).
    public const double CommonCasts = 3;
    public const double RestrictedCasts = 6;
    public const double BigCasts = 20;

    // Hooks per catch (the bait used up): the fish, plus same-tug fish hooked in its place, plus failed hooks.
    public const double CommonHooks = 1.4;
    public const double RestrictedHooks = 1.75;
    public const double BigHooks = 3;

    // Mooches per fish brought up from the fish before it in a chain. A mooched fish draws only a few kinds
    // of fish, so it takes fewer tries than casts with bait take.
    public const double CommonMoochTries = 2;
    public const double RestrictedMoochTries = 3;
    public const double BigMoochTries = 6;

    /// <summary>
    /// A rare bite (a legendary tug on a fish that isn't flagged big, or an earliest bite this many seconds
    /// after the cast or later) multiplies the casts and mooch tries by <see cref="RareBiteCasts"/> and the
    /// hooks by <see cref="RareBiteHooks"/>. Big fish already count as rare.
    /// </summary>
    public const float SlowBiteSeconds = 25;
    public const double RareBiteCasts = 1.5;
    public const double RareBiteHooks = 1.2;

    /// <summary>Casting, reeling in or resting, and getting the next cast out, on top of the wait for a bite.</summary>
    public const double CastOverheadSeconds = 5;
    /// <summary>The wait for a bite when the data has no bite time.</summary>
    public const double UnknownBiteSeconds = 15;
    /// <summary>The typical bite comes at most this long after the earliest one (the latest bites in the data are rare).</summary>
    public const double BiteSpreadSeconds = 15;
    /// <summary>Hooking and landing a fish (the hook and catch animations).</summary>
    public const double LandingSeconds = 6;
    /// <summary>Fisher's Intuition runs per catch when the data doesn't say how long the intuition lasts.</summary>
    public const double UnknownIntuitionRuns = 1.5;

    // Spearfishing has no bait: a catch is mostly swimming between nodes and waiting for the right fish.
    public const double SpearCommonSeconds = 40;
    public const double SpearRestrictedSeconds = 60;
    public const double SpearBigSeconds = 120;

    /// <summary>Predators of predators are followed this deep (the data has no deeper ones).</summary>
    private const int MaxDepth = 3;

    /// <summary>A rare bite: a legendary tug on a fish that isn't flagged big, or a slow earliest bite.</summary>
    public static bool RareBite(FishInfo fish)
        => !fish.Big && (fish.Tug == Tug.Legendary || fish.BiteSeconds is { Min: >= SlowBiteSeconds });

    /// <summary>Casts that land one of this fish, fishing with its own bait (or one mooch, for the chain's last step).</summary>
    public static double CastsPerCatch(FishInfo fish) => Tier(fish, CommonCasts, RestrictedCasts, BigCasts, RareBiteCasts);

    /// <summary>Hooks, and so bait, per landed fish when only its tug is hooked inside its bite time.</summary>
    public static double HooksPerCatch(FishInfo fish) => Tier(fish, CommonHooks, RestrictedHooks, BigHooks, RareBiteHooks);

    /// <summary>Mooches it takes to bring this fish up from the fish before it in its chain.</summary>
    public static double MoochTries(FishInfo fish) => Tier(fish, CommonMoochTries, RestrictedMoochTries, BigMoochTries, RareBiteCasts);

    /// <summary>One cast: the overhead plus the typical wait for a bite (a cast that is rested ends about as early).</summary>
    public static double SecondsPerCast(FishInfo fish)
    {
        if (fish.BiteSeconds is not { } bite || bite.Max <= 0) return CastOverheadSeconds + UnknownBiteSeconds;
        var min = Math.Max(0, bite.Min);
        return CastOverheadSeconds + min + Math.Min(Math.Max(0, bite.Max - min) / 2, BiteSpreadSeconds);
    }

    /// <summary>
    /// The bait one catch of the fish uses up, by bait item: its own tackle (for a mooched fish, the bait the
    /// chain starts with, once for every try the chain takes) and the bait of the fish caught first for Fisher's
    /// Intuition, times their counts and the intuition runs a catch takes. Empty for spearfishing and for fish
    /// with no known bait. The fish's own bait comes first.
    /// </summary>
    public static IReadOnlyList<(uint Bait, double PerCatch)> PerCatch(FishInfo fish, Func<uint, FishInfo?> lookup)
    {
        var result = new List<(uint Bait, double PerCatch)>();
        Add(fish, 1.0, 0, new HashSet<uint>());
        return result;

        void Add(FishInfo f, double times, int depth, HashSet<uint> path)
        {
            if (f.Spear || times <= 0 || depth > MaxDepth || !path.Add(f.ItemId)) return;
            if (f.Bait != 0) Put(f.Bait, OwnTackle(f, lookup) * times);
            if (f.NeedsIntuition)
            {
                var runs = IntuitionRuns(f, lookup);
                foreach (var (id, count) in f.Predators)
                    if (count > 0 && lookup(id) is { } predator) Add(predator, times * count * runs, depth + 1, path);
            }
            path.Remove(f.ItemId);
        }

        void Put(uint bait, double amount)
        {
            for (var i = 0; i < result.Count; i++)
                if (result[i].Bait == bait)
                {
                    result[i] = (bait, result[i].PerCatch + amount);
                    return;
                }
            result.Add((bait, amount));
        }
    }

    /// <summary>
    /// Fishing time for one catch, in seconds: its casts and bite time, a mooch chain caught again for every
    /// try, and the predators for Fisher's Intuition (each intuition run). Travel isn't included.
    /// </summary>
    public static double SecondsPerCatch(FishInfo fish, Func<uint, FishInfo?> lookup)
    {
        return Seconds(fish, 0, new HashSet<uint>());

        double Seconds(FishInfo f, int depth, HashSet<uint> path)
        {
            if (!path.Add(f.ItemId)) return 0;
            var total = f.Spear ? SpearSeconds(f) : OwnSeconds(f, lookup);
            if (f.NeedsIntuition && depth < MaxDepth)
            {
                // Spearfishing predators make swimming shadows appear: one round of them per catch.
                var runs = f.Spear ? 1 : IntuitionRuns(f, lookup);
                foreach (var (id, count) in f.Predators)
                    if (count > 0 && lookup(id) is { } predator) total += runs * count * Seconds(predator, depth + 1, path);
            }
            path.Remove(f.ItemId);
            return total;
        }
    }

    /// <summary>
    /// Fisher's Intuition runs one catch takes: the time the fish itself takes, over how long the intuition
    /// lasts (at least one run). 0 for fish that don't need it.
    /// </summary>
    public static double IntuitionRuns(FishInfo fish, Func<uint, FishInfo?> lookup)
    {
        if (!fish.NeedsIntuition) return 0;
        if (fish.IntuitionSeconds <= 0) return UnknownIntuitionRuns;
        var own = fish.Spear ? SpearSeconds(fish) : OwnSeconds(fish, lookup);
        return Math.Max(1, own / fish.IntuitionSeconds);
    }

    /// <summary>Times the chain's first fish is caught per catch: every mooch in the chain needs its tries.</summary>
    public static double ChainTries(FishInfo fish, Func<uint, FishInfo?> lookup)
    {
        var tries = 1.0;
        for (var k = 1; k <= fish.Mooch.Count; k++)
        {
            var next = k < fish.Mooch.Count ? lookup(fish.Mooch[k]) : fish;
            tries *= next != null ? MoochTries(next) : CommonMoochTries;
        }
        return tries;
    }

    /// <summary>Tackle for the fish's own line: its hooks, or the chain's first fish's hooks for every try.</summary>
    private static double OwnTackle(FishInfo fish, Func<uint, FishInfo?> lookup)
    {
        if (!fish.Mooched) return HooksPerCatch(fish);
        var first = lookup(fish.Mooch[0]);
        return (first != null ? HooksPerCatch(first) : CommonHooks) * ChainTries(fish, lookup);
    }

    /// <summary>
    /// Time for the fish's own line. A mooched fish: every mooch step is tried as often as the steps after it
    /// need (the last step its own tries, the one before that its tries for each of those, and so on), and the
    /// chain's first fish is caught once for every try of the first step.
    /// </summary>
    private static double OwnSeconds(FishInfo fish, Func<uint, FishInfo?> lookup)
    {
        if (!fish.Mooched) return CastsPerCatch(fish) * SecondsPerCast(fish) + LandingSeconds;
        var first = lookup(fish.Mooch[0]);
        var firstCatch = (first != null ? CastsPerCatch(first) * SecondsPerCast(first) : CommonCasts * (CastOverheadSeconds + UnknownBiteSeconds))
                         + LandingSeconds;
        var total = 0.0;
        var needed = 1.0; // landings the current step must bring: the tries of every step after it
        for (var k = fish.Mooch.Count; k >= 1; k--)
        {
            var next = k < fish.Mooch.Count ? lookup(fish.Mooch[k]) : fish;
            var tries = next != null ? MoochTries(next) : CommonMoochTries;
            var cast = next != null ? SecondsPerCast(next) : CastOverheadSeconds + UnknownBiteSeconds;
            total += needed * (tries * cast + LandingSeconds);
            needed *= tries;
        }
        return total + needed * firstCatch;
    }

    private static double SpearSeconds(FishInfo fish) => fish.Big ? SpearBigSeconds : fish.Restricted ? SpearRestrictedSeconds : SpearCommonSeconds;

    private static double Tier(FishInfo fish, double common, double restricted, double big, double rareFactor)
    {
        // Under Fisher's Intuition a fish bites far more readily (that is what the intuition is for): a fish
        // behind it counts as a restricted one while the intuition is up, even a big one.
        if (fish.Big && !fish.NeedsIntuition) return big;
        var value = fish.Restricted || fish.NeedsIntuition ? restricted : common;
        return RareBite(fish) ? value * rareFactor : value;
    }
}
