using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Provenance.Core.Cosmic;
using Provenance.Data;
using Provenance.Integrations;
using Provenance.UI.Theme;

namespace Provenance.UI.Pages;

/// <summary>
/// Cosmic Exploration (Expeditions): the planet and the crafters the loop works with, the goal and time limit, the
/// extras it may take on, the shopping list, and the loop's progress. The plan itself is on the Plan page.
/// </summary>
internal sealed class CosmicPage : Page
{
    private string? startProblem;

    public CosmicPage(Plugin plugin) : base(plugin) { }

    public override string Title => "Cosmic Exploration";
    public override FontAwesomeIcon Icon => FontAwesomeIcon.Rocket;

    private Run.Cosmic.CosmicRun Loop => Plugin.CosmicLoop;
    private CosmicSettings S => Config.Cosmic;

    public override (string Text, Vector4 Color)? Status
        => Loop.Active ? (Loop.Status, Palette.Accent)
            : Loop.Problem != null ? ("The loop stopped with a problem; see below", Palette.Warn)
            : ("Changes are saved as you make them", Palette.TextFaint);

    public override (Vector4 Color, bool Pulse)? Live => Loop.Active ? (Palette.Accent, true) : null;

    public override IEnumerable<FooterAction> Actions
    {
        get
        {
            if (Loop.Active)
            {
                yield return new FooterAction("Stop", Ui.Kind.Danger, FontAwesomeIcon.Stop, () => Loop.Stop(),
                    Tooltip: "Stops once the item being crafted is finished. A mission under way stays as it is.");
                yield break;
            }
            yield return new FooterAction("See the plan", Ui.Kind.Secondary, FontAwesomeIcon.ListUl, Go<PlanPage>,
                Tooltip: "Which mission each crafter would take, what it pays an hour, and how long the goal takes.");
            yield return new FooterAction("Start", Ui.Kind.Primary, FontAwesomeIcon.Play, () => startProblem = Loop.Start());
        }
    }

