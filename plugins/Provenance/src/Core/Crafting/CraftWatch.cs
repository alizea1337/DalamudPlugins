using System;
using System.Collections.Generic;
using System.Linq;

namespace Provenance.Core.Crafting;

/// <summary>
/// What the game shows at one step of a synthesis: the Synthesis window's progress, quality, durability and condition
/// (the game's condition code: 0 Normal, 1 Good, 2 Excellent, 3 Poor, anything else is another condition), the
/// crafter's CP, and the crafting buffs on the player.
/// </summary>
public readonly record struct CraftReading(int Progress, int Quality, int Durability, int Cp, int ConditionCode,
    int InnerQuiet, int Innovation, int Veneration, int GreatStrides, int WasteNot, int Manipulation, int MuscleMemory,
    bool HeartAndSoul, bool TrainedPerfection)
{
    public override string ToString()
        => $"progress {Progress}, quality {Quality}, durability {Durability}, CP {Cp}, condition {ConditionCode}, Inner Quiet {InnerQuiet}, Innovation {Innovation}, "
           + $"Veneration {Veneration}, Great Strides {GreatStrides}, Waste Not {WasteNot}, Manipulation {Manipulation}, Muscle Memory {MuscleMemory}"
           + (HeartAndSoul ? ", Heart and Soul" : "") + (TrainedPerfection ? ", Trained Perfection" : "");
}

/// <summary>
/// Follows one real synthesis with the simulator: predicts every step, checks the game's numbers against the prediction,
/// and picks the <see cref="ConditionReaction"/> edits on Good and Excellent steps. Once anything differs from the
/// prediction (or the simulator can't follow an action), it stops for the rest of the craft.
/// </summary>
public sealed class CraftWatch
{
    private readonly SimSettings settings;
    private readonly int initialQuality;
    private readonly int recipeMaxQuality;
    private SimState predicted;
    /// <summary>The condition of the step the last action was used on; null before the first action.</summary>
    private CraftCondition? lastCondition;
    private bool lastUsedStep;

    /// <param name="initialQuality">The quality the HQ ingredients start the craft with (raphael-cli's <c>initial_quality</c>).</param>
    /// <param name="recipeMaxQuality">The recipe's most quality (the game caps quality there).</param>
    public CraftWatch(SimSettings settings, int initialQuality, int recipeMaxQuality)
    {
        this.settings = settings;
        this.initialQuality = initialQuality;
        this.recipeMaxQuality = recipeMaxQuality;
        predicted = SimState.Start(settings);
    }

    /// <summary>Still following the craft (nothing has differed yet).</summary>
    public bool Active { get; private set; } = true;
    /// <summary>Why it stopped (null while active).</summary>
    public string? Stopped { get; private set; }
    public int GoodReactions { get; private set; }
    public int ExcellentReactions { get; private set; }
    public int Reactions => GoodReactions + ExcellentReactions;
    public SimState Predicted => predicted;

    /// <summary>The quality the game should show now, as the simulator has it.</summary>
    public int PredictedQuality => Math.Min(recipeMaxQuality, initialQuality + predicted.Quality);

    public void Stop(string why)
    {
        if (!Active) return;
        Active = false;
        Stopped = why;
    }

    /// <summary>
    /// Sets up a watch for a rotation, or says why not: an action the simulator doesn't know, or the rotation under Normal
    /// steps doesn't end where Raphael said (<paramref name="plannedQuality"/>, counting the starting quality).
    /// </summary>
    public static CraftWatch? For(SimSettings settings, int initialQuality, int recipeMaxQuality, IReadOnlyList<uint> rotation, int plannedQuality, out string why)
    {
        if (RaphaelSim.FromIds(rotation) is not { } plan)
        {
            why = "the rotation has an action the simulator doesn't know";
            return null;
        }
        if (RaphaelSim.Play(settings, SimState.Start(settings), plan) is not { } end)
        {
            why = "the simulator can't play the rotation as planned";
            return null;
        }
        if (end.Progress < settings.MaxProgress)
        {
            why = $"the simulator doesn't finish the rotation (progress {end.Progress} of {settings.MaxProgress})";
            return null;
        }
        if (plannedQuality >= 0 && initialQuality + end.Quality != plannedQuality)
        {
            why = $"the simulator ends at quality {initialQuality + end.Quality}, Raphael said {plannedQuality}";
            return null;
        }
        why = "";
        return new CraftWatch(settings, initialQuality, recipeMaxQuality);
    }

