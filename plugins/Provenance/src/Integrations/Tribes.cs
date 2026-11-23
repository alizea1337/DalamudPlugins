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
/// day's daily-quest slots and seed, the society quests in the journal and the class each was taken on, which
/// unlock, rank and intersocietal quests are done, which shop items are already owned, and the achievements. Plus the
/// check of the worked-out offers against a giver's "Daily Quests" list (<see cref="Observe"/>).
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

    /// <summary>Society quests in the journal, with their step and the class they're locked to.</summary>
    public static List<AcceptedTribeQuest> Accepted(TribeData data)
    {
        var list = new List<AcceptedTribeQuest>();
        var m = Manager;
        if (m == null) return list;
        var today = DailySlots().Select(s => s.Row).ToHashSet();
        foreach (ref var q in m->NormalQuests)
        {
            if (q.QuestId == 0 || data.Quest(0x10000u + q.QuestId) is not { } quest) continue;
            list.Add(new AcceptedTribeQuest(quest, q.Sequence, q.AcceptClassJob, today.Contains(quest.RowId)));
        }
        return list;
    }

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

    /// <summary>The player's crafters and Miner/Botanist: level, a saved gearset, and its item level.</summary>
    public static List<ClassInfo> Classes()
    {
        var list = new List<ClassInfo>();
        var module = RaptureGearsetModule.Instance();
        foreach (var job in Core.Character.ClassJobs.Crafters.Concat(new uint[] { 16, 17 }))
        {
            var best = GameState.BestGearset(job);
            var itemLevel = best >= 0 && module != null ? module->Entries[best].ItemLevel : 0;
            list.Add(new ClassInfo(job, PlanService.JobLevel(job), best >= 0, itemLevel));
        }
        return list;
    }

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
            if (addon == null || addon->AtkValuesCount < 1 || Clean(Text(addon->AtkValues[0])) != TitleText.Value) return;
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

            var today = Today(data, tribe);
            var mine = today.Givers.FirstOrDefault(g => g.Giver.NpcId == giver.NpcId)?.Offers ?? Array.Empty<TribeOffer>();
            var worked = mine.Select(o => names[o.Quest.RowId]).ToList();
            var open = mine.Where(o => o.State == OfferState.Open).Select(o => names[o.Quest.RowId]).ToList();
            var match = gameDailies.All(n => worked.Contains(n, StringComparer.OrdinalIgnoreCase))
                        && open.All(n => gameDailies.Contains(n, StringComparer.OrdinalIgnoreCase));
            Checked[giver.NpcId] = (day, seed, rank, rankedUp, match);
            DebugLog.Write("tribes", $"{(match ? "" : "[!] ")}{tribe.Name} giver {giver.NpcId} ({giver.Name}) rank {rank}{(rankedUp ? " (ranked up today)" : "")}, seed {seed}: "
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
