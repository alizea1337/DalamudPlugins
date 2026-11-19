using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Fishing;
using Provenance.Core.Run;
using Provenance.Data;
using Provenance.Integrations;
using Provenance.Integrations.Fishing;

namespace Provenance.Run.Fishing;

// Choosing what to fish: each fish's window at each usable spot (the weather is the spot zone's), the lead
// before a window, the best spot per fish and the order between fish (Core/Fishing/SpotChoice), fish that
// can't be had (and why), spots resting after the fish sensed something amiss, and the waits the run asks for.
// A fish's window is the one it can be caught in: its own narrowed by its mooch chain's, or its predators'.
internal sealed partial class Fisher
{
    /// <summary>
    /// A fish's next window at one usable spot. Opens: null when not within the forecast. Closes: MaxValue without a
    /// window. Predator: behind swimming shadows, the first predator whose window (at its own spot) the shadows wait
    /// for, so the window here is when both bite; for a rod fish caught under Fisher's Intuition with no window of its
    /// own, the predator whose window counts (or that doesn't bite within the forecast); 0 otherwise.
    /// </summary>
    private sealed record Place(FishingSpotInfo Spot, DateTimeOffset? Opens, DateTimeOffset Closes, uint Predator = 0)
    {
        /// <summary>The fish only bites here in a window (its own, its mooch chain's or a predator's), or not within the forecast.</summary>
        public bool Timed => Opens == null || Closes != DateTimeOffset.MaxValue;
    }

    /// <summary>Where a fish can be fished (or why not), worked out every so often: the reach and level checks read the teleport list.</summary>
    private sealed class FishPlaces
    {
        public DateTime CheckedAt;
        public string? Blocker;
        public readonly List<Place> Spots = new();
        /// <summary>The fish only bites in a window: its own, its mooch chain's, or a predator's (Fisher's Intuition, swimming shadows).</summary>
        public bool Windowed;
    }

    /// <summary>Whether a fish is bound to a window doesn't depend on the zone's weather table.</summary>
    private static readonly IReadOnlyList<(uint Weather, int Rate)> AnyWeather = Array.Empty<(uint, int)>();

    /// <summary>The fish to work on, at its best spot, as ranked.</summary>
    private sealed record Pick(GatherTarget Target, FishInfo Fish, FishingSpotInfo Spot, SpotCandidate Candidate);

    private static readonly TimeSpan PlacesKeep = TimeSpan.FromSeconds(20);
    /// <summary>A spot where the fish sensed something amiss (or nothing worked) is left alone this long.</summary>
    private static readonly TimeSpan RestFor = TimeSpan.FromMinutes(10);
    /// <summary>Rests because nothing worked (not for amiss) after which a spot isn't tried again in the run.</summary>
    private const int BrokenAfter = 3;

    private readonly Dictionary<uint, FishPlaces> places = new();
    /// <summary>Fish that can't be had right now, with why.</summary>
    private readonly Dictionary<uint, string> unreachable = new();
    /// <summary>The reason last written to the run log per fish, so each is said once.</summary>
    private readonly Dictionary<uint, string> reported = new();
    /// <summary>Spots left alone for a while: until when, and why.</summary>
    private readonly Dictionary<uint, (DateTime Until, string Why)> resting = new();
    /// <summary>Per spot, how often it was left alone because nothing worked there.</summary>
    private readonly Dictionary<uint, int> brokenCounts = new();
    private Dictionary<uint, int> teleportCosts = new();
    private DateTime costsAt;
    private (DateTime At, List<Pick> Picks)? ranked;

    private void ResetChoice()
    {
        places.Clear();
        unreachable.Clear();
        reported.Clear();
        resting.Clear();
        brokenCounts.Clear();
        costsAt = default;
        ranked = null;
        baitShop.Clear();
    }

    /// <summary>
    /// How early before a fish's window the fisher wants to be at work: <see cref="FishingSettings.WindowLeadMinutes"/>
    /// plus catching the fish Fisher's Intuition needs, at least as early as the angler starts on them before the
    /// window (<see cref="SpotChoice.Lead"/>). Zero for an item that isn't a known fish.
    /// </summary>
    public TimeSpan LeadFor(uint itemId)
        => plugin.Plans.Catalog?.Gather.Fish(itemId) is { } f ? Lead(f) : TimeSpan.Zero;

    private TimeSpan Lead(FishInfo f)
        => SpotChoice.Lead(f, Settings.WindowLeadMinutes, Data.Fish, 60.0 / Math.Max(0.2, plugin.Config.Timings.FishPerMinute));

