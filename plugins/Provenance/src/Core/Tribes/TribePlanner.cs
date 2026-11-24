using System;
using System.Collections.Generic;
using System.Linq;

namespace Provenance.Core.Tribes;

/// <summary>A daily the day's run does: one to accept on the class it'll be taken on, or one already in the journal.</summary>
/// <param name="ClassJob">
/// The class it's taken on; for a quest in the journal, the class it was accepted as. A battle society's daily is taken
/// on the combat job that fights it; an Ixal daily with a fight on its crafter (the fight is done on the Monster drops
/// job, <see cref="TribeClasses.Fighter"/>).
/// </param>
/// <param name="Accepted">Already in the journal: the run only does its steps.</param>
/// <param name="Leftover">Accepted before the last reset (it blocks new dailies until it's handed in).</param>
/// <param name="Recipe">The recipe it crafts with on that class (null when it doesn't craft on it).</param>
/// <param name="Crafts">
/// The crafts its crystals are counted for: what's left when the game says (the quest's materials in the bags), else
/// <see cref="QuestCraft.MostCrafts"/> (the count isn't known before accepting).
/// </param>
public sealed record PlannedQuest(TribeQuest Quest, uint ClassJob, bool Accepted, bool Leftover, QuestRecipe? Recipe, int Crafts)
{
    /// <summary>The crystals it may use: per craft, times <see cref="Crafts"/>. Empty when it doesn't craft.</summary>
    public IReadOnlyList<(uint ItemId, int Amount)> Crystals
        => Recipe == null ? Array.Empty<(uint, int)>() : Recipe.Crystals.Select(c => (c.ItemId, c.Amount * Math.Max(0, Crafts))).ToList();
}

/// <summary>
/// One stop of the day's tour: a giver, the class the new dailies are taken on there, and the quests done there (the ones
/// already in the journal first, then the ones to accept).
/// </summary>
/// <param name="ClassJob">
/// The class the run changes to before accepting (one per visit, so it doesn't change between accepts): a crafter or
/// gatherer, or at a battle society the combat job.
/// </param>
public sealed record TribeVisit(Tribe Tribe, TribeGiver Giver, uint ClassJob, IReadOnlyList<PlannedQuest> Quests)
{
    public IEnumerable<PlannedQuest> ToAccept => Quests.Where(q => !q.Accepted);
    public IEnumerable<PlannedQuest> InJournal => Quests.Where(q => q.Accepted);
    /// <summary>Only quests from before the reset: they go first, since they hold back every new daily.</summary>
    public bool Leftover => Quests.Count > 0 && Quests.All(q => q.Leftover);
}

/// <summary>A daily the run doesn't do today, and why (plain words for the page and the log).</summary>
public sealed record LeftOutQuest(Tribe Tribe, TribeQuest Quest, string Reason);

/// <summary>Something about a society the plan wants the player to know ("Rank quest ready: do it yourself for now").</summary>
public sealed record TribeNotice(Tribe Tribe, string Text);

/// <summary>The day's plan: the tour in order, what's left out and why, notices, and why no new daily can be taken (if so).</summary>
/// <param name="Allowances">Allowances left when it was made.</param>
/// <param name="Blocked">Why no new daily can be accepted at all (a daily left over from before the reset, no allowances); null when they can.</param>
public sealed record TribePlan(TribeGoal Goal, IReadOnlyList<TribeVisit> Visits, IReadOnlyList<LeftOutQuest> LeftOut,
    IReadOnlyList<TribeNotice> Notices, int Allowances, string? Blocked)
{
    public static readonly TribePlan Empty = new(TribeGoal.Currency, Array.Empty<TribeVisit>(), Array.Empty<LeftOutQuest>(),
        Array.Empty<TribeNotice>(), 0, null);

    public IEnumerable<PlannedQuest> Quests => Visits.SelectMany(v => v.Quests);

    /// <summary>Dailies the run does (to accept and in the journal).</summary>
    public int Count => Visits.Sum(v => v.Quests.Count);

    /// <summary>Dailies to accept (each takes an allowance).</summary>
    public int ToAccept => Quests.Count(q => !q.Accepted);

    /// <summary>Crystals the whole plan may use, by item.</summary>
    public Dictionary<uint, int> Crystals()
    {
        var all = new Dictionary<uint, int>();
        foreach (var q in Quests)
        foreach (var (id, amount) in q.Crystals)
            all[id] = all.GetValueOrDefault(id) + amount;
        return all;
    }
}

