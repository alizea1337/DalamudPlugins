using System;
using System.Collections.Generic;
using System.Linq;

namespace Provenance.Core.Cosmic;

/// <summary>A mission's result: bronze is finishing at all.</summary>
public enum Tier : byte
{
    None = 0,
    Bronze = 1,
    Silver = 2,
    Gold = 3,
}

/// <summary>Whether a judgement comes from what runs of the mission scored, or from the rotation's quality alone.</summary>
public enum Basis : byte
{
    Estimated,
    Learned,
}

/// <summary>
/// What runs of one mission came to so far: tries per tier, what they paid, how long they took, and the quality of
/// the final craft that did or didn't reach gold. Kept in the configuration; the picker trusts it over estimates.
/// </summary>
[Serializable]
public sealed class MissionRecord
{
    /// <summary>Finished runs per tier (index = <see cref="Tier"/>).</summary>
    public int[] Runs { get; set; } = new int[4];
    /// <summary>Cosmocredits those runs paid, per tier.</summary>
    public int[] Credits { get; set; } = new int[4];
    public double Seconds { get; set; }

    /// <summary>Lowest final-craft quality that reached gold (0: none seen), and the highest that didn't.</summary>
    public int MinGoldQuality { get; set; }
    public int MaxBelowGoldQuality { get; set; }
    /// <summary>The same for silver.</summary>
    public int MinSilverQuality { get; set; }
    public int MaxBelowSilverQuality { get; set; }

    public int Total => Runs.Sum();
    public double AverageSeconds => Total == 0 ? 0 : Seconds / Total;

    /// <summary>Average Cosmocredits of the runs that ended in <paramref name="tier"/>, or null with none.</summary>
    public double? CreditsAt(Tier tier) => Runs[(int)tier] == 0 ? null : (double)Credits[(int)tier] / Runs[(int)tier];

    /// <summary>Notes a finished run: the tier, what it paid, how long it took and its final craft's quality (0: no craft).</summary>
    public void Add(Tier tier, int credits, double seconds, int quality)
    {
        if (tier == Tier.None) return;
        Runs[(int)tier]++;
        Credits[(int)tier] += credits;
        Seconds += seconds;
        if (quality <= 0) return;
        if (tier == Tier.Gold) MinGoldQuality = MinGoldQuality == 0 ? quality : Math.Min(MinGoldQuality, quality);
        else MaxBelowGoldQuality = Math.Max(MaxBelowGoldQuality, quality);
        if (tier >= Tier.Silver) MinSilverQuality = MinSilverQuality == 0 ? quality : Math.Min(MinSilverQuality, quality);
        else MaxBelowSilverQuality = Math.Max(MaxBelowSilverQuality, quality);
    }
}

/// <param name="Tier">What the mission comes to with this gear, reliably (no lucky procs); None when it can't be finished.</param>
/// <param name="Reason">Why, for the plan: "best rotation reaches 2,900 of 3,400 quality".</param>
/// <param name="Seconds">How long a run of it takes.</param>
public sealed record Judgement(Tier Tier, Basis Basis, string Reason, double Seconds);

/// <param name="CreditsPerHour">Cosmocredits per hour at <see cref="Judgement.Tier"/>.</param>
public sealed record MissionChoice(CosmicMission Mission, Judgement Judgement, double Credits, double CreditsPerHour);

public sealed record SkippedMission(CosmicMission Mission, string Reason);

/// <param name="Choices">Best first: all reliably-gold missions, or the silver ones when none reaches gold.</param>
/// <param name="SilverOnly">No mission reaches gold with this gear, so the choices are silver.</param>
public sealed record MissionRanking(IReadOnlyList<MissionChoice> Choices, IReadOnlyList<SkippedMission> Skipped, bool SilverOnly)
{
    public MissionChoice? Best => Choices.Count > 0 ? Choices[0] : null;
}

/// <summary>Judging and ranking stellar missions: pure, so it's tested without the game.</summary>
public static class CosmicPicker
{
    /// <summary>
    /// What a tier pays against the mission's base reward until runs of it say otherwise (community figures:
    /// silver four times, gold five times).
    /// </summary>
    public static double Multiplier(Tier tier) => tier switch
    {
        Tier.Gold => 5,
        Tier.Silver => 4,
        Tier.Bronze => 1,
        _ => 0,
    };

    /// <summary>
    /// Whether a mission class is open to a class: its level, and missions of the class below (C needs 3 different D
    /// missions done, B 4 different C, A and its EX ranks 5 different B with 3 of them gold).
    /// </summary>
    public static bool RankOpen(MissionRank rank, int level, Func<MissionRank, int> completed, Func<MissionRank, int> golded) => rank switch
    {
        MissionRank.D => level >= 10,
        MissionRank.C => level >= 50 && completed(MissionRank.D) >= 3,
        MissionRank.B => level >= 90 && completed(MissionRank.C) >= 4,
        _ => level >= 100 && completed(MissionRank.B) >= 5 && golded(MissionRank.B) >= 3,
    };

    /// <summary>"Class B opens at level 90 and after 4 different class C missions".</summary>
    public static string RankRequirement(MissionRank rank) => rank switch
    {
        MissionRank.D => "Class D opens at level 10",
        MissionRank.C => "Class C opens at level 50, after 3 different class D missions",
        MissionRank.B => "Class B opens at level 90, after 4 different class C missions",
        _ => "Class A opens at level 100, after 5 different class B missions, 3 of them gold",
    };