    private FishPlaces Places(uint itemId)
    {
        var now = DateTimeOffset.UtcNow;
        if (places.TryGetValue(itemId, out var known) && DateTime.UtcNow - known.CheckedAt < PlacesKeep && known.Spots.All(p => p.Closes > now))
            return known;
        var result = new FishPlaces { CheckedAt = DateTime.UtcNow };
        if (Data.Fish(itemId) is not { } f) result.Blocker = "Provenance has no fishing data for it";
        else
        {
            var level = PlanService.JobLevel(FishAccess.Fisher);
            result.Blocker = FishAccess.Blocker(Data, f, level);
            if (result.Blocker == null)
            {
                foreach (var s in FishAccess.UsableSpots(Data, f, level)) result.Spots.Add(PlaceOf(f, s, now));
                if (result.Spots.Count == 0) result.Blocker = "no fishing spot you can use";
            }
            // With no spot to look at, its own window and its mooch chain's decide.
            result.Windowed = result.Spots.Count > 0 ? result.Spots.Any(p => p.Timed) : CatchWindow.For(f, Data.Fish, AnyWeather).Restricted;
        }
        places[itemId] = result;
        return result;
    }

    /// <summary>
    /// A fish's next window at one usable spot, in the spot zone's weather: its own narrowed by its mooch chain's, and
    /// for a rod fish caught under Fisher's Intuition its predators' (<see cref="SpotChoice"/>). Swimming shadows whose
    /// predator has a window of its own: both have to bite (GBR's IntersectPredatorUptime).
    /// </summary>
    private Place PlaceOf(FishInfo f, FishingSpotInfo s, DateTimeOffset now)
    {
        var table = Data.WeatherTable(s.TerritoryId);
        if (ShadowPredator(f, s) is { } p)
        {
            var (wait, bites) = SpotChoice.Window(CatchWindow.For(f, Data.Fish, table),
                CatchWindow.For(p.Fish, Data.Fish, Data.WeatherTable(p.Parent.TerritoryId)), now, SpotChoice.Horizon);
            return ToPlace(s, now, wait, bites, p.Fish.ItemId);
        }
        var (opensIn, lasts, predator) = SpotChoice.Window(f, Data.Fish, table, now, SpotChoice.Horizon);
        return ToPlace(s, now, opensIn, lasts, predator);
    }

    private static Place ToPlace(FishingSpotInfo s, DateTimeOffset now, TimeSpan? wait, TimeSpan bites, uint predator = 0)
    {
        DateTimeOffset? opens = wait is { } w ? now + w : null;
        var closes = opens is not { } o || bites == TimeSpan.MaxValue ? DateTimeOffset.MaxValue : o + bites;
        return new Place(s, opens, closes, predator);
    }

    /// <summary>
    /// The place as it counts now. Behind swimming shadows the predator's window counts until the fisher works the
    /// shadows' own spot for this fish (their node is up, or enough of the predator was gigged); from then on only
    /// the fish's own window does.
    /// </summary>
    private Place Effective(uint itemId, FishInfo f, Place p, DateTimeOffset now)
    {
        if (p.Predator == 0 || !f.Spear || current?.ItemId != itemId || pool?.Id != p.Spot.Id) return p;
        var (wait, bites) = SpotChoice.Window(CatchWindow.For(f, Data.Fish, Data.WeatherTable(p.Spot.TerritoryId)), now, SpotChoice.Horizon);
        return ToPlace(p.Spot, now, wait, bites);
    }

    /// <summary>A fish's next window at a spot as it counts now (see <see cref="Effective"/>); null when the spot isn't usable.</summary>
    private Place? PlaceAt(FishInfo f, uint spotId)
        => Places(f.ItemId).Spots.FirstOrDefault(p => p.Spot.Id == spotId) is { } place ? Effective(f.ItemId, f, place, DateTimeOffset.UtcNow) : null;

    /// <summary>Nothing worked at the spot <see cref="BrokenAfter"/> times this run: it isn't used again.</summary>
    private bool IsBroken(uint spotId) => brokenCounts.GetValueOrDefault(spotId) >= BrokenAfter;

    private bool IsResting(uint spotId) => IsBroken(spotId) || resting.TryGetValue(spotId, out var r) && r.Until > DateTime.UtcNow;

