using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Cosmic;
using Xunit;

namespace Provenance.Tests;

public class CosmicPickerTests
{
    private static CosmicMission Mission(uint id, int credits, MissionRank rank = MissionRank.A, params MissionStep[] steps)
        => new(id, "Mission " + id, Planet.SinusArdorum, 8, 0, rank, MissionKind.None, 0, 700, 1500,
            steps.Length > 0 ? steps : new[] { new MissionStep(100 + id, 200 + id, 48233, 1, 1) }, 48233, 5,
            new[] { new MissionTarget(steps.Length > 0 ? steps[^1].ItemId : 200 + id, 1) },
            StellarAction.None, 0, new MissionReward(credits, 10, 0));

    [Fact]
    public void Gold_missions_are_ranked_by_credits_per_hour()
    {
        var quick = Mission(1, 10);  // 10 credits base, 60 s
        var slow = Mission(2, 20);   // 20 credits base, 240 s
        var judgements = new Dictionary<uint, Judgement>
        {
            [1] = new(Tier.Gold, Basis.Estimated, "", 60),
            [2] = new(Tier.Gold, Basis.Estimated, "", 240),
        };
        var r = CosmicPicker.Rank(new[] { slow, quick }, m => judgements[m.Id], _ => null);
        Assert.False(r.SilverOnly);
        Assert.Equal(new uint[] { 1, 2 }, r.Choices.Select(c => c.Mission.Id));
        Assert.Equal(10 * 5 * 60, r.Choices[0].CreditsPerHour, 3);
    }

    [Fact]
    public void Silver_only_when_nothing_reaches_gold_and_bronze_never()
    {
        var a = Mission(1, 10);
        var b = Mission(2, 10);
        var c = Mission(3, 10);
        var judgements = new Dictionary<uint, Judgement>
        {
            [1] = new(Tier.Silver, Basis.Estimated, "best rotation reaches 2,900 of 3,400 quality", 60),
            [2] = new(Tier.Bronze, Basis.Estimated, "best rotation reaches 1,000 of 3,400 quality", 60),
            [3] = new(Tier.None, Basis.Estimated, "can't finish: progress falls short", 60),
        };
        var r = CosmicPicker.Rank(new[] { a, b, c }, m => judgements[m.Id], _ => null);
        Assert.True(r.SilverOnly);
        Assert.Equal(1u, r.Best!.Mission.Id);
        Assert.Contains(r.Skipped, s => s.Mission.Id == 2 && s.Reason.StartsWith("Only bronze"));
        Assert.Contains(r.Skipped, s => s.Mission.Id == 3 && s.Reason == "can't finish: progress falls short");
    }

    [Fact]
    public void Gold_found_puts_silver_ones_among_the_skipped()
    {
        var r = CosmicPicker.Rank(new[] { Mission(1, 50), Mission(2, 10) },
            m => new Judgement(m.Id == 1 ? Tier.Silver : Tier.Gold, Basis.Estimated, "x", 60), _ => null);
        Assert.Equal(2u, r.Best!.Mission.Id);
        Assert.Contains(r.Skipped, s => s.Mission.Id == 1 && s.Reason.StartsWith("Only silver"));
    }

    [Fact]
    public void Missions_still_being_worked_out_wait()
    {
        var r = CosmicPicker.Rank(new[] { Mission(1, 10) }, _ => null, _ => null);
        Assert.Null(r.Best);
        Assert.Equal("Still working out the best rotation", r.Skipped.Single().Reason);
    }

    [Fact]
    public void What_runs_paid_and_took_beats_the_estimate()
    {
        var rec = new MissionRecord();
        rec.Add(Tier.Gold, 120, 100, 5000);
        var r = CosmicPicker.Rank(new[] { Mission(1, 10) }, _ => new Judgement(Tier.Gold, Basis.Estimated, "", 60), _ => rec);
        Assert.Equal(120, r.Best!.Credits, 3);
        Assert.Equal(120 * 36, r.Best.CreditsPerHour, 3);
    }

