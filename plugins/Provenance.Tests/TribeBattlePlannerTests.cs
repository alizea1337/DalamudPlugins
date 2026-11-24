using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Provenance.Core.Tribes;
using Xunit;

namespace Provenance.Tests;

/// <summary>
/// The day's plan with battle societies in it: their visits on the combat job, crafter and battle societies on one
/// tour, the ARR societies' three givers, and the Ixal dailies with a fight.
/// </summary>
public class TribeBattlePlannerTests
{
    // ------------------------------------------------------------------ fixtures

    private const uint Pld = 19, War = 21, Whm = 24, Crp = 8, Wvr = 13;
    private const uint Amaljaa = 1, Sylphs = 2, Ixal = QuestPolicy.IxalId, Kojin = 9, YokHuy = 20;

    private static TribeQuest Fight(uint tribe, ushort number, uint giver, int level, int reputation = 60, int rank = 1, StepKinds steps = StepKinds.Combat)
        => new(0x10000u + number, tribe, $"Quest {number}", TribeQuestKind.Daily, rank, 1, giver, giver, TribeKind.Battle, level, reputation, steps,
            null, Array.Empty<QuestGather>(), 0, Array.Empty<uint>(), (0, 0), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>());

    private static TribeQuest Crafted(uint tribe, ushort number, uint giver, int level = 90, StepKinds steps = StepKinds.None)
        => new(0x10000u + number, tribe, $"Quest {number}", TribeQuestKind.Daily, 1, 1, giver, giver, TribeKind.Crafter, level, 60, steps | StepKinds.Craft,
            new QuestCraft(40000u + number, 40001u + number,
                Enumerable.Range(8, 8).Select(j => new QuestRecipe(36000u + (uint)j, (uint)j, level, new[] { (10u, 8) })).ToList(), false),
            Array.Empty<QuestGather>(), 0, Array.Empty<uint>(), (0, 0), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>());

    private static TribeGiver Giver(uint npc, int rank = 1, bool pays = true)
        => new(npc, $"Giver {npc}", 100, "Place", Vector3.Zero, rank, rank) { PaysCurrency = pays };

    private static Tribe Society(uint id, TribeKind kind, int maxRank, params TribeGiver[] givers)
        => new(id, $"Society {id}", kind == TribeKind.Battle && maxRank == 4 ? 0 : 4, kind, maxRank, 1, 21000 + id, 100 + id, "Place", 0, 41, givers,
            Array.Empty<TribeUnlockStep>(),
            new[] { new TribeRankQuest(0x10000u + 9000 + id, $"Rank quest {id}", 3, 1, "Npc", 100 + id, "Place", Vector2.Zero, Array.Empty<(uint, string)>()) },
            0, "", Array.Empty<TribeShopItem>(), Array.Empty<TribeAchievement>(), Array.Empty<TribeQuest>());

    private static TribeOffer Open(TribeQuest q) => new(q, OfferState.Open);

    /// <summary>A battle society with one giver (Heavensward and later) and its three offers, at a level.</summary>
    private static TribeToday BattleDay(uint id, int rank, int level, int value = 0, int needed = 1000)
    {
        var g = Giver(id * 100);
        var t = Society(id, TribeKind.Battle, 8, g);
        return new TribeToday(t, rank, false, value, needed, false, new[]
        {
            new GiverToday(g, Enumerable.Range(1, 3).Select(i => Open(Fight(id, (ushort)(id * 100 + i), g.NpcId, level))).ToArray(), 0),
        });
    }

    /// <summary>A crafter society with one giver and its three offers.</summary>
    private static TribeToday CrafterDay(uint id, int rank, int value = 0, int needed = 1000)
    {
        var g = Giver(id * 100);
        var t = Society(id, TribeKind.Crafter, 8, g);
        return new TribeToday(t, rank, false, value, needed, false, new[]
        {
            new GiverToday(g, Enumerable.Range(1, 3).Select(i => Open(Crafted(id, (ushort)(id * 100 + i), g.NpcId))).ToArray(), 0),
        });
    }

