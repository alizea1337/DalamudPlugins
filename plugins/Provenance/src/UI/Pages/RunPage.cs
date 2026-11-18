using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Provenance.Core.Planning;
using Provenance.Integrations;
using Provenance.Run;
using Provenance.UI.Theme;

namespace Provenance.UI.Pages;

/// <summary>The run: what it is doing now, how far along it is, and what happened so far.</summary>
internal sealed class RunPage : Page
{
    public RunPage(Plugin plugin) : base(plugin) { }

    public override string Title => "Run";

    private RunEngine Runs => Plugin.Runs;

    public override (int Count, Vector4 Color)? Badge => null;

    public override (string Text, Vector4 Color)? Status => Runs.Phase switch
    {
        RunPhase.Gathering => (Runs.Headline, Palette.Accent),
        RunPhase.Pausing => ("Pausing after this node", Palette.Warn),
        RunPhase.Paused => ("Paused", Palette.Warn),
        RunPhase.Finished => ("Finished " + Ago(Runs.EndedAt), Palette.Accent),
        RunPhase.Stopped => ("Stopped " + Ago(Runs.EndedAt), Palette.Danger),
        _ => ("Not running", Palette.TextFaint),
    };

    public override IEnumerable<FooterAction> Actions
    {
        get
        {
            switch (Runs.Phase)
            {
                case RunPhase.Gathering:
                    yield return new FooterAction("Stop", Ui.Kind.Danger, FontAwesomeIcon.Stop, () => Runs.Stop());
                    yield return new FooterAction("Pause", Ui.Kind.Secondary, FontAwesomeIcon.Pause, Runs.Pause, Tooltip: "Finishes the node being gathered, then pauses.");
                    break;
                case RunPhase.Pausing:
                    yield return new FooterAction("Stop", Ui.Kind.Danger, FontAwesomeIcon.Stop, () => Runs.Stop());
                    yield return new FooterAction("Keep going", Ui.Kind.Secondary, FontAwesomeIcon.Play, Runs.Resume);
                    break;
                case RunPhase.Paused:
                    yield return new FooterAction("Stop", Ui.Kind.Danger, FontAwesomeIcon.Stop, () => Runs.Stop());
                    yield return new FooterAction("Resume", Ui.Kind.Primary, FontAwesomeIcon.Play, Runs.Resume);
                    break;
                default:
                    var why = Runs.StartBlocker();
                    yield return new FooterAction("Start", Ui.Kind.Primary, FontAwesomeIcon.Play, Runs.Start, Enabled: why == null, Tooltip: why);
                    break;
            }
        }
    }

    public override void Draw()
    {
        if (Runs.Phase == RunPhase.Idle) DrawPreview();
        else DrawRun();
    }

    // ------------------------------------------------------------------ live run

