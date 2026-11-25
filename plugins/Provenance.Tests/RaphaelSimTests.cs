using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Provenance.Core.Crafting;
using Xunit;

namespace Provenance.Tests;

/// <summary>
/// The simulator ported from raphael-sim, checked against raphael-cli 0.2.2's own answers, and the reactions to Good and
/// Excellent steps checked against the Python measurement (rsim.py / mc.py) on the same conditions.
/// </summary>
public class RaphaelSimTests
{
    /// <summary>Rotations from raphael-cli with the quality it says they end at under Normal steps (no HQ ingredients).</summary>
    public static readonly (SimSettings Settings, SimAction[] Rotation, int Quality)[] Rotations =
    {
        // 37820 G4 Gemdraught Dex (rlvl770), no specialist
        (new SimSettings(741, 70, 10040, 21200, 303, 280, 100, true, false, false),
            new[] { SimAction.MuscleMemory, SimAction.TrainedPerfection, SimAction.Veneration, SimAction.Groundwork, SimAction.DelicateSynthesis, SimAction.CarefulSynthesis, SimAction.Innovation, SimAction.PrudentTouch, SimAction.BasicTouch, SimAction.StandardTouch, SimAction.AdvancedTouch, SimAction.ImmaculateMend, SimAction.Innovation, SimAction.PrudentTouch, SimAction.BasicTouch, SimAction.StandardTouch, SimAction.AdvancedTouch, SimAction.Innovation, SimAction.BasicTouch, SimAction.StandardTouch, SimAction.AdvancedTouch, SimAction.ImmaculateMend, SimAction.Innovation, SimAction.PrudentTouch, SimAction.GreatStrides, SimAction.ByregotsBlessing, SimAction.Veneration, SimAction.Groundwork, SimAction.Groundwork, SimAction.CarefulSynthesis, SimAction.CarefulSynthesis }, 14286),
        // 37820 G4 Gemdraught Dex (rlvl770), H&S+QI
        (new SimSettings(741, 70, 10040, 21200, 303, 280, 100, true, true, true),
            new[] { SimAction.QuickInnovation, SimAction.Reflect, SimAction.Veneration, SimAction.DelicateSynthesis, SimAction.Groundwork, SimAction.PrudentSynthesis, SimAction.Groundwork, SimAction.ImmaculateMend, SimAction.Innovation, SimAction.PrudentTouch, SimAction.BasicTouch, SimAction.StandardTouch, SimAction.AdvancedTouch, SimAction.Innovation, SimAction.BasicTouch, SimAction.StandardTouch, SimAction.AdvancedTouch, SimAction.ImmaculateMend, SimAction.Innovation, SimAction.DelicateSynthesis, SimAction.BasicTouch, SimAction.StandardTouch, SimAction.AdvancedTouch, SimAction.GreatStrides, SimAction.Innovation, SimAction.ByregotsBlessing, SimAction.TrainedPerfection, SimAction.Veneration, SimAction.Groundwork, SimAction.PrudentSynthesis, SimAction.BasicSynthesis, SimAction.HeartAndSoul, SimAction.IntensiveSynthesis }, 15571),
        // 36074 G3 Gemdraught Dex (rlvl740), no specialist
        (new SimSettings(741, 70, 9000, 18700, 303, 280, 100, true, false, false),
            new[] { SimAction.Reflect, SimAction.Veneration, SimAction.CarefulSynthesis, SimAction.Groundwork, SimAction.PrudentSynthesis, SimAction.Groundwork, SimAction.TrainedPerfection, SimAction.ImmaculateMend, SimAction.Veneration, SimAction.Groundwork, SimAction.CarefulSynthesis, SimAction.BasicSynthesis, SimAction.CarefulSynthesis, SimAction.Innovation, SimAction.PrudentTouch, SimAction.BasicTouch, SimAction.StandardTouch, SimAction.AdvancedTouch, SimAction.ImmaculateMend, SimAction.Innovation, SimAction.PrudentTouch, SimAction.BasicTouch, SimAction.StandardTouch, SimAction.AdvancedTouch, SimAction.GreatStrides, SimAction.Observe, SimAction.AdvancedTouch, SimAction.GreatStrides, SimAction.Innovation, SimAction.Observe, SimAction.AdvancedTouch, SimAction.GreatStrides, SimAction.ByregotsBlessing, SimAction.BasicSynthesis }, 15078),
        // 36074 G3 Gemdraught Dex (rlvl740), H&S+QI
        (new SimSettings(741, 70, 9000, 18700, 303, 280, 100, true, true, true),
            new[] { SimAction.QuickInnovation, SimAction.Reflect, SimAction.TrainedPerfection, SimAction.Veneration, SimAction.Groundwork, SimAction.DelicateSynthesis, SimAction.PrudentSynthesis, SimAction.CarefulSynthesis, SimAction.Innovation, SimAction.BasicTouch, SimAction.StandardTouch, SimAction.AdvancedTouch, SimAction.ImmaculateMend, SimAction.Innovation, SimAction.PrudentTouch, SimAction.BasicTouch, SimAction.StandardTouch, SimAction.AdvancedTouch, SimAction.Innovation, SimAction.BasicTouch, SimAction.StandardTouch, SimAction.AdvancedTouch, SimAction.ImmaculateMend, SimAction.Innovation, SimAction.BasicTouch, SimAction.StandardTouch, SimAction.GreatStrides, SimAction.ByregotsBlessing, SimAction.Veneration, SimAction.Groundwork, SimAction.CarefulSynthesis, SimAction.PrudentSynthesis, SimAction.HeartAndSoul, SimAction.IntensiveSynthesis }, 16590),
        // 37800 rlvl770 35-dur (reaches 100%), no specialist
        (new SimSettings(741, 35, 5622, 14204, 303, 280, 100, true, false, false),
            new[] { SimAction.Reflect, SimAction.Manipulation, SimAction.Veneration, SimAction.WasteNot, SimAction.Groundwork, SimAction.Groundwork, SimAction.Groundwork, SimAction.PreparatoryTouch, SimAction.TrainedPerfection, SimAction.GreatStrides, SimAction.Manipulation, SimAction.Innovation, SimAction.PreparatoryTouch, SimAction.DelicateSynthesis, SimAction.GreatStrides, SimAction.PreparatoryTouch, SimAction.GreatStrides, SimAction.Innovation, SimAction.PreparatoryTouch, SimAction.TrainedFinesse, SimAction.GreatStrides, SimAction.ByregotsBlessing, SimAction.BasicSynthesis }, 14224),
    };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Raphael_s_rotations_end_where_raphael_says(int i)
    {
        var (s, rotation, quality) = Rotations[i];
        var end = RaphaelSim.Play(s, SimState.Start(s), rotation);
        Assert.NotNull(end);
        Assert.True(end!.Value.Progress >= s.MaxProgress);
        Assert.Equal(quality, end.Value.Quality);
        Assert.NotNull(CraftWatch.For(s, 0, s.MaxQuality, rotation.Select(RaphaelSim.Id).ToList(), quality, out var why));
        Assert.Equal("", why);
        // A different quality than Raphael's: the simulator doesn't follow this rotation.
        Assert.Null(CraftWatch.For(s, 0, s.MaxQuality, rotation.Select(RaphaelSim.Id).ToList(), quality + 1, out why));
        Assert.Contains("Raphael said", why);
    }