/// <summary>What the day's plan is made from: the game's state, the settings, the player's classes, and what the run gave up on.</summary>
public sealed class TribePlanInput
{
    /// <summary>Every society today (from <c>Integrations.Tribes.Today</c>), in the game's order.</summary>
    public IReadOnlyList<TribeToday> Days { get; init; } = Array.Empty<TribeToday>();
    /// <summary>Society quests in the journal (dailies of today and from before the reset, rank and story quests).</summary>
    public IReadOnlyList<AcceptedTribeQuest> Accepted { get; init; } = Array.Empty<AcceptedTribeQuest>();
    public int Allowances { get; init; }
    /// <summary>The goal picked; null: automatic (<see cref="TribeGoals.Effective"/>).</summary>
    public TribeGoal? Goal { get; init; }
    /// <summary>Societies the player unticked (<c>BeastTribe</c> rows).</summary>
    public IReadOnlySet<uint> LeftOut { get; init; } = new HashSet<uint>();
    public bool LeaveOutSay { get; init; }
    /// <summary>The player's crafters, Miner and Botanist, and combat classes and jobs (<see cref="PlayerClasses.All"/>).</summary>
    public IReadOnlyList<ClassInfo> Classes { get; init; } = Array.Empty<ClassInfo>();
    public uint CurrentJob { get; init; }
    /// <summary>The job picked under Settings, Monster drops (0: none): battle dailies and the fights of Ixal dailies are done on it.</summary>
    public uint CombatJob { get; init; }
    /// <summary>The classes that have been on since the run started, the first first (<see cref="PlayerClasses.History"/>); empty outside a run.</summary>
    public IReadOnlyList<uint> ClassHistory { get; init; } = Array.Empty<uint>();
    /// <summary>The zone the character is in: among equals, its society comes first (no trip there).</summary>
    public uint Territory { get; init; }
    /// <summary>The player owns an item (bags, armoury chest or equipped).</summary>
    public Func<uint, bool> Owns { get; init; } = _ => true;
    public Func<uint, string> ItemName { get; init; } = id => $"item {id}";
    /// <summary>An item's name in the plural ("Ixali Oaknots"); the name itself when not given.</summary>
    public Func<uint, string>? ItemNamePlural { get; init; }
    public Func<uint, string> ClassName { get; init; } = id => $"class {id}";
    /// <summary>Crafts still to do for a quest in the journal, when the game says (its materials in the bags); null when not known.</summary>
    public Func<TribeQuest, int?> CraftsLeft { get; init; } = _ => null;
    /// <summary>Quests the run gave up on today (Quest sheet row), with why.</summary>
    public IReadOnlyDictionary<uint, string> Excluded { get; init; } = new Dictionary<uint, string>();
    /// <summary>Givers the run couldn't get to or talk to today (ENpc id), with why.</summary>
    public IReadOnlyDictionary<uint, string> ExcludedGivers { get; init; } = new Dictionary<uint, string>();
}

