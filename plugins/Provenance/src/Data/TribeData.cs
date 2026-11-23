using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Lumina.Excel.Sheets;
using Provenance.Core.Tribes;
using Tribe = Provenance.Core.Tribes.Tribe;

namespace Provenance.Data;

/// <summary>
/// Allied societies ("tribes") from the game data: the 20 societies (<c>BeastTribe</c>) with their givers, unlock and
/// rank quests, portals, shops and achievements; every society quest with what it asks (crafting with a recipe per
/// crafter and its crystals, gathering with its quest-only node, the step kinds read from its script and text); and the
/// rank ladder (<c>BeastReputationRank</c>). Read once with the catalog.
/// </summary>
/// <remarks>
/// <para>
/// Facts from the allied-society research (scratchpad reports, game data 7.58): society dailies are the quests with a
/// <c>BeastTribe</c>, <c>IsRepeatable</c> and a <c>DailyQuestPool</c>; story quests with a rank raise it by one when done,
/// those locked against each other are Qitari's stela pairs (side quests before the rank quest); crafting dailies name
/// their product in <c>RITEM*</c>/<c>QST_PRODUCT_ITEM</c> (8 recipes, one per crafter; <c>RITEM0 = 100</c> is a
/// placeholder); gathering dailies name their item per gatherer in <c>QST_GATHERING_ITEM_MIN/HRV/FSH</c>, on gathering
/// point bases the sheets place nowhere (territory 1), so the society's zone and <c>ExportedGatheringPoint</c> place them.
/// </para>
/// <para>
/// The unlock quests and the society aetherytes are a fact table (the sheets don't link them); the portals in front of
/// the Omicron and Loporrit givers and into Zirgorteh come from <c>Warp</c> and <c>Level</c> rows (queststeps research).
/// </para>
/// </remarks>
internal sealed class TribeData
{
    private readonly Dictionary<uint, Tribe> tribes = new();
    private readonly Dictionary<uint, TribeQuest> quests = new();
    private readonly Dictionary<uint, TribeLadder> ladders = new();
    private readonly Dictionary<uint, (Tribe Tribe, TribeGiver Giver)> givers = new();
    private readonly string[] rankNames = new string[10];
    private readonly string[] alliedNames = new string[10];
    private readonly int[] required = new int[10];

    /// <summary>The societies in the game's order (expansion, then display order).</summary>
    public IReadOnlyList<Tribe> Tribes { get; private set; } = Array.Empty<Tribe>();

    public Tribe? Tribe(uint id) => tribes.GetValueOrDefault(id);

    /// <summary>A society quest by sheet row or by quest number (below 65536).</summary>
    public TribeQuest? Quest(uint rowOrNumber) => quests.GetValueOrDefault(rowOrNumber is > 0 and < 0x10000 ? rowOrNumber + 0x10000 : rowOrNumber);

    /// <summary>Every society quest (dailies, rank, unlock and side quests).</summary>
    public IReadOnlyCollection<TribeQuest> Quests => quests.Values;

    /// <summary>The society and giver of a daily giver's ENpc id.</summary>
    public (Tribe Tribe, TribeGiver Giver)? Giver(uint npcId) => givers.TryGetValue(npcId, out var g) ? g : null;

    /// <summary>The society's reputation ladder (for <see cref="TribeRanks"/>).</summary>
    public TribeLadder Ladder(uint tribe) => ladders.GetValueOrDefault(tribe) ?? new TribeLadder(0, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<bool>());

    /// <summary>Reputation that fills a rank's bar (<c>BeastReputationRank.RequiredReputation</c>).</summary>
    public int Required(int rank) => rank is > 0 and < 10 ? required[rank] : 0;

    /// <summary>
    /// A rank's name as the game shows it: rank 8 reads "Bloodsworn" for societies from Heavensward on, "Allied" once the
    /// expansion's intersocietal quest is done (and for the ARR societies).
    /// </summary>
    public string RankName(Tribe tribe, int rank, bool intersocietalDone)
    {
        if (rank is <= 0 or > 9) return "";
        if (rank >= 8 && tribe.Expansion > 0 && !intersocietalDone) return alliedNames[8] is { Length: > 0 } b ? b : rankNames[8];
        return rankNames[Math.Min(rank, 8)];
    }

    // ------------------------------------------------------------------ facts the sheets don't link