    /// <summary>
    /// Leaves a spot alone for a while: the fish sensed something amiss there, or (<paramref name="broken"/>)
    /// nothing worked (no stand, no way there, no node). True when that makes the spot unusable for this run.
    /// </summary>
    private bool Rest(FishingSpotInfo s, string why, bool broken = false)
    {
        resting[s.Id] = (DateTime.UtcNow + RestFor, why);
        if (broken) brokenCounts[s.Id] = brokenCounts.GetValueOrDefault(s.Id) + 1;
        ranked = null;
        return IsBroken(s.Id);
    }

    /// <summary>
    /// The fish the angler is on right now, while the Fisher's Intuition its predators brought is still up: it bites
    /// now whatever the predators' windows say. A fish whose window is only its predators' would otherwise stop being
    /// due the moment their window closes, and the run would hand over to gathering with the intuition unused.
    /// </summary>
    private bool FishingUnderIntuition(uint itemId)
        => step == Step.Fish && angler.Active && current is { } t && t.ItemId == itemId && left(t) > 0
           && fish is { NeedsIntuition: true } && angler.IntuitionLeft is { } up && up > TimeSpan.Zero;

    /// <summary>"trying it again in 10 minutes", or "giving up on it for this run" once it's broken.</summary>
    private string AfterRest(bool broken) => broken ? "giving up on it for this run" : $"trying it again in {RestFor.TotalMinutes:0} minutes";

    /// <summary>The run's wait for a fish (see <see cref="WaitFor"/>), with resting spots counted from the end of their rest.</summary>
    private TimeSpan? WindowWait(uint itemId)
    {
        if (plugin.Plans.Catalog == null || Data.Fish(itemId) is not { } f) return null;
        var p = Places(itemId);
        if (!p.Windowed) return null;
        if (FishingUnderIntuition(itemId)) return TimeSpan.Zero;
        if (p.Blocker != null) return TimeSpan.MaxValue;
        var now = DateTimeOffset.UtcNow;
        var best = TimeSpan.MaxValue;
        foreach (var raw in p.Spots)
        {
            var place = Effective(itemId, f, raw, now);
            if (place.Opens is not { } opens || now >= place.Closes || IsBroken(place.Spot.Id)) continue;
            var wait = opens <= now ? TimeSpan.Zero : opens - now;
            if (resting.TryGetValue(place.Spot.Id, out var r) && r.Until > DateTime.UtcNow)
            {
                var rest = r.Until - DateTime.UtcNow;
                if (now + rest >= place.Closes) continue; // this window is over before the rest
                if (rest > wait) wait = rest;
            }
            if (wait < best) best = wait;
        }
        return best;
    }

    private int CostTo(uint territoryId)
    {
        if (DateTime.UtcNow - costsAt > TimeSpan.FromSeconds(30))
        {
            teleportCosts = Teleporter.Unlocked().GroupBy(d => d.TerritoryId).ToDictionary(g => g.Key, g => (int)g.Min(d => d.GilCost));
            costsAt = DateTime.UtcNow;
        }
        return teleportCosts.TryGetValue(territoryId, out var cost) ? cost : 5000;
    }

    private SpotCandidate Candidate(uint itemId, FishInfo f, Place p, DateTimeOffset now, uint here)
    {
        p = Effective(itemId, f, p, now);
        TimeSpan? wait = null;
        var bites = TimeSpan.Zero;
        if (p.Opens is { } opens && now < p.Closes)
        {
            wait = opens <= now ? TimeSpan.Zero : opens - now;
            bites = p.Closes == DateTimeOffset.MaxValue ? TimeSpan.MaxValue : p.Closes - (opens <= now ? now : opens);
        }
        var isHere = p.Spot.TerritoryId == here;
        return new SpotCandidate(itemId, p.Spot.Id, p.Timed, wait, bites, isHere, isHere ? 0 : CostTo(p.Spot.TerritoryId), IsResting(p.Spot.Id));
    }

    /// <summary>Why a target can't be fished at all (level, reach, actions, bait, no window within the forecast), or null.</summary>
    private string? Problem(GatherTarget t)
    {
        var p = Places(t.ItemId);
        if (p.Blocker != null) return p.Blocker;
        if (p.Spots.All(x => IsBroken(x.Spot.Id)))
        {
            var names = string.Join(" or ", p.Spots.Select(x => SpotName(x.Spot)).Distinct());
            var last = p.Spots.Select(x => resting.TryGetValue(x.Spot.Id, out var r) ? r.Why : "").FirstOrDefault(w => w.Length > 0) ?? "nothing worked";
            return $"tried {names} {BrokenAfter} times: {last}";
        }
        var f = Data.Fish(t.ItemId)!;
        // The run waits for any window within the forecast (window after window as it moves on). A fish with none
        // at any spot it can use can't be had by a run; the plan blocks it as well. It counts again once one comes.
        var now = DateTimeOffset.UtcNow;
        if (p.Windowed && p.Spots.All(x => Effective(t.ItemId, f, x, now).Opens == null)) return NoWindow(f, p);
        return f.Spear ? null : PlanBait(f, Math.Max(1, left(t))).Problem;
    }

