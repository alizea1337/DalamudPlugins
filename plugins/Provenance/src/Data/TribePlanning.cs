using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Character;
using Provenance.Core.Tribes;
using Provenance.Integrations;
using Game = Provenance.Integrations.Tribes;

namespace Provenance.Data;

/// <summary>Where the crystals for the day's plan come from: how many it may use, how many are on hand, how many the retainers hold.</summary>
/// <param name="OnHand">In the bags and the crystal bag.</param>
/// <param name="Retainers">With the retainers, as last seen (0 when retainers aren't counted, Settings).</param>
public readonly record struct CrystalNeed(uint ItemId, int Needed, int OnHand, int Retainers)
{
    public int Short => Math.Max(0, Needed - OnHand);
    /// <summary>Still short after what the retainers hold: the run gathers it, or leaves quests out.</summary>
    public int ToGather => Math.Max(0, Needed - OnHand - Retainers);
}

/// <summary>
/// The day's allied society plan from the game's state and the Tribes settings (<see cref="TribePlanner"/>), and where
/// its crystals come from. Rebuilt at most once a second for the pages; the run builds its own with what it gave up on.
/// </summary>
internal sealed class TribePlanning
{
    private readonly Plugin plugin;
    private TribePlan cached = TribePlan.Empty;
    private DateTime builtAt;

    public TribePlanning(Plugin plugin) => this.plugin = plugin;

    private TribeSettings Settings => plugin.Config.Tribes;

    /// <summary>The plan, rebuilt at most once a second.</summary>
    public TribePlan Current
    {
        get
        {
            if (DateTime.UtcNow - builtAt < TimeSpan.FromSeconds(1)) return cached;
            builtAt = DateTime.UtcNow;
            cached = Build();
            return cached;
        }
    }

    /// <summary>The plan now; <paramref name="excluded"/> and <paramref name="excludedGivers"/> are what the run gave up on today.</summary>
    public TribePlan Build(IReadOnlyDictionary<uint, string>? excluded = null, IReadOnlyDictionary<uint, string>? excludedGivers = null)
    {
        if (plugin.Plans.Catalog is not { } catalog || catalog.Tribes is not { Tribes.Count: > 0 } data || !Game.Ready) return TribePlan.Empty;
        try
        {
            var accepted = Game.Accepted(data);
            var slots = Game.DailySlots();
            return TribePlanner.Plan(new TribePlanInput
            {
                Days = data.Tribes.Select(t => Game.Today(data, t, accepted, slots)).ToList(),
                Accepted = accepted,
                Allowances = Game.AllowancesLeft,
                Goal = Settings.Goal,
                LeftOut = Settings.LeftOut,
                LeaveOutSay = Settings.LeaveOutSay,
                Classes = Game.Classes(),
                CurrentJob = GameState.CurrentJob,
                Territory = Services.ClientState.TerritoryType,
                Owns = Game.Owns,
                ItemName = catalog.ItemName,
                ClassName = ClassJobs.Abbreviation,
                CraftsLeft = CraftsLeft,
                Excluded = excluded ?? new Dictionary<uint, string>(),
                ExcludedGivers = excludedGivers ?? new Dictionary<uint, string>(),
            });
        }
        catch (Exception e)
        {
            Services.Log.Warning(e, "Planning the allied society dailies failed");
            return TribePlan.Empty;
        }
    }

    /// <summary>
    /// Crafts still to do for a quest in the journal: one per materials item in the bags (the quest hands one out per
    /// craft). Null when none are in the bags yet (they may be handed out later in the quest).
    /// </summary>
    private static int? CraftsLeft(TribeQuest q)
        => q.Craft is { Materials: not 0 } c && QuestSteps.ItemCount(c.Materials) is > 0 and var n ? n : null;

    /// <summary>The crystals a plan may use, with what's on hand and what the retainers hold.</summary>
    public List<CrystalNeed> Crystals(TribePlan plan)
    {
        var retainers = plugin.Config.Stock.Retainers ? RetainerCache.Totals(plugin.Config) : new Dictionary<uint, int>();
        return plan.Crystals().OrderBy(kv => kv.Key)
            .Select(kv => new CrystalNeed(kv.Key, kv.Value, Game.OnHand(kv.Key), retainers.GetValueOrDefault(kv.Key)))
            .ToList();
    }
}
