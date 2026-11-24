using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Character;

namespace Provenance.Core.Tribes;

/// <summary>
/// What the player has of one class: its level, whether a gearset is saved for it, that gearset's item level, and for
/// a combat job whether the run can fight on it.
/// </summary>
/// <param name="Fights">
/// A combat job Provenance's basic combat has a combo for (the jobs Settings, Monster drops offers). Handed in as data,
/// since the combos live with the game code. False for crafters and gatherers, and for a combat class without a combo
/// (a starting class without its job).
/// </param>
public readonly record struct ClassInfo(uint ClassJob, int Level, bool HasGearset, int ItemLevel, bool Fights = false);

/// <summary>The player's classes, as the choice of one for a quest sees them.</summary>
/// <param name="All">The crafters, Miner and Botanist, and the combat classes and jobs (<see cref="ClassInfo.Fights"/> says which the run fights on).</param>
/// <param name="Current">The class that's on.</param>
/// <param name="CombatJob">The job picked under Settings, Monster drops (0: none picked).</param>
public sealed record PlayerClasses(IReadOnlyList<ClassInfo> All, uint Current, uint CombatJob = 0)
{
    /// <summary>
    /// The classes that have been on since the run that's going started, the first first. Empty outside a run: then
    /// only <see cref="Current"/> counts as on. A run changes class between a crafter's and a battle society's visits
    /// and plans again after each; with this the day's dailies keep going to the classes it started with (the player's
    /// own first), so the crystals fetched for one crafter's recipes aren't left for another's, and a combat job that
    /// was on at the start still takes the battle dailies after a crafter's visit.
    /// </summary>
    public IReadOnlyList<uint> History { get; init; } = Array.Empty<uint>();

    /// <summary>
    /// How early a class was on: 0 for the first of the run (outside a run, for the current one), then counting up;
    /// <see cref="int.MaxValue"/> for a class that wasn't on.
    /// </summary>
    public int OnRank(uint classJob)
    {
        for (var i = 0; i < History.Count; i++)
            if (History[i] == classJob) return i;
        return classJob == Current ? History.Count : int.MaxValue;
    }

    /// <summary>The class is on, or was on earlier in the run.</summary>
    public bool WasOn(uint classJob) => OnRank(classJob) != int.MaxValue;

    /// <summary>What the player has of a class (null when it isn't one of <see cref="All"/>).</summary>
    public ClassInfo? Of(uint classJob)
    {
        foreach (var c in All)
            if (c.ClassJob == classJob) return c;
        return null;
    }
}

/// <summary>What a quest still needs before the run can take it.</summary>
public enum NeedKind
{
    None,
    /// <summary>No class that can take it is at its level.</summary>
    ClassLevel,
    /// <summary>A class is at the level but has no saved gearset to switch to.</summary>
    Gearset,
    /// <summary>An item the recipe wants equipped (Ixal: the Ehcatl Wristgloves) isn't owned.</summary>
    EquipItem,
    /// <summary>The crystals for the most crafts it may ask for aren't in the bags.</summary>
    Crystals,
    /// <summary>It fights, and no combat job is picked under Settings, Monster drops (or the one picked has no combo).</summary>
    CombatJob,
}

/// <summary>Whether a quest can be taken now, by which class, and what's missing (in plain words).</summary>
/// <param name="ClassJob">The class the run would take it on (0 when none can).</param>
public sealed record QuestReadiness(uint ClassJob, NeedKind Need, string? Text)
{
    public bool Ready => Need == NeedKind.None;

    /// <summary>What's missing is about the combat job that fights (a battle quest's class, or the fight in a crafter's daily).</summary>
    public bool AboutCombatJob { get; init; }
}

