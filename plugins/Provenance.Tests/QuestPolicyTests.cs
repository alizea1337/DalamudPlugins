using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Tribes;
using Xunit;

namespace Provenance.Tests;

public class QuestPolicyTests
{
    private static TribeQuest Quest(ushort number, uint tribe, StepKinds steps, TribeKind classes = TribeKind.Crafter,
        TribeQuestKind kind = TribeQuestKind.Daily)
        => new(0x10000u + number, tribe, "Quest " + number, kind, 3, 2, 1, 1, classes, 70, 60, steps,
            null, Array.Empty<QuestGather>(), 0, Array.Empty<uint>(), (0, 0), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>());

    private static TribeQuest Battle(ushort number, uint tribe, StepKinds steps, TribeQuestKind kind = TribeQuestKind.Daily)
        => Quest(number, tribe, steps, TribeKind.Battle, kind);

    [Fact]
    public void ACraftingDailyOfACrafterSocietyIsTheRuns()
    {
        var j = QuestPolicy.Judge(Quest(5336, 20, StepKinds.Craft));
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
        var j = QuestPolicy.Judge(Quest(number, 11, StepKinds.Snipe, TribeKind.Either));
        Assert.Equal(PolicyRule.Never, j.Rule);
        Assert.Contains("minigame", j.Reason);
        Assert.Contains("doesn't fake", j.Reason);
    }

    [Fact]
    public void EveryMinigameIsInTheMinigameSetWhichTheDataReadsTheFlagFrom()
    {
        foreach (var (number, reason) in QuestPolicy.Never)
            Assert.Equal(reason.Contains("minigame"), QuestPolicy.Minigames.Contains(number));
        Assert.Equal(6, QuestPolicy.Minigames.Count);
    }

    [Fact]
    public void AMinigameTheListDoesntHaveYetIsNeverAutomatedEither()
    {
        // A later patch's: its script still names the minigame, which the data reads into the step kinds.
        var j = QuestPolicy.Judge(Battle(6001, 18, StepKinds.Snipe));
        Assert.Equal(PolicyRule.Never, j.Rule);
        Assert.Contains("minigame", j.Reason);
    }

    [Fact]
    public void TheNineFateDailiesAreNeverAutomated()
    {
        foreach (var n in new ushort[] { 1245, 1264, 1284, 1336, 1364, 1372, 1379, 1396, 1409 })
        {
            var j = QuestPolicy.Judge(Battle(n, 1, StepKinds.Combat));
            Assert.Equal(PolicyRule.Never, j.Rule);
            Assert.StartsWith("A FATE quest", j.Reason);
        }
    }

    [Fact]
    public void IxalDeliveranceIsNeverAutomated()
    {
        var j = QuestPolicy.Judge(Quest(1569, QuestPolicy.IxalId, StepKinds.None));
        Assert.Equal(PolicyRule.Never, j.Rule);
        Assert.Contains("Deliverance", j.Reason);
    }

    // ------------------------------------------------------------------ battle societies

    [Theory]
    // Field monsters (Amalj'aa "Elite Beating"), enemies that appear at a marked spot with a key item for the weakened
    // ones (Kojin "Disciplinary Action"), a quest mount's action (Vanu Vanu), a plain errand (Pelupelu), a /say (Pixies).
    [InlineData(1242, 1, StepKinds.Combat)]
    [InlineData(3002, 9, StepKinds.Combat | StepKinds.ItemOnEnemy | StepKinds.KeyItem | StepKinds.UseItem | StepKinds.EnterArea)]
    [InlineData(2200, 6, StepKinds.Mount | StepKinds.MountAction)]
    [InlineData(5204, 18, StepKinds.Carry)]
    [InlineData(3691, 12, StepKinds.Say)]
    public void ABattleSocietysDailyIsTheRuns(ushort number, uint tribe, StepKinds steps)
    {
        var j = QuestPolicy.Judge(Battle(number, tribe, steps));
        Assert.True(j.RunCanDo);
        Assert.Equal(PolicyRule.Run, j.Rule);
        Assert.Null(j.Reason);
    }

    [Fact]
    public void TheSocietysKindChangesNoVerdict()
    {
        // The overload that takes the society's kind (older callers) gives the same verdicts.
        var fight = Battle(1242, 1, StepKinds.Combat);
        Assert.True(QuestPolicy.Judge(fight, TribeKind.Battle).RunCanDo);
        Assert.True(QuestPolicy.Judge(fight, TribeKind.Crafter).RunCanDo);
        var stealth = Battle(1262, 2, StepKinds.Stealth);
        Assert.Equal(QuestPolicy.Judge(stealth), QuestPolicy.Judge(stealth, TribeKind.Battle));
    }

    [Theory]
    [InlineData(1262)]
    [InlineData(1273)]
    [InlineData(1281)]
    public void TheSylphStealthDailiesAreYoursForNow(ushort number)
    {
        var j = QuestPolicy.Judge(Battle(number, 2, StepKinds.Stealth | StepKinds.KeyItem | StepKinds.UseItem));
        Assert.Equal(PolicyRule.Stealth, j.Rule);
        Assert.False(j.RunCanDo);
        Assert.Contains("stealth", j.Reason);
        Assert.EndsWith("do it yourself for now", j.Reason);
    }

