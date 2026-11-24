using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Provenance.Core.Tribes;

/// <summary>What the run makes of a mob near the character.</summary>
public enum EnemyVerdict
{
    /// <summary>Left alone.</summary>
    No,
    /// <summary>It attacks the character or the chocobo: always fought.</summary>
    Attacker,
    /// <summary>One of the quest's enemies, and the quest marks it.</summary>
    Marked,
    /// <summary>A field mob the quest counts (its kind and its name), in the marked area, that nobody else is fighting.</summary>
    FieldMob,
    /// <summary>One of the quest's enemies that fights no more and waits for the quest's item: the item is used on it, it isn't attacked.</summary>
    Subdued,
}

/// <summary>A verdict on a mob, with the reason in plain words (for the log and the tests).</summary>
public readonly record struct EnemyJudgement(EnemyVerdict Verdict, string Why)
{
    public bool Allowed => Verdict != EnemyVerdict.No;
}

/// <summary>
/// Which mobs are a quest's to fight, which comes next, when the quest's item goes on one, and where to look when none
/// is in view. No game calls: the run, the watch and the tests go by the same rules.
/// </summary>
/// <remarks>
/// <para>
/// The data, from the battle-society research (game data 7.58, checked against the sheets with Lumina): a quest's
/// enemies are in <c>Quest.QuestListenerParams</c>. A listener below 1,000,000 is a BNpcBase. <c>ConditionType</c> 1
/// names the BNpcName of a field mob whose defeat counts; 3 names the <c>Level</c> row a mob appears at once the quest
/// puts it there (after an object is used, an area walked into, a key item). <c>QuestUInt8A</c> says what's done with
/// it: 5 defeat, 9 defeat for a key item, 8 use a key item on it. <c>ActorSpawnSeq</c> is the sequence it's active in.
/// The <c>ENEMY</c> script arguments name the same bases and Level rows (all 163 dailies with enemies agree), so they
/// only fill in where a quest has no listener for one.
/// </para>
/// <para>
/// The filter, in order of trust (Tribes battle plan): what attacks the character or the chocobo is always fought
/// (and what the run is already fighting stays in the fight while it fights nobody else); then a hostile mob of one
/// of the quest's kinds that the quest marks (its handler's own icon, a quest icon drawn over it, or held by the
/// handler where the quest puts its mobs); then, for a field-mob line, kind and name both matching, inside the marked
/// area, with nobody else fighting it. One that was subdued gets the quest's item when the quest marks it or the run
/// is the one that fought it. A mob of the same kind that fits none of these is never attacked. None of the marks is
/// confirmed in game yet: the hand watch logs each of them per mob, with this filter's verdict, so a bug report shows
/// whether they hold.
/// </para>
/// </remarks>
public static class EnemyRules
{
    /// <summary>Listeners (and script values) below this are BNpcBase ids; from here on they're NPCs, objects, Level rows.</summary>
    public const uint FirstNpc = 1_000_000;
    /// <summary>How far around the character mobs are read from the object table.</summary>
    public const float ViewRange = 100f;
    /// <summary>
    /// Within this the game draws quest icons over nameplates (beyond about 50 yalms it doesn't), so here a field mob
    /// without one, among others that have one, isn't wanted.
    /// </summary>
    public const float IconRange = 45f;
    /// <summary>A mob the quest put somewhere stays about this close to where it appeared.</summary>
    public const float SpawnLeash = 60f;
    /// <summary>A field mob counts as inside a marked area up to this far past its edge (half the radius, when that's more).</summary>
    public const float AreaMargin = 30f;
    /// <summary>With no live marker to give an area's size: this far from the to-do line's area spot.</summary>
    public const float DefaultArea = 150f;
    /// <summary>Times the quest's item is used on one mob before it's given up on.</summary>
    public const int ItemTries = 3;
    /// <summary>
    /// Defeats in a row of a field mob's kind that didn't move the quest on before the kind is left alone (its line is
    /// done, the quest's other kinds aren't): two, since the game can take a moment to count one.
    /// </summary>
    public const int IdleDefeats = 2;
    /// <summary>An item whose action reaches this far is thrown at the enemy before the fight, not used on it once it's subdued.</summary>
    public const float ThrownFrom = 10f;
    /// <summary>How close to get to a place the quest's enemies appear at (the area that calls them is about 5 yalms wide).</summary>
    public const float SpawnWithin = 4f;
    /// <summary>Looking for field mobs: points on a ring around the marked area, this many, this far out (of the radius), gone round this often.</summary>
    public const int RingPoints = 6;
    public const float RingAt = 0.6f;
    public const int RingRounds = 2;
    public const float RingWithin = 12f;
    /// <summary>The ring's radius when no live marker gives the area's.</summary>
    public const float DefaultRing = 60f;

