using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Provenance.Core.Character;
using Provenance.Core.Tribes;
using Provenance.Data;
using Provenance.UI.Theme;
using Game = Provenance.Integrations.Tribes;
using Tribe = Provenance.Core.Tribes.Tribe;

namespace Provenance.UI.Pages;

/// <summary>
/// Tribes (Expeditions): the allied societies. The day (allowances left, the reset, quests left over from before it),
/// the run's goal and options, and every society with its rank and today's status; a society opens to what unlocks it,
/// how far its next rank and its top rank are, today's quests (what the run does, what's yours to do and why, what's
/// missing), whether they match the giver's list, what each rank opens in its shop (and what you own), and its
/// achievements.
/// </summary>
internal sealed class TribesPage : Page
{
    private readonly HashSet<uint> open = new();
    private List<DropdownItem>? goalItems;
    private Snapshot? snapshot;
    private DateTime snapshotAt;

    public TribesPage(Plugin plugin) : base(plugin) { }

    private TribeSettings S => Config.Tribes;
    private TribeData? Data => Plans.Catalog?.Tribes;

    public override string Title => "Tribes";
    public override FontAwesomeIcon Icon => FontAwesomeIcon.Handshake;

    public override (string Text, Vector4 Color)? Status
        => Data is { Tribes.Count: > 0 } && Game.Ready ? ($"{Game.AllowancesLeft} of {Game.DailyAllowance} allowances left today", Palette.TextMuted) : null;

    /// <summary>What the page shows, read from the game at most twice a second.</summary>
    private sealed record Snapshot(List<TribeToday> Days, List<AcceptedTribeQuest> Accepted, int Allowances, List<ClassInfo> Classes, uint Current);

    private Snapshot Read(TribeData data)
    {
        if (snapshot != null && DateTime.UtcNow - snapshotAt < TimeSpan.FromMilliseconds(500)) return snapshot;
        var accepted = Game.Accepted(data);
        var slots = Game.DailySlots();
        var days = data.Tribes.Select(t => Game.Today(data, t, accepted, slots)).ToList();
        snapshot = new Snapshot(days, accepted, Game.AllowancesLeft, Game.Classes(), Integrations.GameState.CurrentJob);
        snapshotAt = DateTime.UtcNow;
        return snapshot;
    }