    /// <summary>
    /// The tier a craft mission comes to when its final craft reaches <paramref name="quality"/>: from what runs of it
    /// scored when there are any that settle it, else from the quality alone (all of it for gold, 70% for silver).
    /// </summary>
    public static (Tier Tier, Basis Basis) PredictCraft(int quality, int maxQuality, MissionRecord? record)
    {
        var estimate = Estimate(quality, maxQuality);
        if (record != null)
        {
            if (record.MinGoldQuality > 0 && quality >= record.MinGoldQuality) return (Tier.Gold, Basis.Learned);
            // At least silver from the lowest quality that reached it; gold only by the estimate, above all that fell short of it.
            if (record.MinSilverQuality > 0 && quality >= record.MinSilverQuality)
                return estimate == Tier.Gold && quality > record.MaxBelowGoldQuality ? (Tier.Gold, Basis.Estimated) : (Tier.Silver, Basis.Learned);
            if (record.MaxBelowSilverQuality > 0 && quality <= record.MaxBelowSilverQuality) return (Tier.Bronze, Basis.Learned);
        }
        return (estimate, Basis.Estimated);
    }

    /// <summary>The tier from the quality alone: all of it for gold, 70% for silver (nothing to reach: gold).</summary>
    private static Tier Estimate(int quality, int maxQuality)
        => maxQuality <= 0 || quality >= maxQuality ? Tier.Gold : quality * 10 >= maxQuality * 7 ? Tier.Silver : Tier.Bronze;

    /// <summary>Cosmocredits a run at <paramref name="tier"/> pays: what runs at that tier paid, else the base times the tier's multiplier.</summary>
    public static double Credits(CosmicMission mission, Tier tier, MissionRecord? record)
        => record?.CreditsAt(tier) ?? mission.Reward.Cosmocredits * Multiplier(tier);

    /// <summary>
    /// Ranks missions for a class by Cosmocredits per hour: every one that reliably reaches gold, or, when none does,
    /// every one that reaches silver. The rest are skipped, each with its reason.
    /// </summary>
    /// <param name="judge">The judgement for a mission with the class's gear, or null while it's still being worked out.</param>
    public static MissionRanking Rank(IEnumerable<CosmicMission> missions, Func<CosmicMission, Judgement?> judge, Func<uint, MissionRecord?> record)
    {
        var judged = new List<MissionChoice>();
        var skipped = new List<SkippedMission>();
        foreach (var m in missions)
        {
            var j = judge(m);
            if (j == null) { skipped.Add(new SkippedMission(m, "Still working out the best rotation")); continue; }
            if (j.Tier == Tier.None) { skipped.Add(new SkippedMission(m, j.Reason)); continue; }
            var rec = record(m.Id);
            var seconds = rec is { Total: > 0 } ? rec.AverageSeconds : j.Seconds;
            var credits = Credits(m, j.Tier, rec);
            judged.Add(new MissionChoice(m, j, credits, seconds <= 0 ? 0 : credits * 3600 / seconds));
        }
        var gold = judged.Where(c => c.Judgement.Tier == Tier.Gold).ToList();
        var pool = gold.Count > 0 ? gold : judged.Where(c => c.Judgement.Tier == Tier.Silver).ToList();
        foreach (var c in judged.Except(pool))
            skipped.Add(new SkippedMission(c.Mission, c.Judgement.Tier == Tier.Silver
                ? "Only silver with this gear: " + c.Judgement.Reason
                : "Only bronze with this gear: " + c.Judgement.Reason));
        return new MissionRanking(pool.OrderByDescending(c => c.CreditsPerHour).ThenByDescending(c => c.Credits).ToList(), skipped,
            gold.Count == 0 && pool.Count > 0);
    }

    /// <summary>How many crafts of each step make what the objective asks for, in crafting order (steps in between first).</summary>
    public static int[] Crafts(CosmicMission mission)
    {
        var crafts = new int[mission.Steps.Count];
        var need = new Dictionary<uint, int>();
        foreach (var t in mission.Targets) need[t.ItemId] = need.GetValueOrDefault(t.ItemId) + Math.Max(1, t.Count);
        // Last step first: what it needs of an earlier step's result adds to that step's count.
        for (var i = crafts.Length - 1; i >= 0; i--)
        {
            var step = mission.Steps[i];
            var wanted = need.GetValueOrDefault(step.ItemId);
            if (wanted <= 0) continue;
            crafts[i] = (wanted + Math.Max(1, step.Yield) - 1) / Math.Max(1, step.Yield);
            need[step.IngredientId] = need.GetValueOrDefault(step.IngredientId) + crafts[i] * step.IngredientAmount;
        }
        return crafts;
    }

    /// <summary>Rough length of a craft mission: every craft's steps at 3 s, 4 s around each craft, 20 s to take it and report it.</summary>
    public static double CraftSeconds(CosmicMission mission, IReadOnlyList<int> stepsPerCraft)
    {
        var crafts = Crafts(mission);
        double seconds = 20;
        for (var i = 0; i < crafts.Length; i++)
            seconds += crafts[i] * ((i < stepsPerCraft.Count ? stepsPerCraft[i] : 20) * 3.0 + 4);
        return seconds;
    }
}