    /// <summary>The first quest of each society's unlock (quest numbers), by <c>BeastTribe</c> row.</summary>
    private static readonly Dictionary<uint, uint> UnlockQuests = new()
    {
        [1] = 1217, [2] = 1252, [3] = 1320, [4] = 1374, [5] = 1486, [6] = 2164, [7] = 2255, [8] = 2320, [9] = 2973, [10] = 3036,
        [11] = 3096, [12] = 3683, [13] = 3794, [14] = 3896, [15] = 4545, [16] = 4601, [17] = 4681, [18] = 5193, [19] = 5255, [20] = 5330,
    };

    /// <summary>The aetheryte a run teleports to for each society's giver (Aetheryte rows; the society's hub).</summary>
    private static readonly Dictionary<uint, uint> Aetherytes = new()
    {
        [1] = 19, [2] = 4, [3] = 16, [4] = 14, [5] = 7, [6] = 73, [7] = 77, [8] = 79, [9] = 105, [10] = 99,
        [11] = 128, [12] = 144, [13] = 143, [14] = 136, [15] = 169, [16] = 181, [17] = 175, [18] = 238, [19] = 206, [20] = 201,
    };

    /// <summary>"Friends Forever" (1473), the ARR intersocietal quest; the ARR rows of <c>BeastTribe</c> leave it out.</summary>
    private const uint ArrIntersocietal = 0x10000 + 1473;

    /// <summary>The Ixal and Moogle gil shops (mounts, minions, the Ehcatl Wristgloves); the others are found by their rank gates.</summary>
    private static readonly Dictionary<uint, uint> GilShops = new() { [5] = 262696, [8] = 262699 };

    /// <summary>
    /// Portals per society: the event object or NPC, where it stands (its Level row, or a recorded position where it has
    /// none), and its warp (destination and question).
    /// </summary>
    private static readonly (uint Tribe, uint Id, bool Npc, uint Level, Vector3 Fallback, uint Territory, uint Warp, PortalUse Use)[] Portals =
    {
        // Omicrons: Stigma-4 stands in A-4 Research, behind this portal (no Level row; position from the research).
        (16, 2013072, false, 0, new Vector3(456.7f, 438.0f, 310.2f), 960, 131483, PortalUse.ToGiver),
        // Omicrons: Elysion, where some steps happen.
        (16, 2013074, false, 9329555, Vector3.Zero, 960, 131480, PortalUse.SubZone),
        // Loporrits: Managingway stands in Hoper's Hold, behind this passage.
        (17, 2013249, false, 9563729, Vector3.Zero, 959, 131509, PortalUse.ToGiver),
        // Yok Huy: Narvurgar, next to Vuyargur, takes you to Zirgorteh the Open-armed for the crafting steps.
        (20, 1054640, true, 11859456, Vector3.Zero, 1187, 131605, PortalUse.SubZone),
    };

    /// <summary>
    /// /say dailies whose phrase has no <c>_SAYTODO_</c> text (the Moogles' 2295 and five Ixal ones; queststeps research).
    /// </summary>
    private static readonly HashSet<ushort> SayWithoutText = new() { 2295, 1497, 1501, 1507, 1515, 1568 };

    private const uint CarryStatus = 404;

    // ------------------------------------------------------------------ building

    public static TribeData Build()
    {
        var d = new TribeData();
        try
        {
            d.Load();
        }
        catch (Exception e)
        {
            Services.Log.Error(e, "Couldn't read the allied societies");
        }
        return d;
    }