    /// <summary>
    /// An ARR battle society as the game has it: a giver per rank (Neutral, Recognized, Friendly), each with three
    /// offers at its own level and reputation, and only the Friendly giver's dailies paying the society's tokens. At
    /// rank 1 only the first giver offers, at 2 the first two, from 3 up all three.
    /// </summary>
    private static TribeToday ArrDay(uint id, int rank, int value = 0, int needed = 510, int[]? levels = null)
    {
        levels ??= new[] { 43, 46, 48 };
        var reputation = new[] { 10, 14, 20 };
        var givers = new[] { Giver(id * 100 + 1, 1, pays: false), Giver(id * 100 + 2, 2, pays: false), Giver(id * 100 + 3, 3) };
        var t = Society(id, TribeKind.Battle, 4, givers);
        var days = new List<GiverToday>();
        for (var g = 0; g < 3; g++)
        {
            var offers = g + 1 > rank
                ? Array.Empty<TribeOffer>()
                : Enumerable.Range(1, 3).Select(i => Open(Fight(id, (ushort)(id * 1000 + (g + 1) * 10 + i), givers[g].NpcId, levels[g], reputation[g], g + 1))).ToArray();
            days.Add(new GiverToday(givers[g], offers, 0));
        }
        return new TribeToday(t, rank, false, value, needed, false, days);
    }

    private static ClassInfo Job(uint job, int level, bool gearset = true) => new(job, level, gearset, 600, Fights: true);

    /// <summary>All eight crafters at 100 (Weaver the best geared), and the combat jobs given.</summary>
    private static List<ClassInfo> Classes(params ClassInfo[] combat)
        => Enumerable.Range(8, 8).Select(j => new ClassInfo((uint)j, 100, true, j == Wvr ? 720 : 700)).Concat(combat).ToList();

    private static TribePlan Plan(IReadOnlyList<TribeToday> days, List<ClassInfo> classes, uint current = Crp, uint combatJob = Pld, int allowances = 12,
        TribeGoal? goal = null, IReadOnlyList<AcceptedTribeQuest>? accepted = null, HashSet<uint>? leftOut = null, bool leaveOutSay = false,
        uint[]? history = null)
        => TribePlanner.Plan(new TribePlanInput
        {
            ClassHistory = history ?? Array.Empty<uint>(),
            Days = days, Accepted = accepted ?? Array.Empty<AcceptedTribeQuest>(), Allowances = allowances, Goal = goal,
            LeftOut = leftOut ?? new HashSet<uint>(), LeaveOutSay = leaveOutSay, Classes = classes, CurrentJob = current, CombatJob = combatJob,
            Territory = 9999, ItemName = id => $"Item {id}", ItemNamePlural = id => $"Items {id}", ClassName = id => $"Class {id}",
        });

    // ------------------------------------------------------------------ battle and crafter societies on one tour

    [Fact]
    public void ABattleSocietysVisitIsOnTheCombatJobAndACraftersOnTheCrafter()
    {
        var plan = Plan(new[] { BattleDay(Kojin, 3, 60), CrafterDay(YokHuy, 3) }, Classes(Job(Pld, 90)), goal: TribeGoal.Currency);
        Assert.Equal(6, plan.ToAccept);
        Assert.Empty(plan.LeftOut);
        Assert.Equal(new[] { (Kojin, Pld), (YokHuy, Crp) }, plan.Visits.Select(v => (v.Tribe.Id, v.ClassJob)).ToArray());
        Assert.All(plan.Visits[0].Quests, q => Assert.Equal(Pld, q.ClassJob));
        // A fight takes no crystals; the crafter's dailies do.
        Assert.All(plan.Visits[0].Quests, q => Assert.Empty(q.Crystals));
        Assert.All(plan.Visits[1].Quests, q => Assert.NotEmpty(q.Crystals));
    }

