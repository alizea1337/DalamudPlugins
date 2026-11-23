using System;
using System.Collections.Generic;
using System.Linq;

namespace Provenance.Core.Tribes;

/// <summary>What the player has of one class: its level, whether a gearset is saved for it, and that gearset's item level.</summary>
public readonly record struct ClassInfo(uint ClassJob, int Level, bool HasGearset, int ItemLevel);

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
}

/// <summary>Whether a quest can be taken now, by which class, and what's missing (in plain words).</summary>
/// <param name="ClassJob">The class the run would take it on (0 when none can).</param>
public sealed record QuestReadiness(uint ClassJob, NeedKind Need, string? Text)
{
    public bool Ready => Need == NeedKind.None;
}

/// <summary>
/// Which class takes a society daily, and what it still needs: the class level (<c>Quest.ClassJobLevel[0]</c>), a gearset
/// to switch to, and for crafting dailies the crystals and any item the recipe wants equipped.
/// </summary>
/// <remarks>
/// Decided with the user: a quest locks to the class it's accepted on; the run takes the current class when it
/// qualifies, otherwise the best-geared one that does; the Levelling goal takes the lowest class that qualifies.
/// Gatherer dailies are taken as Miner or Botanist only (the data has no spot or bait for the quest fish).
/// </remarks>
public static class TribeClasses
{
    private static readonly uint[] CrafterIds = { 8, 9, 10, 11, 12, 13, 14, 15 };
    private static readonly uint[] GathererIds = { 16, 17 };
    private static readonly uint[] EitherIds = CrafterIds.Concat(GathererIds).ToArray();

    /// <summary>The classes a society quest may be taken on by the run (Fisher left out).</summary>
    public static IReadOnlyList<uint> Candidates(TribeKind classes) => classes switch
    {
        TribeKind.Crafter => CrafterIds,
        TribeKind.Gatherer => GathererIds,
        TribeKind.Either => EitherIds,
        _ => Array.Empty<uint>(),
    };

    /// <summary>The class can take the quest: at its level, and either on now or with a gearset to switch to.</summary>
    public static bool Qualifies(ClassInfo c, TribeQuest quest, uint current)
        => c.Level >= Math.Max(1, quest.Level) && (c.HasGearset || c.ClassJob == current) && Candidates(quest.Classes).Contains(c.ClassJob);

    /// <summary>
    /// The class the run takes a quest on: the current class when it qualifies, otherwise the best-geared one that does
    /// (then the highest level); under Levelling the lowest one that qualifies. 0 when none does.
    /// </summary>
    public static uint Choose(TribeQuest quest, IReadOnlyList<ClassInfo> classes, uint current, bool levelling = false)
    {
        var ok = classes.Where(c => Qualifies(c, quest, current)).ToList();
        if (ok.Count == 0) return 0;
        if (levelling) return ok.OrderBy(c => c.Level).ThenBy(c => c.ClassJob).First().ClassJob;
        if (ok.Any(c => c.ClassJob == current)) return current;
        return ok.OrderByDescending(c => c.ItemLevel).ThenByDescending(c => c.Level).ThenBy(c => c.ClassJob).First().ClassJob;
    }

    /// <summary>"a crafter", "a Miner or Botanist", "a crafter, Miner or Botanist".</summary>
    public static string Who(TribeKind classes) => classes switch
    {
        TribeKind.Crafter => "a crafter",
        TribeKind.Gatherer => "a Miner or Botanist",
        TribeKind.Either => "a crafter, Miner or Botanist",
        _ => "a combat class",
    };

    /// <summary>
    /// Whether the run could take the quest now, on which class, and the first thing missing: the class level, a
    /// gearset, an item to equip, or crystals. A crafting daily's count is only known once accepted, so the crystals are
    /// checked for <see cref="QuestCraft.MostCrafts"/> crafts and the text says "up to".
    /// </summary>
    /// <param name="onHand">Crystals in the bags and the crystal bag.</param>
    /// <param name="owns">The player owns an item (bags, armoury chest or equipped).</param>
    /// <param name="name">An item's name; <paramref name="plural"/> its plural ("Wind Crystals").</param>
    public static QuestReadiness Readiness(TribeQuest quest, IReadOnlyList<ClassInfo> classes, uint current, bool levelling,
        Func<uint, int> onHand, Func<uint, bool> owns, Func<uint, string> name, Func<uint, string> plural)
    {
        var level = Math.Max(1, quest.Level);
        var job = Choose(quest, classes, current, levelling);
        if (job == 0)
        {
            var atLevel = classes.Any(c => c.Level >= level && Candidates(quest.Classes).Contains(c.ClassJob));
            return atLevel
                ? new QuestReadiness(0, NeedKind.Gearset, $"Needs a saved gearset for {Who(quest.Classes)} at level {level}")
                : new QuestReadiness(0, NeedKind.ClassLevel, $"Needs {Who(quest.Classes)} at level {level}");
        }
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