/// <summary>
/// Plans the day's allied society dailies, of all 20 societies: the dailies already in the journal (those left over
/// from before the reset first, since the game gives no new ones until they're handed in), then new ones within the
/// allowances left and each giver's 3 a day (6 on the day of a rank-up), for the goal. One class per visit, so the run
/// doesn't change class between accepts. Pure, so it's tested without the game.
/// </summary>
/// <remarks>
/// Decided with the user (Tribes build plans): Rank up takes societies below their top rank first, the lowest rank first;
/// a society whose bar is full (its rank quest pending) is skipped with a notice and its allowances go to the others,
/// while Currency and Levelling keep it. Reputation past a full bar is lost (allied-society research), so under Rank up
/// a society first takes only the dailies that fill its bar (counting the dailies in the journal); the allowances still
/// left after every society's go to the dailies the bars didn't need, which still pay currency, gil, crystals and
/// experience. Under Currency the givers whose dailies pay none of the society's currency (the Ixal's below Respected,
/// the ARR battle societies' Neutral and Recognized ones) get allowances last. The class: for a crafter's or gatherer's
/// daily the current one when it qualifies, otherwise the best-geared one, Levelling the lowest one that qualifies; for
/// a battle society's daily the job picked under Settings, Monster drops, Levelling the lowest combat job at least five
/// levels above the quest (<see cref="TribeClasses"/>). A daily nobody qualifies for, or whose fight has no usable
/// combat job, is left out with what's missing.
/// </remarks>
public static class TribePlanner
{
    public static TribePlan Plan(TribePlanInput input)
    {
        var taking = input.Days.Where(d => d.Unlocked && !input.LeftOut.Contains(d.Tribe.Id)).ToList();
        var goal = TribeGoals.Effective(input.Goal, taking);
        var levelling = goal == TribeGoal.Levelling;
        var who = new PlayerClasses(input.Classes, input.CurrentJob, input.CombatJob) { History = input.ClassHistory };
        var left = new List<LeftOutQuest>();
        var notices = new List<TribeNotice>();
        var dayOf = new Dictionary<uint, TribeToday>();
        foreach (var d in input.Days) dayOf.TryAdd(d.Tribe.Id, d);

        // ---------------------------------------------------------------- quests in the journal
        var leftovers = new List<(Tribe Tribe, TribeGiver Giver, PlannedQuest Quest)>();
        var inJournal = new Dictionary<uint, List<PlannedQuest>>();
        var blockers = new List<AcceptedTribeQuest>();
        foreach (var a in input.Accepted)
        {
            if (!dayOf.TryGetValue(a.Quest.TribeId, out var day)) continue;
            if (JournalReason(a, day.Tribe, input, who) is { } why)
            {
                left.Add(new LeftOutQuest(day.Tribe, a.Quest, why));
                if (a.Leftover) blockers.Add(a);
                continue;
            }
            var recipe = RecipeFor(a.Quest, a.AcceptClassJob);
            var crafts = recipe == null ? 0 : input.CraftsLeft(a.Quest) ?? QuestCraft.MostCrafts;
            var planned = new PlannedQuest(a.Quest, a.AcceptClassJob, true, a.Leftover, recipe, crafts);
            var giver = GiverOf(day.Tribe, a.Quest);
            if (a.Leftover) leftovers.Add((day.Tribe, giver, planned));
            else
            {
                if (!inJournal.TryGetValue(giver.NpcId, out var list)) inJournal[giver.NpcId] = list = new List<PlannedQuest>();
                list.Add(planned);
            }
        }

        string? blocked = null;
        if (blockers.Count > 0)
        {
            var names = string.Join(", ", blockers.Select(b => $"\"{b.Quest.Name}\""));
            blocked = $"{names} from before the reset {(blockers.Count == 1 ? "is" : "are")} still in your journal: the game gives no new dailies until "
                      + $"{(blockers.Count == 1 ? "it's" : "they're")} handed in or abandoned.";
        }
        else if (input.Allowances <= 0) blocked = "No allowances left today: they come back at the daily reset.";

        var visits = new List<TribeVisit>();
        // Left over from before the reset: first, one visit per giver.
        foreach (var g in leftovers.GroupBy(l => l.Giver.NpcId))
        {
            var first = g.First();
            visits.Add(new TribeVisit(first.Tribe, first.Giver, first.Quest.ClassJob, g.Select(x => x.Quest).ToList()));
        }

        // ---------------------------------------------------------------- new dailies, society by society
        // Each giver is a stop of the tour, in the tour's order. Most get their dailies straight away; those that come
        // last (the givers whose dailies pay no currency, under Currency) and the spare dailies (Rank up) are given
        // what's left afterwards, but keep their place in the tour.
        var allowances = Math.Max(0, input.Allowances);
        var stops = new List<Stop>();
        var late = new List<Stop>();
        var spare = new List<(Stop Stop, TribeQuest Quest, uint Job, QuestRecipe? Recipe)>();
        foreach (var day in Order(taking, goal, input.Territory))
        {
            var tribe = day.Tribe;
            if (day.RankQuestReady is { } ready)
                notices.Add(new TribeNotice(tribe, goal == TribeGoal.RankUp
                    ? $"Rank quest ready: do it yourself for now (\"{ready.Name}\"). Until it's done its dailies give no reputation, so the run takes others."
                    : $"Rank quest ready: do it yourself for now (\"{ready.Name}\")."));
            var newOnes = blocked == null && !(goal == TribeGoal.RankUp && day.BarFull);
            // Rank up: first the dailies that fill the bar, counting every daily in the journal (today's, and those left
            // over from before the reset: handed in today, their reputation counts too).
            var forRank = goal == TribeGoal.RankUp && !day.AtTop && !day.BarFull && day.Needed > 0;
            var projected = day.Value + input.Accepted.Where(a => a.Quest.TribeId == tribe.Id && a.Quest.IsDaily).Sum(a => a.Quest.Reputation);

            foreach (var giver in day.Givers.OrderByDescending(g => g.Giver.MaxRank))
            {
                var stop = new Stop(day, giver);
                stops.Add(stop);
                if (!newOnes) continue;
                if (goal == TribeGoal.Currency && !giver.Giver.PaysCurrency)
                {
                    late.Add(stop);
                    continue;
                }
                Fill(stop, forRank, ref projected, lastPass: false);
            }
        }

        // Under Currency: the givers whose dailies pay no currency get what's left.
        foreach (var stop in late)
        {
            var none = 0;
            Fill(stop, false, ref none, lastPass: true);
        }

        // Under Rank up: the allowances still left go to the dailies the bars didn't need. They give no reputation past
        // the full bar, but still pay currency, gil, crystals and experience (decided with the user).
        foreach (var (stop, q, job, recipe) in spare)
        {
            string? why = null;
            if (stop.Room <= 0) why = $"{stop.Giver.Giver.Name} gives no more dailies today";
            else if (allowances <= 0)
                why = "Not needed for the next rank (the dailies before it fill the bar), and the day's allowances went to dailies that are";
            if (why != null)
            {
                left.Add(new LeftOutQuest(stop.Day.Tribe, q, why));
                continue;
            }
            stop.Add(job, new PlannedQuest(q, job, false, false, recipe, recipe == null ? 0 : QuestCraft.MostCrafts));
            allowances--;
        }

        foreach (var stop in stops)
        {
            var tribe = stop.Day.Tribe;
            var giver = stop.Giver.Giver;
            var here = stop.Visits.Select(v => new TribeVisit(tribe, giver, v.Job, v.Quests)).ToList();
            // This giver's quests in the journal go with its first visit, done before the accepts.
            if (inJournal.Remove(giver.NpcId, out var journal))
            {
                if (here.Count > 0) here[0] = here[0] with { Quests = journal.Concat(here[0].Quests).ToList() };
                else here.Add(new TribeVisit(tribe, giver, journal[0].ClassJob, journal));
            }
            visits.AddRange(here);
        }
        // Quests in the journal of a giver the society list didn't cover (shouldn't happen): at the end.
        foreach (var journal in inJournal.Values)
        {
            var q = journal[0].Quest;
            if (dayOf.TryGetValue(q.TribeId, out var day))
                visits.Add(new TribeVisit(day.Tribe, GiverOf(day.Tribe, q), journal[0].ClassJob, journal));
        }

        return new TribePlan(goal, visits, left, notices, input.Allowances, blocked);

        // A giver's open offers that the run can take, by class (one class per visit), within its room and the
        // allowances. Under Rank up, the dailies its bar doesn't need wait for the spare pass.
        void Fill(Stop stop, bool forRank, ref int projected, bool lastPass)
        {
            var day = stop.Day;
            var tribe = day.Tribe;
            var giver = stop.Giver;
            var fresh = new List<(TribeQuest Quest, IReadOnlyList<ClassInfo> Ok)>();
            foreach (var offer in giver.Offers.Where(o => o.State == OfferState.Open))
            {
                var q = offer.Quest;
                if (NewReason(q, giver.Giver, input, who, levelling) is { } why)
                {
                    left.Add(new LeftOutQuest(tribe, q, why));
                    continue;
                }
                fresh.Add((q, TribeClasses.Options(q, who, levelling)));
            }
            // One class per visit: the one that takes the most of them (the preferred one among equals); the others, if
            // any, are another visit on their own class.
            while (fresh.Count > 0)
            {
                var job = TribeClasses.Order(fresh.SelectMany(f => f.Ok).GroupBy(c => c.ClassJob).Select(g => g.First()), who, levelling)
                    .OrderByDescending(c => fresh.Count(f => f.Ok.Any(o => o.ClassJob == c.ClassJob)))
                    .First().ClassJob;
                var group = fresh.Where(f => f.Ok.Any(c => c.ClassJob == job)).ToList();
                fresh.RemoveAll(f => group.Contains(f));
                foreach (var (q, _) in group)
                {
                    var recipe = RecipeFor(q, job);
                    string? why = null;
                    if (recipe is { EquipItem: not 0 } r && !input.Owns(r.EquipItem))
                        why = $"Needs {input.ItemName(r.EquipItem)} equipped to craft it (the society's vendor sells it)";
                    else if (stop.Room <= 0) why = $"{giver.Giver.Name} gives no more dailies today";
                    else if (allowances <= 0)
                        why = lastPass
                            ? $"No allowances left for it today: this giver's dailies pay no {Currency(tribe, input)}, so they come last under Currency"
                            : "No allowances left for it today";
                    else if (forRank && projected >= day.Needed)
                    {
                        spare.Add((stop, q, job, recipe));
                        continue;
                    }
                    if (why != null)
                    {
                        left.Add(new LeftOutQuest(tribe, q, why));
                        continue;
                    }
                    stop.Add(job, new PlannedQuest(q, job, false, false, recipe, recipe == null ? 0 : QuestCraft.MostCrafts));
                    allowances--;
                    projected += q.Reputation;
                }
            }
        }
    }

