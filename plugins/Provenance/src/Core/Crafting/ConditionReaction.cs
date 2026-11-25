using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Provenance.Core.Crafting;

/// <summary>
/// How conditions follow each other on a normal (non-expert) recipe, after Artisan's simulator
/// (<c>Simulator.GetNextCondition</c>, <c>CraftState.NormalCraftConditionProbabilities</c>; Artisan by Puni.sh, BSD 3-Clause).
/// The first step is Normal; after Normal comes Good (25 %, 20 % below level 63), Excellent (4 %) or Normal; Good is
/// followed by Normal, Excellent by Poor, Poor by Normal.
/// </summary>
public static class CraftConditions
{
    public const double ExcellentChance = 0.04;

    public static double GoodChance(int crafterLevel) => crafterLevel >= 63 ? 0.25 : 0.2;

    /// <summary>The next step's condition, for a roll in [0, 1).</summary>
    public static CraftCondition Next(CraftCondition current, double roll, int crafterLevel) => current switch
    {
        CraftCondition.Good or CraftCondition.Poor => CraftCondition.Normal,
        CraftCondition.Excellent => CraftCondition.Poor,
        _ => roll < GoodChance(crafterLevel) ? CraftCondition.Good
            : roll < GoodChance(crafterLevel) + ExcellentChance ? CraftCondition.Excellent
            : CraftCondition.Normal,
    };

    /// <summary>Whether a step can have condition <paramref name="next"/> after one with <paramref name="previous"/>.</summary>
    public static bool CanFollow(CraftCondition previous, CraftCondition next) => previous switch
    {
        CraftCondition.Good or CraftCondition.Poor => next == CraftCondition.Normal,
        CraftCondition.Excellent => next == CraftCondition.Poor,
        _ => next is CraftCondition.Normal or CraftCondition.Good or CraftCondition.Excellent,
    };
}

/// <summary>An edit to the rest of a rotation on a Good or Excellent step.</summary>
/// <param name="Rest">The rotation from this step on, with the edit.</param>
/// <param name="Edit">What changed, for the debug log.</param>
/// <param name="KeptQuality">The quality the rotation as planned reaches from here (this step's condition, then Normal steps); -1 when it wouldn't finish.</param>
/// <param name="ChosenQuality">The same for the edited rotation.</param>
public sealed record ReactionChoice(IReadOnlyList<SimAction> Rest, string Edit, int KeptQuality, int ChosenQuality);

/// <summary>
/// The cheap reaction to Good and Excellent steps: the planned rotation stays, and on such a step one small edit is tried
/// (Precise Touch, Tricks of the Trade or Intensive Synthesis in place of or before the planned action, or one of the next
/// eight planned actions pulled forward). Each is simulated under the step's condition with Normal steps after it; the
/// best final quality that still finishes wins, ties keep the plan. Quality above what the rotation aims for (the
/// target of a collectable, the recipe's most) counts for nothing.
/// </summary>
public static class ConditionReaction
{
    /// <summary>How many planned actions ahead one can be pulled forward from.</summary>
    public const int PullAhead = 8;

    /// <summary>Actions that only work on a Good or Excellent step (or with Heart and Soul): tried in place of or before the planned one.</summary>
    public static readonly SimAction[] Additions = { SimAction.PreciseTouch, SimAction.TricksOfTheTrade, SimAction.IntensiveSynthesis };

    public static bool IsSpecial(CraftCondition c) => c is CraftCondition.Good or CraftCondition.Excellent;

    /// <summary>
    /// The quality a rotation reaches from <paramref name="st"/> with Normal steps (capped at what it aims for), or -1 when
    /// an action can't be used or it doesn't finish (also when it finishes before its last action).
    /// </summary>
    public static int FinalQuality(SimSettings s, SimState st, IReadOnlyList<SimAction> plan, int from = 0)
    {
        for (var i = from; i < plan.Count; i++)
            if (!RaphaelSim.TryUse(st, plan[i], CraftCondition.Normal, s, out st)) return -1;
        return st.Progress >= s.MaxProgress ? Math.Min(st.Quality, s.MaxQuality) : -1;
    }

