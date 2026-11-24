using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Tribes;
using Xunit;

namespace Provenance.Tests;

/// <summary>
/// The class a battle society's daily is taken on, and the combat job an Ixal daily's fight needs: the job picked
/// under Settings, Monster drops, the Levelling rule (five levels above the quest), and what the page says is missing.
/// </summary>
public class TribeBattleClassesTests
{
    private const uint Pld = 19, War = 21, Whm = 24, Blm = 25, Gla = 1, Crp = 8, Bsm = 9;

    /// <summary>A battle daily at a level: 48 the ARR Friendly givers', 60 the Kojin's and Ananta's, 90 the Pelupelu's.</summary>
    private static TribeQuest Battle(int level, StepKinds steps = StepKinds.Combat)
        => new(0x10000u + 1242, 1, "Elite Beating", TribeQuestKind.Daily, 3, 1, 1, 1, TribeKind.Battle, level, 20, steps,
            null, Array.Empty<QuestGather>(), 0, Array.Empty<uint>(), (0, 0), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>());

    /// <summary>An Ixal crafting daily (crafter level 1), with a fight in it or without.</summary>
    private static TribeQuest Ixal(bool fight)
        => new(0x10000u + 1494, QuestPolicy.IxalId, "Trust, but Verify", TribeQuestKind.Daily, 1, 2, 1, 1, TribeKind.Crafter, 1, 20,
            StepKinds.Craft | StepKinds.KeyItem | (fight ? StepKinds.Combat : 0),
            new QuestCraft(8091, 8053, new[] { new QuestRecipe(30511, Crp, 1, new[] { (4u, 1), (3u, 1) }, 8568, 547) }, false),
            Array.Empty<QuestGather>(), 0, Array.Empty<uint>(), (0, 0), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>());

    /// <summary>A combat job the basic combat has a combo for.</summary>
    private static ClassInfo Job(uint job, int level, bool gearset = true, int itemLevel = 600) => new(job, level, gearset, itemLevel, Fights: true);

    /// <summary>A crafter, or a combat class the basic combat has no combo for (a starting class).</summary>
    private static ClassInfo Other(uint job, int level, bool gearset = true) => new(job, level, gearset, 500);

    private static PlayerClasses Who(uint current, uint combatJob, params ClassInfo[] classes) => new(classes, current, combatJob);

    private static QuestReadiness Ready(TribeQuest q, PlayerClasses who, bool levelling = false, int crystals = 999, bool owns = true)
        => TribeClasses.Readiness(q, who, levelling, _ => crystals, _ => owns, id => "Item " + id, id => "Items " + id);

    // ------------------------------------------------------------------ the Monster drops job

    [Fact]
    public void ABattleDailyIsTakenOnTheMonsterDropsJob()
    {
        // On a crafter, with a better-geared Warrior around: the job picked is the one.
        var who = Who(Crp, Pld, Job(Pld, 90, itemLevel: 500), Job(War, 100, itemLevel: 700), Other(Crp, 100));
        Assert.Equal(Pld, TribeClasses.Choose(Battle(60), who));
        var r = Ready(Battle(60), who);
        Assert.True(r.Ready);
        Assert.Equal(Pld, r.ClassJob);
    }

    [Fact]
    public void TheMonsterDropsJobComesBeforeTheCurrentJob()
    {
        var who = Who(War, Pld, Job(Pld, 90), Job(War, 100));
        Assert.Equal(Pld, TribeClasses.Choose(Battle(60), who));
        Assert.Equal(new[] { Pld }, TribeClasses.Options(Battle(60), who).Select(c => c.ClassJob).ToArray());
    }