    private void Load()
    {
        foreach (var r in Services.Data.GetExcelSheet<BeastReputationRank>())
        {
            if (r.RowId is 0 or > 9) continue;
            rankNames[r.RowId] = r.Name.ExtractText();
            alliedNames[r.RowId] = r.AlliedNames.ExtractText();
            required[r.RowId] = r.RequiredReputation;
        }

        var beastTribes = Services.Data.GetExcelSheet<BeastTribe>().Where(b => b.RowId > 0 && b.Name.ExtractText().Length > 0).ToList();
        var ids = beastTribes.Select(b => b.RowId).ToHashSet();
        var questSheet = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Quest>();
        var society = questSheet.Where(q => ids.Contains(q.BeastTribe.RowId)).ToList();

        // Crafting and gathering data for every daily at once: one pass over each big sheet.
        var craftItems = new HashSet<uint>();
        var gatherItems = new HashSet<uint>();
        foreach (var q in society.Where(IsDaily))
        foreach (var p in q.QuestParams)
        {
            var name = p.ScriptInstruction.ExtractText();
            if (name.StartsWith("RITEM", StringComparison.Ordinal) || name == "QST_PRODUCT_ITEM")
            {
                if (p.ScriptArg is > 100 and < 1_000_000) craftItems.Add(p.ScriptArg);
            }
            else if (name is "QST_GATHERING_ITEM_MIN" or "QST_GATHERING_ITEM_HRV" && p.ScriptArg is > 0 and < 1_000_000) gatherItems.Add(p.ScriptArg);
        }
        var recipes = IndexRecipes(craftItems);
        var nodes = IndexNodes(gatherItems);
        var emotes = IndexEmotes();

        var territories = new Dictionary<uint, uint>();
        foreach (var b in beastTribes) territories[b.RowId] = b.Level.ValueNullable?.Territory.RowId ?? 0;

        // Every society quest, read once.
        foreach (var q in society)
        {
            var tribe = q.BeastTribe.RowId;
            var quest = ReadQuest(q, territories.GetValueOrDefault(tribe), recipes, nodes, emotes);
            if (quest != null) quests[quest.RowId] = quest;
        }

        var achievements = IndexAchievements(beastTribes);
        var shops = IndexShops(beastTribes);
        var list = new List<Tribe>();
        foreach (var b in beastTribes)
        {
            var t = BuildTribe(b, territories.GetValueOrDefault(b.RowId), achievements.GetValueOrDefault(b.RowId) ?? new List<TribeAchievement>(),
                shops.GetValueOrDefault(b.RowId) ?? new List<TribeShopItem>());
            tribes[t.Id] = t;
            list.Add(t);
            // Ixal "Deliverance" (70 a day, once) isn't a daily like the others: the ladder goes by the givers' dailies.
            ladders[t.Id] = TribeLadder.From(t.MaxRank, required,
                t.Dailies.Where(q => !QuestPolicy.Never.ContainsKey(q.Number)).Select(q => (q.Rank, q.Reputation)));
            foreach (var g in t.Givers) givers[g.NpcId] = (t, g);
        }
        Tribes = list.OrderBy(t => t.Expansion).ThenBy(t => beastTribes.First(b => b.RowId == t.Id).DisplayOrder).ToList();
        Services.Log.Information("Allied societies: {Tribes} societies, {Dailies} dailies, {Quests} quests in all",
            Tribes.Count, quests.Values.Count(q => q.IsDaily), quests.Count);
    }

    private static bool IsDaily(Lumina.Excel.Sheets.Quest q) => q.IsRepeatable && q.DailyQuestPool != 0;

    // ------------------------------------------------------------------ quests