    /// <summary>The quality a candidate reaches: its first action under <paramref name="c"/>, the rest under Normal; -2 when its first action can't be used here.</summary>
    private static int Evaluate(SimSettings s, in SimState st, IReadOnlyList<SimAction> candidate, CraftCondition c)
    {
        if (candidate.Count == 0 || !RaphaelSim.UsesStep(candidate[0]) || !RaphaelSim.TryUse(st, candidate[0], c, s, out var n)) return -2;
        if (n.IsFinal(s)) return n.Progress >= s.MaxProgress ? Math.Min(n.Quality, s.MaxQuality) : -1;
        return FinalQuality(s, n, candidate, 1);
    }

    private static IEnumerable<(List<SimAction> Actions, string Edit)> Candidates(IReadOnlyList<SimAction> rest)
    {
        foreach (var x in Additions)
        {
            var replace = new List<SimAction>(rest.Count) { x };
            replace.AddRange(rest.Skip(1));
            yield return (replace, $"{Name(x)} in place of {Name(rest[0])}");
            var insert = new List<SimAction>(rest.Count + 1) { x };
            insert.AddRange(rest);
            yield return (insert, $"{Name(x)} before {Name(rest[0])}");
        }
        for (var j = 1; j <= Math.Min(PullAhead, rest.Count - 1); j++)
        {
            var pulled = new List<SimAction>(rest.Count) { rest[j] };
            for (var i = 0; i < rest.Count; i++)
                if (i != j) pulled.Add(rest[i]);
            yield return (pulled, $"{Name(rest[j])} pulled forward from {j + 1} actions ahead, before {Name(rest[0])}");
        }
    }

    /// <summary>
    /// The edit for this step, or null to keep the plan: not a Good or Excellent step, the next planned action doesn't take a
    /// step (Heart and Soul, Quick Innovation), or nothing does better than the plan.
    /// </summary>
    public static ReactionChoice? Choose(SimSettings s, SimState st, IReadOnlyList<SimAction> rest, CraftCondition c)
    {
        if (!IsSpecial(c) || rest.Count == 0 || !RaphaelSim.UsesStep(rest[0])) return null;
        var kept = Evaluate(s, st, rest, c);
        var bestQuality = kept;
        (List<SimAction> Actions, string Edit)? best = null;
        foreach (var cand in Candidates(rest))
        {
            var q = Evaluate(s, st, cand.Actions, c);
            // Only an edit that still finishes the item.
            if (q > bestQuality && q >= 0)
            {
                best = cand;
                bestQuality = q;
            }
        }
        return best is { } b ? new ReactionChoice(b.Actions, b.Edit, kept, bestQuality) : null;
    }

    /// <summary>"Precise Touch" for <see cref="SimAction.PreciseTouch"/>.</summary>
    public static string Name(SimAction a) => a switch
    {
        SimAction.ByregotsBlessing => "Byregot's Blessing",
        SimAction.TricksOfTheTrade => "Tricks of the Trade",
        SimAction.HeartAndSoul => "Heart and Soul",
        SimAction.WasteNot2 => "Waste Not II",
        _ => Regex.Replace(a.ToString(), "(?<=[a-z])(?=[A-Z])", " "),
    };

    /// <summary>
    /// The run log's line for a craft that reacted: "Reacted to 2 Good steps and 1 Excellent step: quality 16,820 instead of
    /// the planned 14,286." Null when it didn't react.
    /// </summary>
    public static string? Summary(int good, int excellent, int finalQuality, int plannedQuality)
    {
        if (good + excellent == 0) return null;
        static string Steps(int n, string what) => $"{n} {what} {(n == 1 ? "step" : "steps")}";
        var parts = new List<string>();
        if (good > 0) parts.Add(Steps(good, "Good"));
        if (excellent > 0) parts.Add(Steps(excellent, "Excellent"));
        var line = $"Reacted to {string.Join(" and ", parts)}";
        if (finalQuality < 0) return line + ".";
        return plannedQuality >= 0
            ? $"{line}: quality {finalQuality:N0} instead of the planned {plannedQuality:N0}."
            : $"{line}: quality {finalQuality:N0}.";
    }
}

