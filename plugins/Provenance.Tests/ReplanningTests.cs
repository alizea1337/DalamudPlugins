using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Provenance.Core.Crafting;
using Xunit;

namespace Provenance.Tests;

/// <summary>Full re-planning on Good and Excellent steps: the serve protocol, the choice between it and the cheap reaction, and the process.</summary>
public class ReplanningTests
{
    private static readonly RaphaelRequest Request = new(38104, 100, 5844, 5182, 572, true, false, null);

    [Fact]
    public void Prepare_takes_solve_s_options()
    {
        Assert.Equal("prepare --recipe-id 38104 --level 100 --stats 5844 5182 572 --manipulation", ServeProtocol.Prepare(Request));
        Assert.Equal("prepare --recipe-id 37825 --level 100 --stats 5700 5300 645 --manipulation --hq-ingredients 2 1 0 --food 44091,HQ",
            ServeProtocol.Prepare(new RaphaelRequest(37825, 100, 5700, 5300, 645, true, false, null, new[] { 2, 1, 0 }, Food: 44091, FoodHq: true)));
        // solve's own command line is as before.
        Assert.Equal("solve --recipe-id 38104 --level 100 --stats 5844 5182 572 --manipulation --output-variables action_ids final_quality recipe_max_quality initial_quality settings",
            RaphaelCli.Arguments(Request));
        Assert.Equal("serve --threads 12", ServeProtocol.Start(12));
        Assert.Equal("serve --threads 1", ServeProtocol.Start(0));
    }

    [Fact]
    public void Solve_sends_the_whole_state()
    {
        var (s, rotation, _) = RaphaelSimTests.Rotations[0];
        var st = RaphaelSim.Play(s, SimState.Start(s), rotation.Take(9))!.Value; // up to Basic Touch: a touch combo
        Assert.Equal(
            $"solve 7 good cp={st.Cp} durability={st.Durability} progress={st.Progress} quality={st.Quality} inner_quiet={st.InnerQuiet} waste_not=0 "
            + $"innovation={st.Innovation} veneration={st.Veneration} great_strides=0 muscle_memory=0 manipulation=0 trained_perfection_available=0 "
            + "heart_and_soul_available=0 quick_innovation_available=0 trained_perfection_active=0 heart_and_soul_active=0 combo=basic",
            ServeProtocol.Solve(7, st, CraftCondition.Good));
        Assert.EndsWith("combo=begin", ServeProtocol.Solve(1, SimState.Start(s), CraftCondition.Excellent));
        Assert.StartsWith("solve 1 excellent cp=741 ", ServeProtocol.Solve(1, SimState.Start(s), CraftCondition.Excellent));
    }

    [Fact]
    public void Reads_the_answers()
    {
        var ready = ServeProtocol.ParseReady("ready 1218 0 21200 \"Settings { max_cp: 741, max_durability: 70, max_progress: 10040, max_quality: 21200, base_progress: 303, base_quality: 280, job_level: 100, allowed_actions: ActionMask { disallowed_actions: [TrainedEye, HeartAndSoul, QuickInnovation] }, adversarial: false, backload_progress: false, stellar_steady_hand_charges: 0 }\"");
        Assert.NotNull(ready);
        Assert.Equal(1218, ready!.Ms);
        Assert.Equal(21200, ready.RecipeMaxQuality);
        Assert.Equal(RaphaelSimTests.Rotations[0].Settings, ready.Settings);
        Assert.Null(ServeProtocol.ParseReady("error no recipe with id 9"));
        Assert.Equal("no recipe with id 9", ServeProtocol.ParsePrepareError("error no recipe with id 9"));

        var solved = ServeProtocol.ParseAnswer("solved 3 1924 [100387, 19297, 100427]")!;
        Assert.Equal((3L, ServeOutcome.Solved, 1924), (solved.Id, solved.Outcome, solved.Ms));
        Assert.Equal(new uint[] { 100387, 19297, 100427 }, solved.Actions);
        Assert.Equal(ServeOutcome.NoSolution, ServeProtocol.ParseAnswer("nosolution 4 12")!.Outcome);
        var error = ServeProtocol.ParseAnswer("error 5 The solver encountered an internal error. | State not found in FinishSolver solved states.")!;
        Assert.Equal((5L, ServeOutcome.Error), (error.Id, error.Outcome));
        Assert.StartsWith("The solver encountered", error.Error);
        Assert.Equal(ServeOutcome.Error, ServeProtocol.ParseAnswer("solved 6 10 []")!.Outcome);
        Assert.Null(ServeProtocol.ParseAnswer("error unknown command bogus"));
        Assert.Null(ServeProtocol.ParseAnswer("serve 1"));
    }