    private TribeQuest? ReadQuest(Lumina.Excel.Sheets.Quest q, uint tribeTerritory, Dictionary<uint, List<Recipe>> recipes,
        Dictionary<uint, List<(GatheringPointBase Base, uint GatheringItem)>> nodes, Dictionary<string, uint> emotes)
    {
        var tribe = q.BeastTribe.RowId;
        var daily = IsDaily(q);
        var rank = (int)q.BeastReputationRank.RowId;
        var alternatives = q.QuestLock.Where(l => l.RowId != 0).Select(l => l.RowId).ToList();
        var kind = daily ? TribeQuestKind.Daily
            : rank == 0 ? TribeQuestKind.Unlock
            : alternatives.Count > 0 ? TribeQuestKind.Side
            : TribeQuestKind.RankUp;
        var classes = KindOf(q.ClassJobCategory0.ValueNullable);
        var facts = QuestFacts.Of(q.RowId);
        var steps = StepKinds.None;
        var keyItems = new List<uint>();
        var subZones = new List<uint>();
        uint mount = 0, action = 0, fish = 0;
        var craftCandidates = new List<uint>();
        var gatherFor = new List<(uint ClassJob, uint Item)>();

        foreach (var (name, value) in facts?.Args ?? Array.Empty<(string, uint)>())
        {
            if (Is(name, "RITEM") || name == "QST_PRODUCT_ITEM")
            {
                if (value is > 100 and < 1_000_000) craftCandidates.Add(value);
            }
            else if (name == "QST_GATHERING_ITEM_MIN") gatherFor.Add((16, value));
            else if (name == "QST_GATHERING_ITEM_HRV") gatherFor.Add((17, value));
            else if (name == "QST_GATHERING_ITEM_FSH") fish = value;
            else if (Is(name, "ITEM") && value >= 2_000_000)
            {
                keyItems.Add(value);
                steps |= StepKinds.KeyItem;
                if (Services.Data.GetExcelSheet<EventItem>().GetRowOrDefault(value) is { } ev && ev.Action.RowId != 0) steps |= StepKinds.UseItem;
            }
            else if (name == "MOUNT0" && value != 0) { mount = value; steps |= StepKinds.Mount; }
            else if (name == "ACTION0" && value != 0) { action = value; steps |= StepKinds.MountAction; }
            else if (Is(name, "ENEMY")) steps |= StepKinds.Combat;
            else if (name == "STATUS0" && value == CarryStatus) steps |= StepKinds.Carry;
            else if (Is(name, "EVENTRANGE")) steps |= StepKinds.EnterArea;
            else if (Is(name, "TERRITORYTYPE") && value != 0)
            {
                steps |= StepKinds.SubZone;
                if (value != tribeTerritory && !subZones.Contains(value)) subZones.Add(value);
            }
            else if (Is(name, "SNP")) steps |= StepKinds.Snipe;
            else if (name.StartsWith("BIND_LEAD", StringComparison.Ordinal) || name.StartsWith("SEQEV_TSURE", StringComparison.Ordinal) || name == "ET_DOUKOU")
                steps |= StepKinds.Escort;
        }
        if (q.ClassJobCategory1.RowId != 0) steps |= StepKinds.OtherClass;

        // Zones the to-do lines point at, besides the society's own.
        foreach (var todo in facts?.Todos ?? Array.Empty<QuestTodo>())
        foreach (var spot in todo.Spots)
            if (spot.Territory != 0 && spot.Territory != tribeTerritory && !subZones.Contains(spot.Territory))
            {
                subZones.Add(spot.Territory);
                steps |= StepKinds.SubZone;
            }

        // Crafting: the candidate that some recipe makes is the product; the recipe's first ingredient is the materials.
        QuestCraft? craft = null;
        foreach (var item in craftCandidates)
        {
            if (!recipes.TryGetValue(item, out var rows) || rows.Count == 0) continue;
            craft = Craft(item, rows);
            steps |= StepKinds.Craft;
            break;
        }

        var gather = new List<QuestGather>();
        foreach (var (job, item) in gatherFor)
        {
            if (item == 0) continue;
            steps |= StepKinds.Gather;
            if (Node(item, job, nodes, tribeTerritory) is { } node) gather.Add(node);
        }
        if (fish != 0) steps |= StepKinds.Fish;

        // The journal text (client language) for /say phrases and emotes. Battle societies' text isn't needed yet.
        var emoteIds = new List<uint>();
        if (classes != TribeKind.Battle && facts != null)
        {
            if (facts.TextWith("_SAYTODO_").Any() || SayWithoutText.Contains((ushort)(q.RowId - 0x10000))) steps |= StepKinds.Say;
            foreach (var line in facts.TodoText)
            foreach (Match m in SlashCommand.Matches(line))
                if (emotes.TryGetValue(m.Value.ToLowerInvariant(), out var emote) && !emoteIds.Contains(emote))
                    emoteIds.Add(emote);
            if (emoteIds.Count > 0) steps |= StepKinds.Emote;
        }

        var level = q.ClassJobLevel.Count > 0 ? q.ClassJobLevel[0] : 0;
        return new TribeQuest(q.RowId, tribe, CleanName(q.Name.ExtractText()), kind, rank, q.DailyQuestPool, q.IssuerStart.RowId, q.TargetEnd.RowId,
            classes, level, q.ReputationReward, steps, craft, gather, fish, keyItems, (mount, action), emoteIds, subZones, alternatives);
    }

    private static readonly Regex SlashCommand = new(@"/[\p{L}\p{N}_\-]+", RegexOptions.Compiled);

