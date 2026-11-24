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
/// <param name="AcceptClassJob">The class it was accepted on (a society quest only goes on with that class).</param>
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

/// <summary>
/// Telling the dailies left over from before the reset, besides the game's slots. <c>Integrations.Tribes</c> takes a
/// daily in the journal without one of today's slots as left over, which rests on the game clearing the slots at the
/// reset while the quest stays in the journal (not checked in game yet). This is the fallback for when it doesn't.
/// </summary>
public static class TribeLeftovers
{
    /// <summary>
    /// A giver's "Daily Quests" list shows none of its dailies although allowances are left, the giver has room today,
    /// some of what it offers today is open, and society dailies are in the journal: the game holds new dailies back,
    /// which it does only while a daily from before the reset is in the journal. Then every society daily in the
    /// journal is from before the reset: one accepted today couldn't have been while any was left over.
    /// </summary>
    /// <param name="listed">Dailies the giver's list shows (rank quests and small talk not counted).</param>
    /// <param name="open">Of the giver's worked-out offers, those not taken today.</param>
    /// <param name="room">Dailies the giver may still give today (its 3, or 6 on a rank-up day, less those taken).</param>
    /// <param name="inJournal">Society dailies in the journal.</param>
    public static bool HeldBack(int allowances, int listed, int open, int room, int inJournal)
        => allowances > 0 && listed == 0 && open > 0 && room > 0 && inJournal > 0;
}

/// <summary>The goal in effect for the day's run: one rule, for the planner and the page.</summary>
public static class TribeGoals
{
    /// <summary>The unlocked societies the run may visit: the ticked ones, of all 20.</summary>
    public static List<TribeToday> Taking(IEnumerable<TribeToday> days, IReadOnlySet<uint> leftOut)
        => days.Where(d => d.Unlocked && !leftOut.Contains(d.Tribe.Id)).ToList();

    /// <summary>
    /// The goal picked, else (decided with the user) "Rank up" while any society taking part is below its top rank,
    /// otherwise "Currency". A society counts only when the run could take one of its dailies today
    /// (<see cref="CanTake"/>): one whose dailies are all left out (a battle society with no usable combat job) gains
    /// no rank from the run, and mustn't turn the Currency order off for the others.
    /// </summary>
    /// <param name="days">Every society today; the locked and the unticked ones don't count.</param>
    /// <param name="leftOut">Societies the player unticked (<c>BeastTribe</c> rows).</param>
    /// <param name="who">The player's classes, as the class choice sees them.</param>
    /// <param name="leaveOutSay">The Tribes option "Leave out quests that /say in public".</param>
    public static TribeGoal Effective(TribeGoal? picked, IEnumerable<TribeToday> days, IReadOnlySet<uint> leftOut, PlayerClasses who, bool leaveOutSay = false)
        => picked ?? (Taking(days, leftOut).Any(t => !t.AtTop && CanTake(t, who, leaveOutSay)) ? TribeGoal.RankUp : TribeGoal.Currency);

    /// <summary>
    /// The run could take one of the society's dailies today: one of today's offers is the run's to do
    /// (<see cref="QuestPolicy"/>) and a class can take it, with a combat job for its fight
    /// (<see cref="TribeClasses.ClassNeed"/>; for a battle society's daily that's <see cref="TribeClasses.Choose"/>
    /// naming a job). Offers already accepted or done today count as well, and the allowances, a giver's room and what
    /// the run gave up on don't, so the goal doesn't change while the day's dailies get done.
    /// </summary>
    public static bool CanTake(TribeToday day, PlayerClasses who, bool leaveOutSay = false)
        => day.Offers.Any(o => QuestPolicy.Judge(o.Quest, new PolicyOptions(leaveOutSay)).RunCanDo && TribeClasses.ClassNeed(o.Quest, who) == null);
}