    [Fact]
    public void Quality_alone_judges_until_runs_settle_it()
    {
        Assert.Equal((Tier.Gold, Basis.Estimated), CosmicPicker.PredictCraft(6000, 6000, null));
        Assert.Equal((Tier.Silver, Basis.Estimated), CosmicPicker.PredictCraft(4200, 6000, null));
        Assert.Equal((Tier.Bronze, Basis.Estimated), CosmicPicker.PredictCraft(4100, 6000, null));

        var rec = new MissionRecord();
        rec.Add(Tier.Gold, 100, 60, 4500);   // gold came at 4,500 already
        rec.Add(Tier.Silver, 80, 60, 3000);
        rec.Add(Tier.Bronze, 20, 60, 1000);
        Assert.Equal((Tier.Gold, Basis.Learned), CosmicPicker.PredictCraft(4600, 6000, rec));
        Assert.Equal((Tier.Silver, Basis.Learned), CosmicPicker.PredictCraft(3000, 6000, rec));
        Assert.Equal((Tier.Bronze, Basis.Learned), CosmicPicker.PredictCraft(900, 6000, rec));
        // Above the lowest silver: at least silver, even where the estimate alone would say bronze.
        Assert.Equal((Tier.Silver, Basis.Learned), CosmicPicker.PredictCraft(4000, 6000, rec));
        var silverOnly = new MissionRecord();
        silverOnly.Add(Tier.Silver, 80, 60, 3000);
        Assert.Equal((Tier.Silver, Basis.Learned), CosmicPicker.PredictCraft(3600, 6000, silverOnly));
        // Full quality with no gold seen yet above what fell short: the estimate's gold stands.
        Assert.Equal((Tier.Gold, Basis.Estimated), CosmicPicker.PredictCraft(6000, 6000, silverOnly));
    }

    [Fact]
    public void Crafts_count_the_steps_in_between()
    {
        // Data Entry Paper: 1 paper from 2 pulp, each pulp from 1 container.
        var m = Mission(10, 3, MissionRank.C,
            new MissionStep(36174, 48287, 48233, 1, 1),
            new MissionStep(36175, 48283, 48287, 2, 1));
        Assert.Equal(new[] { 2, 1 }, CosmicPicker.Crafts(m));
        // Three different items from the supplies, one each.
        var set = Mission(11, 3, MissionRank.A,
            new MissionStep(1, 501, 48233, 1, 1), new MissionStep(2, 502, 48233, 1, 1), new MissionStep(3, 503, 48233, 1, 1)) with
        {
            Targets = new[] { new MissionTarget(501, 1), new MissionTarget(502, 1), new MissionTarget(503, 2) },
        };
        Assert.Equal(new[] { 1, 1, 2 }, CosmicPicker.Crafts(set));
        Assert.Equal(20 + 2 * (10 * 3.0 + 4) + 1 * (20 * 3.0 + 4), CosmicPicker.CraftSeconds(m, new[] { 10, 20 }), 3);
    }

    [Fact]
    public void Ranks_open_with_level_and_missions_below()
    {
        Assert.True(CosmicPicker.RankOpen(MissionRank.D, 10, _ => 0, _ => 0));
        Assert.False(CosmicPicker.RankOpen(MissionRank.C, 50, r => r == MissionRank.D ? 2 : 0, _ => 0));
        Assert.True(CosmicPicker.RankOpen(MissionRank.C, 50, r => r == MissionRank.D ? 3 : 0, _ => 0));
        Assert.False(CosmicPicker.RankOpen(MissionRank.EX, 100, r => r == MissionRank.B ? 5 : 0, r => r == MissionRank.B ? 2 : 0));
        Assert.True(CosmicPicker.RankOpen(MissionRank.EXPlus, 100, r => r == MissionRank.B ? 5 : 0, r => r == MissionRank.B ? 3 : 0));
    }

    [Fact]
    public void Missions_belong_to_their_planets_block()
    {
        Assert.Equal(Planet.SinusArdorum, Planets.OfMission(10));
        Assert.Equal(Planet.Phaenna, Planets.OfMission(545));
        Assert.Equal(Planet.Oizys, Planets.OfMission(1369));
        Assert.Equal(Planet.Auxesia, Planets.OfMission(1395));
        Assert.Equal(Planet.Oizys, Planets.OfTerritory(1310));
        Assert.Null(Planets.OfTerritory(132));
    }
}