    private void DrawRun()
    {
        var s = Ui.S;
        var catalog = Plans.Catalog;
        var plan = Plans.Plan;
        var (color, icon) = Runs.Phase switch
        {
            RunPhase.Gathering => (Palette.Accent, FontAwesomeIcon.Leaf),
            RunPhase.Pausing or RunPhase.Paused => (Palette.Warn, FontAwesomeIcon.Pause),
            RunPhase.Finished => (Palette.Accent, FontAwesomeIcon.CheckCircle),
            _ => (Palette.Danger, FontAwesomeIcon.StopCircle),
        };

        // Now: what the run is doing
        Ui.BeginCard(Palette.WithAlpha(color, 0.07f));
        Ui.Icon(icon, color);
        ImGui.SameLine(0, 10 * s);
        using (Ui.F.Title.Push()) Ui.Text(Runs.Headline);
        if (Runs.Detail.Length > 0) Ui.Wrapped(Runs.Detail, Palette.TextMuted);
        var targets = Runs.Targets;
        var done = targets.Count(t => (plan[t.ItemId]?.Remaining ?? 0) <= 0);
        ImGui.Dummy(new Vector2(0, 2 * s));
        Ui.Bar("##runprogress", targets.Count == 0 ? 1 : done / (float)targets.Count, color, MathF.Min(Ui.CardInnerWidth, 420 * s), 8);
        Ui.Muted($"{done} of {targets.Count} items gathered{Elapsed()}");
        Ui.EndCard();

        // Items being gathered
        if (targets.Count > 0 && catalog != null)
        {
            Ui.BeginCard();
            Ui.Icon(FontAwesomeIcon.Leaf, Palette.SupplyColor(Supply.Gather));
            ImGui.SameLine(0, 10 * s);
            using (Ui.F.Strong.Push()) Ui.Text("Gathering");
            ImGui.SameLine(0, 10 * s);
            Ui.Muted("with GatherBuddy Reborn");
            ImGui.Dummy(new Vector2(0, 2 * s));
            foreach (var t in targets.OrderBy(t => (plan[t.ItemId]?.Remaining ?? 0) <= 0))
            {
                var line = plan[t.ItemId];
                var remaining = Math.Max(0, line?.Remaining ?? 0);
                var start = ImGui.GetCursorScreenPos();
                var width = Ui.CardInnerWidth;
                var h = 34 * s;
                var dl = ImGui.GetWindowDrawList();
                Ui.DrawItemIcon(dl, catalog.ItemIcon(t.ItemId), start + new Vector2(0, (h - 24 * s) / 2), 24 * s);
                var name = catalog.ItemName(t.ItemId);
                var ty = start.Y + (h - ImGui.GetTextLineHeight()) / 2;
                dl.AddText(new Vector2(start.X + 34 * s, ty), Palette.U32(remaining == 0 ? Palette.TextMuted : Palette.Text), name);
                if (t.Supply != Supply.Gather)
                {
                    ImGui.SetCursorScreenPos(new Vector2(start.X + 44 * s + ImGui.CalcTextSize(name).X, start.Y + (h - 20 * s) / 2));
                    Ui.Chip(Palette.SupplyLabel(t.Supply), Palette.SupplyColor(t.Supply));
                }
                var label = remaining == 0 ? "done" : $"{remaining:N0} left";
                using (Ui.F.Strong.Push())
                {
                    var lw = ImGui.CalcTextSize(label).X;
                    dl.AddText(new Vector2(start.X + width - lw, ty), Palette.U32(remaining == 0 ? Palette.Accent : Palette.Text), label);
                }
                var have = line?.FromStock ?? 0;
                var need = line?.Needed ?? 0;
                ImGui.SetCursorScreenPos(new Vector2(start.X + width - 110 * s - 80 * s, start.Y + (h - 6 * s) / 2));
                Ui.Bar("##rb" + t.ItemId, need == 0 ? 1 : have / (float)need, remaining == 0 ? Palette.Accent : Palette.SupplyColor(t.Supply), 110 * s);
                ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + h));
                ImGui.Dummy(new Vector2(width, 0));
            }
            Ui.EndCard();
        }

        // What happened
        if (Runs.Log.Count > 0)
        {
            Ui.BeginCard();
            using (Ui.F.Strong.Push()) Ui.Text("What happened");
            ImGui.Dummy(new Vector2(0, 2 * s));
            foreach (var e in Runs.Log.Take(20))
            {
                using (Ui.F.Small.Push()) Ui.Muted(e.Time.ToString("HH:mm"));
                ImGui.SameLine(56 * s);
                Ui.Wrapped(e.Text, e.Problem ? Palette.Warn : Palette.Text);
            }
            Ui.EndCard();
        }
    }

    private string Elapsed()
    {
        if (Runs.StartedAt is not { } start) return "";
        var span = (Runs.EndedAt ?? DateTime.Now) - start;
        return span.TotalMinutes < 1 ? ", under a minute so far"
            : Runs.EndedAt != null ? $", took {(int)span.TotalMinutes} min"
            : $", {(int)span.TotalMinutes} min so far";
    }

    private static string Ago(DateTime? at)
    {
        if (at is not { } t) return "";
        var span = DateTime.Now - t;
        return span.TotalMinutes < 1 ? "just now" : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago" : t.ToString("HH:mm");
    }

    // ------------------------------------------------------------------ preview

    private void DrawPreview()
    {
        var s = Ui.S;
        Ui.Heading("Run", "A run takes the order from start to finish in these steps and stops on its own when the order is done.");
        ImGui.Dummy(new Vector2(0, 4 * s));

        var why = Runs.StartBlocker();
        Ui.BeginCard(Palette.WithAlpha(why == null ? Palette.Accent : Palette.Warn, 0.07f));
        Ui.Icon(why == null ? FontAwesomeIcon.InfoCircle : FontAwesomeIcon.ExclamationTriangle, why == null ? Palette.Accent : Palette.Warn);
        ImGui.SameLine(0, 10 * s);
        Ui.Wrapped(why == null
            ? "This version runs the gathering step: Provenance hands its list to GatherBuddy Reborn and stops when everything is gathered. Buying, monster drops and crafting follow in the next milestones."
            : why, Palette.Text);
        Ui.EndCard();

        var plan = Plans.Plan;
        var gather = plan.Count(Supply.Gather) + plan.Count(Supply.GatherTimed) + plan.Count(Supply.Fish);
        var crafts = plan.CraftSteps.Sum(l => l.Crafts);
        var steps = new (FontAwesomeIcon Icon, string Title, string Detail, bool Active, bool Available)[]
        {
            (FontAwesomeIcon.Box, "Take items from retainers", "Opens your retainers at the nearest summoning bell and takes out what the order can use.", Config.Stock.Retainers, false),
            (FontAwesomeIcon.Store, "Buy from vendors", plan.Count(Supply.Vendor) > 0 ? $"{plan.Count(Supply.Vendor)} items for about {plan.VendorGil:N0} gil." : "Nothing to buy for this order.", plan.Count(Supply.Vendor) > 0, false),
            (FontAwesomeIcon.Paw, "Farm monster drops", plan.Count(Supply.Drop) > 0 ? $"{plan.Count(Supply.Drop)} items from monsters." : "No monster drops needed.", plan.Count(Supply.Drop) > 0, false),
            (FontAwesomeIcon.Leaf, "Gather", gather > 0 ? $"{gather} materials to gather or fish with GatherBuddy Reborn." : "Nothing to gather.", gather > 0, true),
            (FontAwesomeIcon.Hammer, "Craft", crafts > 0 ? $"{crafts} crafts. Crafting fills the waits between timed nodes." : "Nothing to craft.", crafts > 0, false),
        };

        for (var i = 0; i < steps.Length; i++)
        {
            var st = steps[i];
            var pos = ImGui.GetCursorScreenPos();
            var dl = ImGui.GetWindowDrawList();
            var c = pos + new Vector2(16 * s, 18 * s);
            if (i < steps.Length - 1)
                dl.AddLine(c + new Vector2(0, 16 * s), c + new Vector2(0, 58 * s), Palette.U32(Palette.Line), 1.5f * s);
            dl.AddCircleFilled(c, 16 * s, Palette.U32(st.Active ? Palette.Raised : Palette.Surface));
            using (Ui.F.Strong.Push())
            {
                var n = (i + 1).ToString();
                var ns = ImGui.CalcTextSize(n);
                dl.AddText(c - ns / 2, Palette.U32(st.Active ? Palette.Text : Palette.TextFaint), n);
            }
            ImGui.SetCursorScreenPos(pos + new Vector2(46 * s, 4 * s));
            ImGui.BeginGroup();
            using (Ui.F.Strong.Push()) Ui.Text(st.Title, st.Active ? Palette.Text : Palette.TextFaint);
            if (!st.Available && st.Active) { ImGui.SameLine(0, 8 * s); Ui.Chip("coming later", Palette.TextMuted); }
            Ui.Wrapped(st.Detail, Palette.TextMuted, 520 * s);
            ImGui.EndGroup();
            ImGui.SetCursorScreenPos(new Vector2(pos.X, ImGui.GetItemRectMax().Y + 14 * s));
        }

        // Travel tools, so movement and teleport can be tried before they are used in a run.
        ImGui.Dummy(new Vector2(0, 4 * s));
        using (Ui.F.Strong.Push()) Ui.Text("Travel tools");
        Ui.Wrapped("Type /prov tp followed by an aetheryte name to teleport with Provenance's own teleport, or place a map flag and type /prov flag to walk there with vnavmesh.", Palette.TextMuted);
        var vnav = !Vnav.Available ? ("vnavmesh not loaded", Palette.Danger)
            : Vnav.Ready ? ("vnavmesh ready", Palette.Accent)
            : Vnav.BuildProgress >= 0 ? ($"vnavmesh building {Vnav.BuildProgress:P0}", Palette.Warn)
            : ("vnavmesh loading", Palette.Warn);
        Ui.Chip(vnav.Item1, vnav.Item2);
        ImGui.SameLine(0, 6 * s);
        var tp = Teleporter.Blocked();
        Ui.Chip(tp == null ? "teleport available" : "teleport blocked: " + tp, tp == null ? Palette.Accent : Palette.Warn);
    }
}
