using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Provenance.Core.Tribes;

/// <summary>Which classes a society's quests take (<c>Quest.ClassJobCategory0</c> of its dailies).</summary>
public enum TribeKind
{
    /// <summary>Disciples of war and magic, limited jobs left out (category 142).</summary>
    Battle,
    /// <summary>Disciples of the hand (category 33).</summary>
    Crafter,
    /// <summary>Disciples of the land (category 32).</summary>
    Gatherer,
    /// <summary>A crafter or a gatherer (category 35: the Namazu).</summary>
    Either,
}

/// <summary>What the day's allied society run aims for (the Tribes page's goal).</summary>
public enum TribeGoal
{
    /// <summary>Societies below their top rank first, the lowest first.</summary>
    RankUp,
    /// <summary>
    /// Society currency (and gil and crystals): as many dailies as the day allows. Most societies pay the same for every
    /// daily; the Ixal pay Ixali Oaknots only at their Respected and Honored givers, and the four ARR battle societies
    /// their tokens only at the Friendly giver, so the givers that pay none come last (<see cref="TribeGiver.PaysCurrency"/>).
    /// </summary>
    Currency,
    /// <summary>
    /// Class levels: the lowest class that qualifies takes each quest; a battle quest the lowest combat job that's at
    /// least <see cref="TribeClasses.LevellingMargin"/> levels above it.
    /// </summary>
    Levelling,
}

/// <summary>The part a quest plays for its society.</summary>
public enum TribeQuestKind
{
    /// <summary>A repeatable daily (<c>DailyQuestPool</c> 1, 2 or 3).</summary>
    Daily,
    /// <summary>A story quest that raises the rank by one when done (offered once the reputation bar is full).</summary>
    RankUp,
    /// <summary>A story quest that unlocks the society (rank 0 in the data).</summary>
    Unlock,
    /// <summary>A side story quest at a rank, done before the rank quest (Qitari: one of two stela quests).</summary>
    Side,
}

/// <summary>
/// What a quest asks the player to do besides talking, read from its script arguments (<c>Quest.QuestParams</c>), its
/// class columns and its journal text. One quest has several.
/// </summary>
[Flags]
public enum StepKinds : uint
{
    None = 0,
    /// <summary>Crafts a quest item from materials the quest hands out (<c>RITEM*</c>, <c>QST_PRODUCT_ITEM</c>).</summary>
    Craft = 1 << 0,
    /// <summary>Gathers a quest item as Miner or Botanist (<c>QST_GATHERING_ITEM_MIN/HRV</c>).</summary>
    Gather = 1 << 1,
    /// <summary>Catches a quest fish as Fisher (<c>QST_GATHERING_ITEM_FSH</c>).</summary>
    Fish = 1 << 2,
    /// <summary>Says a phrase in /say (a <c>_SAYTODO_</c> text, or one of the older quests without one).</summary>
    Say = 1 << 3,
    /// <summary>Uses an emote on someone (the journal names its /command).</summary>
    Emote = 1 << 4,
    /// <summary>Rides a quest mount the quest lends (<c>MOUNT0</c>).</summary>
    Mount = 1 << 5,
    /// <summary>Uses the quest mount's action on targets (<c>ACTION0</c>).</summary>
    MountAction = 1 << 6,
    /// <summary>Carries or hands over key items (<c>ITEM*</c>, event items).</summary>
    KeyItem = 1 << 7,
    /// <summary>Uses a key item on something (an event item with an action).</summary>
    UseItem = 1 << 8,
    /// <summary>
    /// Fights: field monsters whose kills the quest counts, or enemies that appear for the quest (<c>ENEMY*</c>, and
    /// the battle NPCs the quest listens to for a defeat).
    /// </summary>
    Combat = 1 << 9,
    /// <summary>Carries an object in both arms (status 404 "Transporting").</summary>
    Carry = 1 << 10,
    /// <summary>Walks or rides into an area (<c>EVENTRANGE*</c>).</summary>
    EnterArea = 1 << 11,
    /// <summary>Goes into another zone the society's steps use (Zirgorteh, Elysion, a portal's other side).</summary>
    SubZone = 1 << 12,
    /// <summary>A first-person shooting minigame (<c>SNP*</c>, <c>SNIPE_*</c>).</summary>
    Snipe = 1 << 13,
    /// <summary>Takes another class partway through (Ixal: <c>ClassJobCategory1</c>, a gatherer or fisher step).</summary>
    OtherClass = 1 << 14,
    /// <summary>Someone walks along with the player (<c>BIND_LEAD_*</c>, <c>SEQEV_TSURE*</c>).</summary>
    Escort = 1 << 15,
    /// <summary>
    /// Done unseen: a spell hides the player for a while and the steps have to be done before it runs out (status 416
    /// "Transparent"; three Sylph dailies and a Sylph rank quest).
    /// </summary>
    Stealth = 1 << 16,
    /// <summary>
    /// Uses a key item on one of the quest's enemies, on most of them once the fight has weakened it (the quest listens
    /// to a battle NPC for an item).
    /// </summary>
    ItemOnEnemy = 1 << 17,
    /// <summary>
    /// An emote whose to-do line names no NPC to aim it at: the line points at an area or an object (the Kobolds'
    /// pygmy bombs, the Vanu Vanu's dancers and totem, the Moogles' malingerer…).
    /// </summary>
    EmoteUnnamed = 1 << 18,
}