    /// <summary>Nameplate icons of FATE NPCs (not a quest's mark).</summary>
    private static readonly uint[] FateIcons = { 60093, 60732 };

    // ------------------------------------------------------------------ the data

    /// <summary>
    /// A quest's enemies from its listeners, with the <c>ENEMY</c> script arguments filling in what no listener names.
    /// </summary>
    /// <param name="level">A <c>Level</c> row as a spot (its object is the BNpcBase it places); null when there's no such row.</param>
    public static IReadOnlyList<QuestEnemy> Read(IEnumerable<(string Name, uint Value)> args, IEnumerable<ListenerRow> listeners, Func<uint, ScriptSpot?> level)
    {
        var rows = listeners.Where(l => l.Listener is > 0 and < FirstNpc).ToList();
        var list = new List<QuestEnemy>();
        void Add(QuestEnemy e)
        {
            if (!list.Any(x => x.Base == e.Base && x.Name == e.Name && x.Sequence == e.Sequence && x.Field == e.Field && x.Task == e.Task && x.Spawn?.Level == e.Spawn?.Level))
                list.Add(e);
        }

        // Kinds the quest puts somewhere in a sequence: a row without a condition for one of those is the same mob.
        var placed = rows.Where(l => l.ConditionType == 3).Select(l => (l.Listener, l.Sequence)).ToHashSet();
        foreach (var l in rows)
        {
            EnemyTask task;
            switch (l.Kind)
            {
                case 5: task = EnemyTask.Defeat; break;
                case 9: task = EnemyTask.DefeatForItem; break;
                case 8: task = EnemyTask.UseItem; break;
                default: continue;
            }
            switch (l.ConditionType)
            {
                case 1:
                    Add(new QuestEnemy(l.Listener, l.ConditionValue, l.Sequence, true, task));
                    break;
                case 3:
                    Add(new QuestEnemy(l.Listener, 0, l.Sequence, false, task, level(l.ConditionValue)));
                    break;
                default:
                    // No condition. The item goes on that kind, wherever the quest put it; a defeat with no place for
                    // the kind counts any mob of it (a field mob by its kind alone).
                    if (task == EnemyTask.UseItem) Add(new QuestEnemy(l.Listener, 0, l.Sequence, false, task));
                    else if (!placed.Contains((l.Listener, l.Sequence))) Add(new QuestEnemy(l.Listener, 0, l.Sequence, true, task));
                    break;
            }
        }

        foreach (var (name, value) in args)
        {
            if (!IsEnemyArg(name) || value == 0) continue;
            if (value < FirstNpc)
            {
                // A kind no listener names: known as the quest's, but only fought when the quest marks it (or it attacks).
                if (!list.Any(e => e.Base == value)) Add(new QuestEnemy(value, 0, 0, false, EnemyTask.Defeat));
            }
            else if (level(value) is { Object: > 0 and < FirstNpc } spot && !list.Any(e => e.Base == spot.Object && e.Spawn?.Level == spot.Level))
                Add(new QuestEnemy(spot.Object, 0, 0, false, EnemyTask.Defeat, spot));
        }
        return list;
    }

    /// <summary>The NPCs and objects a key item is used on, by sequence (listeners of kind 8 that aren't enemies or areas).</summary>
    public static IReadOnlyList<(byte Sequence, uint Object)> ItemObjects(IEnumerable<ListenerRow> listeners)
        => listeners.Where(l => l.Kind == 8 && l.Listener is >= FirstNpc and < 4_000_000).Select(l => (l.Sequence, l.Listener)).Distinct().ToList();

    /// <summary>A script argument of the ENEMY family: "ENEMY0", "ENEMY12" (not "ENEMY_ID").</summary>
    private static bool IsEnemyArg(string name)
        => name.StartsWith("ENEMY", StringComparison.Ordinal) && name.Length > 5 && name.AsSpan(5).IndexOfAnyExceptInRange('0', '9') < 0;

