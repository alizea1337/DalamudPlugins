using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Provenance.Core.Catalog;
using Provenance.Core.Estimating;
using Provenance.Core.Planning;
using Provenance.Data;
using Provenance.Integrations;
using Provenance.UI.Theme;

namespace Provenance.UI.Pages;

/// <summary>
/// The order as totals: what is being made, then every material once, summed over the whole order and
/// grouped by how it is obtained (gather, fish, buy, monsters, craft first), with have/need for each.
/// </summary>
internal sealed partial class PlanPage : Page
{
    private const float RowHeight = 36f;

    private DateTime clearArmedUntil = DateTime.MinValue;
    private static OrderEstimate currentEstimate = OrderEstimate.Empty;

    /// <summary>", about 3 min" for a recipe's crafts in this order.</summary>
    private static string CraftTime(RecipeInfo recipe)
        => currentEstimate.Crafts.FirstOrDefault(c => c.Recipe.RecipeId == recipe.RecipeId) is { } c ? ", " + Estimator.Describe(c.Duration) : "";

    public PlanPage(Plugin plugin) : base(plugin) { }

    public override string Title => "Plan";
    public override FontAwesomeIcon Icon => FontAwesomeIcon.Sitemap;

    public override (int Count, Vector4 Color)? Badge
    {
        get
        {
            var n = Plans.Plan.Missing.Count + Plans.Readiness.Count(i => i.Blocking);
            return n > 0 ? (n, Palette.Danger) : null;
        }
    }

    public override (string Text, Vector4 Color)? Status
    {
        get
        {
            var plan = Plans.Plan;
            if (CosmicMode) return CosmicStatus;
            if (Config.Order.IsEmpty) return ("The order is empty", Palette.TextFaint);
            if (plan.Missing.Count > 0) return (plan.Missing.Count == 1 ? "1 item to get yourself before starting" : $"{plan.Missing.Count} items to get yourself before starting", Palette.Danger);
            var blocking = Plans.Readiness.Count(i => i.Blocking);
            if (blocking > 0) return (blocking == 1 ? "1 thing your character can't do yet" : $"{blocking} things your character can't do yet", Palette.Danger);
            var crafts = plan.CraftSteps.Sum(l => l.Crafts);
            var total = Plans.Estimate.Total;
            var parts = new List<string>();
            var gather = plan.Count(Supply.Gather) + plan.Count(Supply.GatherTimed);
            if (gather > 0) parts.Add($"{gather} to gather");
            if (plan.Count(Supply.Fish) > 0) parts.Add($"{plan.Count(Supply.Fish)} to fish");
            if (plan.Count(Supply.Vendor) > 0) parts.Add($"{plan.Count(Supply.Vendor)} to buy");
            if (plan.Count(Supply.Exchange) > 0) parts.Add($"{plan.Count(Supply.Exchange)} to buy with scrips");
            if (crafts > 0) parts.Add($"{crafts} {(crafts == 1 ? "craft" : "crafts")}");
            var what = parts.Count > 0 ? string.Join(", ", parts) : "everything is in your bags";
            return ("Ready: " + what + (total > TimeSpan.Zero ? ", " + Estimator.Describe(total) + " in total" : ""), Palette.Accent);
        }
    }

    public override IEnumerable<FooterAction> Actions
    {
        get
        {
            var runs = Plugin.Runs;
            if (CosmicMode)
            {
                foreach (var a in CosmicActions) yield return a;
                yield break;
            }
            if (runs.Active)
            {
                yield return new FooterAction("Show the run", Ui.Kind.Primary, FontAwesomeIcon.Running, Go<RunPage>);
                yield break;
            }
            var why = runs.StartBlocker();
            yield return new FooterAction("Start", Ui.Kind.Primary, FontAwesomeIcon.Play, () => { runs.Start(); Go<RunPage>(); },
                Enabled: why == null, Tooltip: why ?? "Gets everything this order needs (retainers, books, vendors, monsters, gathering) and crafts it.");
        }
    }

    public override void Draw()
    {
        var catalog = Plans.Catalog;
        if (catalog == null)
        {
            Ui.Heading("Plan", Plans.LoadError is { } err ? "Game data couldn't be read: " + err : "Reading recipes from the game data…");
            return;
        }
        if (ShowsCosmic)
        {
            DrawCosmic(catalog);
            if (Config.Order.IsEmpty) return;
            ImGui.Dummy(new Vector2(0, 8 * Ui.S));
        }
        if (Config.Order.IsEmpty)
        {
            DrawEmpty();
            return;
        }

        var plan = Plans.Plan;
        currentEstimate = Plans.Estimate;
        DrawOrderHeader();
        DrawLearnFirst(plan, catalog);
        DrawScripsFirst(catalog);
        DrawMissing(plan, catalog);
        DrawReadiness(catalog);
        DrawTargets(plan, catalog);

        // Ordered items that aren't crafted (gathered, fished, bought) also show up with their progress. A line with
        // nothing to make is only there for bait still missing, which the card above lists.
        var materials = plan.Lines.Where(l => (!l.IsTarget || l.Supply != Supply.Craft) && !Items.IsCrystal(l.ItemId) && (l.IsTarget || l.Needed > 0)).ToList();
        Section("To gather", FontAwesomeIcon.Leaf, Palette.SupplyColor(Supply.Gather),
            materials.Where(l => l.Supply is Supply.Gather or Supply.GatherTimed), catalog, null);
        Section("To fish", FontAwesomeIcon.Fish, Palette.SupplyColor(Supply.Fish),
            materials.Where(l => l.Supply == Supply.Fish), catalog, null);
        Section("To reduce", FontAwesomeIcon.Magic, Palette.SupplyColor(Supply.Reduce),
            materials.Where(l => l.Supply == Supply.Reduce), catalog, "gathered or fished as collectables, then reduced");
        Section("To buy", FontAwesomeIcon.Store, Palette.SupplyColor(Supply.Vendor),
            materials.Where(l => l.Supply == Supply.Vendor), catalog, plan.VendorGil > 0 ? $"about {plan.VendorGil:N0} gil" : null);
        Section("To buy with scrips", FontAwesomeIcon.Coins, Palette.SupplyColor(Supply.Exchange),
            materials.Where(l => l.Supply == Supply.Exchange), catalog, ScripSpend(plan, catalog));
        Section("Monster drops", FontAwesomeIcon.Paw, Palette.SupplyColor(Supply.Drop),
            materials.Where(l => l.Supply == Supply.Drop), catalog, null);
        Section("To craft first", FontAwesomeIcon.Hammer, Palette.Accent,
            materials.Where(l => l.Supply == Supply.Craft), catalog, CraftSummary(plan));
        DrawCrystals(plan, catalog);
    }

    // ------------------------------------------------------------------ header and checks

    private void DrawEmpty()
    {
        var s = Ui.S;
        Ui.Heading("Nothing planned yet", "Import a Teamcraft list or pick recipes in Browse. The plan lists every material down to the crystals and where each one comes from.");
        ImGui.Dummy(new Vector2(0, 6 * s));
        if (Ui.Button("Import a list", Ui.Kind.Primary, FontAwesomeIcon.FileImport))
        {
            Go<BrowsePage>();
            Plugin.Main.Page<BrowsePage>()?.OpenImport();
        }
        ImGui.SameLine(0, 10 * s);
        if (Ui.Button("Browse recipes", Ui.Kind.Secondary, FontAwesomeIcon.Search)) Go<BrowsePage>();
    }