    [Theory]
    [InlineData(0, "NNNGNNNNNGNNNNGNNGNNGNNNNNNNGNNNNNNGNNGNGNGNNGNNNNNGNNNNGNNN", 15593, 15593, 0)]
    [InlineData(0, "NNNNNNNNNGNNNNNGNNNNNNNGNNNNNNNNGNNGNNNGNGNEPNGNNNNGNNNNGNNN", 15493, 15493, 0)]
    [InlineData(0, "NNNNNNGNNNNGNNNNGNNNGNNNNGNNNGNNNNNNGNNGNGNNNNNNGNNNEPNNNNNG", 17583, 17625, 1)]
    [InlineData(0, "NNNNNNNNGNNNGNNNNNNNNNNGNNNNNNGNEPNNGNNGNNNNNNNGNNNNGNNGNNNG", 14958, 14958, 0)]
    [InlineData(0, "NNGNNNGNNGNNNNGNNNNNGNNNGNNNNNNNNNNNNNNNGNGNNNNNNNNNNGNGNGNN", 15593, 15593, 0)]
    [InlineData(0, "NNNNNNNNNGNNNNNNNNNNGNNGNNGNNNNGNNNNGNGNNNNGNGNNNGNNGNNNNGNN", 15677, 15677, 0)]
    [InlineData(1, "NNGNNNGNNNNNNNNNGNNNNNNNGNNNNGNNNNGNNEPNNGNNNNGNGNNNGNNNGNNN", 17839, 17839, 0)]
    [InlineData(1, "NNNGNGNGNNNEPNNGNNGNGNGNNNGNNNNNEPNNNNNNNNNGNGNNNEPNNNGNGNGN", 20138, 20327, 1)]
    [InlineData(1, "NGNNNNGNGNEPNNNNNNEPNNNNNNGNNNNGNGNNNNNNNNGNGNNEPNNNNGNGNNNN", 19803, 21441, 1)]
    [InlineData(1, "NNGNNGNNNNNNGNNNNNGNGNGNNNNNGNNNGNGNNNNGNNNNNNNNNEPNGNGNNNGN", 16684, 16684, 0)]
    [InlineData(1, "NGNNNNNGNNNNNNNGNEPNNNEPNGNNNNNNEPNNNGNNNNGNNGNNNNNNNNNNNNNN", 15749, 23449, 2)]
    [InlineData(1, "NEPNGNNGNNGNEPNNNNNNNNNNGNGNNGNNGNGNNNNGNEPNGNNGNGNNNGNNNNNG", 17540, 17540, 0)]
    [InlineData(2, "NNNGNGNNNNNNGNGNNNNNNGNNNNEPNNNNNNNEPNNGNNNNNNGNNNGNNNNNGNNN", 20727, 20727, 0)]
    [InlineData(2, "NNNNGNNGNEPNNNGNNNNNGNNNNNGNNNNNGNNNNNNNNNNNNNNNNGNNNNNNNNNN", 18606, 18606, 0)]
    [InlineData(2, "NNNNGNNGNNGNNNNEPNGNNNGNNNNNNNNGNNNNNGNNGNNGNNNGNNGNGNGNNNGN", 16820, 16820, 0)]
    [InlineData(2, "NNNNNNGNNNGNNNGNGNGNNGNNEPNNNNGNNNNNEPNNGNGNNGNNGNNNGNGNGNNG", 17104, 16789, 1)]
    [InlineData(2, "NNNNNEPNNNNNNNNNNNGNGNNNNNNNGNNNGNGNNNNNNNNNEPNGNNNNNGNGNNNG", 17514, 17514, 0)]
    [InlineData(2, "NEPNNNNNGNNGNNGNNGNNNNNNNNNNGNNNEPNNNGNNNGNNNNNGNNGNNGNNNNNN", 28402, 28601, 1)]
    [InlineData(3, "NNNNNNGNNNNGNNNNNNGNGNNGNGNNGNNGNGNNNEPNNNEPNNGNNNNNGNGNNNGN", 18060, 18060, 0)]
    [InlineData(3, "NNNNNGNGNNNGNNNGNNNNNGNNNNNNNNNNNGNNNGNNNNNGNNGNNNEPNNNNNNGN", 17062, 17062, 0)]
    [InlineData(3, "NGNNNNGNNGNGNGNNNGNNNNNNGNGNNNNNNNNNNGNGNNNNNGNNNGNGNGNGNGNG", 19918, 20233, 1)]
    [InlineData(3, "NNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNEPNNNNNNEPNNGNNGNNNGNNNNNNN", 16590, 16590, 0)]
    [InlineData(3, "NNNNNEPNNGNGNGNNNGNNGNNNNNNNNGNNNNNNNNNGNGNGNNGNNNNNNNNNGNGN", 17923, 17923, 0)]
    [InlineData(3, "NNNNNGNEPNGNNNGNNEPNNGNNNGNNNNNGNGNNNNGNNNGNGNNNNGNNGNNNNNGN", 16726, 16946, 1)]
    [InlineData(4, "NNNNNNGNNNGNNNNNNNNNGNGNGNNNNGNGNNGNNNGNGNNNNGNNNNNNNGNGNNNG", 14224, 14224, 0)]
    [InlineData(4, "NNNGNNNGNGNGNGNGNNNNNNNNNNGNNNEPNNGNNNNNGNGNNNNNNNNNGNGNNNGN", 16086, 16086, 0)]
    [InlineData(4, "NNNNNGNNNNGNGNEPNNGNNNNNNGNNNNNNGNNNNNNGNNNNNGNNGNGNNNNGNNNN", 15344, 15344, 0)]
    [InlineData(4, "NNNNNGNGNNNNGNNNGNNNNNNNNNNNNGNGNGNNNNNNNGNGNGNEPNNNNGNNGNNN", 15540, 15540, 0)]
    [InlineData(4, "NNGNNNNGNNGNNNGNNGNGNNNGNGNGNGNNGNNGNNGNNNNNNNNNGNNNNNNGNNNN", 14980, 14980, 0)]
    [InlineData(4, "NGNGNGNNGNNNNGNGNNGNNNGNNNNGNNNGNNGNNNNGNNNNEPNNNGNNNNNNNNGN", 17080, 17080, 0)]
    public void Reactions_match_the_measurement(int i, string conditions, int fixedQuality, int reactingQuality, int edits)
    {
        var (s, rotation, _) = Rotations[i];
        Assert.Equal(fixedQuality, HqChance.PlayFixed(s, rotation, Sequence(conditions)).Quality);
        var st = HqChance.PlayReacting(s, rotation, Sequence(conditions), out var made);
        Assert.Equal(reactingQuality, st.Quality);
        Assert.Equal(edits, made);
        Assert.True(st.Progress >= s.MaxProgress);
    }