    public override void Draw()
    {
        var s = Ui.S;
        var w = MathF.Min(ImGui.GetContentRegionAvail().X, 760 * s);
        Ui.Heading("Tribes", "The allied societies' daily quests: twelve a day between them, three per society. Open a society for "
                             + "today's quests, how far its next rank is, and what its ranks open.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        if (Data is not { Tribes.Count: > 0 } data)
        {
            Ui.Wrapped(Plans.Catalog == null ? "Reading the game data…" : "The game data has no allied societies.", Palette.TextMuted);
            return;
        }
        if (!Game.Ready)
        {
            DrawNotLoggedIn(w);
            return;
        }

        var snap = Read(data);
        DrawRun(snap, w);
        DrawDay(snap, w);
        DrawSocieties(data, snap, w, true);
        DrawSocieties(data, snap, w, false);
    }

    private static void DrawNotLoggedIn(float w)
    {
        var s = Ui.S;
        Ui.BeginCard(Palette.WithAlpha(Palette.Warn, 0.07f), width: w);
        Ui.Icon(FontAwesomeIcon.InfoCircle, Palette.Warn);
        ImGui.SameLine(0, 10 * s);
        ImGui.BeginGroup();
        using (Ui.F.Strong.Push()) Ui.Text("Log in to see your societies");
        Ui.Wrapped("Ranks, today's quests and allowances come from your character.", Palette.TextMuted);
        ImGui.EndGroup();
        Ui.EndCard();
    }

    // ------------------------------------------------------------------ the run (goal and options)

    private static readonly (TribeGoal Goal, string Label, string Text)[] Goals =
    {
        (TribeGoal.RankUp, "Rank up", "Societies below their top rank first, the lowest rank first. A society whose bar is full waits for its rank quest, and its quests go to the others."),
        (TribeGoal.Currency, "Currency", "As many dailies as the day allows, for each society's currency (and gil and crystals)."),
        (TribeGoal.Levelling, "Levelling", "As many dailies as the day allows, each on the lowest class that can take it."),
    };

    /// <summary>
    /// The run's place on the page, laid out like the Custom Deliveries page: its progress and live log go first (while
    /// it runs, or after it ran), then the goal and options, then the day's plan (shared with the Plan page). Start,
    /// Pause and Stop go on the action bar through an <c>Actions</c> override, as <c>DeliveriesPage</c> does.
    /// </summary>
    private void DrawRun(Snapshot snap, float w)
    {
        // The run's progress and live log: here.
        DrawSettings(snap, w);
        // The day's plan: here.
    }

    private void DrawSettings(Snapshot snap, float w)
    {
        var s = Ui.S;
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("Goal");
        var taking = snap.Days.Where(d => d.Tribe.CraftOrGather && d.Unlocked && !S.LeftOut.Contains(d.Tribe.Id)).ToList();
        var effective = TribeGoals.Effective(S.Goal, taking);
        var shown = Goals.First(g => g.Goal == effective).Label;
        if (Ui.DropdownField("##tribegoal", shown, "Pick a goal", 320 * s, FontAwesomeIcon.Flag, detail: S.Goal == null ? "picked for you" : null))
        {
            goalItems = new List<DropdownItem>
            {
                new("Pick for me", Sub: "Rank up while a society is below its top rank, then Currency"),
            };
            goalItems.AddRange(Goals.Select(g => new DropdownItem(g.Label, Sub: g.Text)));
        }
        var pick = Ui.DropdownList("##tribegoal", goalItems, S.Goal is { } chosen ? 1 + Array.FindIndex(Goals, g => g.Goal == chosen) : 0, "");
        if (pick >= 0)
        {
            S.Goal = pick == 0 ? null : Goals[pick - 1].Goal;
            Config.Save();
        }
        ImGui.Dummy(new Vector2(0, 2 * s));
        Ui.Wrapped(Goals.First(g => g.Goal == effective).Text, Palette.TextMuted);

        ImGui.Dummy(new Vector2(0, 6 * s));
        using (Ui.F.Strong.Push()) Ui.Text("Options");
        var v = S.PauseForCutscenes;
        if (Ui.Checkbox("Pause for cutscenes", ref v, "Off: the run skips quest cutscenes. On: it pauses so you can watch, and Resume carries on."))
        {
            S.PauseForCutscenes = v;
            Config.Save();
        }
        v = S.LeaveOutSay;
        if (Ui.Checkbox("Leave out quests that /say in public", ref v,
                "Some dailies have you say a phrase in /say, which players nearby see. The run says only the quest's own phrase, once. On: those quests are yours to do."))
        {
            S.LeaveOutSay = v;
            Config.Save();
            snapshot = null;
        }
        Ui.EndCard();
    }

    // ------------------------------------------------------------------ the day

    private void DrawDay(Snapshot snap, float w)
    {
        var s = Ui.S;
        Ui.BeginCard(Palette.WithAlpha(Palette.Accent, 0.07f), width: w);
        Ui.Icon(FontAwesomeIcon.CalendarAlt, Palette.Accent);
        ImGui.SameLine(0, 10 * s);
        ImGui.BeginGroup();
        using (Ui.F.Strong.Push()) Ui.Text($"{snap.Allowances} of {Game.DailyAllowance} allowances left today");
        var reset = Game.NextReset;
        Ui.Wrapped($"They come back in {Until(reset)}, at {reset:HH:mm} your time. Each society gives up to 3 dailies a day, "
                   + "and 3 more of the new rank on the day you rank up.", Palette.TextMuted);
        ImGui.EndGroup();

        var leftovers = snap.Accepted.Where(a => a.Leftover).ToList();
        if (leftovers.Count > 0)
        {
            ImGui.Dummy(new Vector2(0, 4 * s));
            Ui.Chip("Left over from yesterday", Palette.Warn, FontAwesomeIcon.ExclamationTriangle);
            var names = string.Join(", ", leftovers.Select(a => $"\"{a.Quest.Name}\""));
            Ui.Wrapped($"{names} {(leftovers.Count == 1 ? "was" : "were")} accepted before the reset. The game gives no new dailies until "
                       + $"{(leftovers.Count == 1 ? "it's" : "they're")} handed in or abandoned.", Palette.Warn);
        }

        var ready = snap.Days.Where(d => d.RankQuestReady != null).ToList();
        if (ready.Count > 0)
        {
            ImGui.Dummy(new Vector2(0, 4 * s));
            Ui.Wrapped($"Rank quest ready for {Join(ready.Select(d => d.Tribe.Name))}. Do it yourself for now: a button for it comes in a later version.",
                Palette.Text);
        }
        Ui.EndCard();
    }

    // ------------------------------------------------------------------ societies

    private void DrawSocieties(TribeData data, Snapshot snap, float w, bool craftOrGather)
    {
        var s = Ui.S;
        var days = snap.Days.Where(d => d.Tribe.CraftOrGather == craftOrGather).ToList();
        if (days.Count == 0) return;
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text(craftOrGather ? "Crafter and gatherer societies" : "Battle societies");
        Ui.Wrapped(craftOrGather
            ? "Their dailies are the run's: open one for today's quests and what's in the way."
            : "Their dailies come in a later version. Here for your rank, what each rank opens and the achievements.", Palette.TextMuted);
        ImGui.Dummy(new Vector2(0, 4 * s));
        foreach (var day in days)
        {
            var state = StateOf(day, snap);
            DrawRow(data, day, state);
            if (open.Contains(day.Tribe.Id)) DrawDetails(data, day, state, snap);
        }
        Ui.EndCard();
    }

    private enum State { Locked, Leftover, RankQuest, Top, DoneToday, UnderWay, LeftOut, Open }

    private State StateOf(TribeToday day, Snapshot snap)
    {
        if (!day.Unlocked) return State.Locked;
        if (snap.Accepted.Any(a => a.Leftover && a.Quest.TribeId == day.Tribe.Id)) return State.Leftover;
        if (day.RankQuestReady != null) return State.RankQuest;
        if (day.Tribe.CraftOrGather && S.LeftOut.Contains(day.Tribe.Id)) return State.LeftOut;
        if (day.Offers.Any() && day.LeftToday(Game.DailyAllowance) == 0)
            return day.Offers.All(o => o.State == OfferState.Done) ? State.DoneToday : State.UnderWay;
        if (day.AtTop) return State.Top;
        return State.Open;
    }

    private void DrawRow(TribeData data, TribeToday day, State state)
    {
        var s = Ui.S;
        var t = day.Tribe;
        var width = Ui.CardInnerWidth;
        var h = 46 * s;
        var start = ImGui.GetCursorScreenPos();
        var isOpen = open.Contains(t.Id);
        if (ImGui.InvisibleButton("##tribe" + t.Id, new Vector2(width, h)))
        {
            if (!open.Remove(t.Id)) open.Add(t.Id);
        }
        var hovered = ImGui.IsItemHovered();
        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        var dl = ImGui.GetWindowDrawList();
        var hover = Ui.Fade(ImGui.GetID("##tribehover" + t.Id), hovered ? 1f : 0f, 22f);
        if (hover > 0.01f)
            dl.AddRectFilled(start - new Vector2(8 * s, 0), start + new Vector2(width + 8 * s, h), Palette.U32(Palette.WithAlpha(Palette.Raised, 0.7f * hover)), Ui.InputRadius * s);

        // Chevron, then name over place.
        using (Ui.F.IconSmall.Push())
        {
            var glyph = (isOpen ? FontAwesomeIcon.AngleDown : FontAwesomeIcon.AngleRight).ToIconString();
            var gs = ImGui.CalcTextSize(glyph);
            dl.AddText(new Vector2(start.X + 6 * s - gs.X / 2, start.Y + (h - gs.Y) / 2), Palette.U32(Palette.Mix(Palette.TextFaint, Palette.TextMuted, hover)), glyph);
        }
        var x = start.X + 22 * s;
        using (Ui.F.Strong.Push()) dl.AddText(new Vector2(x, start.Y + 5 * s), Palette.U32(state == State.Locked ? Palette.TextMuted : Palette.Text), t.Name);
        using (Ui.F.Small.Push()) dl.AddText(new Vector2(x, start.Y + 26 * s), Palette.U32(Palette.TextMuted), t.Place);

        // Status chip on the right; rank and today's count in between, when there's room.
        var (chipText, chipColor, chipIcon) = Chip(day, state);
        var chipLeft = start.X + width;
        if (chipText != null)
        {
            chipLeft -= Ui.ChipWidth(chipText, chipIcon);
            ImGui.SetCursorScreenPos(new Vector2(chipLeft, start.Y + (h - 20 * s) / 2));
            Ui.Chip(chipText, chipColor, chipIcon);
        }
        if (day.Unlocked)
        {
            var rankX = start.X + MathF.Max(190 * s, width * 0.42f);
            if (rankX + 110 * s < chipLeft - 8 * s)
            {
                dl.AddText(new Vector2(rankX, start.Y + 6 * s), Palette.U32(Palette.Text), RankText(data, day));
                ImGui.SetCursorScreenPos(new Vector2(rankX, start.Y + 30 * s));
                var fraction = day.AtTop ? 1f : day.Needed > 0 ? Math.Clamp(day.Value / (float)day.Needed, 0f, 1f) : 0f;
                Ui.Bar("##rep" + t.Id, fraction, day.BarFull ? Palette.Warn : Palette.Accent, 100 * s, 5);
            }
            var todayText = TodayText(day);
            var todayX = rankX + 130 * s;
            using (Ui.F.Small.Push())
            {
                var tw = ImGui.CalcTextSize(todayText).X;
                if (todayText.Length > 0 && todayX + tw < chipLeft - 10 * s)
                    dl.AddText(new Vector2(todayX, start.Y + (h - ImGui.GetTextLineHeight()) / 2), Palette.U32(Palette.TextMuted), todayText);
            }
        }
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + h));
        ImGui.Dummy(new Vector2(width, 0));
    }

    /// <summary>"Honored", "Bloodsworn, the top", "Allied".</summary>
    private static string RankText(TribeData data, TribeToday day)
    {
        var name = data.RankName(day.Tribe, day.Rank, day.Allied);
        return day.AtTop && !day.Allied ? $"{name}, the top" : name;
    }

    /// <summary>"2 of 3 today": taken, of what the day allows the society (6 on a rank-up day, more with several givers).</summary>
    private static string TodayText(TribeToday day)
    {
        var all = day.Taken + day.LeftToday(Game.DailyAllowance);
        return all == 0 ? "" : $"{day.Taken} of {all} today";
    }

    /// <summary>The status chip: what stands in the way, or what's done; nothing while today's quests are open.</summary>
    private static (string? Text, Vector4 Color, FontAwesomeIcon? Icon) Chip(TribeToday day, State state) => state switch
    {
        State.Locked => ("Locked", Palette.TextMuted, FontAwesomeIcon.Lock),
        State.Leftover => ("Left over from yesterday", Palette.Warn, FontAwesomeIcon.ExclamationTriangle),
        State.RankQuest => ("Rank quest ready", Palette.Warn, FontAwesomeIcon.Book),
        State.DoneToday => ("Done today", Palette.Accent, FontAwesomeIcon.Check),
        State.UnderWay => ("Under way", Palette.Accent, FontAwesomeIcon.Hourglass),
        State.LeftOut => ("Left out", Palette.TextMuted, FontAwesomeIcon.MinusCircle),
        State.Top => day.Offers.Any() ? (null, default, null) : ("Top rank", Palette.Accent, FontAwesomeIcon.Crown),
        _ => (null, default, null),
    };

    // ------------------------------------------------------------------ a society opened

    private void DrawDetails(TribeData data, TribeToday day, State state, Snapshot snap)
    {
        var s = Ui.S;
        var t = day.Tribe;
        var indent = 22 * s;
        ImGui.Indent(indent);
        if (!day.Unlocked)
        {
            DrawUnlock(t);
        }
        else
        {
            if (t.CraftOrGather)
            {
                var include = !S.LeftOut.Contains(t.Id);
                ImGui.PushID((int)t.Id);
                if (Ui.Checkbox("Include in the run", ref include, "Left out, the run doesn't visit this society."))
                {
                    if (include) S.LeftOut.Remove(t.Id);
                    else S.LeftOut.Add(t.Id);
                    Config.Save();
                }
                ImGui.PopID();
            }
            foreach (var left in snap.Accepted.Where(a => a.Leftover && a.Quest.TribeId == t.Id))
                Ui.Wrapped($"\"{left.Quest.Name}\" is left over from before the reset: hand it in or abandon it, and the giver offers today's dailies.", Palette.Warn);
            DrawRank(data, day, snap);
            DrawQuests(day, snap);
        }
        DrawShop(data, day);
        DrawAchievements(data, t);
        ImGui.Unindent(indent);
        ImGui.Dummy(new Vector2(0, 8 * s));
    }

    /// <summary>A locked society: the unlock quests still to do, where to get each, and what each needs first.</summary>
    private static void DrawUnlock(Tribe t)
    {
        var s = Ui.S;
        var steps = t.Unlock.Where(u => !Game.QuestDone(u.QuestId)).ToList();
        if (steps.Count == 0)
        {
            Ui.Wrapped("Its unlock quests are done, but the game hasn't sent the rank yet.", Palette.TextMuted);
            return;
        }
        var first = steps[0];
        Ui.Wrapped($"Unlock it with the quest \"{first.Name}\" from {first.NpcName} in {Where(first.Place, first.MapPosition)}"
                   + (first.Requirement.Length > 0 ? $", on {Article(first.Requirement)}." : "."), Palette.Text);
        var before = first.Previous.Where(p => !Game.QuestDone(p.QuestId)).Select(p => $"\"{p.Name}\"").ToList();
        if (before.Count > 0) Ui.Wrapped($"It needs {Join(before)} done first.", Palette.TextMuted);
        foreach (var next in steps.Skip(1))
            Ui.Wrapped($"Then \"{next.Name}\" from {next.NpcName} in {Where(next.Place, next.MapPosition)}.", Palette.TextMuted);
        using (Ui.F.Small.Push()) Ui.Wrapped("The run doesn't do unlock quests.", Palette.TextFaint);
        ImGui.Dummy(new Vector2(0, 2 * s));
    }

    /// <summary>The bar and what's next: the rank quest, or the dailies and days to the next rank and to the top.</summary>
    private void DrawRank(TribeData data, TribeToday day, Snapshot snap)
    {
        var s = Ui.S;
        var t = day.Tribe;
        ImGui.Dummy(new Vector2(0, 2 * s));
        if (day.RankQuestReady is { } rq)
        {
            Ui.Wrapped($"The bar is full: the rank quest \"{rq.Name}\" is next, from {rq.NpcName} in {Where(rq.Place, rq.MapPosition)}. "
                       + "Until it's done, dailies give no reputation.", Palette.Warn);
            var first = rq.OneOfFirst.Where(q => !Game.QuestDone(q.QuestId)).ToList();
            if (first.Count == rq.OneOfFirst.Count && first.Count > 0)
                Ui.Wrapped($"Before it, one of {Join(first.Select(q => $"\"{q.Name}\""), "or")} (either does).", Palette.TextMuted);
            using (Ui.F.Small.Push()) Ui.Wrapped("Do it yourself for now: a button for it comes in a later version.", Palette.TextMuted);
            return;
        }
        if (day.AtTop)
        {
            var allied = t.IntersocietalQuest != 0 && !day.Allied && t.Expansion > 0
                ? $" The quest \"{t.IntersocietalName}\" makes it Allied, once all three of the expansion's societies are at the top (it takes a combat class)."
                : "";
            Ui.Wrapped($"{data.RankName(t, day.Rank, day.Allied)}, the top rank. The dailies still pay currency and gil.{allied}", Palette.TextMuted);
            return;
        }
        var ladder = data.Ladder(t.Id);
        var standing = day.Standing(snap.Allowances);
        var next = TribeRanks.ToNext(ladder, standing);
        var top = TribeRanks.ToTop(ladder, standing);
        var nextName = data.RankName(t, day.Rank + 1, day.Allied);
        var line = $"{day.Value:N0} of {day.Needed:N0} reputation. {Dailies(next)} to {nextName}{Days(next)}";
        if (day.Rank + 1 < t.MaxRank && top.Known)
            line += $", {Dailies(top)} to {data.RankName(t, t.MaxRank, false)}{Days(top)}";
        Ui.Wrapped(line + ".", Palette.TextMuted);
        if (t.RankQuest(day.Rank) is { } upcoming)
            using (Ui.F.Small.Push()) Ui.Wrapped($"Then the rank quest \"{upcoming.Name}\".", Palette.TextFaint);
    }

    private static string Dailies(RankEstimate e) => e.Known ? $"{e.Dailies} more {(e.Dailies == 1 ? "daily" : "dailies")}" : "some dailies";

    private static string Days(RankEstimate e)
        => !e.Known || e.Days == 0 ? "" : e.LastDay == 0 ? ", today" : e.Days == 1 ? ", tomorrow" : $", about {e.Days} days";

    // ------------------------------------------------------------------ today's quests

    private void DrawQuests(TribeToday day, Snapshot snap)
    {
        var s = Ui.S;
        var catalog = Plans.Catalog!;
        ImGui.Dummy(new Vector2(0, 6 * s));
        using (Ui.F.Strong.Push()) Ui.Text("Today's quests");
        var withOffers = day.Givers.Where(g => g.Offers.Count > 0).ToList();
        if (withOffers.Count == 0)
        {
            Ui.Wrapped(day.RankedUpToday
                ? "You ranked up today, and this rank has no dailies of its own: the givers offer nothing more until the reset."
                : "Nothing offered today.", Palette.TextMuted);
            return;
        }
        foreach (var giver in withOffers)
        {
            if (withOffers.Count > 1)
                using (Ui.F.Small.Push()) Ui.Text(giver.Giver.Name, Palette.TextMuted);
            foreach (var offer in giver.Offers) DrawQuest(catalog, day, offer, snap);
            DrawCheck(giver.Giver);
        }
    }

    private void DrawQuest(GameCatalog catalog, TribeToday day, TribeOffer offer, Snapshot snap)
    {
        var s = Ui.S;
        var q = offer.Quest;
        var judgement = QuestPolicy.Judge(q, day.Tribe, new PolicyOptions(S.LeaveOutSay, offer.AcceptedAs));
        var job = offer.State == OfferState.Accepted ? offer.AcceptedAs
            : TribeClasses.Choose(q, snap.Classes, snap.Current, S.Goal == TribeGoal.Levelling);
        QuestReadiness? ready = null;
        if (offer.State == OfferState.Open && judgement.RunCanDo)
            ready = TribeClasses.Readiness(q, snap.Classes, snap.Current, S.Goal == TribeGoal.Levelling, Game.OnHand, Game.Owns,
                catalog.ItemName, catalog.ItemNamePlural);
        if (ready is { Ready: true }) job = ready.ClassJob;

        // The chip says where the quest stands; the line under the name says why, or what it asks.
        string chip;
        Vector4 color;
        FontAwesomeIcon? icon = null;
        uint chipJob = 0;
        string sub;
        if (offer.State == OfferState.Done)
        {
            (chip, color, icon, sub) = ("Done today", Palette.Accent, FontAwesomeIcon.Check, What(catalog, q, job));
        }
        else if (!judgement.RunCanDo)
        {
            (chip, color, icon, sub) = ("Do it yourself", Palette.TextMuted, FontAwesomeIcon.UserCheck, judgement.Reason ?? "");
            if (offer.State == OfferState.Accepted && offer.AcceptedAs != 0) sub = $"Accepted as {ClassJobs.Abbreviation(offer.AcceptedAs)}. " + sub;
        }
        else if (offer.State == OfferState.Accepted)
        {
            (chip, color, chipJob, sub) = ($"Accepted as {ClassJobs.Abbreviation(offer.AcceptedAs)}", Palette.ClassJobColor(offer.AcceptedAs), offer.AcceptedAs,
                What(catalog, q, offer.AcceptedAs));
        }
        else if (ready is { Ready: false } need)
        {
            (chip, color, icon, sub) = (NeedChip(need.Need), Palette.Warn, FontAwesomeIcon.ExclamationTriangle, need.Text ?? "");
            if (need.Need == NeedKind.Crystals) sub += " in your bags. The run takes them from your retainers, or gathers them, when it can.";
        }
        else
        {
            (chip, color, icon, sub) = ("The run does it", Palette.Accent, FontAwesomeIcon.Check, What(catalog, q, job));
        }

        var start = ImGui.GetCursorScreenPos();
        var width = Ui.CardInnerWidth - 22 * s;
        var dl = ImGui.GetWindowDrawList();
        var chipW = Ui.ChipWidth(chip, icon, chipJob);
        var textX = start.X + 38 * s;
        var textW = MathF.Max(80 * s, width - 38 * s - chipW - 12 * s);
        float subH;
        using (Ui.F.Small.Push()) subH = ImGui.CalcTextSize(sub, false, textW).Y;
        var h = MathF.Max(44 * s, 24 * s + subH + 4 * s);

        DrawQuestIcon(dl, catalog, q, job, start + new Vector2(0, 4 * s));
        dl.PushClipRect(start, new Vector2(textX + textW, start.Y + h), true);
        dl.AddText(new Vector2(textX, start.Y + 4 * s), Palette.U32(offer.State == OfferState.Done ? Palette.TextMuted : Palette.Text), q.Name);
        dl.PopClipRect();
        using (Ui.F.Small.Push())
            dl.AddText(ImGui.GetFont(), ImGui.GetFontSize(), new Vector2(textX, start.Y + 24 * s), Palette.U32(Palette.TextMuted), sub, textW);
        ImGui.SetCursorScreenPos(new Vector2(start.X + width - chipW, start.Y + 4 * s));
        Ui.Chip(chip, color, icon, chipJob);
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + h));
        ImGui.Dummy(new Vector2(width, 0));
    }

    private static string NeedChip(NeedKind need) => need switch
    {
        NeedKind.Crystals => "Needs crystals",
        NeedKind.ClassLevel => "Needs a higher level",
        NeedKind.Gearset => "Needs a gearset",
        NeedKind.EquipItem => "Needs an item",
        _ => "Needs something",
    };

    /// <summary>The quest's item (what it crafts or gathers for the class), else a glyph for its kind of errand, in a 28 px square.</summary>
    private static void DrawQuestIcon(ImDrawListPtr dl, GameCatalog catalog, TribeQuest q, uint job, Vector2 pos)
    {
        var s = Ui.S;
        var d = 28 * s;
        var item = Item(q, job);
        if (item != 0)
        {
            Ui.DrawItemIcon(dl, catalog.ItemIcon(item), pos, d);
            return;
        }
        var (glyph, color) = Glyph(q);
        dl.AddRectFilled(pos, pos + new Vector2(d), Palette.U32(Palette.WithAlpha(color, 0.14f)), MathF.Min(Ui.IconRadius * s, d * 0.25f));
        using (Ui.F.IconSmall.Push())
        {
            var text = glyph.ToIconString();
            var size = ImGui.CalcTextSize(text);
            dl.AddText(pos + (new Vector2(d) - size) / 2, Palette.U32(color), text);
        }
    }

    /// <summary>The item the quest has this class make or gather (0 for errands).</summary>
    private static uint Item(TribeQuest q, uint job)
    {
        if (q.Craft is { } c && (job == 0 || ClassJobs.IsCrafter(job))) return c.Product;
        if (q.Gather.Count > 0) return (q.GatherFor(job) ?? q.Gather[0]).ItemId;
        return 0;
    }

    private static (FontAwesomeIcon, Vector4) Glyph(TribeQuest q)
    {
        if (q.Has(StepKinds.Snipe)) return (FontAwesomeIcon.Crosshairs, Palette.TextMuted);
        if (q.Has(StepKinds.Combat)) return (FontAwesomeIcon.FistRaised, Palette.TextMuted);
        if (q.Has(StepKinds.Mount)) return (FontAwesomeIcon.Horse, Palette.Accent);
        if (q.Has(StepKinds.Say)) return (FontAwesomeIcon.CommentDots, Palette.Accent);
        if (q.Has(StepKinds.Emote)) return (FontAwesomeIcon.Smile, Palette.Accent);
        if (q.Has(StepKinds.Carry)) return (FontAwesomeIcon.Box, Palette.Accent);
        if (q.Has(StepKinds.KeyItem)) return (FontAwesomeIcon.Key, Palette.Accent);
        return (FontAwesomeIcon.MapMarkerAlt, Palette.Accent);
    }

    /// <summary>What the quest asks, in a few words: "Crafts Snow Removal Tool as Carpenter (up to 4)", "An errand: rides a quest mount".</summary>
    private static string What(GameCatalog catalog, TribeQuest q, uint job)
    {
        if (q.Craft is { } c && (job == 0 || ClassJobs.IsCrafter(job)))
            return $"Crafts {catalog.ItemName(c.Product)}{(job != 0 ? $" as {ClassName(job)}" : "")}, up to {QuestCraft.MostCrafts}";
        if (q.Gather.Count > 0 && (job == 0 || job is 16 or 17))
        {
            var g = q.GatherFor(job) ?? q.Gather[0];
            return $"Gathers {catalog.ItemName(g.ItemId)}{(job != 0 ? $" as {ClassName(job)}" : "")}";
        }
        var parts = new List<string>();
        if (q.Has(StepKinds.Mount)) parts.Add(q.Has(StepKinds.MountAction) ? "rides a quest mount and uses its action" : "rides a quest mount");
        if (q.Has(StepKinds.Say)) parts.Add("says a phrase in /say");
        if (q.Emotes.Count > 0) parts.Add("uses " + Join(q.Emotes.Select(EmoteCommand)));
        if (q.Has(StepKinds.Carry)) parts.Add("carries a load");
        if (q.Has(StepKinds.UseItem)) parts.Add("uses a key item");
        else if (q.Has(StepKinds.KeyItem)) parts.Add("hands over a key item");
        if (q.SubZones.Count > 0) parts.Add("goes to " + Join(q.SubZones.Select(ZoneName).Where(z => z.Length > 0).Distinct()));
        if (q.Has(StepKinds.Snipe)) parts.Add("shoots targets");
        if (q.Has(StepKinds.Combat)) parts.Add("fights");
        return parts.Count == 0 ? "An errand: talks to people around" : "An errand: " + Join(parts);
    }

    /// <summary>Whether the offers worked out for a giver match what its list showed today.</summary>
    private static void DrawCheck(TribeGiver giver)
    {
        var s = Ui.S;
        ImGui.Dummy(new Vector2(0, 2 * s));
        switch (Game.Matches(giver.NpcId))
        {
            case true:
                Ui.Chip("Matches the game", Palette.Accent, FontAwesomeIcon.Check);
                break;
            case false:
                Ui.Chip("Differs from the game", Palette.Danger, FontAwesomeIcon.ExclamationTriangle);
                Ui.Wrapped($"{giver.Name}'s list showed other quests. A bug report (Settings, General) helps fix this.", Palette.Danger);
                break;
            default:
                using (Ui.F.Small.Push())
                    Ui.Wrapped($"Worked out from today's data. Talking to {giver.Name} checks them.", Palette.TextFaint);
                break;
        }
        ImGui.Dummy(new Vector2(0, 2 * s));
    }

    // ------------------------------------------------------------------ the shop and achievements

    /// <summary>What each rank opens in the society's shops: item icons per rank, with a mark on the ones you already have.</summary>
    private void DrawShop(TribeData data, TribeToday day)
    {
        var s = Ui.S;
        var t = day.Tribe;
        if (t.Shop.Count == 0 || Plans.Catalog is not { } catalog) return;
        ImGui.Dummy(new Vector2(0, 6 * s));
        using (Ui.F.Strong.Push()) Ui.Text("What its ranks open");
        // One entry per item, at the earliest rank that opens it.
        var items = t.Shop.GroupBy(i => i.ItemId).Select(g => g.OrderBy(i => i.Rank < 0 ? int.MaxValue : i.Rank).First()).ToList();
        var labelW = 120 * s;
        foreach (var group in items.GroupBy(i => i.Rank).OrderBy(g => g.Key < 0 ? int.MaxValue : g.Key))
        {
            var label = group.Key switch
            {
                0 => "From the start",
                TribeShopItem.AlliedRank => "Allied",
                < 0 => "After quests",
                _ => data.RankName(t, group.Key, false),
            };
            var reached = group.Key switch
            {
                0 => day.Unlocked,
                TribeShopItem.AlliedRank => day.Allied,
                < 0 => false,
                _ => day.Rank >= group.Key,
            };
            // Placed by screen position: SameLine offsets count from the group and would add the indent again.
            var rowStart = ImGui.GetCursorScreenPos();
            using (Ui.F.Small.Push())
                ImGui.GetWindowDrawList().AddText(new Vector2(rowStart.X, rowStart.Y + (ShopIcon * s - ImGui.GetTextLineHeight()) / 2),
                    Palette.U32(reached ? Palette.Text : Palette.TextMuted), label);
            ImGui.SetCursorScreenPos(new Vector2(rowStart.X + labelW, rowStart.Y));
            ShopIcons(catalog, group.OrderBy(i => i.Kind == ShopItemKind.Other ? 1 : 0).ThenBy(i => i.Kind).ToList(), rowStart.X + labelW);
            ImGui.Dummy(new Vector2(0, 2 * s));
        }
        using (Ui.F.Small.Push())
            Ui.Wrapped("A check marks what you already have (mounts, minions, orchestrion rolls, cards and the like). Hover for the price.", Palette.TextFaint);
    }

    /// <summary>Shop icons are this big (before the interface scale).</summary>
    private const float ShopIcon = 26f;

    private static void ShopIcons(GameCatalog catalog, List<TribeShopItem> items, float left)
    {
        var s = Ui.S;
        var d = ShopIcon * s;
        var first = true;
        foreach (var item in items)
        {
            if (!first)
            {
                ImGui.SameLine(0, 4 * s);
                if (Ui.RemainingWidth() < d)
                {
                    ImGui.NewLine();
                    ImGui.SetCursorScreenPos(new Vector2(left, ImGui.GetCursorScreenPos().Y));
                }
            }
            first = false;
            var pos = ImGui.GetCursorScreenPos();
            Ui.ItemIcon(catalog.ItemIcon(item.ItemId), ShopIcon);
            var owned = item.Unlockable ? Game.Owned(item.ItemId) : null;
            if (owned == true)
            {
                // A small check badge in the corner: owned.
                var dl = ImGui.GetWindowDrawList();
                var c = pos + new Vector2(d - 4 * s, d - 4 * s);
                dl.AddCircleFilled(c, 6.5f * s, Palette.U32(Palette.Surface));
                dl.AddCircleFilled(c, 5.5f * s, Palette.U32(Palette.Accent));
                var ink = Palette.U32(Palette.OnAccent);
                dl.AddLine(c + new Vector2(-2.6f, 0.1f) * s, c + new Vector2(-0.7f, 2f) * s, ink, 1.6f * s);
                dl.AddLine(c + new Vector2(-0.7f, 2f) * s, c + new Vector2(2.8f, -1.9f) * s, ink, 1.6f * s);
            }
            if (ImGui.IsItemHovered())
            {
                var cost = item.Currency == 1 ? $"{item.Cost:N0} gil" : $"{item.Cost:N0} {(item.Cost == 1 ? catalog.ItemName(item.Currency) : catalog.ItemNamePlural(item.Currency))}";
                var gate = item.Rank < 0 && item.GateName.Length > 0 ? $"\nAfter the quest \"{item.GateName}\"." : "";
                var have = owned switch { true => "\nYou have it.", false => "\nNot yours yet.", _ => "" };
                Ui.ShowTooltip($"{catalog.ItemName(item.ItemId)}{(item.Count > 1 ? $" x{item.Count}" : "")}\n{cost}.{gate}{have}");
            }
        }
    }

    private static void DrawAchievements(TribeData data, Tribe t)
    {
        var s = Ui.S;
        if (t.Achievements.Count == 0) return;
        ImGui.Dummy(new Vector2(0, 6 * s));
        using (Ui.F.Strong.Push()) Ui.Text("Achievements");
        foreach (var a in t.Achievements)
        {
            var what = a.Intersocietal ? "the intersocietal quests" : data.RankName(t, a.Rank, false);
            var title = a.Title.Length > 0 ? $", title \"{a.Title}\"" : "";
            var state = Game.AchievementDone(a.Id) switch { true => "done", false => "not yet", _ => "open the Achievements window once to see" };
            Ui.Muted($"\"{a.Name}\" ({what}{title}): {state}");
        }
    }

    // ------------------------------------------------------------------ words

    private static string Where(string place, Vector2 map) => map == Vector2.Zero ? place : $"{place} ({map.X:0.0}, {map.Y:0.0})";

    /// <summary>"Disciple of the Hand, level 50" → "a Disciple of the Hand, level 50" (the game's category names are singular or "Any …").</summary>
    private static string Article(string requirement)
        => requirement.StartsWith("Disciple ", StringComparison.Ordinal) ? "a " + requirement : requirement;

    private static string Join(IEnumerable<string> parts, string last = "and")
    {
        var list = parts.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            _ => string.Join(", ", list.Take(list.Count - 1)) + $" {last} " + list[^1],
        };
    }

    private static string ClassName(uint job)
        => TribeData.Capitalise(Services.Data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>().GetRowOrDefault(job)?.Name.ExtractText() ?? ClassJobs.Abbreviation(job));

    private static string EmoteCommand(uint emote)
        => Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Emote>().GetRowOrDefault(emote)?.TextCommand.ValueNullable?.Command.ExtractText() is { Length: > 0 } c ? c : "an emote";

    private static string ZoneName(uint territory)
        => Services.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().GetRowOrDefault(territory)?.PlaceName.ValueNullable?.Name.ExtractText() ?? "";

    private static string Until(DateTime when)
    {
        var left = when - DateTime.Now;
        if (left <= TimeSpan.Zero) return "a moment";
        if (left.TotalHours >= 1) return $"{(int)left.TotalHours} h {left.Minutes} min";
        return $"{Math.Max(1, left.Minutes)} min";
    }
}