/// <summary>A recipe that makes a crafting daily's item for one crafter, with the crystals one craft takes.</summary>
/// <param name="ClassJob">The crafter's ClassJob row (8 Carpenter … 15 Culinarian).</param>
/// <param name="Level">The recipe's class level (the quest syncs it to yours).</param>
/// <param name="Crystals">Shards, crystals or clusters (items 2-19) one craft takes.</param>
/// <param name="EquipItem">An item that must be equipped to craft it (Ixal: the Ehcatl Wristgloves, 8568; 0 for others).</param>
/// <param name="Status">A status the crafter must have (Ixal: a "Facility Access" status, 547-554; 0 for others).</param>
public sealed record QuestRecipe(uint RecipeId, uint ClassJob, int Level, IReadOnlyList<(uint ItemId, int Amount)> Crystals,
    uint EquipItem = 0, uint Status = 0);

/// <summary>What a crafting daily makes: its product, the materials item the quest hands out (one per craft), and a recipe per crafter.</summary>
/// <param name="CanHq">The product can come out high quality (the hand-over then asks a yes/no question).</param>
/// <param name="NeedsHq">
/// Only high-quality copies of the product count (the quest's script says so): the run crafts it for full quality,
/// which takes a crafter whose gear fills the recipe's quality bar.
/// </param>
public sealed record QuestCraft(uint Product, uint Materials, IReadOnlyList<QuestRecipe> Recipes, bool CanHq, bool NeedsHq = false)
{
    /// <summary>
    /// The most crafts a society daily asks for. The count isn't in the game data (the quest says it once accepted), so
    /// checks made before accepting assume this many and say "up to".
    /// </summary>
    public const int MostCrafts = 4;

    public QuestRecipe? For(uint classJob) => Recipes.FirstOrDefault(r => r.ClassJob == classJob);
}

/// <summary>
/// The item a gathering daily asks of one gatherer, and where its quest-only node is: a gathering point base whose
/// points the sheets place in no zone (territory 1), so the society's zone and the base's exported centre are used.
/// </summary>
/// <param name="ClassJob">16 Miner or 17 Botanist.</param>
/// <param name="Centre">World X/Z of the base's spawn area (<c>ExportedGatheringPoint</c>).</param>
public sealed record QuestGather(uint ClassJob, uint ItemId, uint GatheringItem, uint PointBase, int Level, uint Territory, Vector2 Centre, float Radius);