    /// <summary>The enemy is active at a sequence: its own, or (when the data names none) any but the report.</summary>
    public static bool ActiveIn(QuestEnemy e, byte sequence) => e.Sequence == sequence || (e.Sequence == 0 && sequence != 255);

    /// <summary>The quest's enemies active at a sequence.</summary>
    public static List<QuestEnemy> Active(QuestScript q, byte sequence) => q.Enemies.Where(e => ActiveIn(e, sequence)).ToList();

    /// <summary>The quest has enemies at this sequence: a fight is part of it (or may come, on the way through).</summary>
    public static bool Fights(QuestScript q, byte sequence) => q.Enemies.Any(e => ActiveIn(e, sequence));

    // ------------------------------------------------------------------ the filter

    /// <summary>What to make of a mob: fought (and why), or left alone (and why).</summary>
    /// <param name="m">What the run remembers (which enemies it fought); null for a look that fights nothing (the watch).</param>
    public static EnemyJudgement Judge(QuestScript q, StepSnapshot s, StepEnemy e, StepMemory? m = null)
    {
        if (!e.Targetable) return No("can't be targeted");
        // 1. It attacks the character or the chocobo: always. One the run is fighting stays in the fight while it's in
        // combat with nobody else (between two attacks a mob can be without a target).
        if (e.Hostile && e.AttacksMe) return new EnemyJudgement(EnemyVerdict.Attacker, "it attacks you or your chocobo");
        if (e.Hostile && e.InCombat && !e.FightsOther && !e.Weakened && m?.Fought.Contains(e.Id) == true)
            return new EnemyJudgement(EnemyVerdict.Attacker, "the run is fighting it");

        var lines = q.Enemies.Where(x => x.Base == e.BaseId && ActiveIn(x, s.Sequence)).ToList();
        if (lines.Count == 0) return No(q.Enemies.Any(x => x.Base == e.BaseId) ? "the quest's kind, but not at this step" : "not one of the quest's kinds");
        if (e.Fate != 0) return No("part of a FATE");

        var item = lines.Any(l => l.Task == EnemyTask.UseItem);
        var defeat = lines.Any(l => l.Task != EnemyTask.UseItem);
        var held = EnemyItem(q, s) != null;
        // Nothing but its item is wanted of it: without the item there's nothing to do with it.
        if (!defeat && !held) return No("the quest's item for it isn't in the bags");

        // A field line names the mob (0: any name); a mob the quest puts somewhere is known by where it appeared.
        var names = lines.Where(l => l.Field).Select(l => l.Name).ToHashSet();
        bool? named = names.Count == 0 ? null : names.Contains(0) || names.Contains(e.NameId);
        var atSpawn = lines.Any(l => l.Spawn is { } p && p.Territory == s.Territory && Flat(p.Position, e.Position) <= SpawnLeash);

        // 2. The quest marks it: its handler's own icon; a quest icon drawn over it (not over a field mob of another
        // name: that one is another quest's); or held by the handler where the quest puts its mobs.
        var mark = e.Icon != 0 ? "the quest's own icon is over it"
            : QuestIcon(e.Drawn) && named != false ? "a quest icon is drawn over it"
            : e.Held && atSpawn ? "the quest holds it, where the quest's enemies appear"
            : null;

        // Subdued (it attacks no more, takes no damage and waits for the quest's item): the item goes on it when the
        // quest marks it or the run is the one that subdued it. Someone else's, at the same spawn point, is left alone.
        if (e.Weakened && item)
        {
            if (!held) return No("subdued, but the quest's item isn't in the bags");
            return mark != null || m?.Fought.Contains(e.Id) == true
                ? new EnemyJudgement(EnemyVerdict.Subdued, "subdued, waiting for the quest's item")
                : No("subdued, but not by the run, and the quest doesn't mark it");
        }
        if (!e.Hostile) return No("not hostile");
        if (e.FightsOther) return No("someone else is fighting it");
        if (mark != null) return new EnemyJudgement(EnemyVerdict.Marked, mark);

        // 3. A field-mob line: kind and name match, inside the marked area. Close by, where the game would draw the
        // quest's icon, one without it among others that have it isn't wanted (its line is done).
        if (named == true)
        {
            if (!InArea(q, s, e)) return No("outside the marked area");
            if (Flat(e.Position, s.Player) <= IconRange && OthersMarked(q, s, e)) return No("the quest marks others of its kinds nearby, not this one");
            // Its last defeats didn't count (where defeats were seen to count) while the quest still wants another
            // kind: this kind's line is done.
            if (m is { DefeatsCount: true } && m.IdleDefeats.GetValueOrDefault(e.BaseId) >= IdleDefeats && lines.All(l => l.Task == EnemyTask.Defeat)
                && q.Enemies.Any(x => x.Base != e.BaseId && ActiveIn(x, s.Sequence)))
                return No($"its last {m.IdleDefeats[e.BaseId]} defeats didn't count: the quest wants its other kinds now");
            return new EnemyJudgement(EnemyVerdict.FieldMob, "a field mob the quest counts, in the marked area");
        }
        return No(named == false ? "the quest's kind, but not the name it counts" : "the quest's kind, but the quest doesn't mark it");
    }

