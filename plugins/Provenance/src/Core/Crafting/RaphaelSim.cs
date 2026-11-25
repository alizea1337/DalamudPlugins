using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Provenance.Core.Crafting;

// Adapted from raphael-sim (raphael-rs by KonaeAkira, commit 9311b0d, Apache License 2.0):
// raphael-sim/src/state.rs, actions.rs, effects.rs, settings.rs and the HQ table in raphael-data/src/lib.rs.
// Only the non-adversarial simulator, without backloaded progress, Stellar Steady Hand or Trained Eye, and only the
// conditions of a normal recipe (Normal, Good, Excellent, Poor).

/// <summary>The condition of a crafting step on a normal (non-expert) recipe.</summary>
public enum CraftCondition { Normal, Good, Excellent, Poor }

/// <summary>The crafting actions Raphael plans with (the ones this simulator knows).</summary>
public enum SimAction : byte
{
    BasicSynthesis, BasicTouch, MasterMend, Observe, TricksOfTheTrade, WasteNot, Veneration, StandardTouch, GreatStrides,
    Innovation, WasteNot2, ByregotsBlessing, PreciseTouch, MuscleMemory, CarefulSynthesis, Manipulation, PrudentTouch,
    AdvancedTouch, Reflect, PreparatoryTouch, Groundwork, DelicateSynthesis, IntensiveSynthesis, HeartAndSoul,
    PrudentSynthesis, TrainedFinesse, RefinedTouch, QuickInnovation, ImmaculateMend, TrainedPerfection,
}

public enum SimCombo : byte { None, SynthesisBegin, BasicTouch, StandardTouch }

/// <summary>
/// The numbers Raphael worked the rotation out for (raphael-cli's <c>settings</c>). <see cref="MaxQuality"/> is the quality
/// the rotation aims for on top of what the HQ ingredients start it with (the target, or the recipe's most, less that).
/// </summary>
public sealed record SimSettings(int MaxCp, int MaxDurability, int MaxProgress, int MaxQuality, int BaseProgress, int BaseQuality,
    int JobLevel, bool Manipulation = true, bool HeartAndSoul = false, bool QuickInnovation = false)
{
    /// <summary>
    /// Reads raphael-cli's <c>Settings { max_cp: 741, …, allowed_actions: ActionMask { disallowed_actions: [TrainedEye] }, … }</c>.
    /// Null when a number is missing.
    /// </summary>
    public static SimSettings? Parse(string text)
    {
        var start = text.IndexOf("Settings {", StringComparison.Ordinal);
        if (start < 0) return null;
        text = text[start..];
        int? Field(string name)
        {
            var m = Regex.Match(text, @"\b" + name + @":\s*(\d+)");
            return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        }
        if (Field("max_cp") is not { } cp || Field("max_durability") is not { } dur || Field("max_progress") is not { } prog
            || Field("max_quality") is not { } qual || Field("base_progress") is not { } bp || Field("base_quality") is not { } bq
            || Field("job_level") is not { } level) return null;
        var disallowed = Regex.Match(text, @"disallowed_actions:\s*\[([^\]]*)\]");
        var off = disallowed.Success
            ? disallowed.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet()
            : new HashSet<string>();
        return new SimSettings(cp, dur, prog, qual, bp, bq, level, !off.Contains("Manipulation"), !off.Contains("HeartAndSoul"), !off.Contains("QuickInnovation"));
    }
}

/// <summary>One moment of a synthesis, as raphael-sim keeps it.</summary>
public struct SimState
{
    public int Cp, Durability, Progress, Quality;
    public int InnerQuiet, WasteNot, Innovation, Veneration, GreatStrides, MuscleMemory, Manipulation;
    public bool TrainedPerfectionAvailable, HeartAndSoulAvailable, QuickInnovationAvailable, TrainedPerfectionActive, HeartAndSoulActive;
    public SimCombo Combo;

    public static SimState Start(SimSettings s) => new()
    {
        Cp = s.MaxCp,
        Durability = s.MaxDurability,
        TrainedPerfectionAvailable = true,
        HeartAndSoulAvailable = s.HeartAndSoul,
        QuickInnovationAvailable = s.QuickInnovation,
        Combo = SimCombo.SynthesisBegin,
    };

    public readonly bool IsFinal(SimSettings s) => Durability == 0 || Progress >= s.MaxProgress;

