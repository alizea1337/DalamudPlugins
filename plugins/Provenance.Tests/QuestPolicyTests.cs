using System;
using Provenance.Core.Tribes;
using Xunit;

namespace Provenance.Tests;

public class QuestPolicyTests
{
    private static TribeQuest Quest(ushort number, uint tribe, StepKinds steps, TribeKind classes = TribeKind.Crafter,
        TribeQuestKind kind = TribeQuestKind.Daily)
        => new(0x10000u + number, tribe, "Quest " + number, kind, 3, 2, 1, 1, classes, 70, 60, steps,
            null, Array.Empty<QuestGather>(), 0, Array.Empty<uint>(), (0, 0), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>());

    [Fact]
    public void ACraftingDailyOfACrafterSocietyIsTheRuns()
    {
        var j = QuestPolicy.Judge(Quest(5336, 20, StepKinds.Craft), TribeKind.Crafter);
        Assert.True(j.RunCanDo);
        Assert.Null(j.Reason);
    }

    [Theory]
    [InlineData(3124)]
    [InlineData(2991)]
    [InlineData(3060)]
    [InlineData(3707)]
    [InlineData(2974)]
    [InlineData(3100)]
    public void MinigamesAreNeverAutomated(ushort number)
    {
        var j = QuestPolicy.Judge(Quest(number, 11, StepKinds.Snipe, TribeKind.Either), TribeKind.Either);
        Assert.Equal(PolicyRule.Never, j.Rule);
        Assert.Contains("minigame", j.Reason);
        Assert.Contains("doesn't fake", j.Reason);
    }

    [Fact]
    public void TheNineFateDailiesAreNeverAutomated()
    {
        foreach (var n in new ushort[] { 1245, 1264, 1284, 1336, 1364, 1372, 1379, 1396, 1409 })
        {
            var j = QuestPolicy.Judge(Quest(n, 1, StepKinds.None, TribeKind.Battle), TribeKind.Battle);
            Assert.Equal(PolicyRule.Never, j.Rule);
            Assert.StartsWith("A FATE quest", j.Reason);
        }
    }

    [Fact]
    public void IxalDeliveranceIsNeverAutomated()
    {
        var j = QuestPolicy.Judge(Quest(1569, QuestPolicy.IxalId, StepKinds.None), TribeKind.Crafter);
        Assert.Equal(PolicyRule.Never, j.Rule);
        Assert.Contains("Deliverance", j.Reason);
    }

    [Fact]
    public void BattleSocietiesComeLater()
    {
        var j = QuestPolicy.Judge(Quest(2200, 6, StepKinds.Combat, TribeKind.Battle), TribeKind.Battle);
        Assert.Equal(PolicyRule.BattleSociety, j.Rule);
        Assert.False(j.RunCanDo);
    }

    [Fact]
    public void OnlyIxalsCraftOnlyDailiesAreTheRuns()
    {
        Assert.True(QuestPolicy.Judge(Quest(1495, QuestPolicy.IxalId, StepKinds.Craft | StepKinds.KeyItem), TribeKind.Crafter).RunCanDo);
        var fight = QuestPolicy.Judge(Quest(1494, QuestPolicy.IxalId, StepKinds.Craft | StepKinds.Combat), TribeKind.Crafter);
        Assert.Equal(PolicyRule.IxalNotCraftOnly, fight.Rule);
        Assert.Contains("fight", fight.Reason);
        var say = QuestPolicy.Judge(Quest(1497, QuestPolicy.IxalId, StepKinds.Craft | StepKinds.Say), TribeKind.Crafter);
        Assert.Equal(PolicyRule.IxalNotCraftOnly, say.Rule);
        var gather = QuestPolicy.Judge(Quest(1498, QuestPolicy.IxalId, StepKinds.Craft | StepKinds.OtherClass), TribeKind.Crafter);
        Assert.Equal(PolicyRule.IxalNotCraftOnly, gather.Rule);
    }

    [Fact]
    public void ASayDailyOutsideIxalIsTheRunsUnlessLeftOut()
    {
        var q = Quest(3909, 14, StepKinds.Say);
        Assert.True(QuestPolicy.Judge(q, TribeKind.Crafter).RunCanDo);
        var j = QuestPolicy.Judge(q, TribeKind.Crafter, new PolicyOptions(LeaveOutSay: true));
        Assert.Equal(PolicyRule.LeftOutSay, j.Rule);
        Assert.Contains("/say", j.Reason);
    }

    [Fact]
    public void TheCannonDailiesWaitForAiming()
    {
        foreach (var n in new ushort[] { 3923, 3929 })
        {
            var j = QuestPolicy.Judge(Quest(n, 14, StepKinds.Mount | StepKinds.MountAction), TribeKind.Crafter);
            Assert.Equal(QuestPolicy.CannonAims, j.RunCanDo);
            if (!j.RunCanDo) Assert.Equal(PolicyRule.NeedsHint, j.Rule);
        }
        // Other mount dailies are the run's.
        Assert.True(QuestPolicy.Judge(Quest(4694, 17, StepKinds.Mount | StepKinds.MountAction), TribeKind.Crafter).RunCanDo);
    }

    [Fact]
    public void AGathererDailyTakenAsFisherIsTheirs()
    {
        var q = Quest(5261, 19, StepKinds.Gather | StepKinds.Fish, TribeKind.Gatherer);
        Assert.True(QuestPolicy.Judge(q, TribeKind.Gatherer).RunCanDo);
        Assert.True(QuestPolicy.Judge(q, TribeKind.Gatherer, new PolicyOptions(AcceptedAs: 16)).RunCanDo);
        var j = QuestPolicy.Judge(q, TribeKind.Gatherer, new PolicyOptions(AcceptedAs: 18));
        Assert.Equal(PolicyRule.TakenAsFisher, j.Rule);
        Assert.Contains("Miner or Botanist", j.Reason);
    }

    [Fact]
    public void RankAndStoryQuestsAreNotTheRunsYet()
    {
        Assert.Equal(PolicyRule.RankQuest, QuestPolicy.Judge(Quest(5331, 20, StepKinds.Emote, kind: TribeQuestKind.RankUp), TribeKind.Crafter).Rule);
        Assert.Equal(PolicyRule.StoryQuest, QuestPolicy.Judge(Quest(5330, 20, StepKinds.None, kind: TribeQuestKind.Unlock), TribeKind.Crafter).Rule);
        Assert.Equal(PolicyRule.StoryQuest, QuestPolicy.Judge(Quest(3800, 13, StepKinds.None, TribeKind.Gatherer, TribeQuestKind.Side), TribeKind.Gatherer).Rule);
    }

    [Fact]
    public void NeverComesBeforeEverythingElse()
    {
        // 3100 is a Namazu rank quest with the snipe minigame: the minigame is the reason given.
        var j = QuestPolicy.Judge(Quest(3100, 11, StepKinds.Snipe, TribeKind.Either, TribeQuestKind.RankUp), TribeKind.Either);
        Assert.Equal(PolicyRule.Never, j.Rule);
    }

    [Fact]
    public void EveryReasonIsPlainAndHasNoFullStop()
    {
        foreach (var reason in QuestPolicy.Never.Values)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.False(reason.EndsWith("."));
            Assert.True(char.IsUpper(reason[0]));
        }
    }
}