    private static EnemyJudgement No(string why) => new(EnemyVerdict.No, why);

    /// <summary>A nameplate icon that marks a quest's target (anything drawn but the FATE NPC icons).</summary>
    public static bool QuestIcon(uint drawn) => drawn != 0 && Array.IndexOf(FateIcons, drawn) < 0;

    /// <summary>
    /// Inside the area the quest marks: a live area marker in this zone (its real radius, and a margin), else an open
    /// to-do line's area spot (its radius in the sheet is a code, so a generous distance). With no area at all: anywhere.
    /// </summary>
    private static bool InArea(QuestScript q, StepSnapshot s, StepEnemy e)
    {
        var areas = s.Markers.Where(k => k.IsArea && k.Territory == s.Territory).ToList();
        if (areas.Count > 0) return areas.Any(k => Flat(k.Position, e.Position) <= k.Radius + Math.Max(AreaMargin, k.Radius * 0.5f));
        var spots = StepRules.OpenLines(q, s).SelectMany(t => t.Spots).Where(p => p.Area && p.Territory == s.Territory).ToList();
        return spots.Count == 0 || spots.Any(p => Flat(p.Position, e.Position) <= DefaultArea);
    }

    /// <summary>Another mob of the quest's kinds, close enough for its icon to be drawn, carries the quest's icon.</summary>
    private static bool OthersMarked(QuestScript q, StepSnapshot s, StepEnemy e)
    {
        var kinds = q.Enemies.Where(x => ActiveIn(x, s.Sequence)).Select(x => x.Base).ToHashSet();
        return s.Hostiles.Any(o => o.Id != e.Id && kinds.Contains(o.BaseId) && (o.Icon != 0 || QuestIcon(o.Drawn)) && Flat(o.Position, s.Player) <= IconRange);
    }

    /// <summary>
    /// The enemy to fight next: what attacks first, then one waiting for the quest's item, then the quest's marked
    /// ones, then its field mobs; the nearest of each. The one being fought (<paramref name="keep"/>) stays the target
    /// until it's done, unless something else attacks while it doesn't. Those given up on are left out.
    /// </summary>
    public static StepEnemy? Next(QuestScript q, StepSnapshot s, StepMemory m, ulong keep = 0)
    {
        StepEnemy? best = null, kept = null;
        var bestRank = int.MaxValue;
        var keptRank = int.MaxValue;
        var bestAway = float.MaxValue;
        foreach (var e in s.Hostiles)
        {
            if (m.GivenUp.Contains(e.Id)) continue;
            var verdict = Judge(q, s, e, m).Verdict;
            if (verdict == EnemyVerdict.No) continue;
            var rank = verdict switch { EnemyVerdict.Attacker => 0, EnemyVerdict.Subdued => 1, EnemyVerdict.Marked => 2, _ => 3 };
            var away = Vector3.DistanceSquared(e.Position, s.Player);
            if (e.Id == keep)
            {
                kept = e;
                keptRank = rank;
            }
            if (rank < bestRank || (rank == bestRank && away < bestAway))
            {
                best = e;
                bestRank = rank;
                bestAway = away;
            }
        }
        return kept != null && (keptRank == 0 || bestRank != 0) ? kept : best;
    }

    // ------------------------------------------------------------------ the quest's item

    /// <summary>The key item that goes on enemies: usable, in the bags, aimed at a target (not thrown on the ground, not used on yourself).</summary>
    public static KeyItemFact? EnemyItem(QuestScript q, StepSnapshot s)
        => q.KeyItems.FirstOrDefault(k => k.Usable && !k.Ground && !k.Self && s.Count(k.Id) > 0);