    public override readonly string ToString()
        => $"progress {Progress}, quality {Quality}, durability {Durability}, CP {Cp}, Inner Quiet {InnerQuiet}, Innovation {Innovation}, Veneration {Veneration}, "
           + $"Great Strides {GreatStrides}, Waste Not {WasteNot}, Manipulation {Manipulation}, Muscle Memory {MuscleMemory}, combo {Combo}"
           + (HeartAndSoulActive ? ", Heart and Soul" : "") + (TrainedPerfectionActive ? ", Trained Perfection" : "");
}

/// <summary>raphael-sim's simulator: what an action does to a synthesis under a given condition.</summary>
public static class RaphaelSim
{
    private static readonly Dictionary<uint, SimAction> ById = new()
    {
        [100001] = SimAction.BasicSynthesis, [100002] = SimAction.BasicTouch, [100003] = SimAction.MasterMend,
        [100010] = SimAction.Observe, [100371] = SimAction.TricksOfTheTrade, [4631] = SimAction.WasteNot,
        [19297] = SimAction.Veneration, [100004] = SimAction.StandardTouch, [260] = SimAction.GreatStrides,
        [19004] = SimAction.Innovation, [4639] = SimAction.WasteNot2, [100339] = SimAction.ByregotsBlessing,
        [100128] = SimAction.PreciseTouch, [100379] = SimAction.MuscleMemory, [100203] = SimAction.CarefulSynthesis,
        [4574] = SimAction.Manipulation, [100227] = SimAction.PrudentTouch, [100411] = SimAction.AdvancedTouch,
        [100387] = SimAction.Reflect, [100299] = SimAction.PreparatoryTouch, [100403] = SimAction.Groundwork,
        [100323] = SimAction.DelicateSynthesis, [100315] = SimAction.IntensiveSynthesis, [100419] = SimAction.HeartAndSoul,
        [100427] = SimAction.PrudentSynthesis, [100435] = SimAction.TrainedFinesse, [100443] = SimAction.RefinedTouch,
        [100459] = SimAction.QuickInnovation, [100467] = SimAction.ImmaculateMend, [100475] = SimAction.TrainedPerfection,
    };

