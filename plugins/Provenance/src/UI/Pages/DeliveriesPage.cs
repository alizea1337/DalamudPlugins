using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Provenance.Core.Character;
using Provenance.Core.Deliveries;
using Provenance.Core.Planning;
using Provenance.Data;
using Provenance.UI.Theme;
using Deliver = Provenance.Integrations.Deliveries;
using DeliveryRun = Provenance.Run.Deliveries.DeliveryRun;

namespace Provenance.UI.Pages;

/// <summary>
/// Custom Deliveries (Expeditions): the run (progress, goal and options, this week's plan), the week (deliveries left,
/// the reset, bonus clients) and every client with its level, deliveries this week and status; a client opens to this
/// week's three requests, what they pay, whether they match what the game showed, and what's ahead.
/// </summary>
internal sealed unsafe class DeliveriesPage : Page
{
    private readonly HashSet<uint> open = new();
    private string? startProblem;

    public DeliveriesPage(Plugin plugin) : base(plugin) { }

    private DeliveryRun Loop => Plugin.DeliveryLoop;
    private DeliverySettings S => Config.Deliveries;

    public override string Title => "Custom Deliveries";
    public override FontAwesomeIcon Icon => FontAwesomeIcon.BoxOpen;

    public override (string Text, Vector4 Color)? Status
        => Loop.Active ? (Loop.Status, Palette.Accent)
            : Loop.Problem != null ? ("The run stopped with a problem; see below", Palette.Warn)
            : Deliver.Loaded ? ($"{Deliver.Remaining} of {Deliver.WeeklyAllowance} deliveries left this week", Palette.TextMuted) : null;

    public override (Vector4 Color, bool Pulse)? Live => Loop.Active ? (Palette.Accent, true) : null;

    public override IEnumerable<FooterAction> Actions
    {
        get
        {
            if (Loop.Active)
            {
                foreach (var a in DeliveryRunActions(Loop)) yield return a;
                yield break;
            }
            yield return new FooterAction("Start", Ui.Kind.Primary, FontAwesomeIcon.Play, () => startProblem = Loop.Start(),
                Tooltip: "Makes what this week's plan needs (crafted and gathered requests), then hands it all in.");
        }
    }

    /// <summary>The run's buttons while it goes: Stop, and Pause / Resume.</summary>
    public static IEnumerable<FooterAction> DeliveryRunActions(DeliveryRun loop)
    {
        yield return new FooterAction("Stop", Ui.Kind.Danger, FontAwesomeIcon.Stop, () => loop.Stop(),
            Tooltip: loop.Paused ? "Ends the run." : "Stops once the item being crafted is finished; anything else stops right away.");
        if (loop.Paused)
            yield return new FooterAction("Resume", Ui.Kind.Primary, FontAwesomeIcon.Play, loop.Resume, Tooltip: "Carries on where it paused.");
        else if (loop.Pausing)
            yield return new FooterAction("Keep going", Ui.Kind.Secondary, FontAwesomeIcon.Play, loop.Resume, Tooltip: "Never mind the pause.");
        else
            yield return new FooterAction("Pause", Ui.Kind.Secondary, FontAwesomeIcon.Pause, loop.Pause,
                Tooltip: "Pauses after the item being crafted, otherwise right away. Resume plans again from what's in your bags.");
    }

    private DeliveryData? Data => Plans.Catalog?.Deliveries;