    /// <summary>Hands out the conditions after each step from "NGNEP…" (the first, always N, is the first step's).</summary>
    private static Func<CraftCondition, CraftCondition> Sequence(string conditions)
    {
        var k = 0;
        return _ => conditions[++k] switch
        {
            'G' => CraftCondition.Good,
            'E' => CraftCondition.Excellent,
            'P' => CraftCondition.Poor,
            _ => CraftCondition.Normal,
        };
    }

    [Fact]
    public void Hq_chance_is_close_to_the_measurement()
    {
        // mc.py, 20,000 crafts: 53.03 % as planned, 59.03 % reacting (G4 Gemdraught of Dexterity, no specialist actions).
        var (s, rotation, quality) = Rotations[0];
        Assert.Equal(23, RaphaelSim.HqPercent(quality, s.MaxQuality));
        var plain = HqChance.Estimate(s, 0, s.MaxQuality, rotation, false);
        var reacting = HqChance.Estimate(s, 0, s.MaxQuality, rotation, true);
        Assert.InRange(plain.HqPercent, 50.5, 55.5);
        Assert.InRange(reacting.HqPercent, 56.5, 61.5);
        Assert.True(reacting.HqPercent > plain.HqPercent);
        // The same seed gives the same answer (the Plan page doesn't flicker).
        Assert.Equal(plain, HqChance.Estimate(s, 0, s.MaxQuality, rotation, false));
    }