    private static readonly Dictionary<SimAction, uint> IdOf = ById.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>The action for one of Raphael's action ids (the Carpenter's copy); null for one this simulator doesn't know.</summary>
    public static SimAction? FromId(uint raphaelId) => ById.TryGetValue(raphaelId, out var a) ? a : null;

    /// <summary>Raphael's id of an action (the Carpenter's copy, as raphael-cli answers).</summary>
    public static uint Id(SimAction a) => IdOf[a];

    /// <summary>The whole rotation as simulator actions; null when any of them is one this simulator doesn't know.</summary>
    public static List<SimAction>? FromIds(IEnumerable<uint> ids)
    {
        var list = new List<SimAction>();
        foreach (var id in ids)
        {
            if (FromId(id) is not { } a) return null;
            list.Add(a);
        }
        return list;
    }

    /// <summary>Whether the action takes a step (Heart and Soul and Quick Innovation don't: no condition change, no buff ticks).</summary>
    public static bool UsesStep(SimAction a) => a is not (SimAction.HeartAndSoul or SimAction.QuickInnovation);

    public static int LevelRequirement(SimAction a) => a switch
    {
        SimAction.BasicSynthesis => 1, SimAction.BasicTouch => 5, SimAction.MasterMend => 7, SimAction.Observe => 13,
        SimAction.TricksOfTheTrade => 13, SimAction.WasteNot => 15, SimAction.Veneration => 15, SimAction.StandardTouch => 18,
        SimAction.GreatStrides => 21, SimAction.Innovation => 26, SimAction.WasteNot2 => 47, SimAction.ByregotsBlessing => 50,
        SimAction.PreciseTouch => 53, SimAction.MuscleMemory => 54, SimAction.CarefulSynthesis => 62, SimAction.Manipulation => 65,
        SimAction.PrudentTouch => 66, SimAction.AdvancedTouch => 68, SimAction.Reflect => 69, SimAction.PreparatoryTouch => 71,
        SimAction.Groundwork => 72, SimAction.DelicateSynthesis => 76, SimAction.IntensiveSynthesis => 78, SimAction.HeartAndSoul => 86,
        SimAction.PrudentSynthesis => 88, SimAction.TrainedFinesse => 90, SimAction.RefinedTouch => 92, SimAction.QuickInnovation => 96,
        SimAction.ImmaculateMend => 98, SimAction.TrainedPerfection => 100,
        _ => 100,
    };

    private static bool Allowed(SimAction a, SimSettings s) => a switch
    {
        SimAction.Manipulation => s.Manipulation,
        SimAction.HeartAndSoul => s.HeartAndSoul,
        SimAction.QuickInnovation => s.QuickInnovation,
        _ => true,
    };

    private static int ConditionModifier(CraftCondition c) => c switch
    {
        CraftCondition.Good => 3,
        CraftCondition.Excellent => 8,
        CraftCondition.Poor => 1,
        _ => 2,
    };

    private static bool GoodOrExcellent(CraftCondition c) => c is CraftCondition.Good or CraftCondition.Excellent;

    private static int BaseDurabilityCost(SimAction a) => a switch
    {
        SimAction.BasicSynthesis or SimAction.BasicTouch or SimAction.StandardTouch or SimAction.ByregotsBlessing or SimAction.PreciseTouch
            or SimAction.MuscleMemory or SimAction.CarefulSynthesis or SimAction.AdvancedTouch or SimAction.Reflect
            or SimAction.DelicateSynthesis or SimAction.IntensiveSynthesis or SimAction.RefinedTouch => 10,
        SimAction.PrudentTouch or SimAction.PrudentSynthesis => 5,
        SimAction.PreparatoryTouch or SimAction.Groundwork => 20,
        _ => 0,
    };

    private static int DurabilityCost(in SimState st, int baseCost)
        => st.TrainedPerfectionActive ? 0 : st.WasteNot == 0 ? baseCost : (baseCost + 1) / 2;

    private static int CpCost(in SimState st, SimAction a) => a switch
    {
        SimAction.BasicTouch => 18,
        SimAction.MasterMend => 88,
        SimAction.Observe => 7,
        SimAction.WasteNot => 56,
        SimAction.Veneration => 18,
        SimAction.StandardTouch => st.Combo == SimCombo.BasicTouch ? 18 : 32,
        SimAction.GreatStrides => 32,
        SimAction.Innovation => 18,
        SimAction.WasteNot2 => 98,
        SimAction.ByregotsBlessing => 24,
        SimAction.PreciseTouch => 18,
        SimAction.MuscleMemory => 6,
        SimAction.CarefulSynthesis => 7,
        SimAction.Manipulation => 96,
        SimAction.PrudentTouch => 25,
        SimAction.AdvancedTouch => st.Combo == SimCombo.StandardTouch ? 18 : 46,
        SimAction.Reflect => 6,
        SimAction.PreparatoryTouch => 40,
        SimAction.Groundwork => 18,
        SimAction.DelicateSynthesis => 32,
        SimAction.IntensiveSynthesis => 6,
        SimAction.PrudentSynthesis => 18,
        SimAction.TrainedFinesse => 32,
        SimAction.RefinedTouch => 24,
        SimAction.ImmaculateMend => 112,
        _ => 0,
    };

    private static bool Precondition(in SimState st, SimAction a, CraftCondition c) => a switch
    {
        SimAction.TricksOfTheTrade or SimAction.PreciseTouch or SimAction.IntensiveSynthesis => st.HeartAndSoulActive || GoodOrExcellent(c),
        SimAction.ByregotsBlessing => st.InnerQuiet > 0,
        SimAction.MuscleMemory or SimAction.Reflect => st.Combo == SimCombo.SynthesisBegin,
        SimAction.PrudentTouch or SimAction.PrudentSynthesis => st.WasteNot == 0,
        SimAction.HeartAndSoul => st.HeartAndSoulAvailable,
        SimAction.TrainedFinesse => st.InnerQuiet >= 10,
        SimAction.RefinedTouch => st.Combo == SimCombo.BasicTouch,
        SimAction.QuickInnovation => st.Innovation == 0 && st.QuickInnovationAvailable,
        SimAction.TrainedPerfection => st.TrainedPerfectionAvailable,
        _ => true,
    };

    /// <summary>Quality efficiency of the action in percent (0: no quality).</summary>
    private static int QualityModifier(in SimState st, SimAction a) => a switch
    {
        SimAction.BasicTouch or SimAction.PrudentTouch or SimAction.DelicateSynthesis or SimAction.TrainedFinesse or SimAction.RefinedTouch => 100,
        SimAction.StandardTouch => 125,
        SimAction.ByregotsBlessing => 100 + 20 * st.InnerQuiet,
        SimAction.PreciseTouch or SimAction.AdvancedTouch => 150,
        SimAction.Reflect => 300,
        SimAction.PreparatoryTouch => 200,
        _ => 0,
    };

    /// <summary>Progress efficiency of the action in percent (0: no progress).</summary>
    private static int ProgressModifier(in SimState st, SimAction a) => a switch
    {
        SimAction.BasicSynthesis => 120,
        SimAction.MuscleMemory => 300,
        SimAction.CarefulSynthesis or SimAction.PrudentSynthesis => 180,
        SimAction.Groundwork => DurabilityCost(st, 20) > st.Durability ? 180 : 360,
        SimAction.DelicateSynthesis => 150,
        SimAction.IntensiveSynthesis => 400,
        _ => 0,
    };

    /// <summary>
    /// Uses an action: false (and <paramref name="next"/> unchanged) when it can't be used (raphael-sim's ActionError: level,
    /// not allowed, the craft is over, not enough CP, or its own condition).
    /// </summary>
    public static bool TryUse(in SimState st, SimAction a, CraftCondition c, SimSettings s, out SimState next)
    {
        next = st;
        if (s.JobLevel < LevelRequirement(a) || !Allowed(a, s) || st.IsFinal(s)) return false;
        var cp = CpCost(st, a);
        if (cp > st.Cp || !Precondition(st, a, c)) return false;

        var n = st;
        var baseDurability = BaseDurabilityCost(a);
        if (baseDurability != 0) n.Durability = Math.Max(0, n.Durability - DurabilityCost(st, baseDurability));
        n.Cp -= cp;

        var qualityIncrease = 0;
        if (QualityModifier(st, a) is var qm and > 0)
        {
            long efficiency = (st.InnerQuiet + 10) * (10 + (st.GreatStrides > 0 ? 10 : 0) + (st.Innovation > 0 ? 5 : 0));
            qualityIncrease = (int)((long)s.BaseQuality * qm * efficiency * ConditionModifier(c) / 20000);
        }
        n.Quality = Math.Min(65535, n.Quality + qualityIncrease);
        if (qualityIncrease != 0 && s.JobLevel >= 11) n.InnerQuiet = Math.Min(10, n.InnerQuiet + 1);

        var progressIncrease = 0;
        if (ProgressModifier(st, a) is var pm and > 0)
        {
            long efficiency = 10 + (st.MuscleMemory > 0 ? 10 : 0) + (st.Veneration > 0 ? 5 : 0);
            progressIncrease = (int)((long)s.BaseProgress * pm * efficiency / 1000);
        }
        n.Progress = Math.Min(65535, n.Progress + progressIncrease);

        if (n.IsFinal(s))
        {
            next = n;
            return true;
        }

        // Effects the action ends (raphael-sim's EFFECT_RESET_MASK), the combo first.
        var wasBegin = n.Combo == SimCombo.SynthesisBegin;
        if (a != SimAction.StandardTouch) n.Combo = SimCombo.None;
        switch (a)
        {
            case SimAction.BasicSynthesis or SimAction.MuscleMemory or SimAction.CarefulSynthesis or SimAction.Groundwork
                or SimAction.IntensiveSynthesis or SimAction.PrudentSynthesis:
                n.MuscleMemory = 0;
                n.TrainedPerfectionActive = false;
                break;
            case SimAction.BasicTouch or SimAction.StandardTouch or SimAction.PreciseTouch or SimAction.PrudentTouch
                or SimAction.AdvancedTouch or SimAction.PreparatoryTouch or SimAction.RefinedTouch:
                n.GreatStrides = 0;
                n.TrainedPerfectionActive = false;
                break;
            case SimAction.WasteNot or SimAction.WasteNot2: n.WasteNot = 0; break;
            case SimAction.Veneration: n.Veneration = 0; break;
            case SimAction.GreatStrides: n.GreatStrides = 0; break;
            case SimAction.Innovation: n.Innovation = 0; break;
            case SimAction.Manipulation: n.Manipulation = 0; break;
            case SimAction.ByregotsBlessing:
                n.GreatStrides = 0;
                n.InnerQuiet = 0;
                n.TrainedPerfectionActive = false;
                break;
            case SimAction.Reflect: n.TrainedPerfectionActive = false; break;
            case SimAction.DelicateSynthesis:
                n.MuscleMemory = 0;
                n.GreatStrides = 0;
                n.TrainedPerfectionActive = false;
                break;
            case SimAction.HeartAndSoul: n.HeartAndSoulAvailable = false; break;
            case SimAction.TrainedFinesse: n.GreatStrides = 0; break;
            case SimAction.QuickInnovation:
                n.QuickInnovationAvailable = false;
                n.Innovation = 0;
                break;
            case SimAction.TrainedPerfection: n.TrainedPerfectionAvailable = false; break;
        }
        var usesStep = UsesStep(a);
        // The "first step" combo is really the step count, which these don't raise.
        if (!usesStep && wasBegin) n.Combo = SimCombo.SynthesisBegin;
        if (usesStep)
        {
            if (n.Manipulation != 0) n.Durability = Math.Min(s.MaxDurability, n.Durability + 5);
            if (n.WasteNot > 0) n.WasteNot--;
            if (n.Innovation > 0) n.Innovation--;
            if (n.Veneration > 0) n.Veneration--;
            if (n.GreatStrides > 0) n.GreatStrides--;
            if (n.MuscleMemory > 0) n.MuscleMemory--;
            if (n.Manipulation > 0) n.Manipulation--;
        }

        // The action's own change, then the effects it sets.
        switch (a)
        {
            case SimAction.MasterMend: n.Durability = Math.Min(s.MaxDurability, n.Durability + 30); break;
            case SimAction.TricksOfTheTrade:
                n.Cp = Math.Min(s.MaxCp, n.Cp + 20);
                if (!GoodOrExcellent(c)) n.HeartAndSoulActive = false;
                break;
            case SimAction.StandardTouch: n.Combo = n.Combo == SimCombo.BasicTouch ? SimCombo.StandardTouch : SimCombo.None; break;
            case SimAction.PreciseTouch:
                n.InnerQuiet = Math.Min(10, n.InnerQuiet + 1);
                if (!GoodOrExcellent(c)) n.HeartAndSoulActive = false;
                break;
            case SimAction.IntensiveSynthesis:
                if (!GoodOrExcellent(c)) n.HeartAndSoulActive = false;
                break;
            case SimAction.Reflect or SimAction.PreparatoryTouch or SimAction.RefinedTouch:
                n.InnerQuiet = Math.Min(10, n.InnerQuiet + 1);
                break;
            case SimAction.ImmaculateMend: n.Durability = s.MaxDurability; break;
        }
        switch (a)
        {
            case SimAction.BasicTouch: n.Combo = SimCombo.BasicTouch; break;
            case SimAction.Observe: n.Combo = SimCombo.StandardTouch; break;
            case SimAction.WasteNot: n.WasteNot = 4; break;
            case SimAction.WasteNot2: n.WasteNot = 8; break;
            case SimAction.Veneration: n.Veneration = 4; break;
            case SimAction.GreatStrides: n.GreatStrides = 3; break;
            case SimAction.Innovation: n.Innovation = 4; break;
            case SimAction.MuscleMemory: n.MuscleMemory = 5; break;
            case SimAction.Manipulation: n.Manipulation = 8; break;
            case SimAction.HeartAndSoul: n.HeartAndSoulActive = true; break;
            case SimAction.QuickInnovation: n.Innovation = 1; break;
            case SimAction.TrainedPerfection: n.TrainedPerfectionActive = true; break;
        }
        next = n;
        return true;
    }

    /// <summary>
    /// Plays actions from a state, all under the same condition; null when one of them can't be used. Mostly for
    /// checking a rotation under Normal steps (as Raphael plans it).
    /// </summary>
    public static SimState? Play(SimSettings s, SimState st, IEnumerable<SimAction> actions, CraftCondition condition = CraftCondition.Normal)
    {
        foreach (var a in actions)
            if (!TryUse(st, a, condition, s, out st)) return null;
        return st;
    }

    private static readonly byte[] HqLookup =
    {
        1, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7, 8, 8, 8,
        9, 9, 9, 10, 10, 10, 11, 11, 11, 12, 12, 12, 13, 13, 13, 14, 14, 14, 15, 15, 15, 16, 16, 17,
        17, 17, 18, 18, 18, 19, 19, 20, 20, 21, 22, 23, 24, 26, 28, 31, 34, 38, 42, 47, 52, 58, 64, 68,
        71, 74, 76, 78, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 94, 96, 98, 100,
    };

    /// <summary>The game's chance in percent that an item finishing with this quality comes out HQ.</summary>
    public static int HqPercent(int quality, int maxQuality)
        => maxQuality <= 0 ? 100 : HqLookup[Math.Clamp((int)((long)Math.Max(0, quality) * 100 / maxQuality), 0, 100)];
}