    /// <summary>One giver on the tour: its dailies to accept, a visit per class, and the room it has left today.</summary>
    private sealed class Stop
    {
        public Stop(TribeToday day, GiverToday giver)
        {
            Day = day;
            Giver = giver;
            Room = Math.Max(0, day.CapPerGiver - giver.Taken);
        }

        public TribeToday Day { get; }
        public GiverToday Giver { get; }
        public int Room { get; private set; }
        public List<(uint Job, List<PlannedQuest> Quests)> Visits { get; } = new();

        /// <summary>Takes a daily on a class: into the visit on that class, or a new one after the others.</summary>
        public void Add(uint job, PlannedQuest quest)
        {
            var visit = Visits.FirstOrDefault(v => v.Job == job);
            if (visit.Quests == null) Visits.Add((job, new List<PlannedQuest> { quest }));
            else visit.Quests.Add(quest);
            Room--;
        }
    }

    /// <summary>
    /// The societies in the order the tour takes them. Rank up: below the top rank first, the lowest rank first, then
    /// those at the top. Otherwise the game's order. Among equals the one whose zone the character is in comes first.
    /// </summary>
    public static IEnumerable<TribeToday> Order(IReadOnlyList<TribeToday> days, TribeGoal goal, uint territory)
    {
        var index = days.Select((d, i) => (d, i)).ToDictionary(x => x.d, x => x.i);
        if (goal == TribeGoal.RankUp)
            return days.OrderBy(d => d.AtTop ? 1 : 0).ThenBy(d => d.AtTop ? 0 : d.Rank)
                .ThenBy(d => d.Tribe.Territory == territory ? 0 : 1).ThenBy(d => index[d]);
        return days.OrderBy(d => d.Tribe.Territory == territory ? 0 : 1).ThenBy(d => index[d]);
    }