    [Fact]
    public void Hq_ingredients_count_towards_the_hq_chance()
    {
        // Starting with 5,000 quality: the rotation aims for that much less (as raphael-cli's settings say).
        var (s, rotation, _) = Rotations[0];
        var withHq = HqChance.Estimate(s with { MaxQuality = s.MaxQuality - 5000 }, 5000, s.MaxQuality, rotation, false, 300);
        var without = HqChance.Estimate(s, 0, s.MaxQuality, rotation, false, 300);
        Assert.True(withHq.HqPercent > without.HqPercent + 20);
        Assert.True(withHq.MeanQuality <= s.MaxQuality);
    }

    [Fact]
    public void Says_how_often_a_craft_comes_out_hq()
    {
        Assert.Equal("about 59% of crafts come out HQ", HqChance.Describe(59.03));
        Assert.Equal("about 1% of crafts come out HQ", HqChance.Describe(0.5));
        Assert.Equal("hardly any craft comes out HQ", HqChance.Describe(0.4));
        Assert.Equal("nearly every craft comes out HQ", HqChance.Describe(99.5));
        Assert.Equal("about 99% of crafts come out HQ", HqChance.Describe(99.32));
    }

    [Fact]
    public void Hq_table_matches_raphael()
    {
        Assert.Equal(1, RaphaelSim.HqPercent(0, 100));
        Assert.Equal(23, RaphaelSim.HqPercent(67, 100));
        Assert.Equal(71, RaphaelSim.HqPercent(80, 100));
        Assert.Equal(100, RaphaelSim.HqPercent(100, 100));
        Assert.Equal(100, RaphaelSim.HqPercent(150, 100));
    }

