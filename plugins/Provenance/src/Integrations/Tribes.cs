using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Memory;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.Exd;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Provenance.Core.Tribes;
using Provenance.Data;
using GameAchievement = FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement;
using Tribe = Provenance.Core.Tribes.Tribe;

namespace Provenance.Integrations;

/// <summary>
/// Allied societies in the game: rank, reputation and the "ranked up today" bit per society, the allowances left, the
/// day's daily-quest slots and seed, the society quests in the journal and the class each was taken on, the player's
/// classes a daily can be taken on (crafters, gatherers and combat jobs), which unlock, rank and intersocietal quests
/// are done, which shop items are already owned, and the achievements. Plus the check of the worked-out offers
/// against a giver's "Daily Quests" list (<see cref="Observe"/>).
/// </summary>
/// <remarks>
/// <para>
/// FFXIVClientStructs (MIT): <c>QuestManager.BeastReputation[20]</c> (index = society − 1; the rank byte's high bit is
/// "ranked up today"), <c>DailyQuests[12]</c> (today's accepted dailies, done or not, until the reset),
/// <c>DailyQuestSeed</c>, <c>GetBeastTribeAllowance()</c> (allowances left), <c>NormalQuests[].AcceptClassJob</c>;
/// <c>PlayerState.GetBeastTribeNeededReputation</c>; <c>UIState.IsItemActionUnlocked</c> (1 owned, 2 not yet, 4 not an
/// unlock).
/// </para>
/// <para>
/// Facts from the allied-society research: 12 allowances a day, reset at 15:00 UTC; nothing stores a per-society count,
/// so it's today's slots whose quest belongs to the society; a daily accepted before the reset blocks new ones until
/// it's handed in or abandoned; the giver's list is a <c>SelectIconString</c> titled "Daily Quests" (Addon 102442).
/// </para>
/// <para>
/// Not checked in game yet: that the reset clears the slots while a daily stays in the journal (the slots carry no
/// date, so a daily without one is taken as left over). The first read of each day goes to the debug log to show it,
/// and a giver that lists none of its dailies when nothing else explains it marks the journal's dailies as left over.
/// </para>
/// </remarks>
internal static unsafe class Tribes
{
    /// <summary>Allied society dailies a day, shared by all societies.</summary>
    public const int DailyAllowance = 12;

    /// <summary>The daily reset, 15:00 UTC.</summary>
    public const int ResetHourUtc = 15;

    private static QuestManager* Manager
        => Services.ClientState.IsLoggedIn && Services.Objects.LocalPlayer != null ? QuestManager.Instance() : null;

    /// <summary>The game's quest state can be read (logged in).</summary>
    public static bool Ready => Manager != null;

    /// <summary>Today's seed for the daily pick.</summary>
    public static byte Seed => Manager is var m && m != null ? m->DailyQuestSeed : (byte)0;

    /// <summary>Allowances left today (of 12).</summary>
    public static int AllowancesLeft => Manager is var m && m != null ? (int)Math.Min(m->GetBeastTribeAllowance(), DailyAllowance) : 0;

    /// <summary>The rank byte of a society: rank (0 while locked), and whether it went up today.</summary>
    public static (int Rank, bool RankedUpToday, int Value) Reputation(uint tribe)
    {
        var m = Manager;
        if (m == null || tribe == 0 || tribe > m->BeastReputation.Length) return (0, false, 0);
        var r = m->BeastReputation[(int)tribe - 1];
        return (r.Rank & 0x7F, (r.Rank & 0x80) != 0, r.Value);
    }

    /// <summary>The reputation that fills the society's bar now (the game's figure; the data's when the game has none).</summary>
    public static int Needed(TribeData data, uint tribe, int rank)
    {
        var ps = PlayerState.Instance();
        var game = ps != null && Ready ? ps->GetBeastTribeNeededReputation((byte)tribe) : 0;
        if (game > 0) return game;
        return data.Tribe(tribe) is { } t && rank < t.MaxRank ? data.Required(rank) : 0;
    }

    public static bool QuestDone(uint row) => row != 0 && QuestManager.IsQuestComplete(row);

    /// <summary>Today's daily-quest slots: the quest row and whether it's done.</summary>
    public static List<(uint Row, bool Done)> DailySlots()
    {
        var list = new List<(uint, bool)>();
        var m = Manager;
        if (m == null) return list;
        foreach (ref var d in m->DailyQuests)
            if (d.QuestId != 0) list.Add((0x10000u + d.QuestId, d.IsCompleted));
        return list;
    }