    public override void Draw()
    {
        var s = Ui.S;
        var w = MathF.Min(ImGui.GetContentRegionAvail().X, 760 * s);
        Ui.Heading("Custom Deliveries", "Collectables for the clients below, 12 a week between them. Start makes what this week's plan needs "
                                        + "(crafted and gathered requests for now), then hands it all in.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        OtherPluginsNotice.Draw(Plugin, w);
        if (startProblem != null && !Loop.Active)
        {
            Ui.Icon(FontAwesomeIcon.ExclamationTriangle, Palette.Warn, small: true);
            ImGui.SameLine(0, 8 * s);
            Ui.Wrapped("Can't start: " + startProblem, Palette.Warn, w);
            ImGui.Dummy(new Vector2(0, 2 * s));
        }
        if (Data is not { Clients.Count: > 0 } data)
        {
            Ui.Wrapped(Plans.Catalog == null ? "Reading the game data…" : "The game data has no custom deliveries.", Palette.TextMuted);
            return;
        }

        var weeks = data.Clients.Select(c => Deliver.Week(data, c)).ToList();
        if (Loop.Active || Loop.Lines.Count > 0) DrawProgress(w);
        if (Deliver.Loaded) DrawWeek(weeks, w);
        else DrawNotLoaded(w);
        if (Deliver.Loaded)
        {
            DrawSettings(weeks, w);
            DrawPlan(Plugin, w, false);
        }
        DrawClients(data, weeks, w);
    }

    // ------------------------------------------------------------------ progress

    private void DrawProgress(float w)
    {
        var s = Ui.S;
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text(Loop.Active ? "Under way" : "Last run");
        if (Loop.Active) Ui.Wrapped(Loop.Status, Palette.Text);
        if (Loop.Visiting is { } client) Ui.Chip(client.Name, Palette.Accent, FontAwesomeIcon.BoxOpen);
        using (Ui.F.Small.Push())
            Ui.Muted(Loop.Active
                ? $"{Loop.DeliveredThisRound} of {Loop.Planned} delivered this round, {Loop.Delivered} in all"
                : $"{Loop.Delivered} {(Loop.Delivered == 1 ? "delivery" : "deliveries")}");
        ImGui.Dummy(new Vector2(0, 2 * s));
        foreach (var (at, text, problem) in Loop.Lines.Reverse().Take(8))
        {
            using (Ui.F.Small.Push())
            {
                Ui.Text(at.ToString("HH:mm"), Palette.TextFaint);
                ImGui.SameLine(0, 10 * s);
                Ui.Wrapped(text, problem ? Palette.Warn : Palette.TextMuted);
            }
        }
        Ui.EndCard();
    }

    // ------------------------------------------------------------------ goal and options

    private static readonly string[] Goals = { "Rank up", "Scrips", "Gil" };

    private void DrawSettings(List<ClientWeek> weeks, float w)
    {
        var s = Ui.S;
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("Goal");
        var effective = Plugin.DeliveryPlanning.Goal(weeks);
        var goal = (int)effective;
        var canRank = DeliveryPlanning.CanRankUp(weeks);
        var off = canRank ? null : new[] { "Every client is at the top level, so there's nothing left to rank up.", null, null };
        if (Ui.Segmented("##deliverygoal", Goals, ref goal, 360 * s, off)) { S.Goal = (DeliveryGoal)goal; Config.Save(); }
        ImGui.Dummy(new Vector2(0, 2 * s));
        if (!canRank && S.Goal == DeliveryGoal.RankUp)
            Ui.Wrapped("Every client is at the top level, so Rank up has nothing to do: the run goes for scrips instead.", Palette.TextFaint);
        Ui.Wrapped(effective switch
        {
            DeliveryGoal.RankUp => "Clients below the top level first, the lowest first, up to their next level each round, at the lowest collectability "
                                   + "tier (satisfaction is the same at every tier). Deliveries left over go to clients at the top level, bonus requests first.",
            DeliveryGoal.Scrips => "The most scrips per delivery, at the best collectability tier your gear reliably reaches. Bonus requests come first.",
            _ => "The most gil per delivery, at the best collectability tier your gear reliably reaches. Bonus requests come first.",
        }, Palette.TextMuted);
        if (S.Goal == null)
        {
            Ui.Wrapped("Picked for you: Rank up while a client is below the top level, then Scrips. Choosing a goal keeps it.", Palette.TextFaint);
        }
        else if (Ui.LinkButton("Pick the goal for me again", Palette.Accent, FontAwesomeIcon.Undo, "Rank up while a client is below the top level, then Scrips."))
        {
            S.Goal = null;
            Config.Save();
        }

        ImGui.Dummy(new Vector2(0, 6 * s));
        using (Ui.F.Strong.Push()) Ui.Text("Options");
        var v = S.PreferCraftGather;
        if (Ui.Checkbox("Prefer crafting and gathering", ref v, "Fish only when neither the crafted nor the gathered request can be done. Fish come in a later version."))
        { S.PreferCraftGather = v; Config.Save(); }
        v = S.DeliverAnywayWhileRanking;
        if (Ui.Checkbox("Deliver anyway while ranking up", ref v,
                "Under Rank up, clients below the top level still get their deliveries when the scrips would go over the cap. The scrips over the cap are lost."))
        { S.DeliverAnywayWhileRanking = v; Config.Save(); }
        v = S.PauseForCutscenes;
        if (Ui.Checkbox("Pause for rank-up cutscenes", ref v,
                "Off: the run skips a client's rank-up cutscene. On: the run pauses so you can watch it, and Resume carries on."))
        { S.PauseForCutscenes = v; Config.Save(); }
        v = S.UseConsumables;
        if (Ui.Checkbox("Food and medicine for a higher tier", ref v,
                "Crafts with the food and medicine picked on the Character page when that reaches a higher collectability tier. Never when ranking up: any tier gives the same satisfaction."))
        { S.UseConsumables = v; Config.Save(); }
        Ui.EndCard();
    }

    // ------------------------------------------------------------------ the plan

    /// <summary>This week's plan: what goes to whom, and who gets nothing this time and why. Also on the Plan page.</summary>
    public static void DrawPlan(Plugin plugin, float w, bool fromPlanPage)
    {
        var s = Ui.S;
        if (plugin.Plans.Catalog is not { } catalog) return;
        var planning = plugin.DeliveryPlanning;
        var plan = planning.Current;
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("This week's plan");
        if (planning.Pending > 0)
            Ui.Wrapped($"Working out what your gear reaches ({planning.Pending} {(planning.Pending == 1 ? "request" : "requests")} left)…", Palette.TextMuted);
        if (plan.Count == 0)
        {
            Ui.Wrapped(Deliver.Remaining == 0 ? "No deliveries left this week." : "Nothing to deliver with the current options.", Palette.TextMuted);
        }
        else
        {
            var gil = plan.Deliveries.Sum(d => d.GilEach * d.Count);
            Ui.Wrapped($"{plan.Count} of the {Deliver.Remaining} deliveries left this week, for about {gil:N0} gil and "
                       + string.Join(" and ", plan.Deliveries.GroupBy(d => d.ScripCurrency).Select(g => $"{g.Sum(d => d.ScripsEach * d.Count):N0} {ScripName(g.Key)}")) + ".",
                Palette.TextMuted);
            ImGui.Dummy(new Vector2(0, 2 * s));
            foreach (var d in plan.Deliveries) DrawPlanned(catalog, d);
        }
        var skipped = plan.Skipped.Where(x => !x.Why.StartsWith("locked")).ToList();
        if (skipped.Count > 0)
        {
            ImGui.Dummy(new Vector2(0, 4 * s));
            Ui.Muted("Not this time:");
            foreach (var (client, why) in skipped)
                using (Ui.F.Small.Push()) Ui.Wrapped($"{client.Name}: {why}.", Palette.TextMuted);
        }
        if (fromPlanPage)
        {
            ImGui.Dummy(new Vector2(0, 4 * s));
            if (Ui.LinkButton("Open Custom Deliveries", Palette.Accent, FontAwesomeIcon.BoxOpen)) plugin.Main.Select<DeliveriesPage>();
        }
        Ui.EndCard();
    }

    private static void DrawPlanned(GameCatalog catalog, PlannedDelivery d)
    {
        var s = Ui.S;
        var start = ImGui.GetCursorScreenPos();
        var width = Ui.CardInnerWidth;
        var h = 40 * s;
        var dl = ImGui.GetWindowDrawList();
        Ui.DrawItemIcon(dl, catalog.ItemIcon(d.Request.Item.ItemId), start + new Vector2(0, (h - 28 * s) / 2), 28 * s);
        var x = start.X + 38 * s;
        dl.AddText(new Vector2(x, start.Y + 3 * s), Palette.U32(Palette.Text), $"{d.Count}x {catalog.ItemName(d.Request.Item.ItemId)}");
        using (Ui.F.Small.Push())
            dl.AddText(new Vector2(x, start.Y + 22 * s), Palette.U32(Palette.TextMuted),
                $"for {d.Client.Name}, made by {ClassJobs.Abbreviation(d.ClassJob)}, collectability {d.Collectability} or more");
        var chips = new List<(string Text, Vector4 Color, FontAwesomeIcon Icon)> { KindChip(d.Request.Item) };
        if (d.Request.Bonus) chips.Add(("Bonus, x1.5", Palette.Gold, FontAwesomeIcon.Star));
        var right = start.X + width;
        foreach (var chip in Enumerable.Reverse(chips))
        {
            right -= Ui.ChipWidth(chip.Text, chip.Icon);
            ImGui.SetCursorScreenPos(new Vector2(right, start.Y + (h - 20 * s) / 2));
            Ui.Chip(chip.Text, chip.Color, chip.Icon);
            right -= 6 * s;
        }
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + h));
        ImGui.Dummy(new Vector2(width, 0));
    }

    // ------------------------------------------------------------------ the week

    private void DrawWeek(List<ClientWeek> weeks, float w)
    {
        var s = Ui.S;
        Ui.BeginCard(Palette.WithAlpha(Palette.Accent, 0.07f), width: w);
        Ui.Icon(FontAwesomeIcon.CalendarAlt, Palette.Accent);
        ImGui.SameLine(0, 10 * s);
        ImGui.BeginGroup();
        using (Ui.F.Strong.Push()) Ui.Text($"{Deliver.Remaining} of {Deliver.WeeklyAllowance} deliveries left this week");
        var reset = Deliver.NextReset;
        Ui.Wrapped($"They reset in {Until(reset)}, on {reset:dddd} at {reset:HH:mm}. Each client takes up to 6 a week.", Palette.TextMuted);
        ImGui.EndGroup();

        // Bonus requests (x1.5) only come from clients at the top level.
        var bonus = weeks.SelectMany(x => x.Requests.Where(r => r is { Bonus: true }).Select(r => (x.Client, r!.Item.Kind))).ToList();
        ImGui.Dummy(new Vector2(0, 4 * s));
        if (bonus.Count == 0)
        {
            Ui.Wrapped("No bonus requests this week. They come from clients at the top level, and pay half as much again.", Palette.TextMuted);
        }
        else
        {
            Ui.Muted("Bonus requests this week, paying half as much again:");
            ImGui.Dummy(new Vector2(0, 2 * s));
            var x0 = ImGui.GetCursorPosX();
            var first = true;
            foreach (var (client, kind) in bonus)
            {
                var label = $"{client.Name}, {KindWord(kind)}";
                var cw = Ui.ChipWidth(label, FontAwesomeIcon.Star);
                if (!first)
                {
                    ImGui.SameLine(0, 6 * s);
                    if (Ui.RemainingWidth() < cw) ImGui.NewLine();
                }
                if (ImGui.GetCursorPosX() < x0) ImGui.SetCursorPosX(x0);
                Ui.Chip(label, Palette.Gold, FontAwesomeIcon.Star);
                first = false;
            }
        }
        Ui.EndCard();
    }

    private void DrawNotLoaded(float w)
    {
        var s = Ui.S;
        Ui.BeginCard(Palette.WithAlpha(Palette.Warn, 0.07f), width: w);
        Ui.Icon(FontAwesomeIcon.InfoCircle, Palette.Warn);
        ImGui.SameLine(0, 10 * s);
        ImGui.BeginGroup();
        using (Ui.F.Strong.Push()) Ui.Text("Your delivery data isn't in yet");
        Ui.Wrapped("The game sends it once you open its client list (Timers, then Custom Deliveries) or talk to a client. "
                   + "Until then the levels, this week's deliveries and the requests stay empty here.", Palette.TextMuted);
        ImGui.Dummy(new Vector2(0, 4 * s));
        if (Ui.Button("Open the client list", Ui.Kind.Secondary, FontAwesomeIcon.ListUl, tooltip: "Opens the game's Custom Deliveries client list, which loads the data."))
            OpenClientList();
        ImGui.EndGroup();
        Ui.EndCard();
    }

    private static void OpenClientList()
    {
        var module = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentModule.Instance();
        var agent = module == null ? null : module->GetAgentByInternalId(FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId.SatisfactionList);
        if (agent != null && !agent->IsAgentActive()) agent->Show();
    }

    // ------------------------------------------------------------------ clients

    private void DrawClients(DeliveryData data, List<ClientWeek> weeks, float w)
    {
        var s = Ui.S;
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("Clients");
        Ui.Wrapped("Open a client for this week's requests and what the next level brings.", Palette.TextMuted);
        ImGui.Dummy(new Vector2(0, 4 * s));
        var best = ClassJobs.Crafters.Concat(ClassJobs.Gatherers).Max(j => PlanService.JobLevel(j));
        foreach (var week in weeks)
        {
            var c = week.Client;
            var state = StateOf(week, best);
            if (state is State.Open or State.DoneThisWeek && S.LeftOut.Contains(c.Id)) state = State.LeftOut;
            DrawRow(week, state);
            if (open.Contains(c.Id)) DrawDetails(data, week, state);
        }
        Ui.EndCard();
    }

    private enum State { Locked, NoData, Unknown, QuestFirst, LevelTooLow, DoneThisWeek, LeftOut, Open }

    private static State StateOf(ClientWeek week, int bestLevel)
    {
        var c = week.Client;
        if (!Deliver.QuestDone(c.UnlockQuest)) return State.Locked;
        if (!Deliver.Loaded) return State.NoData;
        if (!Deliver.Known(c.Id) || week.Rank == 0) return State.Unknown;
        if (GatingQuest(week) != null) return State.QuestFirst;
        if (bestLevel < c.LevelUnlock) return State.LevelTooLow;
        if (week.LeftThisWeek == 0 || Deliver.Remaining == 0) return State.DoneThisWeek;
        return State.Open;
    }

    /// <summary>The story quest holding this level's requests back, while it isn't done.</summary>
    private static DeliveryQuest? GatingQuest(ClientWeek week)
        => week.Client.Quests.FirstOrDefault(q => q.Gates && q.Level == week.Rank && !Deliver.QuestDone(q.QuestId));

    private void DrawRow(ClientWeek week, State state)
    {
        var s = Ui.S;
        var c = week.Client;
        var width = Ui.CardInnerWidth;
        var h = 46 * s;
        var start = ImGui.GetCursorScreenPos();
        var isOpen = open.Contains(c.Id);
        if (ImGui.InvisibleButton("##client" + c.Id, new Vector2(width, h)))
        {
            if (!open.Remove(c.Id)) open.Add(c.Id);
        }
        var hovered = ImGui.IsItemHovered();
        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        var dl = ImGui.GetWindowDrawList();
        var hover = Ui.Fade(ImGui.GetID("##clienthover" + c.Id), hovered ? 1f : 0f, 22f);
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
        var dim = state is State.Locked;
        using (Ui.F.Strong.Push()) dl.AddText(new Vector2(x, start.Y + 5 * s), Palette.U32(dim ? Palette.TextMuted : Palette.Text), c.Name);
        using (Ui.F.Small.Push()) dl.AddText(new Vector2(x, start.Y + 26 * s), Palette.U32(Palette.TextMuted), c.Place);

        // Status chip on the right; level and this week's deliveries in between, when there's room.
        var (chipText, chipColor, chipIcon) = Chip(week, state);
        var chipRight = start.X + width;
        var chipLeft = chipRight;
        if (chipText != null)
        {
            var cw = Ui.ChipWidth(chipText, chipIcon);
            chipLeft = chipRight - cw;
            ImGui.SetCursorScreenPos(new Vector2(chipLeft, start.Y + (h - 20 * s) / 2));
            Ui.Chip(chipText, chipColor, chipIcon);
        }
        // A locked client reads as level 1 in the game: nothing to show until it's unlocked.
        if (week.Rank > 0 && state != State.Locked)
        {
            var levelX = start.X + MathF.Max(190 * s, width * 0.42f);
            var levelText = week.Maxed ? "Level 5, the top" : $"Level {week.Rank} of 5";
            var weekText = $"{week.Used} of {c.PerWeek} this week";
            if (levelX + 110 * s < chipLeft - 8 * s)
            {
                dl.AddText(new Vector2(levelX, start.Y + 6 * s), Palette.U32(Palette.Text), levelText);
                ImGui.SetCursorScreenPos(new Vector2(levelX, start.Y + 30 * s));
                var fraction = week.Maxed ? 1f : week.Required > 0 ? Math.Clamp(week.Satisfaction / (float)week.Required, 0f, 1f) : 0f;
                Ui.Bar("##sat" + c.Id, fraction, Palette.Accent, 100 * s, 5);
            }
            var weekX = levelX + 130 * s;
            using (Ui.F.Small.Push())
            {
                var ww = ImGui.CalcTextSize(weekText).X;
                if (weekX + ww < chipLeft - 10 * s)
                    dl.AddText(new Vector2(weekX, start.Y + (h - ImGui.GetTextLineHeight()) / 2), Palette.U32(Palette.TextMuted), weekText);
            }
        }
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + h));
        ImGui.Dummy(new Vector2(width, 0));
    }

    /// <summary>The status chip: what stands in the way, or nothing when the client takes deliveries.</summary>
    private static (string? Text, Vector4 Color, FontAwesomeIcon? Icon) Chip(ClientWeek week, State state) => state switch
    {
        State.Locked => ("Locked", Palette.TextMuted, FontAwesomeIcon.Lock),
        State.NoData => ("No data yet", Palette.TextMuted, null),
        State.Unknown => ("Not readable", Palette.Warn, null),
        State.QuestFirst => ("Story quest first", Palette.Warn, FontAwesomeIcon.Book),
        State.LevelTooLow => ($"Needs level {week.Client.LevelUnlock}", Palette.Warn, null),
        State.DoneThisWeek => ("Done this week", Palette.Accent, FontAwesomeIcon.Check),
        State.LeftOut => ("Left out", Palette.TextMuted, FontAwesomeIcon.MinusCircle),
        _ => week.Requests.Any(r => r is { Bonus: true }) ? ("Bonus", Palette.Gold, FontAwesomeIcon.Star) : (null, default, null),
    };

    // ------------------------------------------------------------------ a client opened

    private void DrawDetails(DeliveryData data, ClientWeek week, State state)
    {
        var s = Ui.S;
        var c = week.Client;
        var indent = 22 * s;
        var catalog = Plans.Catalog!;
        ImGui.Indent(indent);
        switch (state)
        {
            case State.Locked:
                Ui.Wrapped($"Unlock {c.Name} with the quest \"{c.UnlockQuestName}\" (as a crafter or gatherer at level {c.LevelUnlock} or higher).", Palette.TextMuted);
                break;
            case State.NoData:
                Ui.Wrapped("Open the game's client list once and this fills in.", Palette.TextMuted);
                break;
            case State.Unknown:
                Ui.Wrapped("This client is newer than this version of Provenance can read. An update will add it.", Palette.Warn);
                break;
        }

        if (state is not (State.Locked or State.NoData or State.Unknown))
        {
            var include = !S.LeftOut.Contains(c.Id);
            ImGui.PushID((int)c.Id);
            if (Ui.Checkbox("Include in the run", ref include, "Left out, the run skips this client."))
            {
                if (include) S.LeftOut.Remove(c.Id);
                else S.LeftOut.Add(c.Id);
                Config.Save();
            }
            ImGui.PopID();
        }

        if (week.Rank > 0 && state is not (State.Locked or State.NoData or State.Unknown))
        {
            if (state == State.QuestFirst && GatingQuest(week) is { } gate)
                Ui.Wrapped($"Do the story quest \"{gate.Name}\" first: until it's done, {c.Name} takes no deliveries at this level.", Palette.Warn);
            if (state == State.LevelTooLow)
                Ui.Wrapped($"{c.Name} takes deliveries from crafters and gatherers at level {c.LevelUnlock} or higher.", Palette.Warn);

            ImGui.Dummy(new Vector2(0, 2 * s));
            using (Ui.F.Strong.Push()) Ui.Text(week.Maxed ? "This week's requests" : $"Requests at level {week.Rank}");
            foreach (var r in week.Requests)
                if (r != null) DrawRequest(catalog, c, r);
            DrawCheck(week);
        }

        if (state != State.Locked) DrawAhead(catalog, week);
        else DrawUnlockAhead(catalog, week);
        ImGui.Unindent(indent);
        ImGui.Dummy(new Vector2(0, 8 * s));
    }

    private void DrawRequest(GameCatalog catalog, DeliveryClient c, DeliveryRequest r)
    {
        var s = Ui.S;
        var item = r.Item;
        var start = ImGui.GetCursorScreenPos();
        var width = Ui.CardInnerWidth - 22 * s;
        var h = 44 * s;
        var dl = ImGui.GetWindowDrawList();
        Ui.DrawItemIcon(dl, catalog.ItemIcon(item.ItemId), start + new Vector2(0, (h - 28 * s) / 2), 28 * s);
        var x = start.X + 38 * s;
        dl.AddText(new Vector2(x, start.Y + 4 * s), Palette.U32(Palette.Text), catalog.ItemName(item.ItemId));
        using (Ui.F.Small.Push())
            dl.AddText(new Vector2(x, start.Y + 24 * s), Palette.U32(Palette.TextMuted), RewardLine(r));

        // Chips on the right: what kind of request, and the bonus.
        var chips = new List<(string Text, Vector4 Color, FontAwesomeIcon Icon)> { KindChip(item) };
        if (r.Bonus) chips.Add(("Bonus, x1.5", Palette.Gold, FontAwesomeIcon.Star));
        var right = start.X + width;
        foreach (var chip in Enumerable.Reverse(chips))
        {
            var cw = Ui.ChipWidth(chip.Text, chip.Icon);
            right -= cw;
            ImGui.SetCursorScreenPos(new Vector2(right, start.Y + 4 * s));
            Ui.Chip(chip.Text, chip.Color, chip.Icon);
            right -= 6 * s;
        }
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + h));
        ImGui.Dummy(new Vector2(width, 0));
    }

    /// <summary>"Collectability 55, 65 or 75. Up to 1,670 gil and 180 purple crafters' scrips (162 orange at level 100)."</summary>
    private static string RewardLine(DeliveryRequest r)
    {
        var item = r.Item;
        var text = $"Collectability {item.Low}, {item.Mid} or {item.High}.";
        if (r.Reward is not { } pay) return text;
        var times = pay.BonusMultiplier > 0 ? pay.BonusMultiplier / 100.0 : 1.0;
        int Top(int[] values) => (int)Math.Floor(values[2] * times);
        text += $" Up to {Top(pay.Gil):N0} gil and {Top(pay.First):N0} {ScripName(pay.FirstCurrency)}";
        if (pay.SecondCurrency != 0 && Top(pay.Second) > 0) text += $" ({Top(pay.Second):N0} {ScripName(pay.SecondCurrency).Replace(" scrips", "")} at the level cap)";
        return text + ".";
    }

    private static string ScripName(byte special) => special switch
    {
        2 => "purple crafters' scrips",
        4 => "purple gatherers' scrips",
        6 => "orange crafters' scrips",
        7 => "orange gatherers' scrips",
        _ => "scrips",
    };

    private static (string, Vector4, FontAwesomeIcon) KindChip(DeliveryCandidate item) => item.Kind switch
    {
        DeliveryKind.Craft => ("Crafted", Palette.Accent, FontAwesomeIcon.Hammer),
        DeliveryKind.Gather => ("Gathered", Palette.SupplyColor(Supply.Gather), FontAwesomeIcon.Leaf),
        _ => (item.Spearfishing ? "Spearfishing" : "Fish", Palette.SupplyColor(Supply.Fish), FontAwesomeIcon.Fish),
    };

    private static string KindWord(DeliveryKind kind) => kind switch
    {
        DeliveryKind.Craft => "crafted",
        DeliveryKind.Gather => "gathered",
        _ => "fish",
    };

    /// <summary>Whether the requests worked out match what the game's delivery window showed this week.</summary>
    private static void DrawCheck(ClientWeek week)
    {
        var s = Ui.S;
        ImGui.Dummy(new Vector2(0, 2 * s));
        switch (Deliver.Matches(week.Client.Id))
        {
            case true:
                Ui.Chip("Matches the game", Palette.Accent, FontAwesomeIcon.Check);
                break;
            case false:
                Ui.Chip("Differs from the game", Palette.Danger, FontAwesomeIcon.ExclamationTriangle);
                Ui.Wrapped("The client's delivery window showed other items. A bug report (Settings, General) helps fix this.", Palette.Danger);
                break;
            default:
                Ui.Wrapped($"Worked out from the week's data. Opening {week.Client.Name}'s delivery window in the game checks them.", Palette.TextMuted);
                break;
        }
    }

    // ------------------------------------------------------------------ what's ahead

    private void DrawAhead(GameCatalog catalog, ClientWeek week)
    {
        var s = Ui.S;
        var c = week.Client;
        ImGui.Dummy(new Vector2(0, 6 * s));
        using (Ui.F.Strong.Push()) Ui.Text("What's ahead");

        if (week.Rank is > 0 and < DeliveryClient.TopLevel)
        {
            var more = week.ToNextLevel;
            Ui.Wrapped(more > 0
                ? $"{more} more {(more == 1 ? "delivery" : "deliveries")} to level {week.Rank + 1} ({week.Satisfaction} of {week.Required} satisfaction)."
                : $"Level {week.Rank + 1} is next.", Palette.TextMuted);
            if (c.Level(week.Rank) is { RankUpItems.Count: > 0 } level)
            {
                Ui.Muted("For completing this level:");
                ImGui.SameLine(0, 8 * s);
                Icons(catalog, level.RankUpItems.Select(i => (i.ItemId, i.Count, i.Hq)));
            }
        }
        else if (week.Maxed)
        {
            Ui.Wrapped($"Top level: the requests change every week, and bonus requests can come up.{(c.CanGlamour ? $" {c.Name} can be glamoured here." : "")}", Palette.TextMuted);
        }

        // Story quests still to do, with their rewards.
        foreach (var q in c.Quests.Where(q => !Deliver.QuestDone(q.QuestId) && (week.Rank == 0 || q.Level >= week.Rank)))
        {
            Ui.Muted($"Story quest at level {q.Level}: \"{q.Name}\"");
            if (q.Rewards.Count > 0)
            {
                ImGui.SameLine(0, 8 * s);
                Icons(catalog, q.Rewards.Select(id => (id, 1, false)));
            }
        }

        // The two achievement series' last steps, each with a title.
        if (c.Achievements.Rank != 0) DrawAchievement(c.Achievements.Rank + 4, "reach the top level");
        if (c.Achievements.Deliveries != 0) DrawAchievement(c.Achievements.Deliveries + 5, "150 deliveries");
    }

    /// <summary>A locked client: only the story quests and their rewards, as something to look forward to.</summary>
    private static void DrawUnlockAhead(GameCatalog catalog, ClientWeek week)
    {
        var s = Ui.S;
        var quests = week.Client.Quests.Where(q => q.Rewards.Count > 0).ToList();
        if (quests.Count == 0) return;
        ImGui.Dummy(new Vector2(0, 6 * s));
        using (Ui.F.Strong.Push()) Ui.Text("What's ahead");
        foreach (var q in quests)
        {
            Ui.Muted($"Story quest at level {q.Level}: \"{q.Name}\"");
            ImGui.SameLine(0, 8 * s);
            Icons(catalog, q.Rewards.Select(id => (id, 1, false)));
        }
    }

    private static void DrawAchievement(uint id, string what)
    {
        var row = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Achievement>().GetRowOrDefault(id);
        if (row == null) return;
        var name = row.Value.Name.ExtractText();
        var title = row.Value.Title.ValueNullable?.Masculine.ExtractText();
        var done = Deliver.AchievementDone(id);
        var state = done switch { true => "done", false => "not yet", _ => "open the Achievements window once to see" };
        Ui.Muted($"Achievement \"{name}\" ({what}{(title is { Length: > 0 } ? $", title \"{title}\"" : "")}): {state}");
    }

    /// <summary>Item icons in a row, each with its name (and count) on hover.</summary>
    private static void Icons(GameCatalog catalog, IEnumerable<(uint ItemId, int Count, bool Hq)> items)
    {
        var s = Ui.S;
        var first = true;
        foreach (var (id, count, hq) in items)
        {
            if (!first) ImGui.SameLine(0, 4 * s);
            first = false;
            Ui.ItemIcon(catalog.ItemIcon(id), 22, hq);
            if (ImGui.IsItemHovered()) Ui.ShowTooltip($"{catalog.ItemName(id)}{(hq ? " (HQ)" : "")}{(count > 1 ? $" x{count}" : "")}");
        }
    }

    private static string Until(DateTime when)
    {
        var left = when - DateTime.Now;
        if (left <= TimeSpan.Zero) return "a moment";
        if (left.TotalDays >= 1) return $"{(int)left.TotalDays} {((int)left.TotalDays == 1 ? "day" : "days")} {left.Hours} h";
        if (left.TotalHours >= 1) return $"{(int)left.TotalHours} h {left.Minutes} min";
        return $"{Math.Max(1, left.Minutes)} min";
    }
}