    /// <summary>"it only bites in Fog after Rain, which doesn't come in the next 7 days of the weather forecast", and the like.</summary>
    private string NoWindow(FishInfo f, FishPlaces p)
    {
        var days = $"the next {SpotChoice.Horizon.TotalDays:0} days of the weather forecast";
        if (p.Spots.FirstOrDefault(x => x.Predator != 0) is { } bound)
            return f.Spear
                ? $"its swimming shadows need {Name(bound.Predator)} gigged first, and {(f.Restricted ? "the two don't bite at the same time" : "that doesn't bite")} in {days}"
                : $"it needs {Name(bound.Predator)} caught first for Fisher's Intuition, and that doesn't bite in {days}";
        // Mooched from a fish that only bites at some hours or in some weather: both have to bite.
        if (CatchWindow.For(f, Data.Fish, AnyWeather).Links.FirstOrDefault() is { } link)
        {
            if (!f.Restricted) return $"it's mooched from {Name(link.ItemId)}, which doesn't bite in {days}";
            var now = DateTimeOffset.UtcNow;
            if (p.Spots.Any(x => FishWindow.NextOpen(f, Data.WeatherTable(x.Spot.TerritoryId), now, SpotChoice.Horizon) != null))
                return $"it and the {Name(link.ItemId)} it's mooched from don't bite at the same time in {days}";
        }
        var rule = WeatherRule(f);
        if (rule.Length == 0) return $"it doesn't bite at any spot you can use in {days}";
        return $"it only bites {rule}{(f.Window != null ? " at its Eorzea hours" : "")}, which doesn't come in {days}";
    }

    private void NoteUnreachable(uint itemId, string why)
    {
        unreachable[itemId] = why;
        if (reported.TryGetValue(itemId, out var said) && said == why) return;
        reported[itemId] = why;
        log($"Can't fish {Name(itemId)}: {why}.", true);
    }

    /// <summary>
    /// The fish that can be worked on now, best first, each at its best spot (remembered for a second; the run
    /// asks often). Fish that can't be had at all go to <see cref="Unreachable"/> on the way.
    /// </summary>
    private List<Pick> Ranked(bool fresh = false)
    {
        if (!fresh && ranked is { } r && DateTime.UtcNow - r.At < TimeSpan.FromSeconds(1)) return r.Picks;
        var picks = new List<Pick>();
        if (plugin.Plans.Catalog == null) return picks;
        var now = DateTimeOffset.UtcNow;
        var here = Services.ClientState.TerritoryType;
        var best = new List<Pick>();
        foreach (var t in targets)
        {
            if (left(t) <= 0) continue;
            if (Problem(t) is { } why)
            {
                NoteUnreachable(t.ItemId, why);
                continue;
            }
            if (unreachable.Remove(t.ItemId)) reported.Remove(t.ItemId);
            var f = Data.Fish(t.ItemId)!;
            var candidates = Places(t.ItemId).Spots.Select(p => (p.Spot, Candidate: Candidate(t.ItemId, f, p, now, here))).ToList();
            if (SpotChoice.Best(candidates.Select(c => c.Candidate), Lead(f)) is not { } b) continue;
            best.Add(new Pick(t, f, candidates.First(c => c.Candidate.SpotId == b.SpotId).Spot, b));
        }
        foreach (var c in SpotChoice.Rank(best.Select(p => p.Candidate), spot?.Id ?? 0))
            picks.Add(best.First(p => p.Target.ItemId == c.ItemId && p.Candidate.SpotId == c.SpotId));
        ranked = (DateTime.UtcNow, picks);
        return picks;
    }

    // ------------------------------------------------------------------ choosing