    /// <summary>
    /// Society quests in the journal, with their step and the class they're locked to. A daily without one of today's
    /// slots is left over from before the reset, and so is every daily in the journal on a day a giver held new ones back
    /// (<see cref="TribeLeftovers.HeldBack"/>: the fallback, in case the game keeps the slots past the reset).
    /// </summary>
    public static List<AcceptedTribeQuest> Accepted(TribeData data)
    {
        var list = new List<AcceptedTribeQuest>();
        var m = Manager;
        if (m == null) return list;
        var slots = DailySlots();
        var today = slots.Select(s => s.Row).ToHashSet();
        var day = DayNumber;
        var stale = heldBack is { } h && h.Day == day ? h.Rows : null;
        foreach (ref var q in m->NormalQuests)
        {
            if (q.QuestId == 0 || data.Quest(0x10000u + q.QuestId) is not { } quest) continue;
            var todays = today.Contains(quest.RowId) && !(stale?.Contains(quest.RowId) ?? false);
            list.Add(new AcceptedTribeQuest(quest, q.Sequence, q.AcceptClassJob, todays));
        }
        FirstLook(day, slots, list);
        return list;
    }

    /// <summary>The day the slots were last read, and the slots seen that day.</summary>
    private static (long Day, HashSet<uint> Rows) seenSlots = (-1, new HashSet<uint>());