    [Fact]
    public void WithoutAUsableMonsterDropsJobTheCurrentCombatJobTakesIt()
    {
        // None picked.
        Assert.Equal(War, TribeClasses.Choose(Battle(60), Who(War, 0, Job(Pld, 90), Job(War, 100))));
        // The one picked is below the quest's level.
        Assert.Equal(War, TribeClasses.Choose(Battle(60), Who(War, Pld, Job(Pld, 55), Job(War, 100))));
        // The one picked has no gearset to change to.
        Assert.Equal(War, TribeClasses.Choose(Battle(60), Who(War, Pld, Job(Pld, 90, gearset: false), Job(War, 100))));
        // The current job needs no gearset: it's on.
        Assert.Equal(War, TribeClasses.Choose(Battle(60), Who(War, 0, Job(War, 100, gearset: false))));
    }

    [Fact]
    public void NoOtherJobIsPickedForYou()
    {
        // A Warrior at level 100 with a gearset, but neither picked nor on: the run doesn't choose a combat job itself.
        var who = Who(Crp, 0, Job(War, 100), Other(Crp, 100));
        Assert.Equal(0u, TribeClasses.Choose(Battle(60), who));
        Assert.Empty(TribeClasses.Options(Battle(60), who));
    }

    [Fact]
    public void ACombatClassWithoutAComboDoesntFight()
    {
        // On Gladiator (a starting class: no combo), nothing picked.
        var who = Who(Gla, 0, Other(Gla, 50), Job(War, 100));
        Assert.Equal(0u, TribeClasses.Choose(Battle(48), who));
        Assert.False(TribeClasses.CanTake(Other(Gla, 50), TribeKind.Battle));
        Assert.True(TribeClasses.CanTake(Job(War, 100), TribeKind.Battle));
        // Crafters never take a battle quest, combat jobs never a crafter's.
        Assert.False(TribeClasses.CanTake(Other(Crp, 100), TribeKind.Battle));
        Assert.False(TribeClasses.CanTake(Job(War, 100), TribeKind.Crafter));
    }

    [Fact]
    public void ABattleErrandNeedsTheCombatJobToo()
    {
        // No fight in it, but the quest only takes a Disciple of War or Magic.
        var errand = Battle(60, StepKinds.KeyItem);
        Assert.Equal(Pld, TribeClasses.Choose(errand, Who(Crp, Pld, Job(Pld, 90), Other(Crp, 100))));
        Assert.Equal(NeedKind.CombatJob, Ready(errand, Who(Crp, 0, Job(Pld, 90), Other(Crp, 100))).Need);
    }

    // ------------------------------------------------------------------ what's missing

    [Fact]
    public void NothingPickedSaysWhereToPick()
    {
        var r = Ready(Battle(60), Who(Crp, 0, Job(Pld, 90), Other(Crp, 100)));
        Assert.Equal(NeedKind.CombatJob, r.Need);
        Assert.Equal(0u, r.ClassJob);
        Assert.Equal("Pick a combat job under Settings, Monster drops", r.Text);
    }

    [Fact]
    public void TooLowSaysWhichJobAndWhichLevel()
    {
        var r = Ready(Battle(60), Who(Crp, Pld, Job(Pld, 59), Other(Crp, 100)));
        Assert.Equal(NeedKind.ClassLevel, r.Need);
        Assert.Equal("PLD is below level 60", r.Text);
        // At the level it's taken.
        Assert.True(Ready(Battle(60), Who(Crp, Pld, Job(Pld, 60), Other(Crp, 100))).Ready);
    }

    [Fact]
    public void NoGearsetSaysWhichJob()
    {
        var r = Ready(Battle(60), Who(Crp, Pld, Job(Pld, 90, gearset: false), Other(Crp, 100)));
        Assert.Equal(NeedKind.Gearset, r.Need);
        Assert.Equal("No gearset saved for PLD", r.Text);
    }

    [Fact]
    public void AJobWithoutAComboSaysToPickAnother()
    {
        // A settings file can name a job the combos don't know (or none of the player's).
        foreach (var who in new[] { Who(Crp, Gla, Other(Gla, 90), Other(Crp, 100)), Who(Crp, 36, Other(Crp, 100)) })
        {
            var r = Ready(Battle(60), who);
            Assert.Equal(NeedKind.CombatJob, r.Need);
            Assert.Contains("has no combo for", r.Text);
            Assert.EndsWith("pick another combat job under Settings, Monster drops", r.Text);
        }
    }

