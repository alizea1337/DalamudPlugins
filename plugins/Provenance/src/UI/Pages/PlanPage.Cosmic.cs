using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Provenance.Core.Cosmic;
using Provenance.Data;
using Provenance.UI.Theme;

namespace Provenance.UI.Pages;

/// <summary>The Cosmic Exploration plan: per ticked crafter, the mission it takes and why, the pace, and the food suggestion.</summary>
internal sealed partial class PlanPage
{
    private readonly HashSet<uint> openSkipped = new();

    /// <summary>Shown when crafters are ticked for Cosmic Exploration and the order is empty, or while the loop runs.</summary>
    private bool ShowsCosmic => Config.Cosmic.Classes.Any(c => c is >= 8 and <= 15) && (Config.Order.IsEmpty || Plugin.CosmicLoop.Active);

    private void DrawCosmic(GameCatalog catalog)
    {
        var s = Ui.S;
        var w = MathF.Min(ImGui.GetContentRegionAvail().X, 760 * s);
        var plan = Plugin.CosmicPlanner.Plan(hints: true);
        var cs = Config.Cosmic;
        Ui.Heading("Cosmic Exploration", plan.Planet is { } p ? $"On {p.Name()}: {GoalLine(cs)}." : null);
        ImGui.Dummy(new Vector2(0, 2 * s));
        if (plan.Problem != null)
        {
            Ui.BeginCard(Palette.WithAlpha(Palette.Warn, 0.08f), width: w);
            Ui.Wrapped(plan.Problem, Palette.Warn);
            Ui.EndCard();
            return;
        }

        // The pace and the time to the goal.
        Ui.BeginCard(width: w);
        if (plan.Best is { Ranking.Best: { } top } best)
        {
            using (Ui.F.Strong.Push()) Ui.Text($"About {plan.CreditsPerHour:N0} Cosmocredits an hour");
            var line = $"with {CosmicPlanner.JobName(best.ClassJob)} on {top.Mission.Label}";
            if (plan.ToGoal is { } t) line += plan.CreditsNeeded > 0 ? $", so about {CosmicPage.Duration(t)} for the {plan.CreditsNeeded:N0} still to earn" : $", for {CosmicPage.Duration(t)}";
            Ui.Wrapped(line + ".", Palette.TextMuted);
            using (Ui.F.Small.Push())
                Ui.Wrapped("Estimates until the missions have been done a few times; after that they go by what runs of them paid and took.", Palette.TextFaint);
        }
        else Ui.Wrapped("No ticked crafter has a mission it can do yet; see below.", Palette.TextMuted);
        if (!plan.RanksKnown)
            using (Ui.F.Small.Push())
                Ui.Wrapped("Which mission classes you've opened is read once you're on a planet; until then it goes by level.", Palette.TextFaint);
        if (cs.UseConsumables)
        {
            ImGui.Dummy(new Vector2(0, 2 * s));
            Ui.Icon(FontAwesomeIcon.Utensils, Palette.Accent, small: true);
            ImGui.SameLine(0, 8 * s);
            Ui.Text("Using food and medicine from your bags while they last.", Palette.Text);
            ImGui.SameLine(0, 10 * s);
            if (Ui.LinkButton("Stop using them", Palette.TextMuted, null, "Judge and craft without food and medicine again.", "##noconsumables"))
            {
                cs.UseConsumables = false;
                Config.Save();
            }
        }
        Ui.EndCard();

        foreach (var c in plan.Classes) DrawCosmicClass(c, w);
    }