    /// <summary>A script argument of a numbered family: "ITEM0", "ITEM1" … (not "RITEM0", "ITEMCOUNT").</summary>
    private static bool Is(string name, string prefix)
        => name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length && name.AsSpan(prefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;

    private static QuestCraft Craft(uint product, List<Recipe> rows)
    {
        var list = new List<QuestRecipe>();
        uint materials = 0;
        var canHq = false;
        foreach (var r in rows.OrderBy(r => r.CraftType.RowId))
        {
            var crystals = new List<(uint, int)>();
            for (var i = 0; i < r.Ingredient.Count && i < r.AmountIngredient.Count; i++)
            {
                var id = r.Ingredient[i].RowId;
                var amount = r.AmountIngredient[i];
                if (id == 0 || amount == 0) continue;
                if (Core.Catalog.Items.IsCrystal(id)) crystals.Add((id, amount));
                else if (materials == 0) materials = id;
            }
            canHq |= r.CanHq;
            list.Add(new QuestRecipe(r.RowId, 8 + r.CraftType.RowId, r.RecipeLevelTable.ValueNullable?.ClassJobLevel ?? 0, crystals,
                r.ItemRequired.RowId, r.StatusRequired.RowId));
        }
        return new QuestCraft(product, materials, list, canHq);
    }

    /// <summary>The quest-only node of a gathered item for Miner (16) or Botanist (17), placed in the society's zone.</summary>
    private static QuestGather? Node(uint item, uint job, Dictionary<uint, List<(GatheringPointBase Base, uint GatheringItem)>> nodes, uint territory)
    {
        if (!nodes.TryGetValue(item, out var bases)) return null;
        // Types 0-1 are mining and quarrying, 2-3 logging and harvesting.
        foreach (var (b, gi) in bases.Where(x => x.Base.GatheringType.RowId <= 3 && (job == 16) == (x.Base.GatheringType.RowId <= 1)))
        {
            var at = Services.Data.GetExcelSheet<ExportedGatheringPoint>().GetRowOrDefault(b.RowId);
            return new QuestGather(job, item, gi, b.RowId, b.GatheringLevel, territory,
                at is { } e ? new Vector2(e.X, e.Y) : Vector2.Zero, at?.Radius ?? 0);
        }
        return null;
    }

    private static Dictionary<uint, List<Recipe>> IndexRecipes(HashSet<uint> items)
    {
        var result = new Dictionary<uint, List<Recipe>>();
        foreach (var r in Services.Data.GetExcelSheet<Recipe>())
        {
            var item = r.ItemResult.RowId;
            if (!items.Contains(item) || r.CraftType.RowId > 7) continue;
            if (!result.TryGetValue(item, out var list)) result[item] = list = new List<Recipe>(8);
            list.Add(r);
        }
        return result;
    }

    private static Dictionary<uint, List<(GatheringPointBase, uint)>> IndexNodes(HashSet<uint> items)
    {
        var byGatheringItem = new Dictionary<uint, uint>();
        foreach (var gi in Services.Data.GetExcelSheet<GatheringItem>())
            if (items.Contains(gi.Item.RowId)) byGatheringItem[gi.RowId] = gi.Item.RowId;
        var result = new Dictionary<uint, List<(GatheringPointBase, uint)>>();
        foreach (var b in Services.Data.GetExcelSheet<GatheringPointBase>())
        foreach (var reference in b.Item)
        {
            if (reference.RowId == 0 || !byGatheringItem.TryGetValue(reference.RowId, out var item)) continue;
            if (!result.TryGetValue(item, out var list)) result[item] = list = new List<(GatheringPointBase, uint)>(1);
            list.Add((b, reference.RowId));
        }
        return result;
    }

    /// <summary>Emote text commands in the client's language ("/psych", "/poke"), lower case, to their Emote rows.</summary>
    private static Dictionary<string, uint> IndexEmotes()
    {
        var result = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var e in Services.Data.GetExcelSheet<Emote>())
        {
            if (e.TextCommand.ValueNullable is not { } c) continue;
            foreach (var text in new[] { c.Command, c.ShortCommand, c.Alias, c.ShortAlias })
            {
                var s = text.ExtractText().Trim().ToLowerInvariant();
                if (s.Length > 1 && s[0] == '/') result.TryAdd(s, e.RowId);
            }
        }
        return result;
    }

    private static TribeKind KindOf(ClassJobCategory? category)
    {
        if (category is not { } c) return TribeKind.Battle;
        var crafter = c.CRP || c.BSM || c.ARM || c.GSM || c.LTW || c.WVR || c.ALC || c.CUL;
        var gatherer = c.MIN || c.BTN || c.FSH;
        return crafter && gatherer ? TribeKind.Either : crafter ? TribeKind.Crafter : gatherer ? TribeKind.Gatherer : TribeKind.Battle;
    }

    // ------------------------------------------------------------------ societies

