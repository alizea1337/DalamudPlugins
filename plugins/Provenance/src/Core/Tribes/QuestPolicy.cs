using System.Collections.Generic;

namespace Provenance.Core.Tribes;

/// <summary>Why a quest is the player's to do (or nothing: the run can do it).</summary>
public enum PolicyRule
{
    /// <summary>The run can do it.</summary>
    Run,
    /// <summary>Never automated, whatever the version (minigames, FATEs, Ixal "Deliverance").</summary>
    Never,
    /// <summary>A battle society: comes with step 4.</summary>
    BattleSociety,
    /// <summary>An Ixal daily with more than crafting in it (a fight, a /say, another class's step).</summary>
    IxalNotCraftOnly,
    /// <summary>A step the engine can't do reliably yet (aiming the Dwarves' cannon).</summary>
    NeedsHint,
    /// <summary>A rank quest: the page offers a button for it in a later version.</summary>
    RankQuest,
    /// <summary>A story quest that isn't a daily or a rank quest (unlock and side quests).</summary>
    StoryQuest,
    /// <summary>Says something in /say, and the option to leave those out is on.</summary>
    LeftOutSay,
    /// <summary>A gatherer daily already taken as Fisher.</summary>
    TakenAsFisher,
}

/// <summary>A verdict on one quest: the run does it, or the player does, and why (in plain words for the page and the log).</summary>
public sealed record QuestJudgement(PolicyRule Rule, string? Reason)
{
    public bool RunCanDo => Rule == PolicyRule.Run;
    public static readonly QuestJudgement Run = new(PolicyRule.Run, null);
}

/// <summary>The settings and live facts a verdict depends on (the rest comes from the quest's data).</summary>
/// <param name="LeaveOutSay">The Tribes option "Leave out quests that /say in public".</param>
/// <param name="AcceptedAs">The class an accepted quest was taken on (0 when it isn't accepted).</param>
public readonly record struct PolicyOptions(bool LeaveOutSay = false, uint AcceptedAs = 0);

/// <summary>
/// Which society quests step 2's run can do and why not, as one table, so the page, the planner and the run agree.
/// </summary>
/// <remarks>
/// <para>Decided with the user (see the Tribes build plan):</para>
/// <list type="bullet">
/// <item>Never automated: the snipe and bow minigames (the only automation known fakes the minigame's result), the nine
/// ARR FATE dailies, and Ixal "Deliverance" (a quota of hand-ins, not quest steps). These aren't accepted.</item>
/// <item>Not in this version: battle societies, Ixal dailies with anything besides crafting (a fight, a /say, another
/// class's gathering or fishing step), and gatherer dailies on Fisher (the data has no spot or bait for the quest
/// fish), so those are taken as Miner or Botanist only.</item>
/// <item>The Dwarves' "Demolition Demo" (3923) and "Substitute Tester" (3929) fire the Rolling Tankard's cannon, a line
/// in front of the mount (Action 20121, cast type 12, 20 yalms): the mount has to face each target, and 3929 picks its
/// dummies at random. They count as doable only once the step engine turns the mount to face a target
/// (<see cref="CannonAims"/>).</item>
/// </list>
/// </remarks>
public static class QuestPolicy
{
    /// <summary>
    /// Whether the step engine turns a quest mount to face its target before an action that fires straight ahead
    /// (Cannonfire). False until the engine is shown to do it (queststeps research: a "small hint" case); flip it here
    /// and the two cannon dailies become doable everywhere.
    /// </summary>
    public const bool CannonAims = false;

    /// <summary>The two Dwarves dailies that fire the Rolling Tankard's cannon.</summary>
    public static readonly IReadOnlySet<ushort> CannonQuests = new HashSet<ushort> { 3923, 3929 };

    private const string Minigame = "A shooting minigame: Provenance doesn't fake its result";
    private const string Fate = "A FATE quest: the run doesn't take part in FATEs";