    [Fact]
    public void OnTheCombatJobTheCraftersDailiesGoToTheBestGearedCrafter()
    {
        var plan = Plan(new[] { BattleDay(Kojin, 3, 60), CrafterDay(YokHuy, 3) }, Classes(Job(Pld, 90)), current: Pld, goal: TribeGoal.Currency);
        Assert.Equal(new[] { Pld, Wvr }, plan.Visits.Select(v => v.ClassJob).ToArray());
    }

    [Fact]
    public void RankUpOrdersAllSocietiesByRankWhateverTheirKind()
    {
        // The ARR societies top out at Trusted (4), the others at Bloodsworn (8).
        var top = ArrDay(Amaljaa, 4, needed: 0);
        var kojin = BattleDay(Kojin, 5, 60);
        var yok = CrafterDay(YokHuy, 3);
        var sylphs = ArrDay(Sylphs, 2, needed: 360);
        var plan = Plan(new[] { top, sylphs, kojin, yok }, Classes(Job(Pld, 90)), allowances: 30, goal: TribeGoal.RankUp);
        Assert.Equal(new[] { Sylphs, YokHuy, Kojin, Amaljaa }, plan.Visits.Select(v => v.Tribe.Id).Distinct().ToArray());
    }

    [Fact]
    public void TheAutomaticGoalCountsBattleSocieties()
    {
        var classes = Classes(Job(Pld, 90));
        // Everything at the top but a battle society: Rank up.
        Assert.Equal(TribeGoal.RankUp, Plan(new[] { ArrDay(Amaljaa, 4, needed: 0), BattleDay(Kojin, 5, 60) }, classes).Goal);
        // Trusted is the top for an ARR society: Currency.
        Assert.Equal(TribeGoal.Currency, Plan(new[] { ArrDay(Amaljaa, 4, needed: 0) }, classes).Goal);
        // A battle society left out doesn't count.
        Assert.Equal(TribeGoal.Currency, Plan(new[] { ArrDay(Amaljaa, 4, needed: 0), BattleDay(Kojin, 5, 60) }, classes, leftOut: new HashSet<uint> { Kojin }).Goal);
    }

    [Fact]
    public void TheAllowancesAreSharedByBattleAndCrafterSocieties()
    {
        // Five societies with three dailies each, twelve allowances: the first four in the tour's order.
        var days = new[] { BattleDay(6, 3, 50), BattleDay(7, 3, 50), CrafterDay(8, 3), BattleDay(Kojin, 3, 60), CrafterDay(YokHuy, 3) };
        var plan = Plan(days, Classes(Job(Pld, 90)), goal: TribeGoal.Currency);
        Assert.Equal(12, plan.ToAccept);
        Assert.Equal(new uint[] { 6, 7, 8, Kojin }, plan.Visits.Select(v => v.Tribe.Id).ToArray());
        Assert.Equal(3, plan.LeftOut.Count(l => l.Tribe.Id == YokHuy && l.Reason == "No allowances left for it today"));
    }

    [Fact]
    public void ABattleSocietyLeftOutIsntVisited()
    {
        var plan = Plan(new[] { BattleDay(Kojin, 3, 60), CrafterDay(YokHuy, 3) }, Classes(Job(Pld, 90)), goal: TribeGoal.Currency, leftOut: new HashSet<uint> { Kojin });
        Assert.All(plan.Visits, v => Assert.Equal(YokHuy, v.Tribe.Id));
        Assert.DoesNotContain(plan.LeftOut, l => l.Tribe.Id == Kojin);
    }