    /// <summary>Why a quest in the journal isn't the run's today, or null when it is.</summary>
    private static string? JournalReason(AcceptedTribeQuest a, Tribe tribe, TribePlanInput input, PlayerClasses who)
    {
        var q = a.Quest;
        var judgement = QuestPolicy.Judge(q, new PolicyOptions(input.LeaveOutSay, a.AcceptClassJob));
        if (!judgement.RunCanDo) return judgement.Reason;
        if (input.LeftOut.Contains(tribe.Id)) return $"You left {tribe.Name} out of the run";
        if (input.Excluded.TryGetValue(q.RowId, out var excluded)) return excluded;
        if (input.ExcludedGivers.TryGetValue(q.Giver, out var giver)) return giver;
        var info = input.Classes.FirstOrDefault(c => c.ClassJob == a.AcceptClassJob);
        if (a.AcceptClassJob != 0 && a.AcceptClassJob != input.CurrentJob && !info.HasGearset)
            return $"Accepted as {input.ClassName(a.AcceptClassJob)}, which has no saved gearset to change back to";
        if (RecipeFor(q, a.AcceptClassJob) is { EquipItem: not 0 } r && !input.Owns(r.EquipItem))
            return $"Needs {input.ItemName(r.EquipItem)} equipped to craft it (the society's vendor sells it)";
        // A crafter's daily with a fight (the Ixal's): the fight needs the Monster drops job.
        if (TribeClasses.FighterNeed(q, who) is { } fight) return fight.Text;
        return null;
    }