/// <summary>
/// Which class takes a society daily, and what it still needs: the class level (<c>Quest.ClassJobLevel[0]</c>), a gearset
/// to switch to, for crafting dailies the crystals and any item the recipe wants equipped, and for a daily with a fight
/// a combat job to fight on.
/// </summary>
/// <remarks>
/// <para>
/// Decided with the user: a quest locks to the class it's accepted on. A crafter's or gatherer's daily is taken on the
/// current class when it qualifies, otherwise the best-geared one that does; the Levelling goal takes the lowest class
/// that qualifies. Gatherer dailies are taken as Miner or Botanist only (the data has no spot or bait for the quest fish).
/// </para>
/// <para>
/// A battle society's daily (any Disciple of War or Magic but the limited jobs, <c>ClassJobCategory</c> 142) is taken on
/// the job picked under Settings, Monster drops, when it qualifies; else on the current job when that's a combat job
/// the basic combat has a combo for and it qualifies; else it isn't taken, and the page says what's missing. The
/// Levelling goal takes the lowest such combat job that's at least <see cref="LevellingMargin"/> levels above the
/// quest: the basic combat has little healing, and the ARR societies' quests aren't level-synced. When none is that
/// far above, the Monster drops job as usual.
/// </para>
/// <para>
/// An Ixal daily with a fight stays accepted as its crafter; for the fight the run changes to the Monster drops job
/// and back (the quest only moves on as the class it was accepted as). So besides its crafter it needs that job picked,
/// with a gearset saved, at level <see cref="CrafterFightLevel"/> or more (<see cref="Fighter"/>).
/// </para>
/// </remarks>
public static class TribeClasses
{
    private static readonly uint[] CrafterIds = { 8, 9, 10, 11, 12, 13, 14, 15 };
    private static readonly uint[] GathererIds = { 16, 17 };
    private static readonly uint[] EitherIds = CrafterIds.Concat(GathererIds).ToArray();

    /// <summary>
    /// Under the Levelling goal a battle quest goes to a combat job at least this many levels above the quest's level
    /// (decided with the user).
    /// </summary>
    public const int LevellingMargin = 5;

    /// <summary>
    /// The level the combat job needs for the fight in a crafter's daily (the Ixal's are accepted at crafter level 1,
    /// fight monsters of the North Shroud, Coerthas and Mor Dhona, and nothing syncs them): 50 or more, decided with
    /// the user.
    /// </summary>
    public const int CrafterFightLevel = 50;

    /// <summary>
    /// The crafters and gatherers a society quest may be taken on by the run (Fisher left out). Empty for a battle
    /// quest: its candidates are the player's combat jobs the basic combat has a combo for (<see cref="ClassInfo.Fights"/>).
    /// </summary>
    public static IReadOnlyList<uint> Candidates(TribeKind classes) => classes switch
    {
        TribeKind.Crafter => CrafterIds,
        TribeKind.Gatherer => GathererIds,
        TribeKind.Either => EitherIds,
        _ => Array.Empty<uint>(),
    };

    /// <summary>The class is one the run takes such a quest on, whatever its level.</summary>
    public static bool CanTake(ClassInfo c, TribeKind classes)
        => classes == TribeKind.Battle ? c.Fights : Candidates(classes).Contains(c.ClassJob);

    /// <summary>The class can take the quest: at its level, and either on now or with a gearset to switch to.</summary>
    public static bool Qualifies(ClassInfo c, TribeQuest quest, uint current)
        => c.Level >= Math.Max(1, quest.Level) && (c.HasGearset || c.ClassJob == current) && CanTake(c, quest.Classes);

    /// <summary>
    /// The classes the run may take a quest on, the one it prefers first; empty when none can.
    /// <list type="bullet">
    /// <item>A crafter's or gatherer's quest: every class that qualifies, the current one first, then the best-geared
    /// (then the highest level); under Levelling the lowest first.</item>
    /// <item>A battle quest: the Monster drops job when it qualifies, else the current job when it does. Under Levelling
    /// the combat jobs at least <see cref="LevellingMargin"/> levels above the quest, the lowest first; when none is,
    /// the same as without Levelling.</item>
    /// <item>In a run, a class that was on earlier counts as the current one does, the first of them first
    /// (<see cref="PlayerClasses.History"/>).</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<ClassInfo> Options(TribeQuest quest, PlayerClasses who, bool levelling = false)
    {
        var ok = who.All.Where(c => Qualifies(c, quest, who.Current)).ToList();
        if (quest.Classes != TribeKind.Battle) return Order(ok, who, levelling).ToList();
        if (levelling)
        {
            var above = ok.Where(c => c.Level >= quest.Level + LevellingMargin).ToList();
            if (above.Count > 0) return Order(above, who, true).ToList();
        }
        return Order(ok.Where(c => c.ClassJob == who.CombatJob || who.WasOn(c.ClassJob)), who, false).Take(1).ToList();
    }