    [Fact]
    public void PlannedAgainAfterABattleVisitTheCraftersDailiesStayOnTheirCrafter()
    {
        // The run started on Carpenter and has done the battle society's visit as Paladin: it plans the rest again
        // with Paladin on. The crafter's dailies stay Carpenter's (the plan shown at the start, the crystals fetched).
        var rest = new[] { CrafterDay(YokHuy, 3) };
        Assert.Equal(Crp, Plan(rest, Classes(Job(Pld, 90)), current: Pld, goal: TribeGoal.Currency, history: new[] { Crp, Pld }).Visits.Single().ClassJob);
        // Without a run going, on Paladin: the best-geared crafter.
        Assert.Equal(Wvr, Plan(rest, Classes(Job(Pld, 90)), current: Pld, goal: TribeGoal.Currency).Visits.Single().ClassJob);
    }

    [Fact]
    public void PlannedAgainAfterACraftersVisitTheBattleDailiesStayOnTheJobTheRunStartedOn()
    {
        // No Monster drops job: the run started on Warrior, which took the battle dailies in the first plan.
        var rest = new[] { BattleDay(Kojin, 3, 60) };
        var plan = Plan(rest, Classes(Job(War, 90)), current: Crp, combatJob: 0, goal: TribeGoal.Currency, history: new[] { War, Crp });
        Assert.Equal(War, plan.Visits.Single().ClassJob);
        Assert.Empty(plan.LeftOut);
    }

    // ------------------------------------------------------------------ no usable combat job

    [Fact]
    public void WithoutACombatJobTheBattleDailiesAreLeftOutAndTheRestIsPlanned()
    {
        var plan = Plan(new[] { BattleDay(Kojin, 3, 60), CrafterDay(YokHuy, 3) }, Classes(Job(Pld, 90)), combatJob: 0, goal: TribeGoal.Currency);
        Assert.Equal(new[] { YokHuy }, plan.Visits.Select(v => v.Tribe.Id).ToArray());
        Assert.Equal(3, plan.LeftOut.Count(l => l.Tribe.Id == Kojin && l.Reason == "Pick a combat job under Settings, Monster drops"));
        // Their allowances go to the others.
        Assert.Equal(3, plan.ToAccept);
    }

    [Fact]
    public void ACombatJobBelowTheQuestOrWithoutAGearsetIsSaidByName()
    {
        var low = Plan(new[] { BattleDay(Kojin, 3, 60) }, Classes(Job(Pld, 55)), goal: TribeGoal.Currency);
        Assert.Equal(0, low.Count);
        Assert.All(low.LeftOut, l => Assert.Equal("PLD is below level 60", l.Reason));
        var bare = Plan(new[] { BattleDay(Kojin, 3, 60) }, Classes(Job(Pld, 90, gearset: false)), goal: TribeGoal.Currency);
        Assert.All(bare.LeftOut, l => Assert.Equal("No gearset saved for PLD", l.Reason));
        // On that job the run needn't change to it: taken.
        Assert.Equal(3, Plan(new[] { BattleDay(Kojin, 3, 60) }, Classes(Job(Pld, 90, gearset: false)), current: Pld, goal: TribeGoal.Currency).ToAccept);
    }

    [Fact]
    public void TheCurrentCombatJobTakesThemWhenNoneIsPicked()
    {
        var plan = Plan(new[] { BattleDay(Kojin, 3, 60) }, Classes(Job(Pld, 90), Job(War, 100)), current: War, combatJob: 0, goal: TribeGoal.Currency);
        Assert.Equal(War, plan.Visits.Single().ClassJob);
    }

    // ------------------------------------------------------------------ the ARR societies' three givers

    [Fact]
    public void AnArrSocietyGivesThreeDailiesAtEachOfItsGivers()
    {
        var plan = Plan(new[] { ArrDay(Amaljaa, 3) }, Classes(Job(Pld, 90)), goal: TribeGoal.Levelling, combatJob: Pld);
        Assert.Equal(9, plan.ToAccept);
        // A visit per giver, the giver of the highest rank first, all on the combat job.
        Assert.Equal(new uint[] { 103, 102, 101 }, plan.Visits.Select(v => v.Giver.NpcId).ToArray());
        Assert.All(plan.Visits, v => Assert.Equal(3, v.Quests.Count));
        Assert.All(plan.Visits, v => Assert.Equal(Pld, v.ClassJob));
    }