    /// <summary>
    /// The society quests with a snipe or bow minigame: Namazu's daily, three battle dailies, two rank quests. Not all of
    /// them name it in their script (2991 and 3707 have no <c>SNP</c> argument), so the data reads the minigame from here too.
    /// </summary>
    public static readonly IReadOnlySet<ushort> Minigames = new HashSet<ushort> { 3124, 2991, 3060, 3707, 2974, 3100 };

    /// <summary>Quests that are never accepted by a run, by quest number, with the reason.</summary>
    public static readonly IReadOnlyDictionary<ushort, string> Never = new Dictionary<ushort, string>
    {
        // Snipe and bow minigames (Minigames).
        [3124] = Minigame, [2991] = Minigame, [3060] = Minigame, [3707] = Minigame, [2974] = Minigame, [3100] = Minigame,
        // The nine ARR FATE dailies (Amalj'aa, Sylphs, Kobolds, Sahagin).
        [1245] = Fate, [1264] = Fate, [1284] = Fate, [1336] = Fate, [1364] = Fate, [1372] = Fate, [1379] = Fate, [1396] = Fate, [1409] = Fate,
        // Ixal "Deliverance": deliveries for quota points, with no quest steps to follow.
        [1569] = "Ixal \"Deliverance\": a quota of deliveries, not steps the run can follow",
    };

    /// <summary>Miner and Botanist: the classes gatherer dailies are taken on.</summary>
    public static bool GatherClass(uint classJob) => classJob is 16 or 17;

    /// <summary>The verdict on a quest of a society.</summary>
    public static QuestJudgement Judge(TribeQuest quest, TribeKind society, PolicyOptions options = default)
    {
        if (Never.TryGetValue(quest.Number, out var never)) return new QuestJudgement(PolicyRule.Never, never);
        if (society == TribeKind.Battle || quest.Classes == TribeKind.Battle)
            return new QuestJudgement(PolicyRule.BattleSociety, "A battle society's quest: those come in a later version");
        switch (quest.Kind)
        {
            case TribeQuestKind.RankUp:
                return new QuestJudgement(PolicyRule.RankQuest, "A rank quest: do it yourself for now, a button for it comes in a later version");
            case TribeQuestKind.Unlock or TribeQuestKind.Side:
                return new QuestJudgement(PolicyRule.StoryQuest, "A story quest: the run only does dailies");
        }
        if (quest.TribeId == IxalId)
        {
            if (quest.Has(StepKinds.Combat))
                return new QuestJudgement(PolicyRule.IxalNotCraftOnly, "Has a fight on a combat job: Ixal's fighting dailies come in a later version");
            if (quest.Has(StepKinds.OtherClass))
                return new QuestJudgement(PolicyRule.IxalNotCraftOnly, "Gathers or fishes partway through on another class: comes in a later version");
            if (quest.Has(StepKinds.Say))
                return new QuestJudgement(PolicyRule.IxalNotCraftOnly, "Ixal's dailies with a /say phrase come in a later version");
        }
        if (CannonQuests.Contains(quest.Number) && !CannonAims)
            return new QuestJudgement(PolicyRule.NeedsHint, "Fires the Rolling Tankard's cannon, which has to be aimed: Provenance can't aim it yet");
        if (options.AcceptedAs == FisherId && quest.Has(StepKinds.Gather | StepKinds.Fish))
            return new QuestJudgement(PolicyRule.TakenAsFisher, "Taken as Fisher: the run does gatherer dailies as Miner or Botanist only");
        if (options.LeaveOutSay && quest.Has(StepKinds.Say))
            return new QuestJudgement(PolicyRule.LeftOutSay, "Says a phrase in /say, and you chose to leave those out");
        return QuestJudgement.Run;
    }

    /// <summary>The verdict on a society's quest, with the society's kind.</summary>
    public static QuestJudgement Judge(TribeQuest quest, Tribe society, PolicyOptions options = default) => Judge(quest, society.Kind, options);

    /// <summary>The Ixal (<c>BeastTribe</c> 5): only their craft-only dailies are in this version.</summary>
    public const uint IxalId = 5;

    private const uint FisherId = 18;
}