    [Fact]
    public void Never_re_plans_while_heart_and_soul_is_active()
    {
        var (s, _, _) = RaphaelSimTests.Rotations[1];
        var st = SimState.Start(s);
        Assert.Null(Replanning.Blocked(st, null));
        Assert.Equal("Raphael is still preparing", Replanning.Blocked(st, "Raphael is still preparing"));
        Assert.True(RaphaelSim.TryUse(st, SimAction.HeartAndSoul, CraftCondition.Normal, s, out var hs));
        Assert.Equal("Heart and Soul is active", Replanning.Blocked(hs, null));
        Assert.Equal("Heart and Soul is active", Replanning.Blocked(hs, "Raphael is still preparing"));
    }

    [Fact]
    public void Uses_half_the_cores()
    {
        Assert.Equal(12, Replanning.Threads(24));
        Assert.Equal(4, Replanning.Threads(9));
        Assert.Equal(1, Replanning.Threads(1));
    }

    /// <summary>The watch of <see cref="RaphaelSimTests.Rotations"/>[0] played to the first step where the cheap reaction changes something on a Good step.</summary>
    private static (CraftWatch Watch, List<uint> Ids, SimSettings S) WatchAtACheapEdit()
    {
        var (s, rotation, quality) = RaphaelSimTests.Rotations[0];
        var ids = rotation.Select(RaphaelSim.Id).ToList();
        var watch = CraftWatch.For(s, 0, s.MaxQuality, ids, quality, out _)!;
        for (var i = 1; i < ids.Count; i++)
        {
            watch.Used(ids[i - 1], CraftCondition.Normal);
            if (ConditionReaction.Choose(s, watch.Predicted, rotation.Skip(i).ToList(), CraftCondition.Good) != null) return (watch, ids.Skip(i).ToList(), s);
        }
        throw new InvalidOperationException("no step with a cheap edit");
    }

    [Fact]
    public void A_re_plan_is_taken_when_it_does_at_least_as_well_as_the_cheap_reaction()
    {
        var (watch, rest, s) = WatchAtACheapEdit();
        var plan = RaphaelSim.FromIds(rest)!;
        var cheap = ConditionReaction.Choose(s, watch.Predicted, plan, CraftCondition.Good)!;
        var cheapIds = cheap.Rest.Select(RaphaelSim.Id).ToList();

        // As good as the cheap edit: taken, counted as a Good reaction that was re-planned.
        var taken = watch.Replan(CraftCondition.Good, rest, cheapIds, out var quality, out var beaten, out _);
        Assert.Equal(cheapIds, taken);
        Assert.Equal(cheap.ChosenQuality, quality);
        Assert.Equal(cheap.ChosenQuality, beaten);
        Assert.Equal((1, 1), (watch.GoodReactions, watch.Replans));

        // The plan as it stands is worse than the cheap edit here: not taken, nothing counted.
        Assert.Null(watch.Replan(CraftCondition.Good, rest, rest, out _, out _, out var why));
        Assert.Contains("cheap reaction does better", why);
        // One that doesn't finish, or nothing at all: not taken.
        Assert.Null(watch.Replan(CraftCondition.Good, rest, cheapIds.Take(cheapIds.Count / 2).ToList(), out _, out _, out why));
        Assert.Contains("doesn't finish", why);
        Assert.Null(watch.Replan(CraftCondition.Good, rest, Array.Empty<uint>(), out _, out _, out _));
        Assert.Null(watch.Replan(CraftCondition.Good, rest, new uint[] { 999999 }, out _, out _, out why));
        Assert.Contains("doesn't know", why);
        Assert.Equal((1, 1), (watch.GoodReactions, watch.Replans));
    }