    [Fact]
    public void TwoArrSocietiesShareTheTwelveAllowances()
    {
        var plan = Plan(new[] { ArrDay(Amaljaa, 3), ArrDay(Sylphs, 3) }, Classes(Job(Pld, 90)), goal: TribeGoal.Levelling);
        Assert.Equal(12, plan.ToAccept);
        Assert.Equal(new uint[] { 103, 102, 101, 203 }, plan.Visits.Select(v => v.Giver.NpcId).ToArray());
        Assert.Equal(6, plan.LeftOut.Count(l => l.Reason == "No allowances left for it today"));
    }

    [Fact]
    public void AtALowerRankOnlyTheGiversOfThatRankAndBelowOffer()
    {
        var plan = Plan(new[] { ArrDay(Amaljaa, 2, needed: 360) }, Classes(Job(Pld, 90)), goal: TribeGoal.Currency);
        Assert.Equal(new uint[] { 102, 101 }, plan.Visits.Select(v => v.Giver.NpcId).ToArray());
        Assert.Equal(6, plan.ToAccept);
    }

    [Fact]
    public void RankUpFillsAnArrSocietysBarFromItsBestPayingGiverFirst()
    {
        // 30 of 510 to go at Friendly: two of the Friendly giver's dailies (20 each) fill it. The rest are spare,
        // and get the allowances the other society's bar doesn't need.
        var near = ArrDay(Amaljaa, 3, value: 480, needed: 510);
        var far = BattleDay(Kojin, 3, 60);
        var plan = Plan(new[] { near, far }, Classes(Job(Pld, 90)), allowances: 5, goal: TribeGoal.RankUp);
        var amaljaa = plan.Visits.Where(v => v.Tribe.Id == Amaljaa).ToList();
        Assert.Equal(new uint[] { 103 }, amaljaa.Select(v => v.Giver.NpcId).ToArray());
        Assert.Equal(2, amaljaa.Sum(v => v.Quests.Count));
        Assert.Equal(3, plan.Visits.Where(v => v.Tribe.Id == Kojin).Sum(v => v.Quests.Count));
        Assert.Equal(7, plan.LeftOut.Count(l => l.Tribe.Id == Amaljaa && l.Reason.StartsWith("Not needed for the next rank")));

        // With allowances to spare, the spare dailies are taken too, where the society is on the tour.
        plan = Plan(new[] { near, far }, Classes(Job(Pld, 90)), allowances: 12, goal: TribeGoal.RankUp);
        Assert.Equal(12, plan.ToAccept);
        Assert.Equal(new uint[] { 103, 102, 101, 900 }, plan.Visits.Select(v => v.Giver.NpcId).ToArray());
    }

    [Fact]
    public void UnderCurrencyTheArrGiversThatPayNoTokensComeLast()
    {
        // Only the Friendly givers' dailies pay the societies' tokens; the Neutral and Recognized ones pay a Venture.
        var days = new[] { ArrDay(Amaljaa, 3), ArrDay(Sylphs, 3), BattleDay(Kojin, 3, 60) };
        var plan = Plan(days, Classes(Job(Pld, 90)), allowances: 9, goal: TribeGoal.Currency);
        Assert.Equal(9, plan.ToAccept);
        Assert.Equal(new uint[] { 103, 203, 900 }, plan.Visits.Select(v => v.Giver.NpcId).ToArray());
        Assert.Equal(12, plan.LeftOut.Count(l => l.Reason.EndsWith("so they come last under Currency")));
        Assert.Contains(plan.LeftOut, l => l.Tribe.Id == Amaljaa && l.Reason.Contains($"pay no Items {21000 + Amaljaa}"));

        // With allowances left over they get them, each where its society is on the tour.
        plan = Plan(days, Classes(Job(Pld, 90)), allowances: 12, goal: TribeGoal.Currency);
        Assert.Equal(12, plan.ToAccept);
        Assert.Equal(new uint[] { 103, 102, 203, 900 }, plan.Visits.Select(v => v.Giver.NpcId).ToArray());

        // Rank up and Levelling don't put them last.
        Assert.Equal(new uint[] { 103, 102, 101 },
            Plan(days, Classes(Job(Pld, 90)), allowances: 9, goal: TribeGoal.Levelling).Visits.Select(v => v.Giver.NpcId).ToArray());
    }

