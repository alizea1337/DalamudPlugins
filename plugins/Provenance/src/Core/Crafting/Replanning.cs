using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Provenance.Core.Crafting;

/// <summary>What raphael-cli's serve mode answered to <c>prepare</c>.</summary>
/// <param name="Ms">How long the precomputes took.</param>
/// <param name="Settings">The numbers it prepared for (null when they couldn't be read).</param>
public sealed record ServeReady(int Ms, int InitialQuality, int RecipeMaxQuality, SimSettings? Settings);

public enum ServeOutcome { Solved, NoSolution, Error }

/// <summary>What raphael-cli's serve mode answered to one <c>solve</c>.</summary>
/// <param name="Id">The request's id; 0 for an error that names none.</param>
/// <param name="Ms">How long the solve took (-1 when it didn't say).</param>
/// <param name="Actions">The rest of the rotation, as Raphael's action ids (empty unless solved).</param>
/// <param name="Error">The error line (empty unless an error).</param>
public sealed record ServeAnswer(long Id, ServeOutcome Outcome, int Ms, IReadOnlyList<uint> Actions, string Error);

/// <summary>
/// raphael-cli's serve mode (Provenance's patch of raphael-rs, see NOTICE.md): a line protocol on stdin and stdout. It
/// answers <c>serve 1</c> once started; <c>prepare &lt;solve's options&gt;</c> runs the solver's precomputes once
/// (<c>ready …</c> or <c>error …</c>); <c>solve &lt;id&gt; &lt;condition&gt; key=value…</c> asks for the rest of the
/// rotation from a mid-craft state (<c>solved &lt;id&gt; &lt;ms&gt; [ids]</c>, <c>nosolution &lt;id&gt; &lt;ms&gt;</c> or
/// <c>error &lt;id&gt; …</c>); <c>cancel &lt;id&gt;</c> interrupts a solve; <c>quit</c> ends it. No process handling here.
/// </summary>
public static class ServeProtocol
{
    public const string Hello = "serve 1";

    public static string Start(int threads) => $"serve --threads {Math.Max(1, threads)}";

    public static string Prepare(RaphaelRequest r) => "prepare " + RaphaelCli.Options(r);

    public static string Cancel(long id) => $"cancel {id}";

    public const string Quit = "quit";

    /// <summary>The <c>solve</c> line: the next step's condition and the simulator's state (quality without the starting quality).</summary>
    public static string Solve(long id, in SimState st, CraftCondition condition)
    {
        static int B(bool b) => b ? 1 : 0;
        var combo = st.Combo switch
        {
            SimCombo.SynthesisBegin => "begin",
            SimCombo.BasicTouch => "basic",
            SimCombo.StandardTouch => "standard",
            _ => "none",
        };
        return string.Create(CultureInfo.InvariantCulture,
            $"solve {id} {condition.ToString().ToLowerInvariant()} cp={st.Cp} durability={st.Durability} progress={st.Progress} quality={st.Quality} "
            + $"inner_quiet={st.InnerQuiet} waste_not={st.WasteNot} innovation={st.Innovation} veneration={st.Veneration} great_strides={st.GreatStrides} "
            + $"muscle_memory={st.MuscleMemory} manipulation={st.Manipulation} trained_perfection_available={B(st.TrainedPerfectionAvailable)} "
            + $"heart_and_soul_available={B(st.HeartAndSoulAvailable)} quick_innovation_available={B(st.QuickInnovationAvailable)} "
            + $"trained_perfection_active={B(st.TrainedPerfectionActive)} heart_and_soul_active={B(st.HeartAndSoulActive)} combo={combo}");
    }

    /// <summary>Reads <c>ready 1218 0 21200 "Settings { … }"</c>; null for any other line.</summary>
    public static ServeReady? ParseReady(string line)
    {
        var parts = line.Trim().Split(' ', 5);
        if (parts.Length < 4 || parts[0] != "ready") return null;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var initial)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var max)) return null;
        return new ServeReady(ms, initial, max, parts.Length > 4 ? SimSettings.Parse(parts[4]) : null);
    }

    /// <summary>The message of an <c>error …</c> line without an id (prepare's); null for any other line.</summary>
    public static string? ParsePrepareError(string line)
    {
        var t = line.Trim();
        return t.StartsWith("error ", StringComparison.Ordinal) ? t[6..] : null;
    }

    /// <summary>Reads an answer to <c>solve</c>; null for a line that isn't one.</summary>
    public static ServeAnswer? ParseAnswer(string line)
    {
        var t = line.Trim();
        var parts = t.Split(' ', 4);
        if (parts.Length < 2 || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return null;
        int Ms() => parts.Length > 2 && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ms) ? ms : -1;
        switch (parts[0])
        {
            case "solved":
                var ids = RaphaelCli.ParseActionIds(t);
                // An empty list is no rotation either.
                return ids.Count == 0
                    ? new ServeAnswer(id, ServeOutcome.Error, Ms(), Array.Empty<uint>(), "an empty or unreadable rotation")
                    : new ServeAnswer(id, ServeOutcome.Solved, Ms(), ids, "");
            case "nosolution":
                return new ServeAnswer(id, ServeOutcome.NoSolution, Ms(), Array.Empty<uint>(), "");
            case "error":
                var message = t.Split(' ', 3) is { Length: 3 } e ? e[2] : "";
                return new ServeAnswer(id, ServeOutcome.Error, -1, Array.Empty<uint>(), message);
            default:
                return null;
        }
    }
}

