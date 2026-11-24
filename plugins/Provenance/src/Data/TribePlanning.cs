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
            var who = Who();
            return TribePlanner.Plan(new TribePlanInput
            {
                Days = data.Tribes.Select(t => Game.Today(data, t, accepted, slots)).ToList(),
                Accepted = accepted,
                Allowances = Game.AllowancesLeft,
                Goal = Settings.Goal,
                LeftOut = Settings.LeftOut,
                LeaveOutSay = Settings.LeaveOutSay,
                Classes = who.All,
                CurrentJob = who.Current,
                CombatJob = who.CombatJob,
                ClassHistory = who.History,
                Territory = Services.ClientState.TerritoryType,
                Owns = Game.Owns,
                ItemName = catalog.ItemName,
                ItemNamePlural = catalog.ItemNamePlural,
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

    /// <summary>The classes that have been on since the run started, the first first; only the class on now while no run is going.</summary>
    private readonly List<uint> history = new();

    /// <summary>
    /// The player's classes as the choice of one for a quest sees them (<see cref="Game.Who"/>), with the classes that
    /// have been on since the run started. The run changes class from visit to visit and plans again after each, so
    /// without them a crafter's dailies would move to the best-geared crafter once a battle society's visit put the
    /// combat job on (and the crystals fetched before the tour would be another recipe's).
    /// </summary>
    public PlayerClasses Who()
    {
        var who = Game.Who(plugin.Config);
        var running = plugin.TribeLoop is { Active: true };
        if (!running) history.Clear();
        if (who.Current != 0 && !history.Contains(who.Current)) history.Add(who.Current);
        who = who with { History = history.ToArray() };
        if (running) Note(who);
        return who;
    }

    private string noted = "";

    /// <summary>
    /// While the run goes: what the class choice went by, to the debug log whenever it changes (the class on, the
    /// classes it has been on, the Monster drops job, and the combat jobs it could fight on).
    /// </summary>
    private void Note(PlayerClasses who)
    {
        static string Yes(bool b) => b ? "yes" : "no";
        var picked = who.CombatJob == 0 ? "none"
            : who.Of(who.CombatJob) is { } c ? $"{ClassJobs.Abbreviation(c.ClassJob)} (level {c.Level}, gearset {Yes(c.HasGearset)}, combo {Yes(c.Fights)})"
            : $"{who.CombatJob} (not a combat job of this character)";
        var text = $"classes: on {ClassJobs.Abbreviation(who.Current)}, been on [{string.Join(", ", who.History.Select(ClassJobs.Abbreviation))}], Monster drops job {picked}, "
                   + $"combat jobs with a combo [{string.Join(", ", who.All.Where(x => x.Fights && x.Level > 0).Select(x => $"{ClassJobs.Abbreviation(x.ClassJob)} {x.Level}{(x.HasGearset ? "" : " no gearset")}"))}]";
        if (text == noted) return;
        noted = text;
        DebugLog.Write("tribes", text);
    }

    /// <summary>
    /// Crafts still to do for a quest in the journal: one per materials item in the bags (the quest hands one out per
    /// craft). Null when none are in the bags yet (they may be handed out later in the quest).
    /// </summary>
    private static int? CraftsLeft(TribeQuest q)
        => q.Craft is { Materials: not 0 } c && QuestSteps.ItemCount(c.Materials) is > 0 and var n ? n : null;

    private (TribePlan Plan, DateTime At, List<CrystalNeed> Needs)? shown;

    /// <summary>
    /// <see cref="Crystals"/> for the pages, which draw every frame: worked out again at most once a second, or when the
    /// plan changes (the retainer totals and the bag counts aren't free).
    /// </summary>
    public List<CrystalNeed> CrystalsShown(TribePlan plan)
    {
        if (shown is { } c && ReferenceEquals(c.Plan, plan) && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(1)) return c.Needs;
        var needs = Crystals(plan);
        shown = (plan, DateTime.UtcNow, needs);
        return needs;
    }

    /// <summary>The crystals a plan may use, with what's on hand and what the retainers hold, read now.</summary>
    public List<CrystalNeed> Crystals(TribePlan plan)
    {
        var retainers = plugin.Config.Stock.Retainers ? RetainerCache.Totals(plugin.Config) : new Dictionary<uint, int>();
        return plan.Crystals().OrderBy(kv => kv.Key)
            .Select(kv => new CrystalNeed(kv.Key, kv.Value, Game.OnHand(kv.Key), retainers.GetValueOrDefault(kv.Key)))
            .ToList();
    }
}