    private void DrawCosmicClass(CosmicClassPlan c, float w)
    {
        var s = Ui.S;
        var name = CosmicPlanner.JobName(c.ClassJob);
        Ui.BeginCard(width: w);
        Ui.Chip(c.Level > 0 ? $"{name}, level {c.Level}" : name, Palette.ClassJobColor(c.ClassJob), job: c.ClassJob);
        if (c.Problem != null)
        {
            Ui.Wrapped(c.Problem, Palette.Warn);
            Ui.EndCard();
            return;
        }
        if (c.Ranking.Best is { } best)
        {
            var tier = best.Judgement.Tier;
            Ui.Text(best.Mission.Label, Palette.Text);
            ImGui.SameLine(0, 10 * s);
            Ui.Chip(tier.ToString(), tier == Tier.Gold ? Palette.Warn : Palette.TextMuted);
            Ui.Wrapped($"About {best.CreditsPerHour:N0} Cosmocredits an hour ({best.Credits:N0} a mission). {Capital(best.Judgement.Reason)}.", Palette.TextMuted);
            if (c.Ranking.SilverOnly)
                using (Ui.F.Small.Push()) Ui.Wrapped("No mission reaches gold reliably with this gear, so the loop takes silver ones for now.", Palette.Warn);
        }
        else if (c.Ranking.Skipped.Any(x => x.Reason.StartsWith("Still working")))
            Ui.Wrapped("Working out the best rotations…", Palette.TextMuted);
        else
            Ui.Wrapped("No mission it can open reaches silver with this gear.", Palette.Warn);

        if (Plugin.CosmicPlanner.Hint(c.ClassJob) is { } hint) DrawHint(hint);

        // The missions it won't take, with why: folded away.
        var skipped = c.Ranking.Skipped.Where(x => !x.Reason.StartsWith("Still working")).ToList();
        if (skipped.Count > 0)
        {
            var open = openSkipped.Contains(c.ClassJob);
            if (Ui.LinkButton(open ? "Hide the ones it skips" : $"Why not the others ({skipped.Count})", Palette.TextMuted,
                    open ? FontAwesomeIcon.ChevronUp : FontAwesomeIcon.ChevronDown, null, "##skipped" + c.ClassJob))
            {
                if (open) openSkipped.Remove(c.ClassJob);
                else openSkipped.Add(c.ClassJob);
            }
            if (open)
                foreach (var x in skipped.Take(30))
                    using (Ui.F.Small.Push()) Ui.Wrapped($"{x.Mission.Label}: {x.Reason}.", Palette.TextMuted);
        }
        Ui.EndCard();
    }

    /// <summary>"With 4× Rroneek Steak and 8× Cunning Craftsman's Draught, Weaver reliably golds B: … instead: about 40 min sooner."</summary>
    private void DrawHint(ConsumableHint hint)
    {
        var s = Ui.S;
        var catalog = Plans.Catalog!;
        ImGui.Dummy(new Vector2(0, 2 * s));
        var things = new List<string>();
        if (hint.FoodCount > 0) things.Add($"{hint.FoodCount}× {catalog.ItemName(hint.Food)}{(hint.FoodHq ? " (HQ)" : "")}");
        if (hint.MedicineCount > 0) things.Add($"{hint.MedicineCount}× {catalog.ItemName(hint.Medicine)}{(hint.MedicineHq ? " (HQ)" : "")}");
        var gain = hint.MinutesSaved is > 1 and var m ? $"about {CosmicPage.Duration(TimeSpan.FromMinutes(m))} sooner to the goal"
            : $"about {hint.ExtraPerHour:N0} more Cosmocredits an hour";
        Ui.Icon(FontAwesomeIcon.Lightbulb, Palette.Accent, small: true);
        ImGui.SameLine(0, 8 * s);
        Ui.Wrapped($"With {string.Join(" and ", things)}, {CosmicPlanner.JobName(hint.ClassJob)} reliably reaches {hint.With.Judgement.Tier.ToString().ToLowerInvariant()} on "
                   + $"{hint.With.Mission.Label} instead of {hint.Without.Mission.Label}: {gain}. When they run out, it goes back to the missions it reaches without them.", Palette.Text);
        if (Ui.Button("Use them", Ui.Kind.Secondary, FontAwesomeIcon.Utensils, id: "##useconsumables" + hint.ClassJob,
                tooltip: "Eats and takes medicine as set under Settings, Crafting, from your bags, while they last."))
        {
            Config.Cosmic.UseConsumables = true;
            Config.Save();
        }
    }

    private static string GoalLine(CosmicSettings s) => s.Goal switch
    {
        CosmicGoal.Cosmocredits => $"up to {s.CreditsTarget:N0} Cosmocredits",
        CosmicGoal.ShoppingList => "the shopping list",
        _ => "until stopped",
    } + (s.TimeLimitMinutes > 0 ? $", for at most {CosmicPage.Duration(TimeSpan.FromMinutes(s.TimeLimitMinutes))}" : "");

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
