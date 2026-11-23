using System;
using System.Collections.Generic;
using System.Linq;

namespace Provenance.Core.Tribes;

/// <summary>
/// A society's reputation ladder, indexed by rank (index 0 unused): the reputation that fills each rank's bar, what a
/// daily gives at that rank, and whether the rank has dailies of its own (which the giver offers on the day you reach
/// it, on top of the day's three).
/// </summary>
/// <param name="Required"><c>BeastReputationRank.RequiredReputation</c> per rank (0 at the top).</param>
/// <param name="RepPerDaily">The best reputation a daily offered at that rank gives (Ixal's lower givers give less).</param>
/// <param name="OwnDailies">Some dailies need exactly that rank.</param>
public sealed record TribeLadder(int MaxRank, IReadOnlyList<int> Required, IReadOnlyList<int> RepPerDaily, IReadOnlyList<bool> OwnDailies)
{
    public int RequiredAt(int rank) => rank > 0 && rank < Required.Count ? Required[rank] : 0;
    public int PerDailyAt(int rank) => rank > 0 && rank < RepPerDaily.Count ? RepPerDaily[rank] : 0;
    public bool OwnDailiesAt(int rank) => rank > 0 && rank < OwnDailies.Count && OwnDailies[rank];

    /// <summary>The ladder of a society from its dailies (their rank and reputation) and the reputation each rank needs.</summary>
    /// <param name="required">Indexed by rank, as <c>BeastReputationRank</c> rows (index 0 unused).</param>
    public static TribeLadder From(int maxRank, IReadOnlyList<int> required, IEnumerable<(int Rank, int Reputation)> dailies)
    {
        var list = dailies.ToList();
        var per = new int[maxRank + 1];
        var own = new bool[maxRank + 1];
        for (var r = 1; r <= maxRank; r++)
        {
            per[r] = list.Where(d => d.Rank <= r).Select(d => d.Reputation).DefaultIfEmpty(0).Max();
            own[r] = list.Any(d => d.Rank == r);
        }
        var req = new int[maxRank + 1];
        for (var r = 1; r <= maxRank && r < required.Count; r++) req[r] = r < maxRank ? required[r] : 0;
        return new TribeLadder(maxRank, req, per, own);
    }
}

/// <summary>Where the player stands with a society now.</summary>
/// <param name="Rank">The rank (1 Neutral … 8 Bloodsworn; 0 while locked).</param>
/// <param name="Value">Reputation within the rank.</param>
/// <param name="DoneToday">The society's dailies taken today (accepted or done; they count against the day's cap).</param>
/// <param name="LeftToday">Today's offers still to take (0 once the day's are taken, or no allowances are left).</param>
public readonly record struct TribeStanding(int Rank, int Value, int DoneToday, int LeftToday);

/// <summary>How far away a rank is.</summary>
/// <param name="Dailies">Dailies to do (each counts against the allowances).</param>
/// <param name="Days">Days with dailies in them, today included when today still has some.</param>
/// <param name="LastDay">The day the last daily falls on: 0 today, 1 tomorrow… (-1 when no daily is needed).</param>
/// <param name="RankQuests">Rank quests on the way (they cost no allowance).</param>
/// <param name="Known">False when the data has no reputation for a rank on the way (the numbers stop there).</param>
public readonly record struct RankEstimate(int Dailies, int Days, int LastDay, int RankQuests, bool Known = true)
{
    public static readonly RankEstimate Nothing = new(0, 0, -1, 0);
}

/// <summary>
/// Rank arithmetic for the Tribes page and the day's plan: whether the bar is full, and how many dailies and days a
/// rank or the top rank is away.
/// </summary>
/// <remarks>
/// Facts (allied-society research, game data and the wiki): a giver offers 3 dailies a day; the reputation of a daily
/// that would overfill the bar is lost; with the bar full no daily gives reputation until the rank quest is done; on
/// the day of a rank-up the giver offers 3 dailies of exactly the new rank (the day's cap becomes 6), and none when
/// the new rank has no dailies of its own. That gives the wiki's 31 days from Friendly to Bloodsworn without the
/// rank-up days and 28 with them. The estimate assumes each rank quest is done the moment the bar fills.
/// </remarks>
public static class TribeRanks
{
    /// <summary>Dailies a giver offers a day.</summary>
    public const int PerDay = DailyPick.PerGiver;

    /// <summary>Dailies a society takes on the day of a rank-up (the day's 3 and the new rank's 3).</summary>
    public const int RankUpDayCap = 2 * PerDay;

    public static bool AtTop(int rank, int maxRank) => rank >= maxRank;

    /// <summary>The reputation bar is full: no daily gives reputation until the rank quest is done.</summary>
    public static bool BarFull(int rank, int value, int needed, int maxRank)
        => rank > 0 && rank < maxRank && needed > 0 && value >= needed;

    /// <summary>Dailies that fill the bar from <paramref name="value"/> (0 when it's full, or nothing is known).</summary>
    public static int DailiesToFill(int value, int needed, int perDaily)
        => needed <= 0 || perDaily <= 0 ? 0 : (int)Math.Ceiling(Math.Max(0, needed - value) / (double)perDaily);

    /// <summary>The way to the next rank (nothing at the top or while locked).</summary>
    public static RankEstimate ToNext(TribeLadder ladder, TribeStanding from) => Estimate(ladder, from, from.Rank + 1);

    /// <summary>The way to the society's top rank (Bloodsworn for most).</summary>
    public static RankEstimate ToTop(TribeLadder ladder, TribeStanding from) => Estimate(ladder, from, ladder.MaxRank);

    /// <summary>
    /// The dailies, days and rank quests from where the player stands to <paramref name="toRank"/>, day by day: up to 3
    /// dailies a day, the reputation capped at each bar, and on a rank-up day the new rank's 3 dailies when it has any.
    /// </summary>
    public static RankEstimate Estimate(TribeLadder ladder, TribeStanding from, int toRank)
    {
        toRank = Math.Min(toRank, ladder.MaxRank);
        if (from.Rank <= 0 || from.Rank >= toRank) return RankEstimate.Nothing;
        var rank = from.Rank;
        var value = Math.Max(0, from.Value);
        var day = 0;
        var left = Math.Max(0, from.LeftToday);
        var doneToday = Math.Max(0, from.DoneToday);
        int dailies = 0, rankQuests = 0, days = 0, lastDay = -1;
        // The longest ladder is a few hundred dailies; the guard only stops a ladder the data got wrong.
        for (var guard = 0; guard < 10_000; guard++)
        {
            var needed = ladder.RequiredAt(rank);
            if (needed <= 0) return new RankEstimate(dailies, days, lastDay, rankQuests, false);
            if (value >= needed)
            {
                // The rank quest (no allowance), then the new rank's own dailies the same day.
                rankQuests++;
                rank++;
                value = 0;
                if (rank >= toRank) break;
                left = ladder.OwnDailiesAt(rank) ? Math.Min(PerDay, Math.Max(0, RankUpDayCap - doneToday)) : 0;
                continue;
            }
            var per = ladder.PerDailyAt(rank);
            if (per <= 0) return new RankEstimate(dailies, days, lastDay, rankQuests, false);
            if (left <= 0)
            {
                day++;
                left = PerDay;
                doneToday = 0;
            }
            value = Math.Min(needed, value + per);
            dailies++;
            left--;
            doneToday++;
            if (lastDay != day)
            {
                days++;
                lastDay = day;
            }
        }
        return new RankEstimate(dailies, days, lastDay, rankQuests);
    }
}