/// <summary>What a quick Monte Carlo of many crafts gives for one rotation.</summary>
/// <param name="HqPercent">The average chance in percent that a craft comes out HQ.</param>
/// <param name="MeanQuality">The average final quality.</param>
public sealed record HqEstimate(double HqPercent, double MeanQuality, int Crafts, bool Reacting);

/// <summary>
/// A realistic HQ chance for a rotation: many crafts with random conditions (<see cref="CraftConditions"/>), playing the
/// rotation as planned or with the <see cref="ConditionReaction"/> edits.
/// </summary>
public static class HqChance
{
    public const int DefaultCrafts = 2000;

    /// <param name="initialQuality">The quality the HQ ingredients start the craft with.</param>
    /// <param name="recipeMaxQuality">The recipe's most quality (the HQ table's 100 %).</param>
    public static HqEstimate Estimate(SimSettings s, int initialQuality, int recipeMaxQuality, IReadOnlyList<SimAction> plan, bool react,
        int crafts = DefaultCrafts, int seed = 12345)
    {
        var rng = new Random(seed);
        double hq = 0, quality = 0;
        for (var i = 0; i < crafts; i++)
        {
            var st = react ? PlayReacting(s, plan, rng) : PlayFixed(s, plan, rng);
            var final = Math.Min(recipeMaxQuality, initialQuality + Math.Min(st.Quality, s.MaxQuality));
            hq += RaphaelSim.HqPercent(final, recipeMaxQuality);
            quality += final;
        }
        return new HqEstimate(crafts == 0 ? 0 : hq / crafts, crafts == 0 ? 0 : quality / crafts, crafts, react);
    }

    /// <summary>The rotation as planned under random conditions (an action that can't be used is skipped).</summary>
    public static SimState PlayFixed(SimSettings s, IReadOnlyList<SimAction> plan, Random rng)
        => PlayFixed(s, plan, c => CraftConditions.Next(c, rng.NextDouble(), s.JobLevel));

    /// <summary>The rotation as planned; <paramref name="next"/> gives the condition after each step.</summary>
    public static SimState PlayFixed(SimSettings s, IReadOnlyList<SimAction> plan, Func<CraftCondition, CraftCondition> next)
    {
        var st = SimState.Start(s);
        var c = CraftCondition.Normal;
        foreach (var a in plan)
        {
            if (!RaphaelSim.TryUse(st, a, c, s, out var n)) continue;
            st = n;
            if (RaphaelSim.UsesStep(a)) c = next(c);
        }
        return st;
    }

    /// <summary>The rotation with the reactions to Good and Excellent steps under random conditions.</summary>
    public static SimState PlayReacting(SimSettings s, IReadOnlyList<SimAction> plan, Random rng)
        => PlayReacting(s, plan, c => CraftConditions.Next(c, rng.NextDouble(), s.JobLevel), out _);

    /// <summary>The rotation with the reactions; <paramref name="next"/> gives the condition after each step.</summary>
    public static SimState PlayReacting(SimSettings s, IReadOnlyList<SimAction> plan, Func<CraftCondition, CraftCondition> next, out int edits)
    {
        var st = SimState.Start(s);
        var c = CraftCondition.Normal;
        IReadOnlyList<SimAction> rest = plan;
        var at = 0;
        edits = 0;
        while (at < rest.Count && !st.IsFinal(s))
        {
            if (ConditionReaction.IsSpecial(c) && ConditionReaction.Choose(s, st, Tail(rest, at), c) is { } choice)
            {
                rest = choice.Rest;
                at = 0;
                edits++;
            }
            var a = rest[at++];
            if (!RaphaelSim.TryUse(st, a, c, s, out var n)) continue;
            st = n;
            if (RaphaelSim.UsesStep(a)) c = next(c);
        }
        return st;
    }

    private static IReadOnlyList<SimAction> Tail(IReadOnlyList<SimAction> list, int from)
        => from == 0 ? list : list.Skip(from).ToList();
}
