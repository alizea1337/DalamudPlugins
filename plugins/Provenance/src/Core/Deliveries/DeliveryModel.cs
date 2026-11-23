using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Provenance.Core.Deliveries;

/// <summary>The three requests a custom-delivery client makes (the supply row's Slot 1, 2 and 3).</summary>
public enum DeliveryKind { Craft = 1, Gather = 2, Fish = 3 }

/// <summary>One item a client may ask for: a subrow of its <c>SatisfactionSupply</c> row.</summary>
/// <param name="Weight">Chance weight in its slot (1 at levels 1-4; 23, or 8 for the bonus item, at level 5).</param>
/// <param name="Low">Collectability for the first reward tier (the least the client takes).</param>
/// <param name="RewardRow">Its <c>SatisfactionSupplyReward</c> row.</param>
/// <param name="Spearfishing">A fish caught by spearfishing (otherwise with a rod).</param>
public sealed record DeliveryCandidate(int SubRow, uint ItemId, DeliveryKind Kind, int Weight, bool IsBonus, int Low, int Mid, int High,
    uint RewardRow, bool Spearfishing = false);

/// <summary>What a delivery pays per collectability tier (index 0, 1, 2).</summary>
/// <param name="FirstCurrency">Special currency id of the first reward (2 purple crafters', 4 purple gatherers').</param>
/// <param name="SecondCurrency">Special currency id of the second reward (6 orange crafters', 7 orange gatherers'), paid at the level cap.</param>
/// <param name="BonusMultiplier">Percent: 100, or 150 for a bonus item.</param>
public sealed record DeliveryReward(uint RowId, byte FirstCurrency, int[] First, byte SecondCurrency, int[] Second, int[] Satisfaction, int[] Gil,
    int BonusMultiplier);

/// <summary>A satisfaction level of a client: where its requests are and what completing it gives.</summary>
/// <param name="Required">Satisfaction to complete the level (0 at the top level).</param>
/// <param name="RankUpItems">Items for completing it.</param>
public sealed record DeliveryLevel(int Level, int SupplyIndex, int Required, IReadOnlyList<(uint ItemId, int Count, bool Hq)> RankUpItems);

/// <summary>A story quest a client offers at a satisfaction level.</summary>
/// <param name="Rewards">Item ids it gives (mounts, coffers, emotes and the like among them).</param>
/// <param name="Gates">The client takes no deliveries at this level until it's done (the game's <c>SatisfactionArbitration</c>).</param>
public sealed record DeliveryQuest(uint QuestId, string Name, int Level, IReadOnlyList<uint> Rewards, bool Gates = false);

/// <summary>A custom-delivery client (a <c>SatisfactionNpc</c> row).</summary>
/// <param name="Id">The <c>SatisfactionNpc</c> row (1-based; the game's arrays use Id - 1).</param>
/// <param name="LevelUnlock">Class level needed to deliver.</param>
/// <param name="Levels">Satisfaction levels 1 to 5, in order.</param>
/// <param name="Achievements">First achievement of "Satisfaction Guaranteed" (I-V) and of "The Customer Is Always Right" (I-VI).</param>
public sealed record DeliveryClient(uint Id, string Name, uint NpcId, uint Territory, string Place, Vector3 Position, uint UnlockQuest, string UnlockQuestName,
    int LevelUnlock, int PerWeek, bool CanGlamour, IReadOnlyList<DeliveryLevel> Levels, IReadOnlyList<DeliveryQuest> Quests,
    (uint Rank, uint Deliveries) Achievements)
{
    public const int TopLevel = 5;

    public DeliveryLevel? Level(int level) => level >= 1 && level <= Levels.Count ? Levels[level - 1] : null;
}

/// <summary>A request of this week, with what it pays.</summary>
/// <param name="Bonus">Pays the bonus (x1.5): the bonus item came up, or the week's guarantee forced it.</param>
public sealed record DeliveryRequest(DeliveryCandidate Item, DeliveryReward? Reward, bool Bonus);

/// <summary>Where a client stands this week.</summary>
/// <param name="Rank">Satisfaction level 1-5 (0: not unlocked, or the game hasn't sent the data yet).</param>
/// <param name="Satisfaction">Progress within the level.</param>
/// <param name="Used">Deliveries made to the client this week.</param>
/// <param name="Requests">Craft, gather, fish (null where the data has nothing).</param>
public sealed record ClientWeek(DeliveryClient Client, int Rank, int Satisfaction, int Used, DeliveryRequest?[] Requests)
{
    public bool Maxed => Rank >= DeliveryClient.TopLevel;
    public int Required => Client.Level(Rank)?.Required ?? 0;
    public int LeftThisWeek => Math.Max(0, Client.PerWeek - Used);

    /// <summary>Satisfaction one delivery adds at this level (the same at every tier).</summary>
    public int PointsPerDelivery => Requests.FirstOrDefault(r => r?.Reward != null)?.Reward!.Satisfaction[0] ?? 0;

    /// <summary>Deliveries until the next level (0 at the top level, or when nothing is known).</summary>
    public int ToNextLevel => Maxed || PointsPerDelivery <= 0 ? 0 : (int)Math.Ceiling(Math.Max(0, Required - Satisfaction) / (double)PointsPerDelivery);

    public DeliveryRequest? Request(DeliveryKind kind) => Requests[(int)kind - 1];
}