    private Tribe BuildTribe(BeastTribe b, uint territory, List<TribeAchievement> achievements, List<TribeShopItem> shop)
    {
        var id = b.RowId;
        var mine = quests.Values.Where(q => q.TribeId == id).ToList();
        var dailies = mine.Where(q => q.IsDaily).OrderBy(q => q.RowId).ToList();
        var kind = dailies.Count == 0 ? TribeKind.Battle
            : dailies.GroupBy(q => q.Classes).OrderByDescending(g => g.Count()).First().Key;
        var startRank = dailies.Count > 0 ? dailies.Min(q => q.Rank) : 1;
        var level = dailies.Count > 0 ? dailies.Min(q => q.Level) : 0;

        // Givers: each daily's issuer, placed by the quest's issuer Level.
        var givers = new List<TribeGiver>();
        foreach (var group in dailies.GroupBy(q => q.Giver).OrderBy(g => g.Min(q => q.Rank)).ThenBy(g => g.Key))
        {
            var row = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Quest>().GetRowOrDefault(group.First().RowId);
            var at = row?.IssuerLocation.ValueNullable;
            givers.Add(new TribeGiver(group.Key, NpcName(group.Key), at?.Territory.RowId ?? territory, PlaceName(at?.Territory.RowId ?? territory),
                at is { } l ? new Vector3(l.X, l.Y, l.Z) : Vector3.Zero, group.Min(q => q.Rank), group.Max(q => q.Rank)));
        }

        // Unlock: the fact table's first quest, then the society's rank-0 story quests.
        var unlock = new List<TribeUnlockStep>();
        if (UnlockQuests.TryGetValue(id, out var first) && UnlockStep(0x10000 + first) is { } step) unlock.Add(step);
        foreach (var q in mine.Where(q => q.Kind == TribeQuestKind.Unlock && q.Number != first).OrderBy(q => q.RowId))
            if (UnlockStep(q.RowId) is { } s) unlock.Add(s);

        // Rank quests, with the side quests (one of a pair) that come before them.
        var rankQuests = new List<TribeRankQuest>();
        foreach (var q in mine.Where(q => q.Kind == TribeQuestKind.RankUp).OrderBy(q => q.Rank).ThenBy(q => q.RowId))
        {
            var row = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Quest>().GetRowOrDefault(q.RowId);
            var previous = row?.PreviousQuest.Where(p => p.RowId != 0).Select(p => p.RowId).ToHashSet() ?? new HashSet<uint>();
            var before = mine.Where(s => s.Kind == TribeQuestKind.Side && s.Rank == q.Rank && previous.Contains(s.RowId))
                .OrderBy(s => s.RowId).Select(s => (s.RowId, s.Name)).ToList();
            var (npc, npcName, zone, place, map) = Issuer(row);
            rankQuests.Add(new TribeRankQuest(q.RowId, q.Name, q.Rank, npc, npcName, zone, place, map, before));
        }

        var intersocietal = b.IntersocietalQuest.RowId != 0 ? b.IntersocietalQuest.RowId : b.Expansion.RowId == 0 ? ArrIntersocietal : 0;
        var interName = intersocietal != 0 ? CleanName(Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Quest>().GetRowOrDefault(intersocietal)?.Name.ExtractText() ?? "") : "";

        var portals = new List<TribePortal>();
        foreach (var p in Portals.Where(p => p.Tribe == id))
        {
            var spot = p.Level != 0 ? Services.Data.GetExcelSheet<Level>().GetRowOrDefault(p.Level) : null;
            var warp = Services.Data.GetExcelSheet<Warp>().GetRowOrDefault(p.Warp);
            var name = p.Npc ? NpcName(p.Id) : Capitalise(Services.Data.GetExcelSheet<EObjName>().GetRowOrDefault(p.Id)?.Singular.ExtractText() ?? "");
            portals.Add(new TribePortal(p.Id, p.Npc, name, spot?.Territory.RowId ?? p.Territory,
                spot is { } s ? new Vector3(s.X, s.Y, s.Z) : p.Fallback, warp?.TerritoryType.RowId ?? 0, p.Warp,
                warp?.Question.ExtractText() ?? "", p.Use));
        }

        return new Tribe(id, Capitalise(b.Name.ExtractText()), (int)b.Expansion.RowId, kind, b.MaxRank, startRank, b.CurrencyItem.RowId,
            territory, PlaceName(territory), Aetherytes.GetValueOrDefault(id), level, givers, unlock, rankQuests, intersocietal, interName, portals,
            shop.OrderBy(s => s.Rank < 0 ? int.MaxValue : s.Rank).ThenBy(s => s.Kind == ShopItemKind.Other ? 1 : 0).ThenBy(s => s.ItemId).ToList(),
            achievements.OrderBy(a => a.Intersocietal).ThenBy(a => a.Rank).ToList(), dailies);
    }

    private static TribeUnlockStep? UnlockStep(uint row)
    {
        if (Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Quest>().GetRowOrDefault(row) is not { } q) return null;
        var (npc, npcName, zone, place, map) = Issuer(q);
        // Category 1 is "All Classes": nothing worth saying.
        var category = q.ClassJobCategory0.RowId > 1 ? q.ClassJobCategory0.ValueNullable?.Name.ExtractText() ?? "" : "";
        var level = q.ClassJobLevel.Count > 0 ? q.ClassJobLevel[0] : 0;
        var requirement = category.Length == 0 ? "" : level > 1 ? $"{category}, level {level}" : category;
        var previous = q.PreviousQuest.Where(p => p.RowId != 0)
            .Select(p => (p.RowId, CleanName(p.ValueNullable?.Name.ExtractText() ?? ""))).ToList();
        return new TribeUnlockStep(q.RowId, CleanName(q.Name.ExtractText()), npc, npcName, zone, place, map, requirement, previous);
    }