    [Fact]
    public void Conditions_follow_the_normal_recipe_model()
    {
        Assert.Equal(CraftCondition.Normal, CraftConditions.Next(CraftCondition.Good, 0.0, 100));
        Assert.Equal(CraftCondition.Poor, CraftConditions.Next(CraftCondition.Excellent, 0.9, 100));
        Assert.Equal(CraftCondition.Normal, CraftConditions.Next(CraftCondition.Poor, 0.0, 100));
        Assert.Equal(CraftCondition.Good, CraftConditions.Next(CraftCondition.Normal, 0.249, 100));
        Assert.Equal(CraftCondition.Excellent, CraftConditions.Next(CraftCondition.Normal, 0.26, 100));
        Assert.Equal(CraftCondition.Normal, CraftConditions.Next(CraftCondition.Normal, 0.29, 100));
        // Below level 63 Good comes less often.
        Assert.Equal(CraftCondition.Excellent, CraftConditions.Next(CraftCondition.Normal, 0.21, 62));
        Assert.True(CraftConditions.CanFollow(CraftCondition.Normal, CraftCondition.Excellent));
        Assert.False(CraftConditions.CanFollow(CraftCondition.Good, CraftCondition.Good));
        Assert.False(CraftConditions.CanFollow(CraftCondition.Excellent, CraftCondition.Normal));
    }

    [Fact]
    public void Good_and_excellent_steps_multiply_quality()
    {
        var s = Rotations[0].Settings;
        var start = SimState.Start(s);
        int Touch(CraftCondition c) => RaphaelSim.TryUse(start, SimAction.BasicTouch, c, s, out var n) ? n.Quality : -1;
        Assert.Equal(280, Touch(CraftCondition.Normal));
        Assert.Equal(420, Touch(CraftCondition.Good));
        Assert.Equal(1120, Touch(CraftCondition.Excellent));
        Assert.Equal(140, Touch(CraftCondition.Poor));
        // Precise Touch, Tricks of the Trade and Intensive Synthesis only on Good or Excellent.
        Assert.False(RaphaelSim.TryUse(start, SimAction.PreciseTouch, CraftCondition.Normal, s, out _));
        Assert.True(RaphaelSim.TryUse(start, SimAction.PreciseTouch, CraftCondition.Good, s, out var precise));
        Assert.Equal(2, precise.InnerQuiet);
        Assert.False(RaphaelSim.TryUse(start, SimAction.IntensiveSynthesis, CraftCondition.Poor, s, out _));
        // Intensive Synthesis needs level 78.
        Assert.False(RaphaelSim.TryUse(start, SimAction.IntensiveSynthesis, CraftCondition.Good, s with { JobLevel = 77 }, out _));
    }

    [Fact]
    public void Only_good_and_excellent_steps_are_edited()
    {
        var (s, rotation, _) = Rotations[0];
        var st = SimState.Start(s);
        Assert.Null(ConditionReaction.Choose(s, st, rotation, CraftCondition.Normal));
        Assert.Null(ConditionReaction.Choose(s, st, rotation, CraftCondition.Poor));
    }

    [Fact]
    public void An_edit_on_a_good_step_beats_the_plan_and_still_finishes()
    {
        // Rotation 0 on the first Good step where an edit helps.
        var (s, rotation, _) = Rotations[0];
        var at = FirstEdit(0, CraftCondition.Good);
        var st = RaphaelSim.Play(s, SimState.Start(s), rotation.Take(at))!.Value;
        var choice = ConditionReaction.Choose(s, st, rotation.Skip(at).ToList(), CraftCondition.Good);
        Assert.NotNull(choice);
        Assert.True(choice!.ChosenQuality > choice.KeptQuality);
        Assert.True(RaphaelSim.TryUse(st, choice.Rest[0], CraftCondition.Good, s, out var n));
        Assert.Equal(choice.ChosenQuality, ConditionReaction.FinalQuality(s, n, choice.Rest, 1));
    }