    /// <summary>
    /// Compares what the game shows with the prediction. Null when they agree (<paramref name="condition"/> is then this
    /// step's condition); otherwise what differs.
    /// </summary>
    public string? Compare(CraftReading r, out CraftCondition condition)
    {
        condition = CraftCondition.Normal;
        if (r.ConditionCode is < 0 or > 3) return $"condition code {r.ConditionCode} isn't Normal, Good, Excellent or Poor";
        condition = (CraftCondition)r.ConditionCode;
        var expected = lastCondition is not { } last ? (CraftCondition?)CraftCondition.Normal : lastUsedStep ? null : last;
        if (expected is { } e && condition != e) return $"condition {condition}, expected {e}";
        if (expected == null && !CraftConditions.CanFollow(lastCondition!.Value, condition)) return $"condition {condition} after {lastCondition}";
        var p = predicted;
        var diffs = new List<string>();
        void Check(string what, int game, int sim)
        {
            if (game != sim) diffs.Add($"{what} {game} (predicted {sim})");
        }
        Check("progress", r.Progress, p.Progress);
        Check("quality", r.Quality, PredictedQuality);
        Check("durability", r.Durability, p.Durability);
        Check("CP", r.Cp, p.Cp);
        Check("Inner Quiet", r.InnerQuiet, p.InnerQuiet);
        Check("Innovation", r.Innovation, p.Innovation);
        Check("Veneration", r.Veneration, p.Veneration);
        Check("Great Strides", r.GreatStrides, p.GreatStrides);
        Check("Waste Not", r.WasteNot, p.WasteNot);
        Check("Manipulation", r.Manipulation, p.Manipulation);
        Check("Muscle Memory", r.MuscleMemory, p.MuscleMemory);
        if (r.HeartAndSoul != p.HeartAndSoulActive) diffs.Add($"Heart and Soul {(r.HeartAndSoul ? "on" : "off")}");
        if (r.TrainedPerfection != p.TrainedPerfectionActive) diffs.Add($"Trained Perfection {(r.TrainedPerfection ? "on" : "off")}");
        return diffs.Count == 0 ? null : string.Join(", ", diffs);
    }

    /// <summary>
    /// The edit for this step of the rotation (<paramref name="rest"/>: Raphael's ids from this step on), or null to keep
    /// it. Counts the reaction.
    /// </summary>
    public (List<uint> Actions, ReactionChoice Choice)? React(CraftCondition condition, IReadOnlyList<uint> rest)
    {
        if (!Active || !ConditionReaction.IsSpecial(condition) || RaphaelSim.FromIds(rest) is not { } plan) return null;
        if (ConditionReaction.Choose(settings, predicted, plan, condition) is not { } choice) return null;
        if (condition == CraftCondition.Good) GoodReactions++;
        else ExcellentReactions++;
        return (choice.Rest.Select(RaphaelSim.Id).ToList(), choice);
    }

    /// <summary>The quality an edit's numbers mean on screen (counting the starting quality, capped at the recipe's most).</summary>
    public int Shown(int simQuality) => simQuality < 0 ? simQuality : Math.Min(recipeMaxQuality, initialQuality + simQuality);

    /// <summary>
    /// An action went to the game on a step with this condition: the prediction moves on. Stops watching when the simulator
    /// can't follow it.
    /// </summary>
    public void Used(uint raphaelId, CraftCondition condition)
    {
        if (!Active) return;
        if (RaphaelSim.FromId(raphaelId) is not { } a)
        {
            Stop($"action {raphaelId} isn't one the simulator knows");
            return;
        }
        if (!RaphaelSim.TryUse(predicted, a, condition, settings, out var next))
        {
            Stop($"the simulator says {ConditionReaction.Name(a)} can't be used here");
            return;
        }
        predicted = next;
        lastCondition = condition;
        lastUsedStep = RaphaelSim.UsesStep(a);
    }
}