    // ------------------------------------------------------------------ Levelling

    [Fact]
    public void LevellingTakesBattleDailiesOnTheLowestJobFiveLevelsAbove()
    {
        // Level 60 quests: Warrior at 64 is too close, White Mage at 70 is the lowest far enough above.
        var classes = Classes(Job(Pld, 100), Job(War, 64), Job(Whm, 70));
        var plan = Plan(new[] { BattleDay(Kojin, 3, 60), CrafterDay(YokHuy, 3) }, classes, goal: TribeGoal.Levelling);
        Assert.Equal(Whm, plan.Visits.Single(v => v.Tribe.Id == Kojin).ClassJob);
        // The crafter's dailies still go to the lowest crafter (all are 100 here: the first).
        Assert.Equal(Crp, plan.Visits.Single(v => v.Tribe.Id == YokHuy).ClassJob);
    }

    [Fact]
    public void LevellingMayTakeAnArrSocietysGiversOnDifferentJobs()
    {
        // Givers at 43, 46 and 48: a job at 50 is five above the first only; the others go to the job at 53.
        var classes = Classes(Job(Pld, 100), Job(War, 50), Job(Whm, 53));
        var plan = Plan(new[] { ArrDay(Amaljaa, 3) }, classes, goal: TribeGoal.Levelling);
        Assert.Equal(new[] { (103u, Whm), (102u, Whm), (101u, War) }, plan.Visits.Select(v => (v.Giver.NpcId, v.ClassJob)).ToArray());
    }

    [Fact]
    public void LevellingFallsBackToTheMonsterDropsJob()
    {
        // Pelupelu's dailies are level 90: nothing at 95, so the job picked (at 92) takes them.
        var plan = Plan(new[] { BattleDay(18, 3, 90) }, Classes(Job(Pld, 92), Job(War, 94)), goal: TribeGoal.Levelling);
        Assert.Equal(Pld, plan.Visits.Single().ClassJob);
    }

    // ------------------------------------------------------------------ the policy

    [Fact]
    public void TheDailiesThePolicyKeepsForThePlayerAreLeftOutAndTheOthersTaken()
    {
        var g = Giver(200);
        var sylphs = Society(Sylphs, TribeKind.Battle, 4, g);
        var day = new TribeToday(sylphs, 4, false, 0, 0, false, new[]
        {
            new GiverToday(g, new[]
            {
                Open(Fight(Sylphs, 1262, 200, 42, steps: StepKinds.Stealth)),      // "Now You See Me"
                Open(Fight(Sylphs, 1264, 200, 42)),                                // a FATE daily
                Open(Fight(Sylphs, 1267, 200, 45)),                                // field monsters
            }, 0),
        });
        var plan = Plan(new[] { day }, Classes(Job(Pld, 90)), goal: TribeGoal.Currency);
        Assert.Equal(new ushort[] { 1267 }, plan.Quests.Select(q => q.Quest.Number).ToArray());
        Assert.Contains(plan.LeftOut, l => l.Quest.Number == 1262 && l.Reason.StartsWith("A stealth quest"));
        Assert.Contains(plan.LeftOut, l => l.Quest.Number == 1264 && l.Reason.StartsWith("A FATE quest"));
    }

