using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Gathering;
using Xunit;

namespace Provenance.Tests;

public class GatheringTests
{
    private static readonly Dictionary<GatherAction, int> Lv100 = new()
    {
        [GatherAction.SolidAge] = 300, [GatherAction.Yield1] = 400, [GatherAction.Yield2] = 500,
        [GatherAction.Gift1] = 50, [GatherAction.Gift2] = 100, [GatherAction.Tidings] = 200,
        [GatherAction.Bountiful] = 100, [GatherAction.TwelvesBounty] = 150, [GatherAction.GivingLand] = 200,
    };

    [Fact]
    public async System.Threading.Tasks.Task Timed_node_spends_GP_on_yield()
    {
        var plan = await GatherSolver.SolveAsync(new GatherSolverInput
        {
            PlayerLevel = 100, MaxGp = 1000, Gp = 1000, MaxIntegrity = 4, Integrity = 4, Yield = 2,
            RegularNode = false, Learned = Lv100, BountifulYield = 3,
        });
        Assert.Contains(GatherAction.Yield2, plan);                     // King's Yield II on a timed node
        Assert.True(plan.Count(a => a == GatherAction.Gather) >= 4);     // every point of integrity gathered
        Assert.Equal(GatherAction.Gather, plan[^1]);                     // ends on a gather
    }

    [Fact]
    public async System.Threading.Tasks.Task Rare_items_are_just_gathered()
        => Assert.Empty(await GatherSolver.SolveAsync(new GatherSolverInput { IsRare = true, Integrity = 4, MaxIntegrity = 4, Learned = Lv100 }));

    [Fact]
    public async System.Threading.Tasks.Task Regular_node_uses_a_filler_and_gathers_every_integrity()
    {
        var plan = await GatherSolver.SolveAsync(new GatherSolverInput
        {
            PlayerLevel = 100, MaxGp = 1000, Gp = 400, MaxIntegrity = 4, Integrity = 4, Yield = 1, BountifulYield = 3,
            RegularNode = true, Learned = Lv100,
        });
        Assert.Equal(4, plan.Count(a => a == GatherAction.Gather));
        Assert.DoesNotContain(GatherAction.Yield2, plan);               // not worth 500 GP on a plain node
    }

    [Fact]
    public async System.Threading.Tasks.Task Giving_Land_on_crystals_when_ready()
    {
        var plan = await GatherSolver.SolveAsync(new GatherSolverInput
        {
            PlayerLevel = 100, MaxGp = 1000, Gp = 600, MaxIntegrity = 4, Integrity = 4, Yield = 5, IsCrystal = true,
            RegularNode = true, Learned = Lv100,
        });
        Assert.Equal(GatherAction.GivingLand, plan[0]);
    }

    private static readonly CollectableActions Acts = new(50, 0, 50, 100, 50, 0, 50, 200, 25, 300, 90);

    [Fact]
    public void Brazens_best_roll_does_not_stop_scrutiny()
    {
        // 600 of 1000, two integrity left: neither Scour (900) nor Meticulous (825) gets there in one
        // step. Only Brazen's luckiest roll would, so Scrutiny comes first instead of settling at 825.
        var s = new CollectableState(Collectability: 600, Integrity: 2, MaxIntegrity: 4, ScourGain: 300, MeticulousGain: 225, BrazenGainMax: 450,
            LowThreshold: 400, MidThreshold: 700, HighThreshold: 1000, Gp: 500, Level: 100, SolidAgeActive: false, ScrutinyActive: false,
            HighStandard: false, Standard: false, ItemsLeft: 1);
        Assert.Equal(GatherAction.Scrutiny, CollectableRotation.Next(s, Acts, fullRotation: true));
    }

    [Fact]
    public void Collectors_high_standard_makes_brazen_sure()
    {
        // 700 of 1000: Scour (900) and Meticulous (850) fall short, but High Standard makes Brazen's
        // +300 sure, so Brazen goes straight there and Scrutiny's GP is kept.
        var s = new CollectableState(Collectability: 700, Integrity: 2, MaxIntegrity: 4, ScourGain: 200, MeticulousGain: 150, BrazenGainMax: 300,
            LowThreshold: 400, MidThreshold: 700, HighThreshold: 1000, Gp: 200, Level: 100, SolidAgeActive: false, ScrutinyActive: false,
            HighStandard: true, Standard: false, ItemsLeft: 1);
        Assert.Equal(GatherAction.Brazen, CollectableRotation.Next(s, Acts, fullRotation: true));
    }

    [Fact]
    public void Collectors_standard_alone_does_not_make_brazen_sure()
    {
        // Only High Standard makes Brazen's gain sure; with Collector's Standard alone the 600-of-1000
        // case from above still wants Scrutiny, not a Meticulous that settles at 825.
        var s = new CollectableState(Collectability: 600, Integrity: 2, MaxIntegrity: 4, ScourGain: 300, MeticulousGain: 225, BrazenGainMax: 450,
            LowThreshold: 400, MidThreshold: 700, HighThreshold: 1000, Gp: 500, Level: 100, SolidAgeActive: false, ScrutinyActive: false,
            HighStandard: false, Standard: true, ItemsLeft: 1);
        Assert.Equal(GatherAction.Scrutiny, CollectableRotation.Next(s, Acts, fullRotation: true));
    }

    [Fact]
    public void High_standard_with_brazen_short_of_the_target_still_uses_scrutiny()
    {
        // 400 of 1000: even a sure Brazen (+300) only reaches 700, so Scrutiny first, then Brazen under it.
        var s = new CollectableState(Collectability: 400, Integrity: 2, MaxIntegrity: 4, ScourGain: 200, MeticulousGain: 150, BrazenGainMax: 300,
            LowThreshold: 400, MidThreshold: 700, HighThreshold: 1000, Gp: 500, Level: 100, SolidAgeActive: false, ScrutinyActive: false,
            HighStandard: true, Standard: false, ItemsLeft: 1);
        Assert.Equal(GatherAction.Scrutiny, CollectableRotation.Next(s, Acts, fullRotation: true));
        Assert.Equal(GatherAction.Brazen, CollectableRotation.Next(s with { ScrutinyActive = true }, Acts, true));
        // Without High Standard and without GP for Scrutiny: the sure Meticulous.
        Assert.Equal(GatherAction.Meticulous, CollectableRotation.Next(s with { HighStandard = false, Gp = 150 }, Acts, true));
    }

    [Fact]
    public void Collectables_reach_the_top_tier_then_collect()
    {
        var s = new CollectableState(Collectability: 0, Integrity: 4, MaxIntegrity: 4, ScourGain: 150, MeticulousGain: 100, BrazenGainMax: 200,
            LowThreshold: 400, MidThreshold: 600, HighThreshold: 800, Gp: 700, Level: 100, SolidAgeActive: false, ScrutinyActive: false,
            HighStandard: false, Standard: false, ItemsLeft: 1);
        Assert.Equal((800, 400), CollectableRotation.Scores(s));
        Assert.Equal(GatherAction.Scrutiny, CollectableRotation.Next(s, Acts, fullRotation: true));   // no single step reaches 800
        Assert.Equal(GatherAction.Meticulous, CollectableRotation.Next(s with { ScrutinyActive = true }, Acts, true));
        Assert.Equal(GatherAction.Collect, CollectableRotation.Next(s with { Collectability = 810 }, Acts, true));
        Assert.Equal(GatherAction.Collect, CollectableRotation.Next(s with { Collectability = 450, Integrity = 1 }, Acts, true)); // last point: take what we have
    }
}