/// <summary>
/// Full re-planning on Good and Excellent steps: Raphael works out the rest of the rotation from the craft as it stands,
/// with the step's condition. When it can't (or doesn't answer in time), the cheap reaction (<see cref="ConditionReaction"/>)
/// takes the step.
/// </summary>
public static class Replanning
{
    /// <summary>How long a step waits for Raphael's answer before the cheap reaction takes it.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Why this step can't be re-planned (the cheap reaction then takes it), or null when it can. Heart and Soul being active
    /// is a state Raphael's tables don't hold (it fails with "State not found"); <paramref name="serverNotReady"/> is why the
    /// solver isn't ready (null: ready).
    /// </summary>
    public static string? Blocked(in SimState predicted, string? serverNotReady)
    {
        if (predicted.HeartAndSoulActive) return "Heart and Soul is active";
        return serverNotReady;
    }

    /// <summary>
    /// The quality a rotation reaches from <paramref name="st"/> when the step it starts on has condition
    /// <paramref name="c"/> (actions that take no step, like Heart and Soul, keep it), the step after an Excellent one is
    /// Poor and every later one Normal. -1 when an action can't be used or it doesn't finish; capped at what it aims for.
    /// </summary>
    public static int Quality(SimSettings s, SimState st, IReadOnlyList<SimAction> actions, CraftCondition c)
    {
        foreach (var a in actions)
        {
            if (st.IsFinal(s)) break;
            if (!RaphaelSim.TryUse(st, a, c, s, out st)) return -1;
            if (RaphaelSim.UsesStep(a)) c = c == CraftCondition.Excellent ? CraftCondition.Poor : CraftCondition.Normal;
        }
        return st.Progress >= s.MaxProgress ? Math.Min(st.Quality, s.MaxQuality) : -1;
    }

    /// <summary>
    /// Whether Raphael's new rest of the rotation is taken: it must finish, and by the simulator's count end with at least
    /// the quality the cheap reaction would (or the plan, when the cheap reaction keeps it). Returns its quality, or -1
    /// when it isn't taken.
    /// </summary>
    /// <param name="rest">The rotation from this step on as it stands.</param>
    /// <param name="cheap">What the cheap reaction would do here (null: keep the plan).</param>
    public static int Accept(SimSettings s, in SimState st, IReadOnlyList<SimAction> rest, IReadOnlyList<SimAction> replanned, CraftCondition c,
        ReactionChoice? cheap, out int beaten)
    {
        beaten = cheap?.ChosenQuality ?? Quality(s, st, rest, c);
        var q = replanned.Count == 0 ? -1 : Quality(s, st, replanned, c);
        return q >= 0 && q >= beaten ? q : -1;
    }

    /// <summary>
    /// How many actions of a re-planned rest come before its first one that takes a step (Heart and Soul, Quick
    /// Innovation): the steps they're used on are this step still, so they aren't re-planned again.
    /// </summary>
    public static int LeadingNonSteps(IReadOnlyList<SimAction> actions)
    {
        var i = 0;
        while (i < actions.Count && !RaphaelSim.UsesStep(actions[i])) i++;
        return i;
    }

    /// <summary>The state for the debug log: "progress 1,200/10,040, quality 3,400, durability 40, CP 500, Inner Quiet 3, …".</summary>
    public static string Summary(in SimState st, SimSettings s)
        => string.Create(CultureInfo.InvariantCulture,
            $"progress {st.Progress}/{s.MaxProgress}, quality {st.Quality}/{s.MaxQuality}, durability {st.Durability}, CP {st.Cp}, Inner Quiet {st.InnerQuiet}")
           + (st.Innovation > 0 ? $", Innovation {st.Innovation}" : "") + (st.Veneration > 0 ? $", Veneration {st.Veneration}" : "")
           + (st.GreatStrides > 0 ? $", Great Strides {st.GreatStrides}" : "") + (st.WasteNot > 0 ? $", Waste Not {st.WasteNot}" : "")
           + (st.Manipulation > 0 ? $", Manipulation {st.Manipulation}" : "") + (st.MuscleMemory > 0 ? $", Muscle Memory {st.MuscleMemory}" : "")
           + (st.Combo is SimCombo.BasicTouch or SimCombo.StandardTouch ? $", combo {st.Combo}" : "");

    /// <summary>The serve process's thread count: half the logical cores (at least one), so the game stays smooth.</summary>
    public static int Threads(int logicalCores) => Math.Max(1, logicalCores / 2);
}