    /// <summary>
    /// Classes in the order the run prefers them. Under Levelling the lowest level first. Otherwise the Monster drops
    /// job (for a fight), then the current class (in a run: the classes that have been on, the first first), then the
    /// best-geared, then the highest level.
    /// </summary>
    public static IEnumerable<ClassInfo> Order(IEnumerable<ClassInfo> classes, PlayerClasses who, bool levelling)
        => levelling
            ? classes.OrderBy(c => c.Level).ThenBy(c => c.ClassJob)
            : classes.OrderByDescending(c => c.Fights && c.ClassJob == who.CombatJob).ThenBy(c => who.OnRank(c.ClassJob))
                .ThenByDescending(c => c.ItemLevel).ThenByDescending(c => c.Level).ThenBy(c => c.ClassJob);

    /// <summary>The class the run takes a quest on (the first of <see cref="Options"/>); 0 when none can.</summary>
    public static uint Choose(TribeQuest quest, PlayerClasses who, bool levelling = false)
        => Options(quest, who, levelling).Select(c => c.ClassJob).FirstOrDefault();

    /// <summary>"a crafter", "a Miner or Botanist", "a crafter, Miner or Botanist".</summary>
    public static string Who(TribeKind classes) => classes switch
    {
        TribeKind.Crafter => "a crafter",
        TribeKind.Gatherer => "a Miner or Botanist",
        TribeKind.Either => "a crafter, Miner or Botanist",
        _ => "a combat class",
    };

    /// <summary>
    /// The quest has a fight on another class than the one it's accepted as: a crafter's daily with enemies in it (the
    /// Ixal's). A battle society's daily fights on the job it's accepted as.
    /// </summary>
    public static bool FightsOnAnotherJob(TribeQuest quest) => quest.Classes != TribeKind.Battle && quest.Has(StepKinds.Combat);

    /// <summary>
    /// The combat job the run changes to for the fight in a crafter's daily: the Monster drops job, when it's usable (a
    /// combo for it, a gearset saved, level <see cref="CrafterFightLevel"/> or more). 0 when the quest has no such fight,
    /// or the job isn't usable (<see cref="FighterNeed"/> says why).
    /// </summary>
    public static uint Fighter(TribeQuest quest, PlayerClasses who)
        => FightsOnAnotherJob(quest) && FighterNeed(quest, who) == null ? who.CombatJob : 0;

    /// <summary>
    /// What the fight in a crafter's daily still needs of the combat job (none picked, no combo, its level, a gearset);
    /// null when the quest has no such fight, or the job is usable.
    /// </summary>
    public static QuestReadiness? FighterNeed(TribeQuest quest, PlayerClasses who)
    {
        if (!FightsOnAnotherJob(quest)) return null;
        if (who.CombatJob == 0)
            return new QuestReadiness(0, NeedKind.CombatJob, "Has a fight: pick a combat job under Settings, Monster drops") { AboutCombatJob = true };
        var name = ClassJobs.Abbreviation(who.CombatJob);
        if (who.Of(who.CombatJob) is not { Fights: true } job)
            return new QuestReadiness(0, NeedKind.CombatJob, $"Has a fight, and Provenance has no combo for {name}: pick another combat job under Settings, Monster drops") { AboutCombatJob = true };
        if (job.Level < CrafterFightLevel)
            return new QuestReadiness(0, NeedKind.ClassLevel, $"Has a fight: {name} is below level {CrafterFightLevel}") { AboutCombatJob = true };
        // The run changes from the crafter to it and back, so being on it now isn't enough.
        if (!job.HasGearset)
            return new QuestReadiness(0, NeedKind.Gearset, $"Has a fight: no gearset saved for {name}") { AboutCombatJob = true };
        return null;
    }