    [Fact]
    public void ABattleDailyWithASayIsLeftOutWhenYouChoseTo()
    {
        var q = Battle(2277, 7, StepKinds.Say);
        Assert.True(QuestPolicy.Judge(q).RunCanDo);
        Assert.Equal(PolicyRule.LeftOutSay, QuestPolicy.Judge(q, new PolicyOptions(LeaveOutSay: true)).Rule);
    }

    [Fact]
    public void ABattleSocietysRankAndStoryQuestsAreNotTheRunsYet()
    {
        var rank = QuestPolicy.Judge(Battle(1219, 1, StepKinds.Combat | StepKinds.Mount, TribeQuestKind.RankUp));
        Assert.Equal(PolicyRule.RankQuest, rank.Rule);
        Assert.Contains("do it yourself for now", rank.Reason);
        Assert.Equal(PolicyRule.StoryQuest, QuestPolicy.Judge(Battle(1218, 1, StepKinds.Combat, TribeQuestKind.Unlock)).Rule);
        // The Sylphs' stealth rank quest: a rank quest first.
        Assert.Equal(PolicyRule.RankQuest, QuestPolicy.Judge(Battle(1254, 2, StepKinds.Stealth | StepKinds.Carry, TribeQuestKind.RankUp)).Rule);
    }

    // ------------------------------------------------------------------ the Ixal

    [Fact]
    public void IxalDailiesWithAFightOrASayAreTheRunsNow()
    {
        Assert.True(QuestPolicy.Judge(Quest(1495, QuestPolicy.IxalId, StepKinds.Craft | StepKinds.KeyItem)).RunCanDo);
        Assert.True(QuestPolicy.Judge(Quest(1494, QuestPolicy.IxalId, StepKinds.Craft | StepKinds.Combat)).RunCanDo);
        Assert.True(QuestPolicy.Judge(Quest(1506, QuestPolicy.IxalId, StepKinds.Craft | StepKinds.Combat | StepKinds.ItemOnEnemy)).RunCanDo);
        var say = Quest(1497, QuestPolicy.IxalId, StepKinds.Craft | StepKinds.Say);
        Assert.True(QuestPolicy.Judge(say).RunCanDo);
        // The /say option holds for them as for any society.
        Assert.Equal(PolicyRule.LeftOutSay, QuestPolicy.Judge(say, new PolicyOptions(LeaveOutSay: true)).Rule);
    }

    [Theory]
    // The nine with a Miner's, Botanist's or Fisher's step partway through.
    [InlineData(1498)]
    [InlineData(1499)]
    [InlineData(1509)]
    [InlineData(1510)]
    [InlineData(1511)]
    [InlineData(1514)]
    [InlineData(1520)]
    [InlineData(1521)]
    [InlineData(1522)]
    public void IxalDailiesThatGatherOrFishOnAnotherClassAreYoursForNow(ushort number)
    {
        var j = QuestPolicy.Judge(Quest(number, QuestPolicy.IxalId, StepKinds.Craft | StepKinds.KeyItem | StepKinds.OtherClass));
        Assert.Equal(PolicyRule.OtherClass, j.Rule);
        Assert.False(j.RunCanDo);
        Assert.Contains("Miner, Botanist or Fisher", j.Reason);
        Assert.EndsWith("do it yourself for now", j.Reason);
    }

    // ------------------------------------------------------------------ errands

    [Fact]
    public void ASayDailyOutsideIxalIsTheRunsUnlessLeftOut()
    {
        var q = Quest(3909, 14, StepKinds.Say);
        Assert.True(QuestPolicy.Judge(q).RunCanDo);
        var j = QuestPolicy.Judge(q, new PolicyOptions(LeaveOutSay: true));
        Assert.Equal(PolicyRule.LeftOutSay, j.Rule);
        Assert.Contains("/say", j.Reason);
    }

    [Fact]
    public void TheCannonDailiesWaitForAiming()
    {
        foreach (var n in new ushort[] { 3923, 3929 })
        {
            var j = QuestPolicy.Judge(Quest(n, 14, StepKinds.Mount | StepKinds.MountAction));
            Assert.Equal(QuestPolicy.CannonAims, j.RunCanDo);
            if (!j.RunCanDo) Assert.Equal(PolicyRule.NeedsHint, j.Rule);
        }
        // Other mount dailies are the run's.
        Assert.True(QuestPolicy.Judge(Quest(4694, 17, StepKinds.Mount | StepKinds.MountAction)).RunCanDo);
    }