    /// <summary>
    /// The quest's item to use on this enemy now, or null: on one that's subdued (it takes no more damage and waits
    /// for it); before the fight, once, when the item is thrown from afar or the quest wants nothing else of the mob;
    /// and when the fight has stalled (<paramref name="stalled"/>: nothing hurts it, which is what a mob waiting for
    /// its item looks like). <see cref="ItemTries"/> times per mob at most.
    /// </summary>
    public static KeyItemFact? ItemNow(QuestScript q, StepSnapshot s, StepEnemy e, StepMemory m, bool stalled = false)
    {
        var lines = q.Enemies.Where(x => x.Base == e.BaseId && ActiveIn(x, s.Sequence)).ToList();
        if (!lines.Any(l => l.Task == EnemyTask.UseItem)) return null;
        if (EnemyItem(q, s) is not { } item) return null;
        var uses = m.ItemUses.GetValueOrDefault(e.Id);
        if (uses >= ItemTries) return null;
        if (e.Weakened || stalled) return item;
        var defeat = lines.Any(l => l.Task != EnemyTask.UseItem);
        return uses == 0 && (item.Range >= ThrownFrom || !defeat) ? item : null;
    }

    // ------------------------------------------------------------------ where to look

    /// <summary>
    /// Where to look for the quest's enemies when none is in view and nothing else is marked: the places the quest
    /// puts them at (this zone first, the nearest first), each once; then, for field mobs, points around the marked
    /// area, twice round. Null when there's nowhere left (the player is asked then).
    /// </summary>
    public static StepPlan? Search(QuestScript q, StepSnapshot s, StepMemory m)
    {
        var active = Active(q, s.Sequence);
        if (active.Count == 0) return null;
        var spawn = active.Where(e => e.Spawn != null && !m.Looked.Contains(e.Spawn.Level)).Select(e => e.Spawn!)
            .OrderBy(p => p.Territory == s.Territory ? 0 : 1)
            .ThenBy(p => p.Territory == s.Territory ? Vector3.DistanceSquared(p.Position, s.Player) : 0)
            .FirstOrDefault();
        if (spawn != null)
            return new StepPlan(StepKind.Go, "where the quest's enemies appear") { Spot = spawn.Position, Territory = spawn.Territory, Within = SpawnWithin, Level = spawn.Level };

        if (!active.Any(e => e.Field) || FieldArea(q, s) is not { } area) return null;
        for (var i = 0; i < RingPoints * RingRounds; i++)
        {
            var key = RingKey(area.Key, i);
            if (m.Looked.Contains(key)) continue;
            var angle = MathF.Tau * (i % RingPoints) / RingPoints;
            var at = area.Centre + new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle)) * (area.Radius * RingAt);
            return new StepPlan(StepKind.Go, "around the marked area, looking for the quest's enemies") { Spot = at, Territory = s.Territory, Within = RingWithin, Level = key };
        }
        return null;
    }

    /// <summary>The area field mobs are looked for in: the nearest live area marker here, else an open line's area spot.</summary>
    private static (Vector3 Centre, float Radius, uint Key)? FieldArea(QuestScript q, StepSnapshot s)
    {
        var marker = s.Markers.Where(k => k.IsArea && k.Territory == s.Territory).OrderBy(k => Vector3.DistanceSquared(k.Position, s.Player)).FirstOrDefault();
        if (marker != null) return (marker.Position, Math.Clamp(marker.Radius, 30f, 150f), StepRules.MarkerKey(marker));
        var spot = StepRules.OpenLines(q, s).SelectMany(t => t.Spots).Where(p => p.Area && p.Territory == s.Territory)
            .OrderBy(p => Vector3.DistanceSquared(p.Position, s.Player)).FirstOrDefault();
        return spot != null ? (spot.Position, DefaultRing, spot.Level) : null;
    }

    /// <summary>A ring point's key in <see cref="StepMemory.Looked"/> (apart from Level rows and marker keys).</summary>
    public static uint RingKey(uint area, int index)
    {
        unchecked
        {
            return 0x4000_0000u | ((area * 31u + (uint)index) & 0x3FFF_FFFFu);
        }
    }

    private static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));
}