    [Fact]
    public void TheNeedsHaveNoFullStopAndNameTheJobByItsAbbreviation()
    {
        var texts = new[]
        {
            Ready(Battle(60), Who(Crp, 0)).Text, Ready(Battle(60), Who(Crp, Blm, Job(Blm, 10))).Text,
            Ready(Battle(60), Who(Crp, Whm, Job(Whm, 80, gearset: false))).Text, Ready(Ixal(true), Who(Crp, 0, Other(Crp, 50))).Text,
        };
        Assert.Equal("BLM is below level 60", texts[1]);
        Assert.Equal("No gearset saved for WHM", texts[2]);
        Assert.All(texts, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t));
            Assert.False(t!.EndsWith("."));
            Assert.True(char.IsUpper(t[0]));
        });
    }

    // ------------------------------------------------------------------ Levelling

    [Fact]
    public void LevellingTakesTheLowestCombatJobFiveLevelsAboveTheQuest()
    {
        // A level 60 quest: 64 is too close, 65 is the lowest that's far enough above; the Monster drops job is 100.
        var who = Who(Crp, Pld, Job(Pld, 100), Job(War, 64), Job(Whm, 65), Job(Blm, 80), Other(Crp, 100));
        Assert.Equal(Whm, TribeClasses.Choose(Battle(60), who, levelling: true));
        Assert.Equal(new[] { Whm, Blm, Pld }, TribeClasses.Options(Battle(60), who, levelling: true).Select(c => c.ClassJob).ToArray());
        // Without Levelling: the Monster drops job.
        Assert.Equal(Pld, TribeClasses.Choose(Battle(60), who));
    }

    [Fact]
    public void LevellingCountsFromTheQuestsOwnLevel()
    {
        // The ARR givers ask for 41 to 48: a job at 50 takes a level 43 quest (48 and up), not a level 48 one (53 and up).
        var who = Who(Crp, Pld, Job(Pld, 100), Job(War, 50), Other(Crp, 100));
        Assert.Equal(War, TribeClasses.Choose(Battle(43), who, levelling: true));
        Assert.Equal(War, TribeClasses.Choose(Battle(45), who, levelling: true));
        Assert.Equal(Pld, TribeClasses.Choose(Battle(48), who, levelling: true));
        Assert.Equal(5, TribeClasses.LevellingMargin);
    }

    [Fact]
    public void LevellingOnlyTakesJobsTheRunCanChangeToAndFightOn()
    {
        // The lowest far enough above has no gearset, the next is a starting class without a combo: the one after.
        var who = Who(Crp, Pld, Job(Pld, 100), Job(War, 66, gearset: false), Other(Gla, 67), Job(Blm, 70), Other(Crp, 100));
        Assert.Equal(Blm, TribeClasses.Choose(Battle(60), who, levelling: true));
        // Being on it counts as a gearset does.
        Assert.Equal(War, TribeClasses.Choose(Battle(60), who with { Current = War }, levelling: true));
    }

    [Fact]
    public void LevellingFallsBackToTheMonsterDropsJobWhenNoneIsFarEnoughAbove()
    {
        // A level 90 quest: nothing at 95 or more, so the Monster drops job at 92 as usual.
        var who = Who(Crp, Pld, Job(Pld, 92), Job(War, 94), Other(Crp, 100));
        Assert.Equal(Pld, TribeClasses.Choose(Battle(90), who, levelling: true));
        // And what's missing reads as without Levelling.
        var r = Ready(Battle(90), Who(Crp, Pld, Job(Pld, 80), Other(Crp, 100)), levelling: true);
        Assert.Equal("PLD is below level 90", r.Text);
    }

    [Fact]
    public void LevellingBetweenJobsOfOneLevelTakesTheMonsterDropsJobFirst()
    {
        // Paladin, Warrior and White Mage all at 70; the lowest row (Paladin) used to win.
        var who = Who(Crp, Whm, Job(Pld, 70, itemLevel: 700), Job(War, 70, itemLevel: 720), Job(Whm, 70, itemLevel: 600), Job(Blm, 90), Other(Crp, 100));
        Assert.Equal(Whm, TribeClasses.Choose(Battle(60), who, levelling: true));
        Assert.Equal(new[] { Whm, War, Pld, Blm }, TribeClasses.Options(Battle(60), who, levelling: true).Select(c => c.ClassJob).ToArray());
        // A lower job still comes before it: the level decides first.
        var lower = Who(Crp, Whm, Job(Pld, 70), Job(Whm, 70), Job(Blm, 66), Other(Crp, 100));
        Assert.Equal(Blm, TribeClasses.Choose(Battle(60), lower, levelling: true));
    }

    [Fact]
    public void LevellingBetweenJobsOfOneLevelThenTakesTheBetterGeared()
    {
        // The Monster drops job (Black Mage) is higher: among the two at 70 the better-geared one, then the lower row.
        var who = Who(Crp, Blm, Job(Pld, 70, itemLevel: 500), Job(War, 70, itemLevel: 520), Job(Blm, 90), Other(Crp, 100));
        Assert.Equal(War, TribeClasses.Choose(Battle(60), who, levelling: true));
        var even = Who(Crp, Blm, Job(War, 70, itemLevel: 500), Job(Pld, 70, itemLevel: 500), Job(Blm, 90), Other(Crp, 100));
        Assert.Equal(Pld, TribeClasses.Choose(Battle(60), even, levelling: true));
        // Crafters of one level: the better geared as well.
        var crafters = Who(Pld, Pld, Job(Pld, 100), new ClassInfo(Crp, 20, true, 100), new ClassInfo(Bsm, 20, true, 130));
        Assert.Equal(Bsm, TribeClasses.Choose(Ixal(false), crafters, levelling: true));
    }

    [Fact]
    public void LevellingStillTakesACraftersDailyOnTheLowestCrafter()
    {
        var who = Who(Crp, Pld, Job(Pld, 100), new ClassInfo(Crp, 60, true, 500), new ClassInfo(Bsm, 20, true, 300));
        Assert.Equal(Bsm, TribeClasses.Choose(Ixal(false), who, levelling: true));
    }

    // ------------------------------------------------------------------ the Ixal's fights

    [Fact]
    public void AnIxalDailyWithAFightIsTakenOnItsCrafterAndFoughtOnTheMonsterDropsJob()
    {
        var who = Who(Pld, Pld, Job(Pld, 50), Other(Crp, 30));
        Assert.Equal(Crp, TribeClasses.Choose(Ixal(true), who));
        Assert.Equal(Pld, TribeClasses.Fighter(Ixal(true), who));
        Assert.Null(TribeClasses.FighterNeed(Ixal(true), who));
        var r = Ready(Ixal(true), who);
        Assert.True(r.Ready);
        Assert.Equal(Crp, r.ClassJob);
    }

    [Fact]
    public void AnIxalDailyWithoutAFightNeedsNoCombatJob()
    {
        var who = Who(Crp, 0, Other(Crp, 30));
        Assert.True(Ready(Ixal(false), who).Ready);
        Assert.Equal(0u, TribeClasses.Fighter(Ixal(false), who));
        Assert.False(TribeClasses.FightsOnAnotherJob(Ixal(false)));
        Assert.True(TribeClasses.FightsOnAnotherJob(Ixal(true)));
        // A battle society's daily fights on the job it's accepted as.
        Assert.False(TribeClasses.FightsOnAnotherJob(Battle(60)));
        Assert.Equal(0u, TribeClasses.Fighter(Battle(60), Who(Pld, Pld, Job(Pld, 90))));
    }

    [Fact]
    public void ItsFightNeedsACombatJobPicked()
    {
        var who = Who(Crp, 0, Job(War, 100), Other(Crp, 30));
        var r = Ready(Ixal(true), who);
        Assert.Equal(NeedKind.CombatJob, r.Need);
        Assert.Equal("Has a fight: pick a combat job under Settings, Monster drops", r.Text);
        // The crafter it would be taken on is still known (the page shows its item).
        Assert.Equal(Crp, r.ClassJob);
        Assert.Equal(0u, TribeClasses.Fighter(Ixal(true), who));
    }

    [Fact]
    public void ItsFightNeedsTheJobAtLevelFifty()
    {
        var r = Ready(Ixal(true), Who(Crp, Pld, Job(Pld, 49), Other(Crp, 30)));
        Assert.Equal(NeedKind.ClassLevel, r.Need);
        Assert.Equal("Has a fight: PLD is below level 50", r.Text);
        Assert.Equal(50, TribeClasses.CrafterFightLevel);
    }

    [Fact]
    public void ItsFightNeedsAGearsetSavedEvenWhenTheJobIsOn()
    {
        // The run changes to the crafter to accept, to the combat job to fight, and back: both need a gearset.
        var who = Who(Pld, Pld, Job(Pld, 90, gearset: false), Other(Crp, 30));
        var r = Ready(Ixal(true), who);
        Assert.Equal(NeedKind.Gearset, r.Need);
        Assert.Equal("Has a fight: no gearset saved for PLD", r.Text);
        // The same job takes a battle daily while it's on.
        Assert.Equal(Pld, TribeClasses.Choose(Battle(60), who));
    }

    [Fact]
    public void TheCraftersOwnNeedsComeFirstAndTheCrystalsLast()
    {
        // No crafter at all: that's said, not the fight.
        var none = Ready(Ixal(true), Who(Pld, 0, Job(Pld, 90)));
        Assert.Equal(NeedKind.ClassLevel, none.Need);
        Assert.Equal("Needs a crafter at level 1", none.Text);
        // Crafter and combat job fine, the wristgloves missing, then the crystals.
        var who = Who(Crp, Pld, Job(Pld, 90), Other(Crp, 30));
        Assert.Equal(NeedKind.EquipItem, Ready(Ixal(true), who, owns: false).Need);
        Assert.Equal(NeedKind.Crystals, Ready(Ixal(true), who, crystals: 0).Need);
        // The fight's need comes before both.
        Assert.Equal(NeedKind.CombatJob, Ready(Ixal(true), who with { CombatJob = 0 }, crystals: 0, owns: false).Need);
    }

    [Fact]
    public void TheClassNeedLeavesCrystalsAndGearToTheRun()
    {
        var who = Who(Crp, Pld, Job(Pld, 90), Other(Crp, 30));
        Assert.Null(TribeClasses.ClassNeed(Ixal(true), who));
        Assert.Null(TribeClasses.ClassNeed(Battle(60), who));
        Assert.Equal(NeedKind.ClassLevel, TribeClasses.ClassNeed(Battle(95), who)!.Need);
    }

    [Fact]
    public void PlayerClassesFindsAClass()
    {
        var who = Who(Crp, Pld, Job(Pld, 90), Other(Crp, 30));
        Assert.Equal(90, who.Of(Pld)!.Value.Level);
        Assert.Null(who.Of(War));
    }

    // ------------------------------------------------------------------ the classes a run has been on

    [Fact]
    public void InARunACraftersDailyStaysOnTheCrafterItStartedWith()
    {
        // Started on Carpenter; a battle society's visit put Paladin on. Blacksmith is the better geared.
        var classes = new[] { Job(Pld, 90), new ClassInfo(Crp, 100, true, 700), new ClassInfo(Bsm, 100, true, 720) };
        var before = new PlayerClasses(classes, Crp, Pld);
        var after = new PlayerClasses(classes, Pld, Pld) { History = new[] { Crp, Pld } };
        Assert.Equal(Crp, TribeClasses.Choose(Ixal(false), before));
        Assert.Equal(Crp, TribeClasses.Choose(Ixal(false), after));
        // Outside a run, on Paladin, the best-geared crafter takes it.
        Assert.Equal(Bsm, TribeClasses.Choose(Ixal(false), new PlayerClasses(classes, Pld, Pld)));
    }

    [Fact]
    public void InARunTheFirstClassThatWasOnComesFirst()
    {
        // Started on Paladin: the best-geared crafter (Blacksmith) took the first crafter's visit. A quest in the
        // journal then put Carpenter on: the later dailies still go to Blacksmith.
        var classes = new[] { Job(Pld, 90), new ClassInfo(Crp, 100, true, 700), new ClassInfo(Bsm, 100, true, 720) };
        var who = new PlayerClasses(classes, Crp, Pld) { History = new[] { Pld, Bsm, Crp } };
        Assert.Equal(Bsm, TribeClasses.Choose(Ixal(false), who));
        Assert.Equal(1, who.OnRank(Bsm));
        Assert.Equal(int.MaxValue, who.OnRank(War));
        Assert.True(who.WasOn(Crp));
        // Outside a run only the current class is on.
        var idle = new PlayerClasses(classes, Crp, Pld);
        Assert.Equal(0, idle.OnRank(Crp));
        Assert.False(idle.WasOn(Bsm));
    }

    [Fact]
    public void OnlyTheRunsPlanningNotesAClass()
    {
        var history = new ClassHistory();
        // No run: nothing seen, whatever is asked.
        Assert.Empty(history.Seen(running: false));
        Assert.Empty(history.Seen(running: true));
        // The run plans on Warrior, then (after a crafter's visit) on Carpenter.
        Assert.Equal(new[] { War }, history.Note(War));
        Assert.Equal(new[] { War, Crp }, history.Note(Crp));
        Assert.Equal(new[] { War, Crp }, history.Note(War));
        // Looking (the page, twice a second) notes nothing, whatever class is on by then.
        Assert.Equal(new[] { War, Crp }, history.Seen(running: true));
        Assert.Equal(new[] { War, Crp }, history.Seen(running: true));
        // An unknown class isn't noted.
        Assert.Equal(new[] { War, Crp }, history.Note(0));
    }

    [Fact]
    public void TheNextRunStartsItsClassesAnew()
    {
        var history = new ClassHistory();
        history.Note(War);
        history.Note(Crp);
        // The run ended: nothing counts as having been on, and a look doesn't bring it back.
        Assert.Empty(history.Seen(running: false));
        history.RunOver();
        Assert.Empty(history.Seen(running: true));
        // The next run's first planning starts with the class that's on then.
        Assert.Equal(new[] { Bsm }, history.Note(Bsm));
        Assert.Equal(new[] { Bsm }, history.Seen(running: true));
    }

    [Fact]
    public void InARunTheCombatJobItStartedOnStillTakesBattleDailiesAfterACraftersVisit()
    {
        // No Monster drops job picked, started on Warrior, a crafter's visit put Carpenter on.
        var classes = new[] { Job(War, 90), Job(Pld, 100), Other(Crp, 100) };
        var after = new PlayerClasses(classes, Crp, 0) { History = new[] { War, Crp } };
        Assert.Equal(War, TribeClasses.Choose(Battle(60), after));
        // Outside a run, on Carpenter, nothing is picked for you.
        Assert.Equal(0u, TribeClasses.Choose(Battle(60), new PlayerClasses(classes, Crp, 0)));
        // It needs a gearset to change back to.
        var bare = new PlayerClasses(new[] { Job(War, 90, gearset: false), Other(Crp, 100) }, Crp, 0) { History = new[] { War, Crp } };
        Assert.Equal(0u, TribeClasses.Choose(Battle(60), bare));
        // The Monster drops job still comes first.
        Assert.Equal(Pld, TribeClasses.Choose(Battle(60), after with { CombatJob = Pld }));
    }
}