    /// <summary>The first step of a rotation (all Normal before it) where an edit beats the plan on a step with this condition.</summary>
    private static int FirstEdit(int i, CraftCondition c)
    {
        var (s, rotation, _) = Rotations[i];
        for (var at = 0; at < rotation.Length; at++)
            if (ConditionReaction.Choose(s, RaphaelSim.Play(s, SimState.Start(s), rotation.Take(at))!.Value, rotation.Skip(at).ToList(), c) != null) return at;
        Assert.Fail("No step where an edit helps.");
        return -1;
    }

    [Fact]
    public void A_quality_target_is_worth_nothing_above_it()
    {
        // A collectable whose target the plan reaches with room to spare: nothing beats it, the plan stays.
        var (s, rotation, quality) = Rotations[0];
        var target = s with { MaxQuality = quality - 2000 };
        for (var i = 0; i < rotation.Length - 1; i++)
        {
            var st = RaphaelSim.Play(target, SimState.Start(target), rotation.Take(i))!.Value;
            Assert.Null(ConditionReaction.Choose(target, st, rotation.Skip(i).ToList(), CraftCondition.Excellent));
        }
    }

    [Fact]
    public void Edits_never_break_the_item()
    {
        // On any step of any rotation, Good or Excellent, the chosen edit still finishes the item under Normal steps after it.
        foreach (var (s, rotation, _) in Rotations)
            for (var i = 0; i < rotation.Length; i++)
            {
                var st = RaphaelSim.Play(s, SimState.Start(s), rotation.Take(i))!.Value;
                foreach (var c in new[] { CraftCondition.Good, CraftCondition.Excellent })
                    if (ConditionReaction.Choose(s, st, rotation.Skip(i).ToList(), c) is { } choice)
                    {
                        Assert.True(choice.ChosenQuality >= 0);
                        Assert.True(RaphaelSim.TryUse(st, choice.Rest[0], c, s, out var n));
                        var end = n.IsFinal(s) ? n : RaphaelSim.Play(s, n, choice.Rest.Skip(1));
                        Assert.NotNull(end);
                        Assert.True(end!.Value.Progress >= s.MaxProgress);
                        Assert.True(end.Value.Durability > 0 || end.Value.Progress >= s.MaxProgress);
                    }
            }
    }

    [Fact]
    public void The_watch_follows_a_craft_and_stops_at_the_first_difference()
    {
        var (s, rotation, quality) = Rotations[0];
        var ids = rotation.Select(RaphaelSim.Id).ToList();
        var watch = CraftWatch.For(s, 0, s.MaxQuality, ids, quality, out _)!;
        static CraftReading Read(SimState p, int condition, int quality) => new(p.Progress, quality, p.Durability, p.Cp, condition, p.InnerQuiet, p.Innovation,
            p.Veneration, p.GreatStrides, p.WasteNot, p.Manipulation, p.MuscleMemory, p.HeartAndSoulActive, p.TrainedPerfectionActive);

        // The first step: Normal, nothing done yet.
        Assert.Null(watch.Compare(Read(watch.Predicted, 0, 0), out var c));
        Assert.Equal(CraftCondition.Normal, c);
        Assert.NotNull(watch.Compare(Read(watch.Predicted, 1, 0), out _)); // the first step is never Good
        watch.Used(ids[0], CraftCondition.Normal);
        // Muscle Memory done; the second step is Good.
        Assert.Null(watch.Compare(Read(watch.Predicted, 1, 0), out c));
        Assert.Equal(CraftCondition.Good, c);
        Assert.NotNull(watch.Compare(Read(watch.Predicted, 7, 0), out _)); // an expert condition
        Assert.NotNull(watch.Compare(Read(watch.Predicted, 1, 5), out _)); // quality the simulator didn't expect
        Assert.NotNull(watch.Compare(Read(watch.Predicted with { Cp = watch.Predicted.Cp - 1 }, 1, 0), out _));
        watch.Used(ids[1], CraftCondition.Good);
        // After Good comes Normal.
        Assert.NotNull(watch.Compare(Read(watch.Predicted, 1, 0), out _));
        Assert.Null(watch.Compare(Read(watch.Predicted, 0, 0), out _));
        // An action the simulator can't follow ends the watch.
        watch.Used(RaphaelSim.Id(SimAction.ByregotsBlessing), CraftCondition.Normal);
        Assert.False(watch.Active);
        Assert.NotNull(watch.Stopped);
        Assert.Null(watch.React(CraftCondition.Excellent, ids.Skip(2).ToList()));
    }