/// <summary>A society quest: a daily, a rank quest, an unlock quest or a side story quest.</summary>
/// <param name="RowId">The Quest sheet row (65536 + the quest number the game and the wiki use).</param>
/// <param name="Rank">
/// Dailies: the lowest rank that offers it. Rank quests: the rank it's offered at (done, it raises the rank by one).
/// Unlock quests: 0.
/// </param>
/// <param name="Pool">
/// <c>DailyQuestPool</c>: 1 or 2 for a normal daily, 3 for an "exclusive" one (ARR battle givers), 0 for a story quest.
/// </param>
/// <param name="Giver">The ENpc that offers it (<c>IssuerStart</c>).</param>
/// <param name="TurnIn">The ENpc it's handed in to (<c>TargetEnd</c>).</param>
/// <param name="Classes">The classes that can take it (<c>ClassJobCategory0</c>).</param>
/// <param name="Level">The class level it needs (<c>ClassJobLevel[0]</c>).</param>
/// <param name="Reputation">Reputation it gives (<c>ReputationReward</c>).</param>
/// <param name="Craft">The crafting part, when it crafts.</param>
/// <param name="Gather">The gathering part per gatherer (Miner, Botanist), when it gathers.</param>
/// <param name="FishItem">The fish a Fisher would catch (0 when none).</param>
/// <param name="KeyItems">Event items it hands out or asks for (2,000,000+).</param>
/// <param name="Mount">Quest mount (Mount row, 0 when none) and its action (Action row).</param>
/// <param name="Emotes">Emote rows its journal names.</param>
/// <param name="SubZones">Other zones its steps go to (sub-zones and portals, not the society's own zone).</param>
/// <param name="Alternatives">Quests that lock this one out (Qitari stela pairs: one or the other).</param>
public sealed record TribeQuest(
    uint RowId, uint TribeId, string Name, TribeQuestKind Kind, int Rank, byte Pool, uint Giver, uint TurnIn,
    TribeKind Classes, int Level, int Reputation, StepKinds Steps,
    QuestCraft? Craft, IReadOnlyList<QuestGather> Gather, uint FishItem,
    IReadOnlyList<uint> KeyItems, (uint Mount, uint Action) Mount, IReadOnlyList<uint> Emotes, IReadOnlyList<uint> SubZones,
    IReadOnlyList<uint> Alternatives)
{
    /// <summary>The quest number the game, QuestManager and the wiki use (row − 65536).</summary>
    public ushort Number => (ushort)(RowId - 0x10000);

    public bool IsDaily => Kind == TribeQuestKind.Daily;

    /// <summary>An "exclusive" daily (pool 3): while the player's rank isn't above the giver's, one offer comes from these.</summary>
    public bool Exclusive => Pool == 3;

    public bool Has(StepKinds kind) => (Steps & kind) != 0;

    /// <summary>The gathering part for a gatherer (16 Miner, 17 Botanist), if it has one.</summary>
    public QuestGather? GatherFor(uint classJob) => Gather.FirstOrDefault(g => g.ClassJob == classJob);

    /// <summary>This quest as the daily pick sees it.</summary>
    public DailyCandidate Candidate => new(RowId, Rank, Exclusive);

    /// <summary>
    /// The field monsters whose kills it counts, by name (plural, in the client's language): the quest listens for
    /// them by name. Empty when it fights nothing, or only enemies that appear for the quest (the sheets don't name those).
    /// </summary>
    public IReadOnlyList<string> FieldEnemies { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Its reward includes the society's currency. False for the dailies that pay something else: those of the Ixal's
    /// givers below Respected and of the ARR battle societies' Neutral and Recognized givers (a Venture).
    /// </summary>
    public bool PaysCurrency { get; init; } = true;
}

/// <summary>A daily quest giver: who, where, and the ranks of its dailies (ARR givers each serve one rank).</summary>
/// <param name="Position">World position from its <c>Level</c> row.</param>
public sealed record TribeGiver(uint NpcId, string Name, uint Territory, string Place, Vector3 Position, int MinRank, int MaxRank)
{
    /// <summary>
    /// Its dailies pay the society's currency (<see cref="TribeQuest.PaysCurrency"/>). All do but the Ixal's givers
    /// below Respected and the ARR battle societies' Neutral and Recognized givers, whose dailies pay a Venture.
    /// </summary>
    public bool PaysCurrency { get; init; } = true;
}

/// <summary>A quest on the way to unlocking a society, with where to get it.</summary>
/// <param name="Npc">The NPC who offers it, and where (zone name and map coordinates).</param>
/// <param name="Requirement">The class it needs, in plain words with the level ("a crafter at level 50"); empty when any class does.</param>
/// <param name="Previous">Quests that must be done before it's offered.</param>
public sealed record TribeUnlockStep(uint QuestId, string Name, uint Npc, string NpcName, uint Territory, string Place, Vector2 MapPosition,
    string Requirement, IReadOnlyList<(uint QuestId, string Name)> Previous);

/// <summary>The story quest that raises a society from <paramref name="Rank"/> to the next rank, and where to get it.</summary>
/// <param name="OneOfFirst">Side quests at this rank of which one must be done first (Qitari's stela quests); empty for most.</param>
public sealed record TribeRankQuest(uint QuestId, string Name, int Rank, uint Npc, string NpcName, uint Territory, string Place, Vector2 MapPosition,
    IReadOnlyList<(uint QuestId, string Name)> OneOfFirst);

/// <summary>What kind of thing a shop entry gives, for the page's highlights and the "owned" check.</summary>
public enum ShopItemKind
{
    Other,
    Mount,
    Minion,
    Orchestrion,
    /// <summary>An emote, a hairstyle or another unlock the item teaches (item action type 2633).</summary>
    Unlock,
    Card,
    Barding,
    FramersKit,
    Accessory,
    Materia,
    Gear,
    Furnishing,
}

/// <summary>An entry of a society's shop.</summary>
/// <param name="Currency">The item it costs (1 for gil).</param>
/// <param name="Rank">
/// The rank that opens it: 0 from the start, the rank a rank quest leads to, <see cref="TribeShopItem.AlliedRank"/>
/// after the expansion's intersocietal quest, or -1 for another quest (see <paramref name="GateName"/>).
/// </param>
/// <param name="Gate">The quest whose completion opens it (0: none).</param>
public sealed record TribeShopItem(uint ItemId, int Count, uint Currency, int Cost, int Rank, uint Gate, string GateName, ShopItemKind Kind)
{
    /// <summary>The rank value for entries the intersocietal quest opens ("Allied").</summary>
    public const int AlliedRank = 9;

    /// <summary>A collectible the game keeps an "obtained" mark for (mounts, minions, orchestrion rolls, cards…).</summary>
    public bool Unlockable => Kind is ShopItemKind.Mount or ShopItemKind.Minion or ShopItemKind.Orchestrion or ShopItemKind.Unlock
        or ShopItemKind.Card or ShopItemKind.Barding or ShopItemKind.FramersKit or ShopItemKind.Accessory;
}

/// <summary>A society's achievement: one per rank reached (type 15), or the expansion's intersocietal quest (type 6).</summary>
/// <param name="Rank">The rank it's for (0 for the intersocietal one).</param>
/// <param name="Title">The title it gives (empty when none).</param>
public sealed record TribeAchievement(uint Id, string Name, int Rank, string Title, bool Intersocietal);

/// <summary>An allied society (a <c>BeastTribe</c> row) with everything the page, the planner and the run need from the game data.</summary>
/// <param name="Id">The <c>BeastTribe</c> row, 1-20 (the game's reputation array uses Id − 1).</param>
/// <param name="Expansion">0 A Realm Reborn … 5 Dawntrail.</param>
/// <param name="MaxRank">The top rank: 4 Trusted (ARR battle), 7 Sworn (Ixal), 8 Bloodsworn (later ones).</param>
/// <param name="StartRank">The rank the unlock quest leaves you at (1 Neutral, 3 Friendly).</param>
/// <param name="Territory">The zone of its main giver (<c>BeastTribe.Level</c>).</param>
/// <param name="Aetheryte">The aetheryte the run teleports to for its giver.</param>
/// <param name="Unlock">The quests that unlock it, in order.</param>
/// <param name="RankQuests">One per rank below the top, lowest first.</param>
/// <param name="IntersocietalQuest">The expansion's intersocietal quest row (0 when none): done, the rank reads "Allied".</param>
/// <param name="Level">The class level its dailies need.</param>
public sealed record Tribe(
    uint Id, string Name, int Expansion, TribeKind Kind, int MaxRank, int StartRank, uint CurrencyItem,
    uint Territory, string Place, uint Aetheryte, int Level,
    IReadOnlyList<TribeGiver> Givers, IReadOnlyList<TribeUnlockStep> Unlock, IReadOnlyList<TribeRankQuest> RankQuests,
    uint IntersocietalQuest, string IntersocietalName,
    IReadOnlyList<TribeShopItem> Shop, IReadOnlyList<TribeAchievement> Achievements, IReadOnlyList<TribeQuest> Dailies)
{
    /// <summary>The rank quest offered at a rank (null at the top, or when the data has none).</summary>
    public TribeRankQuest? RankQuest(int rank) => RankQuests.FirstOrDefault(q => q.Rank == rank);

    /// <summary>The dailies one giver offers, sorted by quest id (the order the daily pick works on).</summary>
    public IReadOnlyList<TribeQuest> DailiesOf(uint giver) => Dailies.Where(q => q.Giver == giver).OrderBy(q => q.RowId).ToList();

    /// <summary>A crafter society, a gatherer society or the Namazu (either), as against a battle society: the page lists the two apart.</summary>
    public bool CraftOrGather => Kind != TribeKind.Battle;
}
