using System;
using System.Collections.Generic;
using System.Linq;

namespace Provenance.Core.Tribes;

/// <summary>Where one of today's offered dailies stands.</summary>
public enum OfferState
{
    /// <summary>Offered and not taken yet.</summary>
    Open,
    /// <summary>Accepted and in the journal.</summary>
    Accepted,
    /// <summary>Done today.</summary>
    Done,
}

/// <summary>A daily a giver offers today.</summary>
/// <param name="AcceptedAs">The ClassJob it was accepted on (0 unless <see cref="OfferState.Accepted"/>).</param>
public sealed record TribeOffer(TribeQuest Quest, OfferState State, uint AcceptedAs = 0);

/// <summary>One giver's day: what it offers and how many of its dailies were taken today.</summary>
/// <param name="Taken">The giver's dailies in today's daily-quest slots (accepted or done; offered or not).</param>
public sealed record GiverToday(TribeGiver Giver, IReadOnlyList<TribeOffer> Offers, int Taken)
{
    public int Open => Offers.Count(o => o.State == OfferState.Open);
}

/// <summary>A society quest in the journal, with the class it's locked to.</summary>
/// <param name="Sequence">The quest's step (0 just accepted, 255 ready to hand in).</param>
/// <param name="AcceptClassJob">The class it was accepted on (DoH/DoL quests only go on with that class).</param>
/// <param name="Today">It has one of today's daily-quest slots; an accepted daily without one is left over from before the reset.</param>
public sealed record AcceptedTribeQuest(TribeQuest Quest, byte Sequence, uint AcceptClassJob, bool Today)
{
    /// <summary>A daily accepted before the last reset: the game takes no new dailies until it's handed in or abandoned.</summary>
    public bool Leftover => Quest.IsDaily && !Today;
}

/// <summary>
/// A society today: rank, reputation, today's offers per giver, and what's taken. Built by
/// <c>Integrations.Tribes.Today</c> from the game; pure, so the planner can work on it.
/// </summary>
/// <param name="Rank">1 Neutral … 8 Bloodsworn; 0 while locked.</param>
/// <param name="RankedUpToday">The rank went up today (the givers then offer dailies of exactly this rank).</param>
/// <param name="Value">Reputation within the rank.</param>
/// <param name="Needed">Reputation that fills the rank's bar (0 at the top).</param>
/// <param name="Allied">The expansion's intersocietal quest is done ("Allied").</param>
public sealed record TribeToday(Tribe Tribe, int Rank, bool RankedUpToday, int Value, int Needed, bool Allied, IReadOnlyList<GiverToday> Givers)
{
    public bool Unlocked => Rank > 0;
    public bool AtTop => TribeRanks.AtTop(Rank, Tribe.MaxRank);

    /// <summary>The reputation bar is full: the rank quest is next, and dailies give no reputation until it's done.</summary>
    public bool BarFull => TribeRanks.BarFull(Rank, Value, Needed, Tribe.MaxRank);

    /// <summary>The rank quest offered now (bar full), or null.</summary>
    public TribeRankQuest? RankQuestReady => BarFull ? Tribe.RankQuest(Rank) : null;

    /// <summary>Dailies one giver takes today: 3, or 6 on the day of a rank-up (3 more of the new rank).</summary>
    public int CapPerGiver => RankedUpToday ? TribeRanks.RankUpDayCap : TribeRanks.PerDay;

    /// <summary>The society's dailies taken today (accepted or done).</summary>
    public int Taken => Givers.Sum(g => g.Taken);

    public IEnumerable<TribeOffer> Offers => Givers.SelectMany(g => g.Offers);

    /// <summary>Today's offers that can still be taken, with <paramref name="allowances"/> left in all.</summary>
    public int LeftToday(int allowances)
        => Math.Min(Math.Max(0, allowances), Givers.Sum(g => Math.Min(g.Open, Math.Max(0, CapPerGiver - g.Taken))));

    /// <summary>
    /// Where the player stands, for <see cref="TribeRanks"/>. For societies with a giver per rank (the ARR ones) the
    /// day counts the giver of the player's own rank, whose dailies give the most reputation.
    /// </summary>
    public TribeStanding Standing(int allowances)
    {
        if (Givers.Count <= 1)
            return new TribeStanding(Rank, Value, Taken, LeftToday(allowances));
        // Ixal's "Deliverance" giver serves rank 1 too, with one quest the run never takes: the rank's own giver comes first.
        var top = Givers.Where(g => g.Giver.MaxRank <= Math.Max(1, Rank))
            .OrderByDescending(g => g.Giver.MaxRank)
            .ThenByDescending(g => g.Offers.Count(o => !QuestPolicy.Never.ContainsKey(o.Quest.Number)))
            .FirstOrDefault() ?? Givers[0];
        var left = Math.Min(Math.Max(0, allowances), Math.Min(top.Open, Math.Max(0, CapPerGiver - top.Taken)));
        return new TribeStanding(Rank, Value, top.Taken, left);
    }
}

/// <summary>The goal in effect for the day's run.</summary>
public static class TribeGoals
{
    /// <summary>
    /// The goal picked, else (decided with the user) "Rank up" while any society taking part is below its top rank,
    /// otherwise "Currency".
    /// </summary>
    /// <param name="taking">The unlocked societies the run may visit (ticked, crafter or gatherer).</param>
    public static TribeGoal Effective(TribeGoal? picked, IEnumerable<TribeToday> taking)
        => picked ?? (taking.Any(t => t.Unlocked && !t.AtTop) ? TribeGoal.RankUp : TribeGoal.Currency);
}
