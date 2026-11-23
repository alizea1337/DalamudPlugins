using System;
using System.Collections.Generic;
using System.Linq;

namespace Provenance.Core.Tribes;

/// <summary>A daily as the pick sees it: its id (sheet row), the lowest rank that offers it, and whether it's in the "exclusive" pool.</summary>
public readonly record struct DailyCandidate(uint QuestId, int Rank, bool Exclusive);

/// <summary>
/// The dailies a society's quest giver offers today, worked out the way the game does it
/// (<c>DailyQuestMap.CalculateAvailableQuests</c>): from the day's seed (<c>QuestManager.DailyQuestSeed</c>), the
/// player's rank with the society, the "ranked up today" bit, and the giver's dailies sorted by quest id with the
/// exclusive pool (<c>Quest.DailyQuestPool</c> 3) last. So the day's quests are known before talking to anyone.
/// </summary>
/// <remarks>
/// <para>
/// Ported from BossMod's <c>BossMod/Debug/DebugQuests.cs</c> (awgil/ffxiv_bossmod, BSD 3-Clause, Copyright (c)
/// 2022-2024 Andrew Gilewsky; see NOTICE.md): the generator and the pick. BossMod leaves the exclusive pool out (its
/// lookup is commented out as "TODO map Unknown11", the column now named <c>DailyQuestPool</c>), so its copy is wrong
/// for the four ARR battle societies' givers at their own rank. The exclusive branch here is written from the
/// algorithm as the allied-society research describes it (FFXIVClientStructs' <c>DailyQuestMap</c> comments, MIT, and
/// Questionable's reimplementation, used for facts only): one offer from the exclusive pool first, then normal ones up
/// to three in all.
/// </para>
/// <para>
/// BossMod checked its copy against the game's own function over every seed and rank. This one is checked in game
/// whenever a giver's "Daily Quests" list is open (<c>Integrations/Tribes.Observe</c> logs <c>[tribes] match</c> or
/// <c>MISMATCH</c>); the unit tests pin the generator and the selection rules on small hand-made inputs.
/// </para>
/// </remarks>
public static class DailyPick
{
    /// <summary>A giver offers this many dailies a day (fewer when fewer are eligible).</summary>
    public const int PerGiver = 3;

    /// <summary>
    /// The game's 128-bit xorshift-style generator, seeded with (seed, 0, 0, 0) and restarted for every giver. Each draw
    /// works out a new second word from the first two and rotates the four words.
    /// </summary>
    public struct Generator
    {
        private uint s0, s1, s2, s3;

        public Generator(byte seed)
        {
            s0 = seed;
            s1 = s2 = s3 = 0;
        }

        /// <summary>The next raw draw: the new second word.</summary>
        public uint NextRaw()
        {
            unchecked
            {
                var t = s0 ^ (s0 << 11);
                var next = s1 ^ t ^ ((t ^ (s1 >> 11)) >> 8);
                (s0, s1, s2, s3) = (s3, next, s1, s2);
                return next;
            }
        }

        /// <summary>A draw below <paramref name="range"/> (which must be at least 1).</summary>
        public int Next(int range) => (int)(NextRaw() % (uint)range);
    }

    /// <summary>The order the game keeps a giver's dailies in: normal pools by quest id, then the exclusive pool by quest id.</summary>
    public static List<DailyCandidate> Order(IEnumerable<DailyCandidate> quests)
        => quests.OrderBy(q => q.Exclusive).ThenBy(q => q.QuestId).ToList();

    /// <summary>
    /// The player's rank is above the rank of every daily the giver has (all of them, eligible or not). Then the
    /// exclusive pool counts like the others.
    /// </summary>
    public static bool OutranksAll(IEnumerable<DailyCandidate> giverQuests, int rank) => giverQuests.All(q => rank > q.Rank);

    /// <summary>
    /// Whether the giver may offer a daily today: one of the player's rank or below, or, on the day the player ranked
    /// up, only one of exactly the new rank.
    /// </summary>
    public static bool Eligible(DailyCandidate quest, int rank, bool rankedUpToday)
        => rankedUpToday ? quest.Rank == rank : quest.Rank <= rank;

    /// <summary>
    /// The dailies a giver offers today, in the order the game picks them (empty while the society is locked, rank 0).
    /// </summary>
    /// <param name="giverQuests">All the giver's dailies (any order; any rank).</param>
    /// <param name="seed"><c>QuestManager.DailyQuestSeed</c>.</param>
    /// <param name="rank">The player's rank with the society (the rank byte without its high bit).</param>
    /// <param name="rankedUpToday">The rank byte's high bit: the player ranked up today.</param>
    public static List<DailyCandidate> Offers(IEnumerable<DailyCandidate> giverQuests, byte seed, int rank, bool rankedUpToday)
    {
        var picked = new List<DailyCandidate>(PerGiver);
        if (rank <= 0) return picked;
        var all = Order(giverQuests);
        var outranks = OutranksAll(all, rank);
        var eligible = all.Where(q => Eligible(q, rank, rankedUpToday)).ToList();
        if (eligible.Count == 0) return picked;

        var rng = new Generator(seed);
        if (outranks)
        {
            // Above every daily's rank: the pools are one list. A pick that's taken already steps to the next free one.
            for (var i = 0; i < Math.Min(eligible.Count, PerGiver); i++)
            {
                var index = rng.Next(eligible.Count);
                while (picked.Contains(eligible[index])) index = (index + 1) % eligible.Count;
                picked.Add(eligible[index]);
            }
            return picked;
        }

        // Otherwise one offer comes from the exclusive pool when one of its dailies is eligible, then normal ones up to
        // three in all (or as many as the normal pool has).
        var firstExclusive = eligible.FindIndex(q => q.Exclusive);
        if (firstExclusive >= 0) picked.Add(eligible[firstExclusive + rng.Next(eligible.Count - firstExclusive)]);
        else firstExclusive = eligible.Count;
        for (var i = picked.Count; i < Math.Min(firstExclusive, PerGiver); i++)
        {
            var index = rng.Next(firstExclusive);
            while (picked.Contains(eligible[index])) index = (index + 1) % firstExclusive;
            picked.Add(eligible[index]);
        }
        return picked;
    }

    /// <summary>The same pick on a giver's <see cref="TribeQuest"/>s.</summary>
    public static List<TribeQuest> Offers(IReadOnlyList<TribeQuest> giverQuests, byte seed, int rank, bool rankedUpToday)
    {
        var byId = giverQuests.ToDictionary(q => q.RowId);
        return Offers(giverQuests.Select(q => q.Candidate), seed, rank, rankedUpToday).Select(c => byId[c.QuestId]).ToList();
    }
}