    /// <summary>
    /// The first read of a day (after the reset, or since Provenance started) goes to the debug log: the slots, the
    /// society quests in the journal with whether each has a slot, and which slots were already there before the reset
    /// (when Provenance saw them). Bug reports then show whether the game clears the slots at the reset while a daily
    /// stays in the journal, which the leftover check rests on.
    /// </summary>
    private static void FirstLook(long day, List<(uint Row, bool Done)> slots, List<AcceptedTribeQuest> journal)
    {
        var rows = slots.Select(s => s.Row).ToHashSet();
        if (seenSlots.Day == day)
        {
            seenSlots.Rows.UnionWith(rows);
            return;
        }
        var before = seenSlots.Day >= 0 ? rows.Where(seenSlots.Rows.Contains).Select(r => r - 0x10000).ToList() : null;
        var since = seenSlots.Day >= 0 ? "since the reset" : "since Provenance started";
        seenSlots = (day, rows);
        // Nothing to show for players who don't do society quests.
        if (slots.Count == 0 && journal.Count == 0) return;
        DebugLog.Write("tribes", $"Day {day}, first look {since}: {AllowancesLeft} allowances left, "
                                 + $"slots [{string.Join(", ", slots.Select(s => $"{s.Row - 0x10000}{(s.Done ? " done" : "")}"))}]"
                                 + (before != null ? $", already there before the reset [{string.Join(", ", before)}]" : "")
                                 + $"; society quests in the journal [{string.Join(", ", journal.Select(a => $"{a.Quest.Number} {a.Quest.Kind} step {a.Sequence} "
                                     + $"as {Core.Character.ClassJobs.Abbreviation(a.AcceptClassJob)}, {(rows.Contains(a.Quest.RowId) ? "has a slot" : "no slot")}"
                                     + (a.Leftover ? ", left over" : "")))}]");
    }

    /// <summary>
    /// The society dailies in the journal on the day a giver held new ones back (<see cref="TribeLeftovers.HeldBack"/>):
    /// they're from before the reset, whatever the slots say. Dailies accepted later that day aren't among them.
    /// </summary>
    private static (long Day, HashSet<uint> Rows)? heldBack;

    /// <summary>
    /// A society today: rank and reputation, and per giver today's offers (worked out from the seed) with what's taken.
    /// </summary>
    public static TribeToday Today(TribeData data, Tribe tribe, IReadOnlyList<AcceptedTribeQuest>? accepted = null,
        IReadOnlyList<(uint Row, bool Done)>? slots = null)
    {
        var (rank, rankedUp, value) = Reputation(tribe.Id);
        accepted ??= Accepted(data);
        slots ??= DailySlots();
        var m = Manager;
        var seed = Seed;
        var givers = new List<GiverToday>();
        foreach (var giver in tribe.Givers)
        {
            var dailies = tribe.DailiesOf(giver.NpcId);
            var offers = new List<TribeOffer>();
            foreach (var q in DailyPick.Offers(dailies, seed, rank, rankedUp))
            {
                var taken = accepted.FirstOrDefault(a => a.Quest.RowId == q.RowId);
                var state = taken != null ? OfferState.Accepted
                    : m != null && m->IsDailyQuestCompleted(q.Number) ? OfferState.Done : OfferState.Open;
                offers.Add(new TribeOffer(q, state, taken?.AcceptClassJob ?? 0));
            }
            var count = slots.Count(s => data.Quest(s.Row) is { } q && q.Giver == giver.NpcId && q.TribeId == tribe.Id);
            givers.Add(new GiverToday(giver, offers, count));
        }
        var allied = tribe.IntersocietalQuest != 0 && QuestDone(tribe.IntersocietalQuest);
        return new TribeToday(tribe, rank, rankedUp, value, rank > 0 ? Needed(data, tribe.Id, rank) : 0, allied, givers);
    }

    // ------------------------------------------------------------------ the day

    /// <summary>Server time (Unix seconds), or the PC's clock when the game has none.</summary>
    public static long ServerTime() => Deliveries.ServerTime();

    /// <summary>The next daily reset (15:00 UTC), as local time.</summary>
    public static DateTime NextReset
    {
        get
        {
            var now = DateTimeOffset.FromUnixTimeSeconds(ServerTime());
            var reset = new DateTimeOffset(now.Year, now.Month, now.Day, ResetHourUtc, 0, 0, TimeSpan.Zero);
            if (reset <= now) reset = reset.AddDays(1);
            return reset.LocalDateTime;
        }
    }

    /// <summary>The day (counted from the Unix epoch's) the server time falls in, the reset being at 15:00 UTC.</summary>
    public static long DayNumber => (ServerTime() - ResetHourUtc * 3600) / 86400;

    // ------------------------------------------------------------------ what the player has

    /// <summary>
    /// The combat classes and jobs a battle society's quests take: every Disciple of War or Magic but the limited jobs,
    /// which is what the quests' <c>ClassJobCategory</c> 142 admits (checked against the 7.58 sheets: the same 30 rows).
    /// </summary>
    private static readonly Lazy<uint[]> CombatClasses = new(() =>
    {
        try
        {
            return Services.Data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>()
                .Where(c => c.RowId != 0 && c.ClassJobCategory.RowId is DiscipleOfWar or DiscipleOfMagic && !c.IsLimitedJob)
                .Select(c => c.RowId).ToArray();
        }
        catch (Exception) { return Array.Empty<uint>(); }
    });

    /// <summary><c>ClassJob.ClassJobCategory</c> of a combat class: 30 Disciple of War, 31 Disciple of Magic.</summary>
    private const uint DiscipleOfWar = 30, DiscipleOfMagic = 31;

    /// <summary>
    /// The player's crafters, Miner and Botanist, and combat classes and jobs: level, a saved gearset and its item
    /// level; and for a combat job whether the basic combat has a combo for it (the jobs Settings, Monster drops offers:
    /// a starting class without its job has none).
    /// </summary>
    public static List<ClassInfo> Classes()
    {
        var list = new List<ClassInfo>();
        // The best gearset of every class in one pass over the gearsets.
        var best = new Dictionary<uint, int>();
        var module = RaptureGearsetModule.Instance();
        if (module != null)
            for (var i = 0; i < 100; i++)
            {
                var e = module->Entries[i];
                if ((e.Flags & RaptureGearsetModule.GearsetFlag.Exists) == 0) continue;
                if (!best.TryGetValue(e.ClassJob, out var level) || e.ItemLevel > level) best[e.ClassJob] = e.ItemLevel;
            }
        foreach (var job in Core.Character.ClassJobs.Crafters.Concat(new uint[] { 16, 17 }))
            list.Add(new ClassInfo(job, PlanService.JobLevel(job), best.ContainsKey(job), best.GetValueOrDefault(job)));
        var combos = Combat.BasicCombo.Jobs;
        foreach (var job in CombatClasses.Value)
            list.Add(new ClassInfo(job, PlanService.JobLevel(job), best.ContainsKey(job), best.GetValueOrDefault(job), combos.Contains(job)));
        return list;
    }

    /// <summary>
    /// The player's classes as the choice of one for a quest sees them: all of <see cref="Classes"/>, the class that's
    /// on, and the combat job picked under Settings, Monster drops.
    /// </summary>
    public static PlayerClasses Who(Configuration config) => new(Classes(), GameState.CurrentJob, config.Combat.ClassJob);

    /// <summary>An item in the four bags and the crystal bag (where crafting takes its crystals from), as the plan counts them.</summary>
    public static int OnHand(uint itemId)
    {
        var inv = InventoryManager.Instance();
        if (inv == null || itemId == 0) return 0;
        var total = 0;
        foreach (var type in new[] { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4, InventoryType.Crystals })
            total += inv->GetItemCountInContainer(itemId, type, false) + inv->GetItemCountInContainer(itemId, type, true);
        return total;
    }

    /// <summary>The player has an item anywhere on them: bags, armoury chest or equipped.</summary>
    public static bool Owns(uint itemId)
    {
        var inv = InventoryManager.Instance();
        return inv != null && itemId != 0 && inv->GetInventoryItemCount(itemId, false, true, true) + inv->GetInventoryItemCount(itemId, true, true, true) > 0;
    }

    private static readonly Dictionary<uint, (DateTime At, bool? Owned)> owned = new();

    /// <summary>
    /// Whether a shop item that unlocks something (a mount, minion, orchestrion roll, emote, card…) is already the
    /// player's, by the game's own check; null for items without an unlock or while the game can't say. Re-checked
    /// every few seconds.
    /// </summary>
    public static bool? Owned(uint itemId)
    {
        if (owned.TryGetValue(itemId, out var known) && DateTime.UtcNow - known.At < TimeSpan.FromSeconds(5)) return known.Owned;
        bool? result = null;
        var ui = UIState.Instance();
        if (ui != null && Ready && ExdModule.GetItemRowById(itemId) is var row && row != null)
            result = ui->IsItemActionUnlocked(row) switch { 1 => true, 2 => false, _ => null };
        owned[itemId] = (DateTime.UtcNow, result);
        return result;
    }

    /// <summary>An achievement is complete (null until the game has the achievement data, once the Achievements window was opened).</summary>
    public static bool? AchievementDone(uint id)
    {
        var a = GameAchievement.Instance();
        return a != null && a->IsLoaded() ? a->IsComplete((int)id) : null;
    }

    // ------------------------------------------------------------------ checking the worked-out offers

    /// <summary>Addon text 102442: the title of a giver's list ("Daily Quests").</summary>
    private const uint DailyQuestsTitle = 102442;

    private static readonly Lazy<string> TitleText = new(() =>
    {
        try { return Clean(Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRowOrDefault(DailyQuestsTitle)?.Text.ExtractText() ?? ""); }
        catch (Exception) { return ""; }
    });

    /// <summary>Per giver: the day, seed and rank byte it was checked at, and whether the game's list matched.</summary>
    private static readonly Dictionary<uint, (long Day, byte Seed, int Rank, bool RankedUp, bool Match)> Checked = new();

    /// <summary>The check for a giver: true when its list matched the worked-out offers today, false when not, null when not seen.</summary>
    public static bool? Matches(uint giver)
    {
        if (!Checked.TryGetValue(giver, out var c) || c.Day != DayNumber || c.Seed != Seed) return null;
        return c.Match;
    }

    /// <summary>
    /// Every frame: when a society giver's "Daily Quests" list is open, compares its entries with the offers worked out
    /// for today (once per giver, day, seed and rank) and writes <c>[tribes] … match</c> or <c>MISMATCH</c> to the debug
    /// log. The list leaves out what's taken today and may add the rank quest or small talk, so it matches when every
    /// daily it lists was worked out and every worked-out daily not yet taken is listed.
    /// </summary>
    public static void Observe(TribeData? data)
    {
        try
        {
            if (data == null || data.Tribes.Count == 0 || !Ready || TitleText.Value.Length == 0) return;
            var addon = Interact.Ready("SelectIconString");
            if (addon == null || addon->AtkValuesCount < 1
                || !string.Equals(Clean(Text(addon->AtkValues[0])), TitleText.Value, StringComparison.OrdinalIgnoreCase)) return;
            if (GiverInFront(data) is not { } at) return;
            var (tribe, giver) = at;
            var (rank, rankedUp, _) = Reputation(tribe.Id);
            var day = DayNumber;
            var seed = Seed;
            if (Checked.TryGetValue(giver.NpcId, out var seen) && seen.Day == day && seen.Seed == seed && seen.Rank == rank && seen.RankedUp == rankedUp) return;

            var listed = new AddonMaster.SelectIconString((nint)addon).Entries.Select(e => Clean(e.Text)).Where(t => t.Length > 0).ToList();
            var names = tribe.Dailies.ToDictionary(q => q.RowId, q => Clean(q.Name));
            var dailyNames = names.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var gameDailies = listed.Where(dailyNames.Contains).ToList();
            var others = listed.Where(t => !dailyNames.Contains(t)).ToList();

            var accepted = Accepted(data);
            var today = Today(data, tribe, accepted);
            var giverToday = today.Givers.FirstOrDefault(g => g.Giver.NpcId == giver.NpcId);
            var mine = giverToday?.Offers ?? Array.Empty<TribeOffer>();
            var worked = mine.Select(o => names[o.Quest.RowId]).ToList();
            var allowances = AllowancesLeft;

            // The fallback for leftovers: none of its dailies listed, though nothing else explains it.
            var room = giverToday == null ? 0 : Math.Max(0, today.CapPerGiver - giverToday.Taken);
            var journal = accepted.Where(a => a.Quest.IsDaily).ToList();
            if (heldBack is not { } held || held.Day != day)
                if (TribeLeftovers.HeldBack(allowances, gameDailies.Count, mine.Count(o => o.State == OfferState.Open), room, journal.Count)
                    && journal.Any(a => !a.Leftover))
                {
                    heldBack = (day, journal.Select(a => a.Quest.RowId).ToHashSet());
                    DebugLog.Write("tribes", $"[!] {tribe.Name} giver {giver.NpcId} lists none of its dailies with {allowances} allowances left and room for {room}: "
                                             + $"the dailies in the journal [{string.Join(", ", journal.Select(a => a.Quest.Number))}] are taken as left over from before the reset");
                    accepted = Accepted(data);
                }

            // With no allowances left, or a daily left over from before the reset, the giver may list no dailies at all:
            // then only what it does list is checked.
            var blocked = allowances == 0 || accepted.Any(a => a.Leftover);
            var open = blocked ? new List<string>() : mine.Where(o => o.State == OfferState.Open).Select(o => names[o.Quest.RowId]).ToList();
            var match = gameDailies.All(n => worked.Contains(n, StringComparer.OrdinalIgnoreCase))
                        && open.All(n => gameDailies.Contains(n, StringComparer.OrdinalIgnoreCase));
            Checked[giver.NpcId] = (day, seed, rank, rankedUp, match);
            DebugLog.Write("tribes", $"{(match ? "" : "[!] ")}{tribe.Name} giver {giver.NpcId} ({giver.Name}) rank {rank}{(rankedUp ? " (ranked up today)" : "")}, seed {seed}{(blocked ? ", nothing can be taken" : "")}: "
                                     + $"game [{string.Join(" | ", gameDailies)}], worked out [{string.Join(" | ", mine.Select(o => $"{names[o.Quest.RowId]} ({o.State})"))}]"
                                     + (others.Count > 0 ? $", other entries [{string.Join(" | ", others)}]" : "") + ": " + (match ? "match" : "MISMATCH"));
        }
        catch (Exception e)
        {
            Services.Log.Debug("Checking the giver's daily list failed: {Message}", e.Message);
        }
    }

    /// <summary>The society giver the player is talking to: the target, else a giver within a few yalms.</summary>
    private static (Tribe Tribe, TribeGiver Giver)? GiverInFront(TribeData data)
    {
        if (Services.Targets.Target is { } target && data.Giver(target.BaseId) is { } g) return g;
        var me = Services.Objects.LocalPlayer?.Position ?? Vector3.Zero;
        var near = Services.Objects
            .Where(o => o.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc && data.Giver(o.BaseId) != null)
            .OrderBy(o => Vector3.DistanceSquared(o.Position, me))
            .FirstOrDefault(o => Vector3.Distance(o.Position, me) < 8f);
        return near != null ? data.Giver(near.BaseId) : null;
    }

    private static string Text(AtkValue v)
    {
        var type = v.Type & AtkValueType.TypeMask;
        if (type is not (AtkValueType.String or AtkValueType.ConstString) || v.String.Value == null) return "";
        return MemoryHelper.ReadSeStringNullTerminated((nint)v.String.Value).TextValue;
    }

    /// <summary>Text without the icon glyphs the game puts in names and lists, trimmed.</summary>
    private static string Clean(string text) => TribeData.CleanName(text);
}