    [Fact]
    public void AnEmoteTheQuestNamesNobodyForIsYours()
    {
        // What the data marks: the line with the emote points at an area or an object (Kobolds 1333, Kojin 2981).
        foreach (var q in new[] { Battle(1333, 3, StepKinds.Emote | StepKinds.EmoteUnnamed), Battle(2981, 9, StepKinds.Emote | StepKinds.EmoteUnnamed | StepKinds.EnterArea) })
        {
            var j = QuestPolicy.Judge(q);
            Assert.Equal(PolicyRule.NeedsHint, j.Rule);
            Assert.Contains("emote", j.Reason);
            Assert.Contains("do this one yourself for now", j.Reason);
        }
        // The first two found stay refused by number, whatever the data marks.
        foreach (var n in QuestPolicy.EmoteAtArea)
            Assert.Equal(PolicyRule.NeedsHint, QuestPolicy.Judge(Quest(n, 8, StepKinds.Emote | StepKinds.Craft)).Rule);
        // An emote at someone the line names is the run's (Dwarves 3919, Loporrits 4690).
        Assert.True(QuestPolicy.Judge(Quest(3919, 14, StepKinds.Emote)).RunCanDo);
    }

    [Fact]
    public void AGathererDailyTakenAsFisherIsTheirs()
    {
        var q = Quest(5261, 19, StepKinds.Gather | StepKinds.Fish, TribeKind.Gatherer);
        Assert.True(QuestPolicy.Judge(q).RunCanDo);
        Assert.True(QuestPolicy.Judge(q, new PolicyOptions(AcceptedAs: 16)).RunCanDo);
        var j = QuestPolicy.Judge(q, new PolicyOptions(AcceptedAs: 18));
        Assert.Equal(PolicyRule.TakenAsFisher, j.Rule);
        Assert.Contains("Miner or Botanist", j.Reason);
    }

    [Fact]
    public void RankAndStoryQuestsAreNotTheRunsYet()
    {
        Assert.Equal(PolicyRule.RankQuest, QuestPolicy.Judge(Quest(5331, 20, StepKinds.Emote, kind: TribeQuestKind.RankUp)).Rule);
        Assert.Equal(PolicyRule.StoryQuest, QuestPolicy.Judge(Quest(5330, 20, StepKinds.None, kind: TribeQuestKind.Unlock)).Rule);
        Assert.Equal(PolicyRule.StoryQuest, QuestPolicy.Judge(Quest(3800, 13, StepKinds.None, TribeKind.Gatherer, TribeQuestKind.Side)).Rule);
    }

    [Fact]
    public void NeverComesBeforeEverythingElse()
    {
        // 3100 is a Namazu rank quest with the snipe minigame: the minigame is the reason given.
        var j = QuestPolicy.Judge(Quest(3100, 11, StepKinds.Snipe, TribeKind.Either, TribeQuestKind.RankUp));
        Assert.Equal(PolicyRule.Never, j.Rule);
        // 2974 is the Kojin's: the same.
        Assert.Equal(PolicyRule.Never, QuestPolicy.Judge(Battle(2974, 9, StepKinds.Snipe, TribeQuestKind.RankUp)).Rule);
    }

    [Fact]
    public void EveryReasonIsPlainAndHasNoFullStop()
    {
        var reasons = new List<string>(QuestPolicy.Never.Values);
        // Every refusal a quest can get, each from a quest that gets it.
        var refused = new (TribeQuest Quest, PolicyOptions Options)[]
        {
            (Battle(1262, 2, StepKinds.Stealth), default),
            (Quest(1498, QuestPolicy.IxalId, StepKinds.Craft | StepKinds.OtherClass), default),
            (Quest(3923, 14, StepKinds.Mount | StepKinds.MountAction), default),
            (Battle(4574, 15, StepKinds.Emote | StepKinds.EmoteUnnamed), default),
            (Battle(1219, 1, StepKinds.Combat, TribeQuestKind.RankUp), default),
            (Battle(1218, 1, StepKinds.Combat, TribeQuestKind.Unlock), default),
            (Battle(2277, 7, StepKinds.Say), new PolicyOptions(LeaveOutSay: true)),
            (Quest(5261, 19, StepKinds.Gather | StepKinds.Fish, TribeKind.Gatherer), new PolicyOptions(AcceptedAs: 18)),
        };
        foreach (var (quest, options) in refused)
        {
            var j = QuestPolicy.Judge(quest, options);
            if (j.RunCanDo) continue; // the cannon dailies, once the engine aims
            reasons.Add(j.Reason!);
        }
        Assert.True(reasons.Count >= QuestPolicy.Never.Count + refused.Length - 1);
        foreach (var reason in reasons)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.False(reason.EndsWith("."));
            Assert.True(char.IsUpper(reason[0]));
            // Nothing is promised for "a later version" except the rank quests' button.
            Assert.True(!reason.Contains("later version") || reason.StartsWith("A rank quest"));
        }
        // Every rule but Run has a quest above (or in the never-list) that gets it.
        var rules = refused.Select(r => QuestPolicy.Judge(r.Quest, r.Options).Rule).Append(PolicyRule.Never).Distinct().ToHashSet();
        foreach (var rule in Enum.GetValues<PolicyRule>())
            Assert.True(rule == PolicyRule.Run || rules.Contains(rule), $"no quest in this test gets {rule}");
    }
}