    private void Choose()
    {
        Waiting = false;
        if (SomethingOut) { Settle(Step.Choose); return; }
        if (pausing) { PauseOnceIn(); return; }
        if (upkeep.Begin(neededCollectables())) { Go(Step.Upkeep); return; }

        var needed = targets.Where(t => left(t) > 0).ToList();
        if (needed.Count == 0)
        {
            current = null;
            Status = "Every fish is caught";
            Wait(3);
            return;
        }
        var picks = Ranked(fresh: true);
        if (needed.All(t => unreachable.ContainsKey(t.ItemId)))
        {
            var why = string.Join("; ", needed.Select(t => $"{Name(t.ItemId)} ({unreachable[t.ItemId]})"));
            Fail($"Can't fish {why}. Get {(needed.Count == 1 ? "it" : "them")} yourself.");
            return;
        }
        if (picks.Count == 0)
        {
            // Every fish left waits for its window (or for a resting spot).
            Waiting = true;
            Status = WaitingStatus(needed);
            Wait(3);
            return;
        }
        SetTarget(picks[0]);
        Go(Step.Gear);
    }

    /// <summary>"Waiting for Silverfin: bites in 8 min (in Fog)", for the fish that comes soonest.</summary>
    private string WaitingStatus(List<GatherTarget> needed)
    {
        var now = DateTimeOffset.UtcNow;
        var here = Services.ClientState.TerritoryType;
        (string Text, TimeSpan Wait)? best = null;
        foreach (var t in needed)
        {
            if (unreachable.ContainsKey(t.ItemId) || Data.Fish(t.ItemId) is not { } f) continue;
            foreach (var p in Places(t.ItemId).Spots)
            {
                if (IsBroken(p.Spot.Id)) continue;
                var c = Candidate(t.ItemId, f, p, now, here);
                (string, TimeSpan)? option = null;
                if (c.Resting && resting.TryGetValue(p.Spot.Id, out var r))
                    option = ($"{Name(t.ItemId)} at {SpotName(p.Spot)}: {r.Why}; again in {Span(r.Until - DateTime.UtcNow)}", r.Until - DateTime.UtcNow);
                else if (c.Wait is { } w)
                    option = ($"{Name(t.ItemId)}: {WindowText(f, p.Spot, true)}", w);
                if (option is { } o && (best == null || o.Item2 < best.Value.Wait)) best = o;
            }
        }
        return best is { } b ? "Waiting for " + b.Text : "No fish left bites within the forecast";
    }

    private void SetTarget(Pick pick)
    {
        var sameSpot = spot?.Id == pick.Spot.Id;
        if (!sameSpot)
        {
            castsAtSpot = 0;
            nodesAtSpot = 0;
            amissLeaves = 0;
            standKey = null;
            ResetSpearSpot();
        }
        if (!sameSpot || current?.ItemId != pick.Target.ItemId)
        {
            // The bait step picks this fish's bait.
            bait = 0;
            var window = WindowText(pick.Fish, pick.Spot, true);
            log($"Fishing for {Name(pick.Target.ItemId)} at {SpotLabel(pick.Spot)}{(window.Length > 0 ? $" ({window})" : "")}.", false);
        }
        current = pick.Target;
        fish = pick.Fish;
        spot = pick.Spot;
    }

    /// <summary>
    /// While fishing for a fish without a window (its own, its mooch chain's or a predator's): another fish's window
    /// needs the fisher now (GBR re-sorts its list the same way). The message for the run log, or null.
    /// </summary>
    private string? MoreUrgent()
    {
        if (fish == null || current is not { } cur) return null;
        var first = Ranked().FirstOrDefault();
        // Judged by the spot it's picked at, as the ranking put it first: a predator's window counts too.
        if (first == null || first.Target.ItemId == cur.ItemId || !first.Candidate.Timed || Places(cur.ItemId).Windowed) return null;
        // "Silverfin bites in 2 min (in Fog)", or "Snailfish: Ichimonji bites in 2 min, for the swimming shadows".
        var named = PlaceAt(first.Fish, first.Spot.Id) is { Predator: not 0 } ? ":" : "";
        return $"{Name(first.Target.ItemId)}{named} {WindowText(first.Fish, first.Spot, true)}; going there after this catch.";
    }

    /// <summary>The fish and spot under way are still worth the trip (enough wanted, the window within the lead; its own, its chain's or a predator's).</summary>
    private bool StillWorkable()
    {
        if (current is not { } t || fish == null || spot == null || left(t) <= 0) return false;
        var p = Places(t.ItemId);
        if (p.Blocker != null || p.Spots.FirstOrDefault(x => x.Spot.Id == spot.Id) is not { } place) return false;
        // Under way, the way there is already being made: judge as if there.
        var c = Candidate(t.ItemId, fish, place, DateTimeOffset.UtcNow, spot.TerritoryId) with { Resting = false };
        return SpotChoice.Workable(c, Lead(fish) + TimeSpan.FromMinutes(1));
    }
}