    /// <summary>
    /// What the quest needs of the player's classes before the run can take it: a class at its level with a gearset,
    /// and for a fight a usable combat job. Null when a class can take it (<see cref="Choose"/>) and any fight is covered.
    /// </summary>
    public static QuestReadiness? ClassNeed(TribeQuest quest, PlayerClasses who, bool levelling = false)
    {
        var job = Choose(quest, who, levelling);
        if (job != 0) return FighterNeed(quest, who) is { } fight ? fight with { ClassJob = job } : null;

        var level = Math.Max(1, quest.Level);
        if (quest.Classes == TribeKind.Battle)
        {
            // What keeps the Monster drops job from it (the current job, had it qualified, would have been chosen).
            if (who.CombatJob == 0)
                return new QuestReadiness(0, NeedKind.CombatJob, "Pick a combat job under Settings, Monster drops") { AboutCombatJob = true };
            var name = ClassJobs.Abbreviation(who.CombatJob);
            if (who.Of(who.CombatJob) is not { Fights: true } picked)
                return new QuestReadiness(0, NeedKind.CombatJob, $"Provenance has no combo for {name}: pick another combat job under Settings, Monster drops") { AboutCombatJob = true };
            return picked.Level < level
                ? new QuestReadiness(0, NeedKind.ClassLevel, $"{name} is below level {level}") { AboutCombatJob = true }
                : new QuestReadiness(0, NeedKind.Gearset, $"No gearset saved for {name}") { AboutCombatJob = true };
        }
        var atLevel = who.All.Any(c => c.Level >= level && CanTake(c, quest.Classes));
        return atLevel
            ? new QuestReadiness(0, NeedKind.Gearset, $"Needs a saved gearset for {Who(quest.Classes)} at level {level}")
            : new QuestReadiness(0, NeedKind.ClassLevel, $"Needs {Who(quest.Classes)} at level {level}");
    }

    /// <summary>
    /// Whether the run could take the quest now, on which class, and the first thing missing: the class level, a
    /// gearset, a combat job for its fight, an item to equip, or crystals. A crafting daily's count is only known once
    /// accepted, so the crystals are checked for <see cref="QuestCraft.MostCrafts"/> crafts and the text says "up to".
    /// </summary>
    /// <param name="onHand">Crystals in the bags and the crystal bag.</param>
    /// <param name="owns">The player owns an item (bags, armoury chest or equipped).</param>
    /// <param name="name">An item's name; <paramref name="plural"/> its plural ("Wind Crystals").</param>
    public static QuestReadiness Readiness(TribeQuest quest, PlayerClasses who, bool levelling,
        Func<uint, int> onHand, Func<uint, bool> owns, Func<uint, string> name, Func<uint, string> plural)
    {
        if (ClassNeed(quest, who, levelling) is { } need) return need;
        var job = Choose(quest, who, levelling);
        // A gatherer on a Namazu daily gathers; only a crafter crafts.
        if (quest.Craft?.For(job) is { } recipe)
        {
            if (recipe.EquipItem != 0 && !owns(recipe.EquipItem))
                return new QuestReadiness(job, NeedKind.EquipItem, $"Needs {name(recipe.EquipItem)} equipped to craft it (the society's vendor sells it)");
            var missing = recipe.Crystals
                .Select(c => (c.ItemId, Need: c.Amount * QuestCraft.MostCrafts, Have: onHand(c.ItemId)))
                .Where(c => c.Have < c.Need).ToList();
            if (missing.Count > 0)
                return new QuestReadiness(job, NeedKind.Crystals,
                    "Needs up to " + string.Join(" and ", missing.Select(c => $"{c.Need} {plural(c.ItemId)}"))
                    + $" (you have {string.Join(" and ", missing.Select(c => c.Have.ToString("N0")))})");
        }
        return new QuestReadiness(job, NeedKind.None, null);
    }
}