    [Fact]
    public void ABattleDailyWithASayIsLeftOutWhenTheOptionSaysSo()
    {
        var g = Giver(1200);
        var pixies = Society(12, TribeKind.Battle, 8, g);
        var day = new TribeToday(pixies, 3, false, 0, 1000, false, new[]
        {
            new GiverToday(g, new[] { Open(Fight(12, 3691, 1200, 70, steps: StepKinds.Say)), Open(Fight(12, 3692, 1200, 70)) }, 0),
        });
        Assert.Equal(2, Plan(new[] { day }, Classes(Job(Pld, 90)), goal: TribeGoal.Currency).ToAccept);
        var plan = Plan(new[] { day }, Classes(Job(Pld, 90)), goal: TribeGoal.Currency, leaveOutSay: true);
        Assert.Equal(new ushort[] { 3692 }, plan.Quests.Select(q => q.Quest.Number).ToArray());
        Assert.Contains(plan.LeftOut, l => l.Quest.Number == 3691 && l.Reason.Contains("/say"));
    }

    // ------------------------------------------------------------------ the Ixal's fights

    private static TribeToday IxalDay(params TribeQuest[] dailies)
    {
        var g = Giver(500);
        var t = Society(Ixal, TribeKind.Crafter, 7, g);
        return new TribeToday(t, 3, false, 0, 1000, false, new[] { new GiverToday(g, dailies.Select(Open).ToArray(), 0) });
    }

    [Fact]
    public void AnIxalDailyWithAFightIsTakenOnItsCrafter()
    {
        var day = IxalDay(Crafted(Ixal, 1494, 500, 1, StepKinds.Combat), Crafted(Ixal, 1495, 500, 1));
        var plan = Plan(new[] { day }, Classes(Job(Pld, 60)), goal: TribeGoal.Currency);
        var visit = Assert.Single(plan.Visits);
        Assert.Equal(Crp, visit.ClassJob);
        Assert.Equal(new ushort[] { 1494, 1495 }, visit.Quests.Select(q => q.Quest.Number).ToArray());
        Assert.All(visit.Quests, q => Assert.Equal(Crp, q.ClassJob));
    }

    [Fact]
    public void ItsFightNeedsAUsableCombatJobAndTheOtherIxalDailiesDont()
    {
        var day = IxalDay(Crafted(Ixal, 1494, 500, 1, StepKinds.Combat), Crafted(Ixal, 1495, 500, 1));
        var none = Plan(new[] { day }, Classes(Job(Pld, 60)), combatJob: 0, goal: TribeGoal.Currency);
        Assert.Equal(new ushort[] { 1495 }, none.Quests.Select(q => q.Quest.Number).ToArray());
        Assert.Contains(none.LeftOut, l => l.Quest.Number == 1494 && l.Reason == "Has a fight: pick a combat job under Settings, Monster drops");

        var low = Plan(new[] { day }, Classes(Job(Pld, 45)), goal: TribeGoal.Currency);
        Assert.Contains(low.LeftOut, l => l.Quest.Number == 1494 && l.Reason == "Has a fight: PLD is below level 50");

        // Being on the combat job isn't enough for it: the run changes to the crafter and back.
        var bare = Plan(new[] { day }, Classes(Job(Pld, 60, gearset: false)), current: Pld, goal: TribeGoal.Currency);
        Assert.Contains(bare.LeftOut, l => l.Quest.Number == 1494 && l.Reason == "Has a fight: no gearset saved for PLD");
    }

    [Fact]
    public void AnIxalDailyThatGathersOnAnotherClassIsLeftOut()
    {
        var day = IxalDay(Crafted(Ixal, 1498, 500, 1, StepKinds.OtherClass), Crafted(Ixal, 1497, 500, 1, StepKinds.Say));
        var plan = Plan(new[] { day }, Classes(Job(Pld, 60)), goal: TribeGoal.Currency);
        Assert.Equal(new ushort[] { 1497 }, plan.Quests.Select(q => q.Quest.Number).ToArray());
        Assert.Contains(plan.LeftOut, l => l.Quest.Number == 1498 && l.Reason.Contains("Miner, Botanist or Fisher"));
    }