    [Fact]
    public void A_re_plan_that_keeps_the_rotation_isn_t_a_reaction()
    {
        var (s, rotation, quality) = RaphaelSimTests.Rotations[0];
        var ids = rotation.Select(RaphaelSim.Id).ToList();
        var watch = CraftWatch.For(s, 0, s.MaxQuality, ids, quality, out _)!;
        // On a Normal step nothing beats the plan, and the plan itself is taken without counting.
        Assert.Equal(ids, watch.Replan(CraftCondition.Normal, ids, ids, out _, out _, out _));
        Assert.Equal((0, 0), (watch.Reactions, watch.Replans));
    }

    [Fact]
    public void Quality_counts_the_poor_step_after_an_excellent_one_and_keeps_the_condition_through_heart_and_soul()
    {
        var (s, rotation, _) = RaphaelSimTests.Rotations[0];
        var at = Array.IndexOf(rotation, SimAction.BasicTouch);
        var st = RaphaelSim.Play(s, SimState.Start(s), rotation.Take(at))!.Value;
        var rest = rotation.Skip(at).ToList();
        Assert.Equal(ConditionReaction.FinalQuality(s, st, rotation, at, CraftCondition.Normal), Replanning.Quality(s, st, rest, CraftCondition.Normal));
        // Excellent on Basic Touch, then Poor on Standard Touch: less than Excellent followed by Normal.
        var excellentThenNormal = ConditionReaction.FinalQuality(s, RaphaelSim.Play(s, st, rest.Take(1), CraftCondition.Excellent)!.Value, rest, 1);
        Assert.True(Replanning.Quality(s, st, rest, CraftCondition.Excellent) < excellentThenNormal);

        // Precise Touch after Heart and Soul on a Good step: both on that step.
        var (hs, hsRotation, _) = RaphaelSimTests.Rotations[1];
        var start = SimState.Start(hs);
        Assert.Equal(-1, Replanning.Quality(hs, start, new[] { SimAction.PreciseTouch }, CraftCondition.Normal));
        Assert.Equal(2, Replanning.LeadingNonSteps(new[] { SimAction.QuickInnovation, SimAction.HeartAndSoul, SimAction.PreciseTouch }));
        Assert.Equal(0, Replanning.LeadingNonSteps(hsRotation.Skip(1).ToList()));
    }

    [Fact]
    public void Says_how_many_steps_were_re_planned()
    {
        Assert.Equal("Reacted to 3 Good steps (2 re-planned): quality 16,820 instead of the planned 14,286.", ConditionReaction.Summary(3, 0, 16820, 14286, 2));
        Assert.Equal("Reacted to 1 Excellent step (re-planned): quality 21,200 instead of the planned 15,571.", ConditionReaction.Summary(0, 1, 21200, 15571, 1));
        Assert.Equal("Reacted to 3 Good steps: quality 16,820 instead of the planned 14,286.", ConditionReaction.Summary(3, 0, 16820, 14286));
    }

    [Fact]
    public void The_hq_chance_is_a_floor_while_re_planning()
    {
        Assert.Equal("at least about 59% of crafts come out HQ", HqChance.Describe(59.03, floor: true));
        Assert.Equal("about 59% of crafts come out HQ", HqChance.Describe(59.03));
        Assert.Equal("nearly every craft comes out HQ", HqChance.Describe(99.5, floor: true));
        Assert.Equal("hardly any craft comes out HQ", HqChance.Describe(0.4, floor: true));
    }

    // ------------------------------------------------------------------ the process (needs a built raphael-cli with serve)

