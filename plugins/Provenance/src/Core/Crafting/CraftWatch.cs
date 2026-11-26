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
    /// <summary>The last action used (null before the first).</summary>
    private SimAction? lastAction;

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
    /// <summary>Of the reactions, the ones where Raphael worked out the rest of the rotation afresh (<see cref="Replanning"/>).</summary>
    public int Replans { get; private set; }
    public SimState Predicted => predicted;
    public SimSettings Settings => settings;

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
        if (lastCondition is not { } last)
        {
            if (condition != CraftCondition.Normal) return $"condition {condition} on the first step";
        }
        // Heart and Soul keeps the step's condition. Raphael counts Quick Innovation as no step too, but the game may roll a
        // new condition after it (Artisan's simulator does): either is fine there.
        else if (lastAction == SimAction.HeartAndSoul ? condition != last
                 : lastAction == SimAction.QuickInnovation ? condition != last && !CraftConditions.CanFollow(last, condition)
                 : !CraftConditions.CanFollow(last, condition))
            return $"condition {condition} after {last} ({(lastAction is { } la ? ConditionReaction.Name(la) : "")})";
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
    /// Whether this step is one to react to: still watching, a Good or Excellent step, and the rotation's next action takes a
    /// step (not Heart and Soul or Quick Innovation, as with <see cref="ConditionReaction.Choose"/>).
    /// </summary>
    public bool CanReact(CraftCondition condition, IReadOnlyList<uint> rest)
        => Active && ConditionReaction.IsSpecial(condition) && rest.Count > 0 && RaphaelSim.FromId(rest[0]) is { } first && RaphaelSim.UsesStep(first)
           && RaphaelSim.FromIds(rest) != null;

    /// <summary>
    /// The edit for this step of the rotation (<paramref name="rest"/>: Raphael's ids from this step on), or null to keep
    /// it. Counts the reaction.
    /// </summary>
    public (List<uint> Actions, ReactionChoice Choice)? React(CraftCondition condition, IReadOnlyList<uint> rest)
    {
        if (Cheap(condition, rest) is not { } choice) return null;
        Count(condition);
        return (choice.Rest.Select(RaphaelSim.Id).ToList(), choice);
    }

    /// <summary>
    /// The rotation as it stands already ends at the most quality it aims for (the recipe's most, or a collectable's target)
    /// from this step: nothing to re-plan for.
    /// </summary>
    public bool RestReachesCap(CraftCondition condition, IReadOnlyList<uint> rest)
        => RaphaelSim.FromIds(rest) is { } plan && Replanning.Quality(settings, predicted, plan, condition) >= settings.MaxQuality;

    /// <summary>What the cheap reaction would do on this step (nothing counted).</summary>
    private ReactionChoice? Cheap(CraftCondition condition, IReadOnlyList<uint> rest)
        => !Active || !ConditionReaction.IsSpecial(condition) || RaphaelSim.FromIds(rest) is not { } plan ? null
            : ConditionReaction.Choose(settings, predicted, plan, condition);

    private void Count(CraftCondition condition)
    {
        if (condition == CraftCondition.Good) GoodReactions++;
        else ExcellentReactions++;
    }

    /// <summary>
    /// Raphael's new rest of the rotation for this step (<paramref name="replanned"/>, worked out from <see cref="Predicted"/>
    /// under <paramref name="condition"/>), checked with the simulator against the cheap reaction (<see cref="Replanning.Accept"/>).
    /// Null, with why, when it isn't taken; the cheap reaction then takes the step. Counts the reaction when the rotation
    /// changes.
    /// </summary>
    /// <param name="quality">The quality the new rest reaches, and <paramref name="beaten"/> what the cheap reaction would (simulator numbers).</param>
    public List<uint>? Replan(CraftCondition condition, IReadOnlyList<uint> rest, IReadOnlyList<uint> replanned, out int quality, out int beaten, out string why)
    {
        quality = beaten = -1;
        why = "";
        if (!Active || RaphaelSim.FromIds(rest) is not { } plan)
        {
            why = "not watching any more";
            return null;
        }
        if (RaphaelSim.FromIds(replanned) is not { } actions)
        {
            why = "it has an action the simulator doesn't know";
            return null;
        }
        quality = Replanning.Accept(settings, predicted, plan, actions, condition, Cheap(condition, rest), out beaten);
        if (quality < 0)
        {
            quality = Replanning.Quality(settings, predicted, actions, condition);
            why = quality < 0 ? "the simulator says it doesn't finish" : "the simulator says the cheap reaction does as well, in as few actions";
            return null;
        }
        if (!replanned.SequenceEqual(rest))
        {
            Count(condition);
            Replans++;
        }
        return replanned.ToList();
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
        lastAction = a;
    }
}