    // ------------------------------------------------------------------ the journal

    [Fact]
    public void ABattleDailyInTheJournalIsDoneAsTheJobItWasAcceptedOn()
    {
        // Accepted as Warrior this morning; the Monster drops job is Paladin: the new ones are taken as Paladin.
        var day = BattleDay(Kojin, 3, 60);
        var mine = Fight(Kojin, 999, day.Givers[0].Giver.NpcId, 60);
        var plan = Plan(new[] { day }, Classes(Job(Pld, 90), Job(War, 100)), goal: TribeGoal.Currency,
            accepted: new[] { new AcceptedTribeQuest(mine, 1, War, true) });
        var visit = Assert.Single(plan.Visits);
        Assert.Equal(mine.RowId, visit.Quests[0].Quest.RowId);
        Assert.Equal(War, visit.Quests[0].ClassJob);
        Assert.True(visit.Quests[0].Accepted);
        Assert.Equal(Pld, visit.ClassJob);
        Assert.Equal(3, visit.ToAccept.Count());
    }

    [Fact]
    public void ABattleDailyInTheJournalIsDoneEvenWithoutAMonsterDropsJob()
    {
        var day = BattleDay(Kojin, 3, 60);
        var mine = Fight(Kojin, 999, day.Givers[0].Giver.NpcId, 60);
        var plan = Plan(new[] { day }, Classes(Job(War, 100)), combatJob: 0, allowances: 0,
            accepted: new[] { new AcceptedTribeQuest(mine, 1, War, true) });
        Assert.Equal(War, plan.Quests.Single().ClassJob);
    }

    [Fact]
    public void AnIxalFightInTheJournalWaitsForACombatJob()
    {
        var day = IxalDay(Crafted(Ixal, 1495, 500, 1));
        var mine = Crafted(Ixal, 1494, 500, 1, StepKinds.Combat);
        var accepted = new[] { new AcceptedTribeQuest(mine, 1, Crp, true) };
        var without = Plan(new[] { day }, Classes(Job(Pld, 60)), combatJob: 0, accepted: accepted, goal: TribeGoal.Currency);
        Assert.DoesNotContain(without.Quests, q => q.Quest.Number == 1494);
        Assert.Contains(without.LeftOut, l => l.Quest.Number == 1494 && l.Reason == "Has a fight: pick a combat job under Settings, Monster drops");
        // With one, it's done first at its giver, as the crafter it was accepted on.
        var with = Plan(new[] { day }, Classes(Job(Pld, 60)), accepted: accepted, goal: TribeGoal.Currency);
        Assert.Equal(new ushort[] { 1494, 1495 }, with.Quests.Select(q => q.Quest.Number).ToArray());
        Assert.Equal(Crp, with.Quests.First().ClassJob);
    }

    [Fact]
    public void ALeftoverStealthDailyBlocksNewDailiesLikeAnyTheRunCantDo()
    {
        var day = BattleDay(Kojin, 3, 60);
        var stealth = Fight(Sylphs, 1262, 200, 42, steps: StepKinds.Stealth);
        var sylphs = new TribeToday(Society(Sylphs, TribeKind.Battle, 4, Giver(200)), 3, false, 0, 510, false,
            new[] { new GiverToday(Giver(200), Array.Empty<TribeOffer>(), 0) });
        var plan = Plan(new[] { sylphs, day }, Classes(Job(Pld, 90)), accepted: new[] { new AcceptedTribeQuest(stealth, 1, Pld, false) });
        Assert.Equal(0, plan.ToAccept);
        Assert.Contains("\"Quest 1262\"", plan.Blocked);
    }
}