    /// <summary>Why an offered daily can't be taken by the run at all today, or null (allowances and room are counted later).</summary>
    private static string? NewReason(TribeQuest q, TribeGiver giver, TribePlanInput input, PlayerClasses who, bool levelling)
    {
        var judgement = QuestPolicy.Judge(q, new PolicyOptions(input.LeaveOutSay));
        if (!judgement.RunCanDo) return judgement.Reason;
        if (input.Excluded.TryGetValue(q.RowId, out var excluded)) return excluded;
        if (input.ExcludedGivers.TryGetValue(giver.NpcId, out var why)) return why;
        // The class level, a gearset, or a combat job for its fight (the crystals are the run's to get).
        return TribeClasses.ClassNeed(q, who, levelling)?.Text;
    }

    /// <summary>The society's currency by name, in the plural ("Ixali Oaknots"), for a reason; "society currency" when it has none.</summary>
    private static string Currency(Tribe tribe, TribePlanInput input)
        => tribe.CurrencyItem == 0 ? "society currency" : (input.ItemNamePlural ?? input.ItemName)(tribe.CurrencyItem);

    /// <summary>The recipe a quest crafts with on a class (a gatherer on a Namazu daily gathers instead: none).</summary>
    public static QuestRecipe? RecipeFor(TribeQuest q, uint classJob) => q.Craft?.For(classJob);

    /// <summary>The giver of a quest, as the society lists it (its first giver when the data names another).</summary>
    public static TribeGiver GiverOf(Tribe tribe, TribeQuest q)
        => tribe.Givers.FirstOrDefault(g => g.NpcId == q.Giver) ?? tribe.Givers.FirstOrDefault()
           ?? new TribeGiver(q.Giver, tribe.Name, tribe.Territory, tribe.Place, default, 0, 0);
}