    /// <summary>One line under the order name: the whole order, split into gathering, fishing and crafting.</summary>
    private void DrawEstimateLine()
    {
        var e = Plans.Estimate;
        if (e.Total <= TimeSpan.Zero) return;
        var parts = new List<string>();
        // Fishing is part of the gathering stage; a second or two apart is rounding.
        var gathering = e.Gathering - e.Fishing;
        if (gathering > TimeSpan.FromSeconds(1)) parts.Add(Short(gathering) + " gathering");
        if (e.Fishing > TimeSpan.Zero) parts.Add(Short(e.Fishing) + " fishing");
        if (e.Crafting > TimeSpan.Zero) parts.Add(Short(e.Crafting) + " crafting");
        var text = $"{Capitalize(Estimator.Describe(e.Total))} for the whole order" + (parts.Count > 0 ? $" ({string.Join(", ", parts)})." : ".");
        if (e.GpWait.Wait > TimeSpan.Zero)
            text += $" Gathering includes {Estimator.Describe(e.GpWait.Wait)} of waiting for GP before collectables"
                    + (e.GpWait.Cordials > 0 ? $", after {CordialCount(e.GpWait.Cordials)}." : ".");
        else if (e.GpWait.Cordials > 0)
            text += $" {Capitalize(CordialCount(e.GpWait.Cordials))} {(e.GpWait.Cordials == 1 ? "keeps" : "keep")} the GP up for the collectables.";
        if (e.FishWait >= TimeSpan.FromMinutes(1))
            text += $" Fishing includes {Estimator.Describe(e.FishWait)} of waiting for fish to start biting; the run crafts in the meantime when it can.";
        // Fish with no window in the forecast block the start (the readiness card says so); fish whose windows there
        // don't hold all their catches are fished window by window as the forecast moves on.
        var days = PlanService.FishHorizon.TotalDays;
        var catalog = Plans.Catalog;
        if (catalog != null && e.FishBeyondForecast is { Count: > 0 } none)
            text += $" {Names(none, catalog)} {(none.Count == 1 ? "doesn't" : "don't")} bite in the next {days:0} days of the weather forecast, "
                    + $"so a run can't catch {(none.Count == 1 ? "it" : "them")} yet.";
        if (catalog != null && e.FishShortOfWindows is { Count: > 0 } tight)
            text += $" {Names(tight, catalog)} {(tight.Count == 1 ? "takes" : "take")} more windows than the next {days:0} days of the forecast hold; "
                    + "waiting for the later ones comes on top.";
        if (e.HasTimedNodes) text += " Waiting for timed nodes comes on top.";
        if (e.Approximate) text += Plans.Profile == null ? " Check your character for closer crafting times." : " Still working out some rotations.";
        Ui.Icon(FontAwesomeIcon.Clock, Palette.TextMuted, small: true);
        ImGui.SameLine(0, 8 * Ui.S);
        Ui.Wrapped(text, Palette.TextMuted);
        Ui.Tooltip("Estimates follow your own runs: each craft and gathering session adjusts the timings. Fish go by their bite times, "
                   + "the casts a catch usually takes and when they bite.");
    }

    /// <summary>"12 min", "1 h 05 min", "under a minute".</summary>
    private static string Short(TimeSpan span) => Estimator.Describe(span).Replace("about ", "");

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string CordialCount(int n) => n == 1 ? "one cordial" : $"{n} cordials";