    /// <summary>Who offers a quest and where: the NPC, the zone and its name, and the map coordinates.</summary>
    private static (uint Npc, string Name, uint Territory, string Place, Vector2 Map) Issuer(Lumina.Excel.Sheets.Quest? q)
    {
        if (q is not { } quest) return (0, "", 0, "", Vector2.Zero);
        var npc = quest.IssuerStart.RowId;
        var at = quest.IssuerLocation.ValueNullable;
        var zone = at?.Territory.RowId ?? 0;
        return (npc, NpcName(npc), zone, PlaceName(zone), at is { } l ? MapPosition(l) : Vector2.Zero);
    }

    /// <summary>Map coordinates of a Level row, as the game's map shows them (41 / scale, offset by the map).</summary>
    public static Vector2 MapPosition(Level level)
    {
        if (level.Map.ValueNullable is not { } map) return Vector2.Zero;
        var c = map.SizeFactor / 100f;
        float To(float world, short offset) => 41f / c * ((world + offset) * c + 1024f) / 2048f + 1f;
        return new Vector2(To(level.X, map.OffsetX), To(level.Z, map.OffsetY));
    }

    private static string NpcName(uint npc)
        => Capitalise(Services.Data.GetExcelSheet<ENpcResident>().GetRowOrDefault(npc)?.Singular.ExtractText() ?? "");