    /// <summary>plugins/Provenance/Resources/raphael-cli.exe (gitignored; built by CI after the tests, so they skip there).</summary>
    private static string? Exe()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var exe = Path.Combine(dir.FullName, "plugins", "Provenance", "Resources", "raphael-cli.exe");
            if (File.Exists(exe)) return exe;
            exe = Path.Combine(dir.FullName, "Provenance", "Resources", "raphael-cli.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    private static RaphaelSolution SolveOnce(string exe, RaphaelRequest r)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, RaphaelCli.Arguments(r)) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return RaphaelCli.ParseSolution(output)!;
    }

    private static void WaitUntil(Func<bool> ready, TimeSpan most)
    {
        var until = DateTime.UtcNow + most;
        while (!ready() && DateTime.UtcNow < until) Thread.Sleep(50);
    }

    [Fact]
    public async Task Re_plans_a_real_craft()
    {
        if (Exe() is not { } exe) return;
        var solution = SolveOnce(exe, Request);
        var s = solution.Sim!;
        var lines = new List<string>();
        using var server = new RaphaelServer(() => exe, l => { lock (lines) lines.Add(l); });
        Assert.NotNull(server.NotReady(Request));
        Assert.Null(server.Solve(Request, SimState.Start(s), CraftCondition.Good));
        server.Prepare(Request, s);
        WaitUntil(() => server.NotReady(Request) == null, TimeSpan.FromSeconds(60));
        Assert.Null(server.NotReady(Request));
        Assert.True(server.Supported);

        // A few actions in, a Good step: the answer beats or matches the cheap reaction, within the crafter's wait.
        var plan = RaphaelSim.FromIds(solution.Actions)!;
        var st = RaphaelSim.Play(s, SimState.Start(s), plan.Take(6))!.Value;
        var sent = server.Solve(Request, st, CraftCondition.Good)!.Value;
        // One at a time.
        Assert.Null(server.Solve(Request, st, CraftCondition.Good));
        Assert.NotNull(server.NotReady(Request));
        var answer = await sent.Answer.WaitAsync(Replanning.Wait);
        Assert.Equal(ServeOutcome.Solved, answer.Outcome);
        Assert.Equal(sent.Id, answer.Id);
        var rest = plan.Skip(6).ToList();
        var cheap = ConditionReaction.Choose(s, st, rest, CraftCondition.Good);
        Assert.True(Replanning.Accept(s, st, rest, RaphaelSim.FromIds(answer.Actions)!, CraftCondition.Good, cheap, out var beaten) >= beaten);

        // Another recipe's request isn't served; ending it makes it unready.
        Assert.NotNull(server.NotReady(Request with { Control = 5000 }));
        server.End("test");
        Assert.NotNull(server.NotReady(Request));
        lock (lines) Assert.Contains(lines, l => l.StartsWith("serve: prepared in", StringComparison.Ordinal));
    }

    [Fact]
    public void A_raphael_cli_without_serve_falls_back_quietly()
    {
        // hostname.exe answers something else than "serve 1" and ends: no serve mode.
        var other = Path.Combine(Environment.SystemDirectory, "hostname.exe");
        if (!File.Exists(other)) return;
        var (s, _, _) = RaphaelSimTests.Rotations[0];
        var lines = new List<string>();
        using var server = new RaphaelServer(() => other, l => { lock (lines) lines.Add(l); });
        server.Prepare(Request, s);
        WaitUntil(() => server.Supported != null, TimeSpan.FromSeconds(10));
        Assert.False(server.Supported);
        Assert.StartsWith("Raphael's re-planning failed earlier", server.NotReady(Request));
        // Another recipe doesn't try it again, nor log again.
        int before;
        lock (lines) before = lines.Count;
        server.Prepare(Request with { RecipeId = 1 }, s);
        WaitUntil(() => server.NotReady(Request with { RecipeId = 1 })?.StartsWith("Raphael's re-planning failed", StringComparison.Ordinal) == true, TimeSpan.FromSeconds(5));
        Assert.StartsWith("Raphael's re-planning failed earlier", server.NotReady(Request with { RecipeId = 1 }));
        lock (lines) Assert.Equal(before, lines.Count);
    }
}