    public override void Draw()
    {
        var s = Ui.S;
        var w = MathF.Min(ImGui.GetContentRegionAvail().X, 760 * s);
        Ui.Heading("Cosmic Exploration", "Stellar missions on the planets. The loop takes the best-paying mission each ticked crafter reliably golds with "
                                         + "its gear (silver when none does), crafts it and reports it, until the goal is met.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        if (startProblem != null && !Loop.Active)
        {
            Ui.Icon(FontAwesomeIcon.ExclamationTriangle, Palette.Warn, small: true);
            ImGui.SameLine(0, 8 * s);
            Ui.Wrapped("Can't start: " + startProblem, Palette.Warn, w);
            ImGui.Dummy(new Vector2(0, 2 * s));
        }
        if (Loop.Active || Loop.Lines.Count > 0) DrawProgress(w);
        DrawWhere(w);
        DrawGoal(w);
        DrawExtras(w);
        DrawShopping(w);
    }

    // ------------------------------------------------------------------ progress

    private void DrawProgress(float w)
    {
        var s = Ui.S;
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text(Loop.Active ? "Under way" : "Last run");
        if (Loop.Active) Ui.Wrapped(Loop.Status, Palette.Text);
        if (Loop.Mission is { } m)
        {
            Ui.Chip(m.Label, Palette.Accent, FontAwesomeIcon.Rocket);
            if (Loop.CraftStatus.Length > 0) { ImGui.SameLine(0, 8 * s); Ui.Muted(Loop.CraftStatus); }
        }
        var parts = new List<string> { $"{Loop.Missions} {(Loop.Missions == 1 ? "mission" : "missions")}" };
        if (Loop.Golds > 0) parts.Add($"{Loop.Golds} gold");
        if (Loop.Active) parts.Add($"+{Loop.Earned:N0} Cosmocredits");
        if (Loop.Active) parts.Add(Duration(Loop.Elapsed));
        using (Ui.F.Small.Push()) Ui.Muted(string.Join(", ", parts));
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

    // ------------------------------------------------------------------ planet and crafters

    private List<DropdownItem>? planetItems;

    private void DrawWhere(float w)
    {
        var s = Ui.S;
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("Planet");
        var here = Cosmic.CurrentPlanet;
        var shown = S.Planet is >= 0 and <= 3 ? ((Planet)S.Planet).Name() : "Where you are";
        var detail = S.Planet < 0 ? CosmicPlanner.TargetPlanet(S) is { } t ? t.Name() : null : null;
        if (Ui.DropdownField("##planet", shown, "Pick a planet", 320 * s, FontAwesomeIcon.GlobeAmericas, detail: detail))
        {
            planetItems = new List<DropdownItem> { new("Where you are", Sub: "The planet you're on, else the newest you've opened") };
            foreach (var p in Planets.All)
            {
                var open = Cosmic.Unlocked(p);
                planetItems.Add(new DropdownItem(p.Name(), here == p ? "you're here" : null,
                    Sub: open ? null : $"Opens with the quest \"{Cosmic.UnlockQuestName(p)}\"", Disabled: !open,
                    Tooltip: open ? null : $"{p.Name()} isn't open yet: it opens with the quest \"{Cosmic.UnlockQuestName(p)}\"."));
            }
        }
        var pick = Ui.DropdownList("##planet", planetItems, S.Planet + 1, "");
        if (pick >= 0) { S.Planet = pick - 1; Config.Save(); }
        // What's at hand against the caps: Cosmocredits everywhere, the planet's own credits, and dronebits where there are any.
        if (CosmicPlanner.TargetPlanet(S) is { } at)
        {
            var parts = new List<string>
            {
                $"{Cosmic.Credits:N0} of {Planets.CosmocreditCap:N0} Cosmocredits",
                $"{GameState.CurrencyCount(at.CreditItem()):N0} of {Planets.PlanetCreditCap:N0} {at.Name()} credits",
            };
            if (at.Dronebit() is var bit and not 0) parts.Add($"{GameState.CurrencyCount(bit):N0} of {Planets.DronebitCap:N0} dronebits");
            using (Ui.F.Small.Push()) Ui.Wrapped(string.Join(", ", parts) + ".", Palette.TextMuted);
        }

        ImGui.Dummy(new Vector2(0, 4 * s));
        using (Ui.F.Strong.Push()) Ui.Text("Crafters");
        using (Ui.F.Small.Push()) Ui.Wrapped("The loop switches gearsets between the ticked ones, to whichever has the best mission on the board.", Palette.TextMuted);
        ImGui.Dummy(new Vector2(0, 2 * s));
        var start = ImGui.GetCursorScreenPos();
        var inner = Ui.CardInnerWidth;
        var x = 0f;
        var y = 0f;
        var gap = 8 * s;
        var rowH = 30 * s + gap;
        void Place(float chipW)
        {
            if (x > 0 && x + chipW > inner) { x = 0; y += rowH; }
            ImGui.SetCursorScreenPos(start + new Vector2(x, y));
            x += chipW + gap;
        }
        for (uint job = 8; job <= 15; job++)
        {
            var name = CosmicPlanner.JobName(job);
            Place(Ui.ToggleChipWidth(name, job));
            var on = S.Classes.Contains(job);
            if (Ui.ToggleChip("##cj" + job, name, Palette.ClassJobColor(job), on, job))
            {
                if (on) S.Classes.Remove(job);
                else S.Classes.Add(job);
                Config.Save();
            }
        }
        // Gatherers and the fisher: two-class missions for now (Sinus Ardorum, with the crafter they pair with).
        for (uint job = 16; job <= 18; job++)
        {
            var name = CosmicPlanner.JobName(job);
            Place(Ui.ToggleChipWidth(name, job));
            var on = S.Classes.Contains(job);
            if (Ui.ToggleChip("##cj" + job, name, Palette.ClassJobColor(job), on, job))
            {
                if (on) S.Classes.Remove(job);
                else S.Classes.Add(job);
                Config.Save();
            }
            Ui.Tooltip($"For now {name} takes two-class missions on Sinus Ardorum: it {(job == 18 ? "fishes" : "gathers")} the material and a crafter makes the item. "
                       + $"Needs \"Two-class missions\" on under Extras and the crafter it pairs with ticked too. Its own missions come in a later version.");
        }
        ImGui.SetCursorScreenPos(start + new Vector2(0, y + 30 * s));
        ImGui.Dummy(new Vector2(inner, 0));
        ImGui.Dummy(new Vector2(0, 2 * s));
        var noEx = S.NoExMissions;
        if (Ui.Checkbox("Leave out EX and EX+ missions", ref noEx,
                "Takes nothing above class A, even when EX or EX+ missions would reliably reach gold. Crafters that haven't opened class A yet take their own class as usual. "
                + "Sinus Ardorum's two-class missions are all EX or EX+, so they're left out too."))
        {
            S.NoExMissions = noEx;
            Config.Save();
        }
        Ui.EndCard();
    }

    // ------------------------------------------------------------------ goal

    private static readonly string[] Goals = { "Cosmocredits", "Shopping list", "Until stopped" };

    private void DrawGoal(float w)
    {
        var s = Ui.S;
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("Goal");
        var goal = (int)S.Goal;
        if (Ui.Segmented("##cosmicgoal", Goals, ref goal, 440 * s)) { S.Goal = (CosmicGoal)goal; Config.Save(); }
        ImGui.Dummy(new Vector2(0, 2 * s));
        switch (S.Goal)
        {
            case CosmicGoal.Cosmocredits:
            {
                Ui.Text("Stop at");
                ImGui.SameLine(0, 10 * s);
                var target = S.CreditsTarget;
                ImGui.SetNextItemWidth(150 * s);
                if (ImGui.InputInt("##creditstarget", ref target, 500, 5000)) { S.CreditsTarget = Math.Clamp(target, 100, Planets.CosmocreditCap); Config.Save(); }
                ImGui.SameLine(0, 10 * s);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 6 * s);
                Ui.Muted($"Cosmocredits (you have {Cosmic.Credits:N0}; they cap at {Planets.CosmocreditCap:N0})");
                break;
            }
            case CosmicGoal.ShoppingList:
                Ui.Wrapped("Earns what the shopping list below costs, buys it from the Cosmocredit trader, and stops.", Palette.TextMuted);
                break;
            default:
                Ui.Wrapped("Keeps going until you stop it (or the time limit below).", Palette.TextMuted);
                break;
        }
        ImGui.Dummy(new Vector2(0, 2 * s));
        var limited = S.TimeLimitMinutes > 0;
        if (Ui.Checkbox("Stop after a set time", ref limited, "Whatever the goal, the loop stops once this much time has gone by, after the mission under way."))
        {
            S.TimeLimitMinutes = limited ? 60 : 0;
            Config.Save();
        }
        if (limited)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 30 * s);
            var minutes = S.TimeLimitMinutes;
            ImGui.SetNextItemWidth(150 * s);
            if (ImGui.InputInt("##timelimit", ref minutes, 15, 60)) { S.TimeLimitMinutes = Math.Clamp(minutes, 5, 24 * 60); Config.Save(); }
            ImGui.SameLine(0, 10 * s);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 6 * s);
            Ui.Muted("minutes");
        }
        Ui.EndCard();
    }

    // ------------------------------------------------------------------ extras

    /// <param name="Ready">Built in this version; the others say when they come.</param>
    private sealed record Extra(CosmicExtra Flag, string Label, string Tooltip, bool Ready);

    private static readonly Extra[] Extras =
    {
        new(CosmicExtra.ChainedMissions, "Chained missions",
            "Missions that open once the one before them in their chain is done with gold. Taken when they're on the board and pay best.", true),
        new(CosmicExtra.WeatherTimeMissions, "Weather and time missions",
            "Missions that are only on the board in certain weather or at certain Eorzea times. Taken while they're there, never waited for.", true),
        new(CosmicExtra.MasterMissions, "Master missions (Auxesia)",
            "Tool Mastery missions on Auxesia: long crafts with a high score to reach. They pay Exploration Tokens.", true),
        new(CosmicExtra.MateriaAtCap, "Buy materia at the Cosmocredit cap",
            "When Cosmocredits reach the 30,000 cap (within a mission's worth) and the shopping list is empty, buys the top crafter and gatherer materia, one of each in turn, until 5,000 are left. "
            + "Off, the loop keeps going and what's beyond the cap is lost.", true),
        new(CosmicExtra.CriticalMissions, "Critical missions",
            "A red alert's missions, with big rewards. They come with gathering missions in the next version.", false),
        new(CosmicExtra.RedAlerts, "Red alerts",
            "Going to a red alert's sites when one starts on the planet. Comes with gathering missions in the next version.", false),
        new(CosmicExtra.TwoClassMissions, "Two-class missions",
            "Sinus Ardorum missions where a miner, botanist or fisher gathers the material and a crafter makes the item, in rounds until gold. "
            + "Tick both: the gatherer and the crafter it pairs with.", true),
        new(CosmicExtra.CosmicFortunes, "Cosmic Fortunes",
            "Draws cosmic fortunes with the planet's credits (1,000 a spin; they buy nothing else): when they near the 10,000 cap, and before the "
            + "loop ends. Big Bang tickets are drawn too. It picks the wheel with the better prizes (unlockables, then rarer items).", true),
        new(CosmicExtra.MechOps, "Mech ops",
            "Taking part in mech ops. Comes in a later version.", false),
    };

    private List<DropdownItem>? extraItems;

    private void DrawExtras(float w)
    {
        var s = Ui.S;
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("Extras");
        using (Ui.F.Small.Push()) Ui.Wrapped("What the loop may take on besides basic missions. Hover one for what it does.", Palette.TextMuted);
        ImGui.Dummy(new Vector2(0, 2 * s));
        var on = Extras.Count(e => e.Ready && S.Has(e.Flag));
        var ready = Extras.Count(e => e.Ready);
        if (Ui.DropdownField("##extras", $"{on} of {ready} on", "Pick extras", 320 * s, FontAwesomeIcon.Star))
            extraItems = Extras.Select(e => new DropdownItem(e.Label, e.Ready ? null : "later", Disabled: !e.Ready, Tooltip: e.Tooltip)).ToList();
        var pick = Ui.DropdownList("##extras", extraItems, -1, "", 380 * s, i => S.Has(Extras[i].Flag));
        if (pick >= 0)
        {
            S.Extras ^= Extras[pick].Flag;
            Config.Save();
        }
        var names = Extras.Where(e => e.Ready && S.Has(e.Flag)).Select(e => e.Label).ToList();
        if (names.Count > 0)
            using (Ui.F.Small.Push()) Ui.Wrapped("On: " + string.Join(", ", names) + ".", Palette.TextMuted);
        Ui.EndCard();
    }

    // ------------------------------------------------------------------ shopping list

    private List<DropdownItem>? shopItems;
    private List<CosmicShopItem> shopList = new();

    private void DrawShopping(float w)
    {
        var s = Ui.S;
        if (Plans.Catalog?.Cosmic is not { } data) return;
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("Shopping list");
        using (Ui.F.Small.Push())
            Ui.Wrapped("Things from the Cosmocredit trader. With the shopping list as the goal the loop earns what they cost and buys them; "
                       + "at the Cosmocredit cap it buys them first, whatever the goal.", Palette.TextMuted);
        ImGui.Dummy(new Vector2(0, 2 * s));
        CosmicWish? remove = null;
        foreach (var wish in S.Shopping)
        {
            var item = data.Shop.FirstOrDefault(x => x.ItemId == wish.ItemId);
            if (item == null) continue;
            Ui.ItemIcon(item.Icon, 28);
            ImGui.SameLine(0, 10 * s);
            var rowY = ImGui.GetCursorPosY();
            ImGui.SetCursorPosY(rowY + 5 * s);
            Ui.Text(item.Name);
            ImGui.SameLine(0, 12 * s);
            ImGui.SetCursorPosY(rowY + 1 * s);
            var count = wish.Count;
            ImGui.SetNextItemWidth(110 * s);
            if (ImGui.InputInt("##wish" + wish.ItemId, ref count, 1, 10)) { wish.Count = Math.Clamp(count, 1, 999); Config.Save(); }
            ImGui.SameLine(0, 10 * s);
            ImGui.SetCursorPosY(rowY + 5 * s);
            Ui.Muted($"{wish.Count * item.Cost:N0} {CurrencyName(item)}");
            ImGui.SameLine(0, 6 * s);
            ImGui.SetCursorPosY(rowY);
            if (Ui.IconButton("##unwish" + wish.ItemId, FontAwesomeIcon.Times, "Take it off the list", Ui.Kind.Danger)) remove = wish;
        }
        if (remove != null) { S.Shopping.Remove(remove); Config.Save(); }
        // Saved items the trader no longer sells: say so, so they can go.
        foreach (var gone in S.Shopping.Where(w => data.Shop.All(x => x.ItemId != w.ItemId)).ToList())
        {
            using (Ui.F.Small.Push()) Ui.Wrapped($"{Plans.Catalog?.ItemName(gone.ItemId) ?? "An item"} isn't sold by the trader any more; the loop takes it off the list.", Palette.Warn);
        }

        if (Ui.DropdownField("##addshop", null, "Add something from the Cosmocredit trader", 400 * s, FontAwesomeIcon.Plus))
        {
            shopList = data.Shop.OrderBy(x => x.ShopId == 1770977 ? 0 : 1).ThenBy(x => x.Index).ToList();
            shopItems = shopList.Select(x => new DropdownItem(x.Name, x.Dronebits ? $"{x.Cost:N0} dronebits" : $"{x.Cost:N0}", GameIcon: x.Icon,
                Tooltip: x.Dronebits ? $"From Kaede on {x.Planet?.Name()}, for its dronebits; it only works on {x.Planet?.Name()}." : null)).ToList();
        }
        var pick = Ui.DropdownList("##addshop", shopItems, -1, "The trader's stock couldn't be read.", 420 * s);
        if (pick >= 0)
        {
            var item = shopList[pick];
            var existing = S.Shopping.FirstOrDefault(x => x.ItemId == item.ItemId);
            if (existing != null) existing.Count++;
            else S.Shopping.Add(new CosmicWish { ItemId = item.ItemId, Count = 1 });
            Config.Save();
        }
        if (S.Shopping.Count > 0)
        {
            var cost = CosmicPlanner.ShoppingCost(S, data);
            var have = Cosmic.Credits;
            if (cost > 0)
                using (Ui.F.Small.Push())
                    Ui.Wrapped(cost <= have ? $"Costs {cost:N0} Cosmocredits; you have {have:N0}, enough for all of it."
                        : $"Costs {cost:N0} Cosmocredits; you have {have:N0}, so {cost - have:N0} to earn.", Palette.TextMuted);
            foreach (var p in new[] { Planet.Oizys, Planet.Auxesia })
            {
                var bits = CosmicPlanner.DronebitCost(S, data, p);
                if (bits <= 0) continue;
                var got = GameState.CurrencyCount(p.Dronebit());
                using (Ui.F.Small.Push())
                    Ui.Wrapped($"{p.Name()} Drone Modules cost {bits:N0} dronebits; you have {got:N0}."
                               + (bits > Planets.DronebitCap ? $" That's more than the {Planets.DronebitCap:N0} dronebits can hold at once, so they're bought in parts." : ""),
                        Palette.TextMuted);
            }
        }
        Ui.EndCard();
    }

    private string CurrencyName(CosmicShopItem item)
        => item.Dronebits ? Plans.Catalog?.ItemNamePlural(item.Currency) ?? "dronebits" : "Cosmocredits";

    /// <summary>"1 h 20 min", "45 min".</summary>
    public static string Duration(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))} min";
}