    private static string PlaceName(uint territory)
        => Services.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory)?.PlaceName.ValueNullable?.Name.ExtractText() ?? "";

    /// <summary>"moogles" → "Moogles" (some society and NPC names are lower case in the sheets).</summary>
    public static string Capitalise(string text)
        => text.Length == 0 || char.IsUpper(text[0]) ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>A quest name without the journal icon the sheet puts in front (a private-use glyph) and spaces.</summary>
    public static string CleanName(string text)
    {
        var chars = text.Where(c => c is < '' or > '').ToArray();
        return new string(chars).Trim();
    }

    // ------------------------------------------------------------------ achievements and shops

    private static Dictionary<uint, List<TribeAchievement>> IndexAchievements(List<BeastTribe> tribes)
    {
        var result = new Dictionary<uint, List<TribeAchievement>>();
        var byIntersocietal = new Dictionary<uint, List<uint>>();
        foreach (var b in tribes)
        {
            var quest = b.IntersocietalQuest.RowId != 0 ? b.IntersocietalQuest.RowId : b.Expansion.RowId == 0 ? ArrIntersocietal : 0;
            if (quest == 0) continue;
            if (!byIntersocietal.TryGetValue(quest, out var list)) byIntersocietal[quest] = list = new List<uint>();
            list.Add(b.RowId);
        }
        foreach (var a in Services.Data.GetExcelSheet<Achievement>())
        {
            var name = a.Name.ExtractText();
            if (name.Length == 0) continue;
            var title = a.Title.ValueNullable?.Masculine.ExtractText() ?? "";
            if (a.Type == 15 && a.Key.RowId is > 0 and <= 20)
            {
                var rank = a.Data.Count > 0 ? (int)a.Data[0].RowId : 0;
                Add(a.Key.RowId, new TribeAchievement(a.RowId, name, rank, title, false));
            }
            else if (a.Type == 6 && byIntersocietal.TryGetValue(a.Key.RowId, out var owners))
            {
                foreach (var owner in owners) Add(owner, new TribeAchievement(a.RowId, name, 0, title, true));
            }
        }
        return result;

        void Add(uint tribe, TribeAchievement achievement)
        {
            if (!result.TryGetValue(tribe, out var list)) result[tribe] = list = new List<TribeAchievement>();
            list.Add(achievement);
        }
    }

    private Dictionary<uint, List<TribeShopItem>> IndexShops(List<BeastTribe> tribes)
    {
        var result = new Dictionary<uint, List<TribeShopItem>>();
        var byCurrency = tribes.Where(b => b.CurrencyItem.RowId != 0).ToDictionary(b => b.CurrencyItem.RowId, b => b.RowId);
        var rankQuestTribe = quests.Values.Where(q => q.Kind == TribeQuestKind.RankUp).ToDictionary(q => q.RowId, q => q.TribeId);
        var intersocietal = tribes.ToDictionary(b => b.RowId, b => b.IntersocietalQuest.RowId != 0 ? b.IntersocietalQuest.RowId : b.Expansion.RowId == 0 ? ArrIntersocietal : 0);
        var items = Services.Data.GetExcelSheet<Item>();

        // Currency shops: every entry that costs a society's currency.
        foreach (var shop in Services.Data.GetExcelSheet<SpecialShop>())
        foreach (var entry in shop.Item)
        {
            // A default struct from Lumina throws on its fields, so no FirstOrDefault here.
            uint tribe = 0, currency = 0, cost = 0, received = 0, count = 0;
            foreach (var c in entry.ItemCosts)
                if (c.ItemCost.RowId != 0 && byCurrency.TryGetValue(c.ItemCost.RowId, out var t)) { tribe = t; currency = c.ItemCost.RowId; cost = c.CurrencyCost; break; }
            if (tribe == 0) continue;
            foreach (var r in entry.ReceiveItems)
                if (r.Item.RowId != 0) { received = r.Item.RowId; count = r.ReceiveCount; break; }
            if (received == 0) continue;
            Add(tribe, received, (int)Math.Max(1, count), currency, (int)cost, entry.Quest.RowId);
        }

        // Gil shops: the Ixal and Moogle ones, and any whose entries wait for a society's rank quest.
        var gil = Services.Data.GetSubrowExcelSheet<GilShopItem>();
        var owners = new Dictionary<uint, uint>(GilShops.ToDictionary(kv => kv.Value, kv => kv.Key));
        foreach (var row in gil)
        foreach (var entry in row)
        foreach (var gate in entry.QuestRequired)
            if (gate.RowId != 0 && rankQuestTribe.TryGetValue(gate.RowId, out var t)) owners.TryAdd(row.RowId, t);
        foreach (var (shopId, tribe) in owners)
        {
            if (!gil.HasRow(shopId)) continue;
            foreach (var entry in gil.GetRow(shopId))
            {
                if (entry.Item.RowId == 0) continue;
                uint gate = 0;
                foreach (var g in entry.QuestRequired)
                    if (g.RowId != 0) { gate = g.RowId; break; }
                Add(tribe, entry.Item.RowId, 1, 1, (int)(items.GetRowOrDefault(entry.Item.RowId)?.PriceMid ?? 0), gate);
            }
        }
        return result;

        void Add(uint tribe, uint item, int count, uint currency, int cost, uint gate)
        {
            if (!result.TryGetValue(tribe, out var list)) result[tribe] = list = new List<TribeShopItem>();
            if (list.Any(s => s.ItemId == item && s.Currency == currency && s.Gate == gate)) return;
            int rank;
            var gateName = "";
            if (gate == 0) rank = 0;
            else if (quests.TryGetValue(gate, out var q) && q.TribeId == tribe && q.Kind == TribeQuestKind.RankUp) rank = q.Rank + 1;
            // Gated by the unlock quests: there from the start.
            else if (q != null && q.TribeId == tribe && q.Kind == TribeQuestKind.Unlock || gate == 0x10000 + UnlockQuests.GetValueOrDefault(tribe)) rank = 0;
            else if (gate == intersocietal.GetValueOrDefault(tribe)) rank = TribeShopItem.AlliedRank;
            else rank = -1;
            if (gate != 0) gateName = CleanName(Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Quest>().GetRowOrDefault(gate)?.Name.ExtractText() ?? "");
            list.Add(new TribeShopItem(item, count, currency, cost, rank, gate, gateName, KindOf(items.GetRowOrDefault(item))));
        }
    }

    /// <summary>What a shop item is, from its item action (mounts 1322, minions 853 …) and its category.</summary>
    private static ShopItemKind KindOf(Item? item)
    {
        if (item is not { } i) return ShopItemKind.Other;
        switch (i.ItemAction.ValueNullable?.Action.RowId ?? 0)
        {
            case 1322: return ShopItemKind.Mount;
            case 853: return ShopItemKind.Minion;
            case 25183: return ShopItemKind.Orchestrion;
            case 2633: return ShopItemKind.Unlock;
            case 3357: return ShopItemKind.Card;
            case 1013: return ShopItemKind.Barding;
            case 29459: return ShopItemKind.FramersKit;
            case 20086: return ShopItemKind.Accessory;
        }
        var category = i.ItemUICategory.RowId;
        if (category is 58 or 83) return ShopItemKind.Materia;
        if (category is 57 or >= 65 and <= 80 or 95) return ShopItemKind.Furnishing;
        if (i.EquipSlotCategory.RowId != 0) return ShopItemKind.Gear;
        return ShopItemKind.Other;
    }
}