    private void DrawOrderHeader()
    {
        var s = Ui.S;
        var name = Config.Order.Name;
        using (Ui.F.Title.Push())
        {
            ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0, 4) * s);
            ImGui.SetNextItemWidth(MathF.Min(ImGui.GetContentRegionAvail().X - 160 * s, 520 * s));
            if (ImGui.InputText("##ordername", ref name, 80)) Config.Order.Name = name;
            if (ImGui.IsItemDeactivatedAfterEdit()) Config.Save();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor();
        }
        Ui.Tooltip("Rename the order");
        DrawEstimateLine();

        var armed = DateTime.UtcNow < clearArmedUntil;
        var label = armed ? "Click again to clear" : "Clear order";
        ImGui.SameLine(ImGui.GetContentRegionMax().X - Ui.ButtonWidth(label, FontAwesomeIcon.TrashAlt));
        if (Ui.Button(label, armed ? Ui.Kind.Danger : Ui.Kind.Ghost, FontAwesomeIcon.TrashAlt, id: "##clear"))
        {
            if (armed)
            {
                Config.Order.Lines.Clear();
                clearArmedUntil = DateTime.MinValue;
                Changed();
            }
            else clearArmedUntil = DateTime.UtcNow.AddSeconds(3);
        }
    }

    /// <summary>Master books and folklore tomes the order needs, with what the run does about them.</summary>
    private void DrawLearnFirst(Plan plan, GameCatalog catalog)
    {
        if (plan.Learn.Count == 0) return;
        var s = Ui.S;
        var onlyOrdered = plan.Learn.All(l => l.NeededFor.Count == 0);
        Ui.BeginCard(Palette.WithAlpha(Palette.Warn, 0.07f));
        Ui.Icon(FontAwesomeIcon.Book, Palette.Warn);
        ImGui.SameLine(0, 10 * s);
        using (Ui.F.Strong.Push()) Ui.Text(onlyOrdered ? "Books to learn" : "Learn first", Palette.Warn);
        Ui.Wrapped(onlyOrdered
            ? "The run buys these at an exchange, earning the scrips with collectables when you're short, and reads them."
            : "Some recipes or legendary nodes in this order need a book you haven't learned. The run buys it at an exchange when you have the scrips or tokens, and reads it before anything else.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        foreach (var line in plan.Learn)
        {
            Ui.ItemIcon(catalog.ItemIcon(line.ItemId), 28);
            ImGui.SameLine(0, 10 * s);
            ImGui.BeginGroup();
            Ui.Text(catalog.ItemName(line.ItemId));
            ImGui.SameLine(0, 8 * s);
            if (line.Remaining == 0) Ui.Chip("in your bags, read first", Palette.Accent);
            else if (line.WillBuy && Plans.BuyNow(line.ItemId)) Ui.Chip("bought first", Palette.Accent);
            else if (line.WillBuy) Ui.Chip("bought once the scrips are in", Palette.Accent);
            else Ui.Chip("get it first", Palette.Danger);
            var forText = line.NeededFor.Count == 0
                ? "On the order."
                : "Needed for " + string.Join(", ", line.NeededFor.Take(4).Select(catalog.ItemName)) + (line.NeededFor.Count > 4 ? $" and {line.NeededFor.Count - 4} more" : "") + ".";
            if (line.Remaining > 0 && line.Purchase is { } purchase) forText += " " + purchase.Text + ".";
            else if (line.Remaining > 0 && catalog.SoldAt(line.ItemId) is { } shop) forText += $" Sold at the {shop} exchange.";
            using (Ui.F.Small.Push()) Ui.Muted(forText);
            ImGui.EndGroup();
        }
        Ui.EndCard();
    }

    /// <summary>"150 Orange Crafters' Scrips": what the materials from scrip exchanges still cost, per scrip.</summary>
    private string? ScripSpend(Plan plan, GameCatalog catalog)
    {
        var spend = plan.Lines.Where(l => l.Supply == Supply.Exchange && l.Remaining > 0)
            .Select(l => Plans.BookOffer(l.ItemId) is { } o ? (Currency: o.CurrencyItemId, Cost: (long)o.Cost * l.Remaining) : (Currency: 0u, Cost: 0L))
            .Where(x => x.Currency != 0).GroupBy(x => x.Currency).Select(g => $"{g.Sum(x => x.Cost):N0} {catalog.ItemNamePlural(g.Key)}").ToList();
        return spend.Count > 0 ? string.Join(" and ", spend) : null;
    }

    /// <summary>Collectables the run makes or gathers for scrips before the order, with what that takes.</summary>
    private void DrawScripsFirst(GameCatalog catalog)
    {
        var tasks = Plans.ScripTasks;
        if (tasks.Count == 0) return;
        var s = Ui.S;
        var scripPlan = Plans.ScripPlan;
        Ui.BeginCard(Palette.WithAlpha(Palette.Accent, 0.07f));
        Ui.Icon(FontAwesomeIcon.Coins, Palette.Accent);
        ImGui.SameLine(0, 10 * s);
        using (Ui.F.Strong.Push()) Ui.Text("Earn scrips first", Palette.Accent);
        Ui.Wrapped("What the order buys at scrip exchanges (books, materials) costs more scrips than you have. Before the order, the run makes or gathers these collectables, turns them in at a collectable appraiser and buys it all."
            + (Plans.SeveralScripRounds ? " Scrips stop at 4,000, so it buys what it can, earns more and buys again until everything is in." : ""));
        ImGui.Dummy(new Vector2(0, 2 * s));
        foreach (var t in tasks)
        {
            Ui.ItemIcon(catalog.ItemIcon(t.Option.ItemId), 28);
            ImGui.SameLine(0, 10 * s);
            ImGui.BeginGroup();
            Ui.Text($"{t.Count}× {catalog.ItemName(t.Option.ItemId)}");
            ImGui.SameLine(0, 8 * s);
            Ui.Chip(Core.Character.ClassJobs.Abbreviation(t.Option.ClassJobId), Palette.ClassJobColor(t.Option.ClassJobId), job: t.Option.ClassJobId);
            using (Ui.F.Small.Push())
                Ui.Muted($"At least {t.Expected:N0} {catalog.ItemNamePlural(t.Option.Currency)} (collectability {t.Option.LowCollectability}+), " +
                         $"{t.Option.HighReward * t.Count:N0} at {t.Option.HighCollectability}+. Leftover materials become more collectables.");
            ImGui.EndGroup();
        }
        var gather = scripPlan.Lines.Where(l => l.Supply is Supply.Gather or Supply.GatherTimed && l.Remaining > 0).Sum(l => l.Remaining);
        var fish = scripPlan.Lines.Where(l => l.Supply == Supply.Fish && l.Remaining > 0).Sum(l => l.Remaining);
        var buy = scripPlan.Lines.Where(l => l.Supply == Supply.Vendor && l.Remaining > 0).Sum(l => l.Remaining);
        var crafts = scripPlan.CraftSteps.Sum(l => l.Crafts);
        var timed = scripPlan.Lines.Count(l => l.Supply == Supply.GatherTimed && l.Remaining > 0);
        var parts = new List<string>();
        if (gather > 0) parts.Add($"{gather:N0} materials to gather" + (timed > 0 ? $" ({timed} from timed nodes)" : ""));
        if (fish > 0) parts.Add($"{fish:N0} fish to catch");
        if (buy > 0) parts.Add($"{buy:N0} to buy");
        if (crafts > 0) parts.Add($"{crafts} {(crafts == 1 ? "craft" : "crafts")}");
        if (parts.Count > 0) using (Ui.F.Small.Push()) Ui.Muted("That takes " + string.Join(", ", parts) + ".");
        foreach (var line in scripPlan.Missing)
            using (Ui.F.Small.Push()) Ui.Wrapped($"Get yourself: {ToGet(line)}× {catalog.ItemName(line.ItemId)} ({line.ManualReason}).", Palette.Danger);
        Ui.EndCard();
    }

    private void DrawMissing(Plan plan, GameCatalog catalog)
    {
        var missing = plan.Missing.Where(l => l.Supply != Supply.Learn).ToList();
        if (missing.Count == 0) return;
        var s = Ui.S;
        Ui.BeginCard(Palette.WithAlpha(Palette.Danger, 0.08f));
        CardTitle(FontAwesomeIcon.HandPaper, "Get these yourself before starting", Palette.Danger,
            missing.SelectMany(l => plan.TargetsNeeding(l.ItemId)).Distinct().ToList(), catalog, "missing");
        // Bait only counts in the main inventory: that's where the game casts it from.
        Ui.Wrapped("The run can't get these by itself. Put them in your inventory or saddlebag and this list updates on its own"
                   + (missing.Any(IsBait) ? "; bait only counts in your inventory, where it can be cast." : "."));
        ImGui.Dummy(new Vector2(0, 6 * s));
        foreach (var line in missing)
        {
            if (line != missing[0]) ImGui.Dummy(new Vector2(0, 4 * s));
            var targets = plan.TargetsNeeding(line.ItemId);
            RemovableRow("missing" + line.ItemId, targets, line.ItemId, catalog, width =>
            {
                Ui.ItemIcon(catalog.ItemIcon(line.ItemId), 28);
                ImGui.SameLine(0, 10 * s);
                ImGui.BeginGroup();
                Ui.Text(catalog.ItemName(line.ItemId));
                // Bait says which fish it's for, what of it is elsewhere, and that Versatile Lure could stand in when that's switched off.
                var text = IsBait(line)
                    ? $"Need {ToGet(line)} more as bait for {Names(line.NeededFor, catalog)}. {line.ManualReason}." + BaitElsewhere(line) + StandInHint(catalog)
                    : $"Need {ToGet(line)} more. {line.ManualReason}.";
                using (Ui.F.Small.Push()) Ui.Wrapped(text, Palette.TextMuted, width - 38 * s);
                ImGui.EndGroup();
            });
        }
        Ui.EndCard();
    }

    /// <summary>How many to get yourself: what's missing, or for bait the run only crafts after the fishing, the casts it lacks.</summary>
    private static int ToGet(PlanLine line) => line.MissingBait > 0 ? line.MissingBait : line.Remaining;

    /// <summary>" You have 99 more in your saddlebag or with retainers; …" when bait the run needs is there, where it can't be cast.</summary>
    private string BaitElsewhere(PlanLine line)
    {
        var elsewhere = Plans.Owned.GetValueOrDefault(line.ItemId) - GameState.BagCount(line.ItemId);
        return elsewhere > 0 ? $" You have {elsewhere:N0} more in your saddlebag or with retainers; move them into your inventory to cast them." : "";
    }

    /// <summary>Crafts and gathering the character can't do with its levels and stats, or should double-check.</summary>
    private void DrawReadiness(GameCatalog catalog)
    {
        var issues = Plans.Readiness;
        if (issues.Count == 0) return;
        var s = Ui.S;
        var plan = Plans.Plan;
        var blocking = issues.Any(i => i.Blocking);
        var color = blocking ? Palette.Danger : Palette.Warn;
        Ui.BeginCard(Palette.WithAlpha(color, blocking ? 0.08f : 0.07f));
        CardTitle(blocking ? FontAwesomeIcon.UserTimes : FontAwesomeIcon.UserCheck, blocking ? "Your character can't do this yet" : "Worth a look", color,
            issues.Where(i => i.Blocking && i.ItemId != 0).SelectMany(i => plan.TargetsNeeding(i.ItemId)).Distinct().ToList(), catalog, "ready");
        Ui.Wrapped(blocking
            ? "Levels are live; stats come from your best gearset for each job at the last character check, with the food and medicine you picked. The run starts once these are sorted."
            : "These won't stop the run, but the numbers might not be the whole story.");

        // When the stats were read, with a quiet way to read them again, on one centred line.
        ImGui.Dummy(new Vector2(0, 2 * s));
        var checkedAt = Plans.Profile?.CheckedAtUtc;
        var label = checkedAt is { } at && at > DateTime.MinValue ? $"Stats checked {at.ToLocalTime():d MMM, HH:mm}." : "Stats not checked yet.";
        var linkH = Ui.LinkButtonSize("Check again", FontAwesomeIcon.Sync).Y;
        var lineY = ImGui.GetCursorPosY();
        using (Ui.F.Small.Push())
        {
            ImGui.SetCursorPosY(lineY + (linkH - ImGui.GetTextLineHeight()) / 2);
            Ui.Muted(label);
        }
        ImGui.SameLine(0, 6 * s);
        ImGui.SetCursorPosY(lineY);
        if (Ui.LinkButton("Check again", Palette.Accent, FontAwesomeIcon.Sync, "Reads gearsets, stats, books and tomes again.", "##readycheck")) Plans.CheckCharacter();
        ImGui.Dummy(new Vector2(0, 4 * s));

        var first = true;
        foreach (var issue in issues.OrderByDescending(i => i.Blocking))
        {
            if (!first) ImGui.Dummy(new Vector2(0, 4 * s));
            first = false;
            var targets = issue.ItemId != 0 ? plan.TargetsNeeding(issue.ItemId) : Array.Empty<uint>();
            RemovableRow($"ready{issue.ItemId}_{issue.ClassJobId}_{issue.Text.GetHashCode()}", targets, issue.ItemId, catalog, width =>
            {
                if (issue.ItemId != 0)
                {
                    Ui.ItemIcon(catalog.ItemIcon(issue.ItemId), 28);
                    ImGui.SameLine(0, 10 * s);
                }
                ImGui.BeginGroup();
                if (issue.ItemId != 0)
                {
                    Ui.Text(catalog.ItemName(issue.ItemId));
                    if (issue.ClassJobId != 0)
                    {
                        ImGui.SameLine(0, 8 * s);
                        Ui.Chip(Core.Character.ClassJobs.Abbreviation(issue.ClassJobId), Palette.ClassJobColor(issue.ClassJobId), job: issue.ClassJobId);
                    }
                }
                using (Ui.F.Small.Push()) Ui.Wrapped(issue.Text, issue.Blocking ? Palette.Text : Palette.TextMuted, width - (issue.ItemId != 0 ? 38 * s : 0));
                ImGui.EndGroup();
            });
        }
        Ui.EndCard();
    }

    /// <summary>A card's icon and title, with a quiet "Remove all" on the right when there is anything to remove.</summary>
    private void CardTitle(FontAwesomeIcon icon, string title, Vector4 color, IReadOnlyList<uint> removable, GameCatalog catalog, string id)
    {
        var s = Ui.S;
        // Placed in screen space against the card's own width, like the rows' remove buttons, so both
        // right edges line up (SameLine offsets count from the window and add the card's indent again).
        var start = ImGui.GetCursorScreenPos();
        var avail = Ui.CardInnerWidth;
        Ui.Icon(icon, color);
        ImGui.SameLine(0, 10 * s);
        float titleH;
        using (Ui.F.Strong.Push())
        {
            Ui.Text(title, color);
            titleH = ImGui.GetTextLineHeight();
        }
        if (removable.Count == 0) return;
        var after = ImGui.GetCursorScreenPos();
        var label = removable.Count == 1 ? "Remove it" : $"Remove all {removable.Count}";
        var size = Ui.LinkButtonSize(label, FontAwesomeIcon.TrashAlt);
        // The link's text ends 10 px inside its box; the trash icon sits centred in its round button.
        // Line up what the eye sees: the end of the text with the right edge of the trash icons.
        var iconInset = (RemoveButton * s - Ui.IconSize(FontAwesomeIcon.TrashAlt).X) / 2;
        var right = start.X + avail - iconInset + 10 * s;
        ImGui.SetCursorScreenPos(new Vector2(right - size.X, start.Y + (titleH - size.Y) / 2));
        if (Ui.LinkButton(label, Palette.Danger, FontAwesomeIcon.TrashAlt, "Takes these out of the order: " + Names(removable, catalog) + ".", "##removeall" + id))
            RemoveFromOrder(removable, catalog);
        ImGui.SetCursorScreenPos(after);
    }

    /// <summary>Size of the round remove button on each row (before scaling).</summary>
    private const float RemoveButton = 28;

    /// <summary>Last frame's height of each removable row, so the hover highlight can be drawn behind this frame's content.</summary>
    private readonly Dictionary<string, float> rowHeights = new();

    /// <summary>
    /// One list row with a quiet round remove button centred on its right edge. The button stays muted
    /// until hovered (then soft red); hovering the row lightly highlights it so the button reads as
    /// belonging to that row. The content gets the row width minus the button column.
    /// </summary>
    private void RemovableRow(string id, IReadOnlyList<uint> targets, uint itemId, GameCatalog catalog, Action<float> content)
    {
        var s = Ui.S;
        var start = ImGui.GetCursorScreenPos();
        var avail = Ui.CardInnerWidth;
        var button = RemoveButton * s;
        var canRemove = targets.Count > 0;
        rowHeights.TryGetValue(id, out var height);

        // Highlight reaches 10 px into the card's padding on both sides, never past the card.
        var min = start - new Vector2(10 * s, 6 * s);
        var max = start + new Vector2(avail + 10 * s, height + 6 * s);
        if (canRemove && height > 0 && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows) && ImGui.IsMouseHoveringRect(min, max))
            ImGui.GetWindowDrawList().AddRectFilled(min, max, Palette.U32(Palette.WithAlpha(Palette.Raised, 0.6f)), 10 * s);

        content(canRemove ? avail - button - 12 * s : avail);
        var end = ImGui.GetCursorScreenPos();
        height = end.Y - start.Y - ImGui.GetStyle().ItemSpacing.Y;
        rowHeights[id] = height;
        if (canRemove)
        {
            ImGui.SetCursorScreenPos(new Vector2(start.X + avail - button, start.Y + (height - button) / 2));
            var tip = targets.Count == 1 && targets[0] == itemId
                ? $"Remove {catalog.ItemName(itemId)} from the order"
                : $"Remove {Names(targets, catalog)} from the order (they need {catalog.ItemName(itemId)})";
            if (Ui.IconButton("##rm" + id, FontAwesomeIcon.TrashAlt, tip, Ui.Kind.Danger, RemoveButton)) RemoveFromOrder(targets, catalog);
            ImGui.SetCursorScreenPos(end);
        }
    }

    private static string Names(IReadOnlyList<uint> items, GameCatalog catalog)
        => string.Join(", ", items.Take(5).Select(catalog.ItemName)) + (items.Count > 5 ? $" and {items.Count - 5} more" : "");

    /// <summary>Takes ordered items out of the order and plans again.</summary>
    private void RemoveFromOrder(IReadOnlyList<uint> items, GameCatalog catalog)
    {
        var removed = Config.Order.Lines.RemoveAll(l => items.Contains(l.ItemId));
        if (removed == 0) return;
        Changed();
        Services.Chat.Print($"[Provenance] Removed from the order: {Names(items, catalog)}.");
    }

    // ------------------------------------------------------------------ what is being made

    private void DrawTargets(Plan plan, GameCatalog catalog)
    {
        var s = Ui.S;
        Ui.BeginCard();
        SectionHeader("Making", FontAwesomeIcon.Box, Palette.Accent, Config.Order.Lines.Count == 1 ? "1 item" : $"{Config.Order.Lines.Count} items");
        var inner = Ui.CardInnerWidth;

        foreach (var orderLine in Config.Order.Lines.ToList())
        {
            var line = plan[orderLine.ItemId];
            var start = ImGui.GetCursorScreenPos();
            var h = 44 * s;
            var dl = ImGui.GetWindowDrawList();

            Ui.DrawItemIcon(dl, catalog.ItemIcon(orderLine.ItemId), start + new Vector2(0, (h - 32 * s) / 2), 32 * s);
            var tx = start.X + 44 * s;
            using (Ui.F.Strong.Push())
                dl.AddText(new Vector2(tx, start.Y + 4 * s), Palette.U32(Palette.Text), catalog.ItemName(orderLine.ItemId));
            ImGui.SetCursorScreenPos(new Vector2(tx, start.Y + 24 * s));
            if (line?.Recipe is { } r)
            {
                Ui.Chip($"{r.Job.Abbreviation()} level {r.Level}", Palette.JobColor(r.Job));
                if (line.Crafts == 0) { ImGui.SameLine(0, 6 * s); Ui.Chip("covered by what you own", Palette.Accent); }
                else if (r.Yield > 1) { ImGui.SameLine(0, 6 * s); Ui.Chip($"{line.Crafts} crafts make {line.Produced}", Palette.TextMuted); }
                ReadyChip(orderLine.ItemId);
            }
            else if (line != null)
            {
                Ui.Chip(Palette.SupplyLabel(line.Supply), Palette.SupplyColor(line.Supply));
                ReadyChip(orderLine.ItemId);
            }

            var qtyW = 104 * s;
            if (line?.Recipe is { CanHq: true })
            {
                ImGui.SetCursorScreenPos(new Vector2(start.X + inner - qtyW - 40 * s - 70 * s, start.Y + (h - 30 * s) / 2));
                if (Ui.ToggleChip("##hq" + orderLine.ItemId + "_" + orderLine.RecipeId, "HQ", Palette.Accent, orderLine.Hq)) { orderLine.Hq = !orderLine.Hq; Changed(); }
                Ui.Tooltip(orderLine.Hq ? "Crafted aiming for high quality, with HQ ingredients (the crafted ones are made HQ too)." : "Crafted with the fastest rotation, normal quality.");
            }
            ImGui.SetCursorScreenPos(new Vector2(start.X + inner - qtyW - 40 * s, start.Y + (h - 30 * s) / 2));
            var qty = orderLine.Quantity;
            ImGui.SetNextItemWidth(qtyW);
            if (ImGui.InputInt("##tq" + orderLine.ItemId + "_" + orderLine.RecipeId, ref qty, 1, 10)) { orderLine.Quantity = Math.Clamp(qty, 1, 9999); Changed(); }
            Ui.Tooltip("How many to make");
            ImGui.SetCursorScreenPos(new Vector2(start.X + inner - 30 * s, start.Y + (h - 30 * s) / 2));
            if (Ui.IconButton("##rm" + orderLine.ItemId + "_" + orderLine.RecipeId, FontAwesomeIcon.TrashAlt, "Remove from the order", Ui.Kind.Danger))
            {
                Config.Order.Lines.Remove(orderLine);
                Changed();
            }
            ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + h + 4 * s));
        }
        Ui.EndCard();
    }

    /// <summary>"ready in about 12 min", counted from the start of the run in the order it works.</summary>
    private void ReadyChip(uint itemId)
    {
        if (!Plans.Estimate.ReadyAfter.TryGetValue(itemId, out var after) || after <= TimeSpan.Zero) return;
        ImGui.SameLine(0, 6 * Ui.S);
        Ui.Chip("ready in " + Estimator.Describe(after), Palette.TextMuted, FontAwesomeIcon.Clock);
        Ui.Tooltip("From the start of the run: gathering first, then the recipes in order, ingredients before the items that use them.");
    }

    // ------------------------------------------------------------------ material totals

    private static string? CraftSummary(Plan plan)
    {
        var crafts = plan.CraftSteps.Where(l => !l.IsTarget).Sum(l => l.Crafts);
        return crafts > 0 ? $"{crafts} {(crafts == 1 ? "craft" : "crafts")}" : null;
    }

    private void Section(string title, FontAwesomeIcon icon, Vector4 color, IEnumerable<PlanLine> source, GameCatalog catalog, string? extra)
    {
        var lines = source
            .OrderByDescending(l => l.Remaining > 0)
            .ThenByDescending(l => l.Remaining)
            .ThenBy(l => catalog.ItemName(l.ItemId), StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (lines.Count == 0) return;

        var left = lines.Sum(l => l.Remaining);
        var summary = left == 0 ? "all in your bags" : $"{lines.Count} {(lines.Count == 1 ? "item" : "items")}, {left:N0} still to get";
        if (title == "To craft first") summary = left == 0 ? "all in your bags" : $"{lines.Count} {(lines.Count == 1 ? "item" : "items")}";
        if (extra != null) summary += ", " + extra;

        Ui.BeginCard();
        SectionHeader(title, icon, color, summary);
        foreach (var line in lines) MaterialRow(line, catalog);
        Ui.EndCard();
    }

    private static void SectionHeader(string title, FontAwesomeIcon icon, Vector4 color, string summary)
    {
        var s = Ui.S;
        Ui.Icon(icon, color);
        ImGui.SameLine(0, 10 * s);
        using (Ui.F.Strong.Push()) Ui.Text(title);
        ImGui.SameLine(0, 10 * s);
        Ui.Muted(summary);
        ImGui.Dummy(new Vector2(0, 2 * s));
    }

    private void MaterialRow(PlanLine line, GameCatalog catalog)
    {
        var s = Ui.S;
        var width = Ui.CardInnerWidth;
        var h = RowHeight * s;
        var start = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        var hovered = ImGui.IsMouseHoveringRect(start, start + new Vector2(width, h)) && ImGui.IsWindowHovered();
        if (hovered) dl.AddRectFilled(start - new Vector2(6 * s, 0), start + new Vector2(width + 6 * s, h), Palette.U32(Palette.WithAlpha(Palette.Raised, 0.6f)), Ui.InputRadius * s);

        var icon = 26 * s;
        Ui.DrawItemIcon(dl, catalog.ItemIcon(line.ItemId), start + new Vector2(0, (h - icon) / 2), icon);
        var tx = start.X + icon + 12 * s;
        var textY = start.Y + (h - ImGui.GetTextLineHeight()) / 2;
        var name = catalog.ItemName(line.ItemId);
        dl.AddText(new Vector2(tx, textY), Palette.U32(line.Remaining == 0 ? Palette.TextMuted : Palette.Text), name);
        var chipX = tx + ImGui.CalcTextSize(name).X + 10 * s;

        // Right: still to get, then have/need with a bar. Laid out first, so the chips know how much room they have.
        var have = line.FromStock;
        var need = line.Needed;
        var done = line.Remaining == 0;
        var count = $"{have}/{need}";
        var cw = ImGui.CalcTextSize(count).X;
        var right = start.X + width;
        var barW = 110 * s;
        var barX = right - MathF.Max(cw, 52 * s) - 12 * s - barW;
        var leftText = done ? "done" : $"{line.Remaining:N0} left";
        float lw;
        using (Ui.F.Strong.Push()) lw = ImGui.CalcTextSize(leftText).X;
        var leftX = barX - 14 * s - lw;

        // Detail chips: how it is obtained when that needs saying, as much of it as fits before the counts.
        FitChips(Chips(line, catalog), chipX, start.Y + (h - 20 * s) / 2, leftX - 12 * s);

        dl.AddText(new Vector2(right - cw, textY), Palette.U32(done ? Palette.Accent : Palette.TextMuted), count);
        var color = line.Supply == Supply.Craft && line.Recipe != null ? Palette.JobColor(line.Recipe.Job) : Palette.SupplyColor(line.Supply);
        ImGui.SetCursorScreenPos(new Vector2(barX, start.Y + (h - 6 * s) / 2));
        Ui.Bar("##bar" + line.ItemId, need == 0 ? 1f : have / (float)need, done ? Palette.Accent : color, barW);
        using (Ui.F.Strong.Push())
            dl.AddText(new Vector2(leftX, textY), Palette.U32(done ? Palette.Accent : Palette.Text), leftText);

        if (hovered) Ui.ShowTooltip(RowTooltip(line, catalog));

        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + h));
        ImGui.Dummy(new Vector2(width, 0));
    }

    /// <summary>A detail chip: its text from the fullest form to the shortest, its colour, and the icon leading it.</summary>
    private readonly record struct ChipText(string[] Forms, Vector4 Color, FontAwesomeIcon? Icon = null);

    /// <summary>The detail chips of a material row, most important first.</summary>
    private List<ChipText> Chips(PlanLine line, GameCatalog catalog)
    {
        var chips = new List<ChipText>(2);
        switch (line.Supply)
        {
            case Supply.GatherTimed:
                chips.Add(new ChipText(TimedChip(catalog.TimedMask(line.ItemId)), Palette.SupplyColor(Supply.GatherTimed)));
                break;
            case Supply.Fish:
                FishChips(line, catalog, chips);
                break;
            case Supply.Vendor:
                // Bait says what it's for first; its price follows like on any vendor row.
                if (IsBait(line))
                    chips.Add(new ChipText(new[] { "bait for " + FishList(line.NeededFor, catalog), "bait" }, Palette.SupplyColor(Supply.Fish), FontAwesomeIcon.Worm));
                var price = $"{line.VendorUnitPrice:N0} gil each";
                chips.Add(new ChipText(VendorPlace(line.ItemId, catalog) is { Length: > 0 } place ? new[] { price + place, price } : new[] { price },
                    Palette.SupplyColor(Supply.Vendor)));
                break;
            case Supply.Exchange when Plans.BookOffer(line.ItemId) is { } offer:
            {
                // Bait sold for scrips says what it's for first, like bait from a vendor.
                if (IsBait(line))
                    chips.Add(new ChipText(new[] { "bait for " + FishList(line.NeededFor, catalog), "bait" }, Palette.SupplyColor(Supply.Fish), FontAwesomeIcon.Worm));
                var each = $"{offer.Cost:N0} {offer.CurrencyName} each";
                chips.Add(new ChipText(new[] { $"{each}, {offer.Who} in {offer.PlaceName}", each, $"{offer.Cost:N0} each" },
                    Palette.SupplyColor(Supply.Exchange), FontAwesomeIcon.Coins));
                // Bought with the scrips at hand, or once a round of collectables has earned them.
                if (line.WillBuy && line.Remaining > 0)
                    chips.Add(new ChipText(Plans.BuyNowAmount(line.ItemId) >= line.Remaining ? new[] { "bought first" } : new[] { "scrips earned first", "earned first" }, Palette.Accent));
                break;
            }
            case Supply.Reduce:
            {
                // The collectables the run gathers for it (the gatherer picks the node that suits best).
                var from = Reduction.Sources(catalog, line.ItemId, Config.Fishing.FishForReduction);
                var names = from.Take(2).Select(catalog.ItemName).ToList();
                var full = "from " + string.Join(" or ", names) + (from.Count > 2 ? $" (or {from.Count - 2} more)" : "");
                chips.Add(new ChipText(new[] { full, "reduce " + names.FirstOrDefault(), "reduce" }, Palette.SupplyColor(Supply.Reduce), FontAwesomeIcon.Magic));
                break;
            }
            case Supply.Drop:
                chips.Add(catalog.Mobs.For(line.ItemId).FirstOrDefault() is { } mob
                    ? new ChipText(new[] { $"{mob.Name}, {mob.PlaceName} (level {mob.Level})", mob.Name }, Palette.SupplyColor(Supply.Drop))
                    : new ChipText(new[] { "no known spawn" }, Palette.SupplyColor(Supply.Drop)));
                break;
            case Supply.Craft when line.Recipe != null && line.Crafts > 0:
                var crafts = $"{line.Recipe.Job.Abbreviation()}, {line.Crafts} {(line.Crafts == 1 ? "craft" : "crafts")}";
                chips.Add(new ChipText(new[] { crafts + CraftTime(line.Recipe), crafts }, Palette.JobColor(line.Recipe.Job)));
                if (line.Hq && !line.IsTarget) chips.Add(new ChipText(new[] { "HQ" }, Palette.Accent));
                break;
        }
        return chips;
    }

    /// <summary>
    /// A fish's window ("bites 18:00–06:00 ET in Fog, opens in 12 min"), live, and what it is cast with
    /// (spearfishing needs no bait). A fish mooched from one that keeps hours or weather shows that one's window.
    /// </summary>
    private void FishChips(PlanLine line, GameCatalog catalog, List<ChipText> chips)
    {
        if (Plans.Outlook(line.ItemId) is not { } outlook) return;
        var fish = outlook.Fish;
        if (outlook.Restricted)
        {
            var (status, brief) = WindowStatus(outlook, DateTimeOffset.UtcNow);
            var rule = Rule(fish, outlook.Times.Links);
            chips.Add(new ChipText(new[] { $"bites {rule.Text}, {status}", brief }, Palette.SupplyColor(Supply.Fish),
                rule.Hours ? FontAwesomeIcon.Clock : FontAwesomeIcon.CloudSun));
        }
        if (fish.Spear) chips.Add(new ChipText(new[] { "spearfishing" }, Palette.TextMuted, FontAwesomeIcon.Water));
        else if (CastWith(line.ItemId, fish, catalog) is { } bait) chips.Add(new ChipText(new[] { catalog.ItemName(bait) }, Palette.TextMuted, FontAwesomeIcon.Worm));
    }

    /// <summary>"opens in 12 min", "9 min left" (and "biting, 9 min left" for the short chip), "biting now", or not in the forecast.</summary>
    private static (string Status, string Brief) WindowStatus(FishOutlook outlook, DateTimeOffset now)
    {
        if (outlook.OpensIn(now) is not { } wait)
        {
            var none = $"not in the next {PlanService.FishHorizon.TotalDays:0} days";
            return (none, none);
        }
        if (wait > TimeSpan.Zero) return ($"opens in {Short(wait)}", $"opens in {Short(wait)}");
        var left = outlook.OpenFor(now);
        return left >= TimeSpan.FromHours(2) ? ("biting now", "biting now") : ($"{Short(left)} left", $"biting, {Short(left)} left");
    }

    /// <summary>
    /// The bait a fish is cast with first: its own tackle, or Versatile Lure when that stands in for all of it
    /// (none in the bags and no vendor sells it under the cap). Null when there's no bait to show.
    /// </summary>
    private uint? CastWith(uint fishId, Core.Fishing.FishInfo fish, GameCatalog catalog)
    {
        if (fish.Bait == 0) return null;
        var plan = Plans.Plan;
        if (fish.Bait != FishAccess.VersatileLure && plan[FishAccess.VersatileLure] is { } lure && lure.NeededFor.Contains(fishId)
            && !CastsOwn(plan[fish.Bait], catalog))
            return FishAccess.VersatileLure;
        return fish.Bait;
    }

    /// <summary>
    /// Some of a fish's own bait gets cast: the plan has it, or for bait crafted only after the fishing (its line
    /// counts the crafts), the bags hold some or the fisher buys it (a gil vendor sells it under the cap).
    /// </summary>
    private bool CastsOwn(PlanLine? bait, GameCatalog catalog)
        => bait is { Supply: Supply.Craft }
            ? GameState.BagCount(bait.ItemId) > 0 || Planner.BuysBait(Plans.Plan, catalog, bait.ItemId, Config.Buying.VendorMaxPrice)
            : bait is { Needed: > 0 };

    /// <summary>
    /// Draws chips left to right from <paramref name="x"/>, each in the fullest form that keeps them all before
    /// <paramref name="maxX"/>: the last chips shorten first, then drop. The row's tooltip carries the rest.
    /// </summary>
    private static void FitChips(IReadOnlyList<ChipText> chips, float x, float y, float maxX)
    {
        var gap = 6 * Ui.S;
        for (var count = chips.Count; count > 0; count--)
        {
            var forms = new int[count];
            while (true)
            {
                var total = -gap;
                for (var i = 0; i < count; i++) total += Ui.ChipWidth(chips[i].Forms[forms[i]], chips[i].Icon) + gap;
                if (x + total <= maxX)
                {
                    for (var i = 0; i < count; i++)
                    {
                        var text = chips[i].Forms[forms[i]];
                        ImGui.SetCursorScreenPos(new Vector2(x, y));
                        Ui.Chip(text, chips[i].Color, chips[i].Icon);
                        x += Ui.ChipWidth(text, chips[i].Icon) + gap;
                    }
                    return;
                }
                // Shorten the last chip that still can; when none can, try one chip fewer.
                var j = count - 1;
                while (j >= 0 && forms[j] >= chips[j].Forms.Length - 1) j--;
                if (j < 0) break;
                forms[j]++;
            }
        }
    }

    /// <summary>The row's tooltip: have and need, then what's worth knowing about how it's got.</summary>
    private string RowTooltip(PlanLine line, GameCatalog catalog)
    {
        var hqMaterial = line.Hq && !line.IsTarget;
        var tip = hqMaterial
            ? $"The whole order needs {line.Needed:N0} in high quality; you have {line.FromStock:N0} HQ. It goes into an HQ item, and HQ ingredients start that craft with quality, so the run makes these HQ too."
            : $"The whole order needs {line.Needed:N0}; you have {line.FromStock:N0}.";
        if (line.Supply == Supply.Craft && line.Recipe != null && line.Crafts > 0)
            tip += $" {line.Crafts} {(line.Crafts == 1 ? "craft makes" : "crafts make")} {line.Produced}.";
        if (line.Supply == Supply.GatherTimed) tip += " Only on timed nodes; the run crafts while it waits for them.";
        if (line.Supply == Supply.Fish) tip += FishTip(line, catalog);
        if (IsBait(line)) tip += BaitTip(line, catalog);
        if (line.Supply == Supply.Vendor && catalog.Vendors.For(line.ItemId).OrderBy(o => o.Price).FirstOrDefault() is { } offer)
            tip += $"\nSold by {offer.NpcName}, {offer.PlaceName}.";
        if (line.Supply == Supply.Exchange && line.Remaining > 0 && line.Purchase is { } purchase)
            tip += $"\n{purchase.Text}.";
        return tip;
    }

    /// <summary>
    /// Where and when a fish bites (its mooch chain's window included), its bait and chain, what comes first and when
    /// that bites, and about how long it takes.
    /// </summary>
    private string FishTip(PlanLine line, GameCatalog catalog)
    {
        if (Plans.Outlook(line.ItemId) is not { } outlook) return "";
        var fish = outlook.Fish;
        var now = DateTimeOffset.UtcNow;
        var parts = new List<string>();
        var where = outlook.Spots.FirstOrDefault() is { } spot ? SpotName(spot) : null;
        if (outlook.Restricted)
        {
            // A predator that doesn't come leaves it no window; the predator's line below says so.
            var rule = Rule(fish, outlook.Times.Links);
            var when = outlook.Times.PredatorBeyond != null ? "" : ": " + When(outlook.Windows, now);
            parts.Add($"Bites {rule.Text}{(where != null ? " at " + where : "")}{when}." + MoochedFrom(rule, catalog));
        }
        else if (where != null)
            parts.Add($"{(fish.Spear ? "Spearfished" : "Fished")} at {where}{(outlook.Times.Predators.Count == 0 ? ", any time" : "")}.");
        if (!fish.Spear && fish.Bait != 0)
        {
            var chain = fish.Mooch.Count > 0 ? " Mooch chain: " + string.Join(", then ", fish.Mooch.Select(catalog.ItemName)) + ", then this fish." : "";
            parts.Add($"Bait: {catalog.ItemName(fish.Bait)}.{chain}");
            if (CastWith(line.ItemId, fish, catalog) == FishAccess.VersatileLure && fish.Bait != FishAccess.VersatileLure)
                parts.Add($"{catalog.ItemName(fish.Bait)} can't be bought for gil, so the run casts Versatile Lure instead.");
        }
        // The fish caught first that keep hours or weather of their own, and when they bite.
        if (fish.NeedsIntuition)
            parts.Add((fish.Spear ? "Swimming shadows first: " : "Fisher's Intuition first: ")
                      + string.Join(" and ", fish.Predators.Select(p => $"{p.Count}× {catalog.ItemName(p.ItemId)}")) + "."
                      + string.Concat(outlook.Times.Predators.Select(p => $" {catalog.ItemName(p.Fish.ItemId)} only bites {Rule(p.Fish, p.Links).Text}: {When(p.Windows, now)}.")));
        if (fish.Big) parts.Add("A big fish: expect many casts.");
        var seconds = Plans.CatchSeconds(line.ItemId) * line.Remaining;
        if (seconds > 0) parts.Add($"About {Short(TimeSpan.FromSeconds(seconds))} of fishing for {line.Remaining:N0}, from its bite time and the casts a catch usually takes.");
        if (outlook.Blocker is { } why) parts.Add($"Can't be fished yet: {why}.");
        return parts.Count == 0 ? "" : "\n" + string.Join("\n", parts);
    }

    /// <summary>Which fish the bait is for, how the amount comes about, and what doesn't count.</summary>
    private string BaitTip(PlanLine line, GameCatalog catalog)
    {
        var parts = new List<string>();
        var spare = Math.Max(0, Config.Fishing.SpareBait);
        var fish = Names(line.NeededFor, catalog);
        var they = line.NeededFor.Count == 1 ? "it usually takes" : "they usually take";
        var cap = Config.Buying.VendorMaxPrice;
        parts.Add(line.Supply == Supply.Craft
            ? Planner.BuysBait(Plans.Plan, catalog, line.ItemId, cap)
                ? $"Also bait for {fish}, but the run crafts it only after the fishing, so it casts what's in your bags, then buys more from a vendor when it fishes."
                : $"Also bait for {fish}, but the run crafts it only after the fishing, so it casts what's in your bags"
                  + (Plans.Plan[FishAccess.VersatileLure] is { } stand && line.NeededFor.Any(f => stand.NeededFor.Contains(f)) ? ", then Versatile Lure." : ".")
            : line.Supply == Supply.Vendor && !line.IsTarget && line.Needed > spare && spare > 0
                ? $"Bait for {fish}: about {line.Needed - spare:N0} for the casts {they}, plus {spare:N0} spare (Settings)."
                : $"Bait for {fish}, for the casts {they}.");
        if (line.ItemId == FishAccess.VersatileLure)
        {
            var plan = Plans.Plan;
            var replaced = line.NeededFor.SelectMany(Plans.BaitPerCatch).Select(b => b.Bait)
                .Where(b => b != FishAccess.VersatileLure && !Planner.BuysBait(plan, catalog, b, cap)).Distinct().Select(catalog.ItemName).ToList();
            if (replaced.Count > 0) parts.Add($"Stands in for {string.Join(" and ", replaced)}, which can't be bought for gil.");
        }
        var elsewhere = Plans.Owned.GetValueOrDefault(line.ItemId) - GameState.BagCount(line.ItemId);
        if (elsewhere > 0) parts.Add($"{elsewhere:N0} more in your saddlebag or with retainers don't count: only bait in your bags can be cast.");
        return "\n" + string.Join("\n", parts);
    }

    /// <summary>Bait lines list the fish they are cast for (books and tomes list their users too, but they're Learn lines).</summary>
    private static bool IsBait(PlanLine line) => line.Supply != Supply.Learn && line.NeededFor.Count > 0;

    /// <summary>"Goosefish", "Goosefish and 2 more".</summary>
    private static string FishList(IReadOnlyList<uint> fish, GameCatalog catalog)
        => fish.Count switch
        {
            0 => "fish",
            1 => catalog.ItemName(fish[0]),
            _ => $"{catalog.ItemName(fish[0])} and {fish.Count - 1} more",
        };

    /// <summary>"Mistbeard Cove, Western La Noscea" (the zone alone when the spot has no name of its own).</summary>
    private static string SpotName(Core.Fishing.FishingSpotInfo spot)
    {
        var zone = FishAccess.Zone(spot.TerritoryId);
        if (spot.Name.Length == 0 || spot.Name == zone) return zone.Length > 0 ? zone : "an unnamed spot";
        return zone.Length > 0 ? $"{spot.Name}, {zone}" : spot.Name;
    }

    /// <summary>
    /// When a fish bites, in words, with the hours or weather of the links of its mooch chain it has none of its own
    /// (<see cref="PlanService.Rule"/>); worked out once per fish (weather names come from the game data).
    /// </summary>
    private BiteRuleText Rule(Core.Fishing.FishInfo fish, IReadOnlyList<Core.Fishing.FishInfo> links)
    {
        if (biteRules.TryGetValue(fish.ItemId, out var rule)) return rule;
        rule = PlanService.Rule(fish, links);
        biteRules[fish.ItemId] = rule;
        return rule;
    }

    private readonly Dictionary<uint, BiteRuleText> biteRules = new();

    /// <summary>"next in 12 min", "biting now", "biting now, for 9 min", or not in the forecast: when windows (null: any time) are open.</summary>
    private static string When(IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)>? windows, DateTimeOffset now)
    {
        if (FishSchedule.OpensIn(windows, now) is not { } wait) return $"not in the next {PlanService.FishHorizon.TotalDays:0} days of the weather forecast";
        if (wait > TimeSpan.Zero) return $"next in {Short(wait)}";
        var left = FishSchedule.OpenFor(windows, now);
        return left >= TimeSpan.FromHours(2) ? "biting now" : $"biting now, for {Short(left)}";
    }

    /// <summary>" Mooched from Fullmoon Sardine, which only bites then." when a rule's hours or weather are a mooch link's; "" otherwise.</summary>
    private static string MoochedFrom(BiteRuleText rule, GameCatalog catalog)
        => rule.From.Count == 0 ? ""
            : $" Mooched from {string.Join(" and ", rule.From.Select(l => catalog.ItemName(l.ItemId)))}, which only {(rule.From.Count == 1 ? "bites" : "bite")} then.";

    /// <summary>" Or let the run cast Versatile Lure instead (Settings)." while that stand-in is off and could be bought.</summary>
    private string StandInHint(GameCatalog catalog)
    {
        if (Config.Fishing.VersatileLureFallback || !Config.Fishing.BuyBait) return "";
        var src = catalog.Sources(FishAccess.VersatileLure);
        var cap = Config.Buying.VendorMaxPrice;
        if (!src.Has(Obtain.Vendor) || (cap != 0 && src.VendorPrice > cap)) return "";
        return " Or let the run cast Versatile Lure instead (turn it on under Fishing in Settings).";
    }

    /// <summary>" at Material Supplier, Limsa Lominsa Lower Decks" for the cheapest reachable vendor.</summary>
    private static string VendorPlace(uint itemId, GameCatalog catalog)
    {
        var offer = catalog.Vendors.For(itemId).OrderBy(o => o.Price).FirstOrDefault();
        return offer == null ? "" : $" at {offer.NpcName}, {offer.PlaceName}";
    }

    /// <summary>"timed node, up for 12 min" / "timed node, opens in 12 min" (and the same without "timed node"), from the node's Eorzea hours.</summary>
    private static string[] TimedChip(uint mask)
    {
        var now = DateTimeOffset.UtcNow;
        if (mask == 0) return new[] { "timed node" };
        if (Core.Timing.Eorzea.IsUp(mask, now))
        {
            var up = "up for " + Short(Core.Timing.Eorzea.UpFor(mask, now));
            return new[] { "timed node, " + up, up };
        }
        if (Core.Timing.Eorzea.UntilOpen(mask, now) is not { } wait) return new[] { "timed node" };
        var opens = "opens in " + Short(wait);
        return new[] { "timed node, " + opens, opens };
    }

    private static void DrawCrystals(Plan plan, GameCatalog catalog)
    {
        var crystals = plan.Lines.Where(l => Items.IsCrystal(l.ItemId) && l.Needed > 0).OrderBy(l => l.ItemId).ToList();
        if (crystals.Count == 0) return;
        var s = Ui.S;
        Ui.BeginCard();
        var left = crystals.Sum(c => c.Remaining);
        SectionHeader("Crystals", FontAwesomeIcon.Gem, Palette.SupplyColor(Supply.Gather), left == 0 ? "all in your bags" : $"{left:N0} still to get");
        var first = true;
        foreach (var c in crystals)
        {
            if (!first) ImGui.SameLine(0, 18 * s);
            first = false;
            Ui.ItemIcon(catalog.ItemIcon(c.ItemId), 24);
            Ui.Tooltip($"{catalog.ItemName(c.ItemId)}: need {c.Needed}, have {c.FromStock}");
            ImGui.SameLine(0, 6 * s);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 3 * s);
            Ui.Text($"{c.FromStock}/{c.Needed}", c.Remaining == 0 ? Palette.Accent : Palette.TextMuted);
        }
        Ui.EndCard();
    }
}