    [Fact]
    public void The_watch_reacts_and_counts()
    {
        var (s, rotation, quality) = Rotations[0];
        var ids = rotation.Select(RaphaelSim.Id).ToList();
        var watch = CraftWatch.For(s, 0, s.MaxQuality, ids, quality, out _)!;
        var at = FirstEdit(0, CraftCondition.Good);
        for (var i = 0; i < at; i++) watch.Used(ids[i], CraftCondition.Normal);
        Assert.Null(watch.React(CraftCondition.Normal, ids.Skip(at).ToList()));
        var edit = watch.React(CraftCondition.Good, ids.Skip(at).ToList());
        Assert.NotNull(edit);
        Assert.Equal(1, watch.GoodReactions);
        Assert.Equal(0, watch.ExcellentReactions);
        Assert.Equal(RaphaelSim.FromIds(edit!.Value.Actions), edit.Value.Choice.Rest);
    }

    [Fact]
    public void Reads_raphael_s_settings()
    {
        var s = SimSettings.Parse("Settings { max_cp: 741, max_durability: 70, max_progress: 10040, max_quality: 21200, base_progress: 266, base_quality: 271, job_level: 100, "
                                  + "allowed_actions: ActionMask { disallowed_actions: [TrainedEye, HeartAndSoul, QuickInnovation] }, adversarial: false, backload_progress: false, stellar_steady_hand_charges: 0 }");
        Assert.Equal(new SimSettings(741, 70, 10040, 21200, 266, 271, 100, true, false, false), s);
        Assert.Null(SimSettings.Parse("Settings { max_cp: 741 }"));
        Assert.Null(SimSettings.Parse(""));
        var noManipulation = SimSettings.Parse("Settings { max_cp: 1, max_durability: 2, max_progress: 3, max_quality: 4, base_progress: 5, base_quality: 6, job_level: 90, "
                                               + "allowed_actions: ActionMask { disallowed_actions: [Manipulation] } }");
        Assert.Equal(new SimSettings(1, 2, 3, 4, 5, 6, 90, false, true, true), noManipulation);
    }

    [Fact]
    public void Reads_the_full_raphael_answer()
    {
        var output = "\"[100379, 19297, 100403]\",12719,21200,1500,\"Settings { max_cp: 741, max_durability: 70, max_progress: 10040, max_quality: 19700, base_progress: 266, "
                     + "base_quality: 271, job_level: 100, allowed_actions: ActionMask { disallowed_actions: [TrainedEye, HeartAndSoul, QuickInnovation] }, adversarial: false, "
                     + "backload_progress: false, stellar_steady_hand_charges: 0 }\"\n";
        var s = RaphaelCli.ParseSolution(output)!;
        Assert.Equal(new uint[] { 100379, 19297, 100403 }, s.Actions);
        Assert.Equal(12719, s.FinalQuality);
        Assert.Equal(21200, s.MaxQuality);
        Assert.Equal(1500, s.InitialQuality);
        Assert.Equal(19700, s.Sim!.MaxQuality);
        Assert.True(s.Simulable);
        // An answer from before: no numbers for the simulator.
        Assert.False(RaphaelCli.ParseSolution("\"[100323]\",21293,21200")!.Simulable);
    }

    [Fact]
    public void Every_raphael_action_id_maps_both_ways()
    {
        foreach (var a in Enum.GetValues<SimAction>())
            Assert.Equal(a, RaphaelSim.FromId(RaphaelSim.Id(a)));
        Assert.Null(RaphaelSim.FromId(100283)); // Trained Eye: not simulated
        Assert.Null(RaphaelSim.FromIds(new uint[] { 100001, 46843 })); // Stellar Steady Hand
    }

    [Fact]
    public void Says_what_it_reacted_to()
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Null(ConditionReaction.Summary(0, 0, 100, 100));
            Assert.Equal("Reacted to 3 Good steps: quality 16,820 instead of the planned 14,286.", ConditionReaction.Summary(3, 0, 16820, 14286));
            Assert.Equal("Reacted to 1 Good step and 1 Excellent step: quality 21,200 instead of the planned 15,571.", ConditionReaction.Summary(1, 1, 21200, 15571));
            Assert.Equal("Reacted to 2 Excellent steps.", ConditionReaction.Summary(0, 2, -1, 15571));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }
}
