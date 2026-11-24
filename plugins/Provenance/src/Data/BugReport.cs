using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Provenance.Core.Character;
using Provenance.Core.Planning;
using Provenance.Integrations;

namespace Provenance.Data;

/// <summary>
/// Everything worth knowing to understand a problem, as plain text to paste into a bug report: where the character
/// is and what it's doing, the gathering nodes around it, the order and its plan, what the run is doing (its own,
/// the gatherer's and the fisher's state) and its log, the settings, and the plugins loaded. Nothing that names the
/// player: no character name, world, retainer names or content ids.
/// </summary>
internal static class BugReport
{
    /// <summary>The repository's bug report form.</summary>
    private const string IssueForm = "https://github.com/alizea1337/DalamudPlugins/issues/new?template=bug_report.yml";

    /// <summary>
    /// The bug report form on GitHub with the plugin and this version filled in. The report itself is too long
    /// for a link: it's pasted into the form.
    /// </summary>
    public static string IssueFormUrl()
        => $"{IssueForm}&plugin=Provenance&version={Uri.EscapeDataString(Plugin.Version)}";

    /// <summary>Gathering nodes this close are listed.</summary>
    private const float NodesWithin = 120f;

    public static string Build(Plugin plugin)
    {
        var sb = new StringBuilder();
        void Line(string text = "") => sb.AppendLine(text);
        void Section(string title)
        {
            Line();
            Line("## " + title);
        }

        // Each part is written on its own: one that fails says so, and the rest of the report still comes.
        void Part(string what, Action body)
        {
            try { body(); }
            catch (Exception e) { Line($"({what} couldn't be written: {e.GetType().Name}: {e.Message})"); }
        }

        var catalog = plugin.Plans.Catalog;
        string Name(uint id) => catalog?.ItemName(id) ?? "item " + id;
        var now = DateTimeOffset.UtcNow;

        Line("Provenance bug report");
        Part("The header", () =>
        {
            Line($"Version {Plugin.Version}, game language {Services.ClientState.ClientLanguage}, made {DateTime.Now:yyyy-MM-dd HH:mm} local ({now:HH:mm} UTC), Eorzea {EorzeaTime(now)}");
            Line("No character name, world or retainer names are in this report.");
            Line($"Interface scale {plugin.Config.Window.Scale}%, text size {plugin.Config.Window.TextScale}%, Dalamud scale {Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale:0.00}; fonts: {UI.Ui.F.Describe()}");
            var colors = plugin.Config.Window.Colors;
            if (!colors.IsDefault)
                Line($"Colours: accent {Core.Appearance.ThemeColors.ToHex(colors.Accent)}, background {Core.Appearance.ThemeColors.ToHex(colors.Background)}, "
                     + $"cards {Core.Appearance.ThemeColors.ToHex(colors.Cards)}, text {Core.Appearance.ThemeColors.ToHex(colors.Text)}");
        });

        // ---------------------------------------------------------------- where and what
        var me = Services.Objects.LocalPlayer;
        Part("The character", () =>
        {
            Section("Character");
            var territory = Services.ClientState.TerritoryType;
            var zone = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().GetRowOrDefault(territory);
            Line($"Zone: {zone?.PlaceName.ValueNullable?.Name.ExtractText() ?? "?"} (territory {territory}, intended use {zone?.TerritoryIntendedUse.RowId})");
            if (me != null)
            {
                Line($"Position: {Xyz(me.Position)}, facing {me.Rotation:0.00}");
                var job = GameState.CurrentJob;
                Line($"Job: {ClassJobs.Abbreviation(job)} {GameState.JobLevel(job)}, GP {me.CurrentGp}/{me.MaxGp}, CP {me.CurrentCp}/{me.MaxCp}, HP {me.CurrentHp}/{me.MaxHp}");
            }
            else Line("Not logged in, or changing areas.");
            var flags = new[]
            {
                ConditionFlag.Mounted, ConditionFlag.InFlight, ConditionFlag.Gathering, ConditionFlag.ExecutingGatheringAction, ConditionFlag.Fishing,
                ConditionFlag.Crafting, ConditionFlag.InCombat, ConditionFlag.BetweenAreas, ConditionFlag.Occupied, ConditionFlag.Occupied39,
                ConditionFlag.OccupiedInQuestEvent, ConditionFlag.Casting, ConditionFlag.Jumping, ConditionFlag.Swimming, ConditionFlag.Diving, ConditionFlag.BoundByDuty,
            }.Where(f => Services.Condition[f]).ToList();
            Line("Conditions: " + (flags.Count > 0 ? string.Join(", ", flags) : "none of note"));
            Line("Levels: " + string.Join(", ", ClassJobs.Crafters.Concat(ClassJobs.Gatherers).Select(j => $"{ClassJobs.Abbreviation(j)} {PlanService.JobLevel(j)}")));
            Line($"Free inventory slots: {Stock.FreeSlots()}");
        });

        Part("The gathering nodes", () =>
        {
            Section($"Gathering nodes within {NodesWithin:0} yalms");
            var points = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.GatheringPoint>();
            var gatheringItems = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.GatheringItem>();
            var nodes = Services.Objects.Where(o => o.ObjectKind == ObjectKind.GatheringPoint && me != null && Vector3.Distance(o.Position, me.Position) <= NodesWithin)
                .OrderBy(o => Vector3.Distance(o.Position, me!.Position)).ToList();
            if (nodes.Count == 0) Line("None in view.");
            foreach (var o in nodes)
            {
                var row = points.GetRowOrDefault(o.BaseId);
                var pointBase = row?.GatheringPointBase.ValueNullable;
                var items = pointBase?.Item.Where(r => r.RowId != 0).Select(r => gatheringItems.GetRowOrDefault(r.RowId)?.Item.RowId ?? 0).Where(i => i != 0)
                    .Select(i => $"{Name(i)} ({i})").ToList() ?? new List<string>();
                Line($"- {o.Name.TextValue}: point {o.BaseId}, group {row?.GatheringPointBase.RowId}, level {pointBase?.GatheringLevel}, at {Xyz(o.Position)}, "
                     + $"{Vector3.Distance(o.Position, me!.Position):0.0} yalms, {(o.IsTargetable ? "can be opened" : "not targetable")}"
                     + (items.Count > 0 ? $"; items: {string.Join(", ", items)}" : ""));
            }
        });

        // ---------------------------------------------------------------- the order and its plan
        Part("The order", () =>
        {
            var order = plugin.Config.Order;
            Section($"Order \"{order.Name}\"");
            if (order.IsEmpty) Line("Empty.");
            foreach (var l in order.Lines.Where(l => l.Quantity > 0))
                Line($"- {l.Quantity}x {Name(l.ItemId)} ({l.ItemId}){(l.Hq ? ", HQ" : "")}{(l.RecipeId != 0 ? $", recipe {l.RecipeId}" : "")}");
            Line($"Favourite orders saved: {plugin.Config.Favourites.Count}");
        });

        Part("The plan", () =>
        {
            Section("Plan");
            var plan = plugin.Plans.Plan;
            if (plugin.Plans.LoadError is { } loadError) Line("Game data couldn't be read: " + loadError);
            foreach (var l in plan.Lines.OrderBy(l => l.Supply).ThenBy(l => Name(l.ItemId)))
            {
                var extra = new List<string>();
                if (l.Hq) extra.Add("HQ");
                if (l.Crafts > 0) extra.Add($"{l.Crafts} crafts");
                if (l.Supply == Supply.Vendor) extra.Add($"{l.VendorUnitPrice} gil each");
                if (l.Purchase is { } p) extra.Add((p.CanBuy ? "buys: " : "can't buy: ") + p.Text);
                if (l.ManualReason is { } why && l.Purchase == null) extra.Add("why: " + why);
                if (l.MissingBait > 0) extra.Add($"{l.MissingBait} bait missing");
                Line($"- {Name(l.ItemId)} ({l.ItemId}): {l.Supply}{(l.IsTarget ? ", ordered" : "")}, needs {l.Needed}, has {l.FromStock}, still {l.Remaining}"
                     + (extra.Count > 0 ? "; " + string.Join("; ", extra) : ""));
            }
            foreach (var issue in plugin.Plans.Readiness)
                Line($"Readiness{(issue.Blocking ? " (blocks the start)" : "")}: {(issue.ItemId != 0 ? Name(issue.ItemId) + ": " : "")}{issue.Text}");
            foreach (var t in plugin.Plans.ScripTasks)
                Line($"Scrips to earn: {t.Count}x {Name(t.Option.ItemId)} for about {t.Expected} {Name(t.Option.Currency)}");
            Line($"Estimate: {Core.Estimating.Estimator.Describe(plugin.Plans.Estimate.Total)}");
        });

        // ---------------------------------------------------------------- the run
        var runs = plugin.Runs;
        Part("The run", () =>
        {
            Section("Run");
            foreach (var l in runs.Describe()) Line(l);
        });
        Part("The gatherer", () =>
        {
            Section("Gatherer");
            foreach (var l in runs.Gather.Describe()) Line(l);
        });
        Part("The fisher", () =>
        {
            Section("Fisher");
            foreach (var l in runs.Fish.Describe()) Line(l);
        });
        Part("The run log", () =>
        {
            Section("Run log (newest first)");
            if (runs.Log.Count == 0) Line("Empty.");
            foreach (var e in runs.Log.Take(40)) Line($"{e.Time:HH:mm:ss} {(e.Problem ? "[!] " : "")}{e.Text}");
        });

        // ---------------------------------------------------------------- Custom deliveries
        Part("Custom deliveries", () =>
        {
            if (catalog?.Deliveries is { Clients.Count: > 0 } deliveries)
            {
                Section("Custom deliveries");
                Line($"Data from the game: {(Integrations.Deliveries.Loaded ? "yes" : "no")}, seed {Integrations.Deliveries.Seed}, bonus row {Integrations.Deliveries.GuaranteeRow(deliveries.GuaranteeRows)} of {deliveries.GuaranteeRows}, "
                     + $"week {Integrations.Deliveries.WeekNumber}, {Integrations.Deliveries.Remaining} of {Integrations.Deliveries.WeeklyAllowance} left, resets {Integrations.Deliveries.NextReset:yyyy-MM-dd HH:mm}");
                foreach (var client in deliveries.Clients)
                {
                    var w = Integrations.Deliveries.Week(deliveries, client);
                    var checkedText = Integrations.Deliveries.Matches(client.Id) switch { true => "matches the game", false => "DIFFERS from the game", _ => "not checked" };
                    Line($"{client.Id} {client.Name}: unlocked {Integrations.Deliveries.QuestDone(client.UnlockQuest)}, level {w.Rank}, satisfaction {w.Satisfaction}/{w.Required}, used {w.Used}/{client.PerWeek}, "
                         + $"requests [{string.Join(", ", w.Requests.Select(r => r == null ? "-" : $"{r.Item.ItemId}{(r.Bonus ? " bonus" : "")}"))}], {checkedText}");
                }
            }

            if (catalog?.Deliveries is { Clients.Count: > 0 })
            {
                var d = plugin.Config.Deliveries;
                Line($"Settings: goal {d.Goal?.ToString() ?? "automatic"}, left out [{string.Join(",", d.LeftOut)}], prefer craft/gather {d.PreferCraftGather}, "
                     + $"anyway {d.DeliverAnywayWhileRanking}, pause for cutscenes {d.PauseForCutscenes}, consumables {d.UseConsumables}");
                Line($"Run: {(plugin.DeliveryLoop.Active ? "running" : "idle")}, {plugin.DeliveryLoop.Status}; delivered {plugin.DeliveryLoop.Delivered}, planned {plugin.DeliveryLoop.Planned}");
                foreach (var e in plugin.DeliveryLoop.Lines.Reverse().Take(25)) Line($"{e.At:HH:mm:ss} {(e.Problem ? "[!] " : "")}{e.Text}");
            }
        });

        // ---------------------------------------------------------------- Allied societies (Tribes)
        if (catalog?.Tribes is { Tribes.Count: > 0 } tribes)
        {
            Part("Tribes", () =>
            {
                Section("Tribes");
                var ready = Integrations.Tribes.Ready;
                Line($"State from the game: {(ready ? "yes" : "no")}, seed {Integrations.Tribes.Seed}, {Integrations.Tribes.AllowancesLeft} of {Integrations.Tribes.DailyAllowance} allowances left, "
                     + $"day {Integrations.Tribes.DayNumber}, resets {Integrations.Tribes.NextReset:yyyy-MM-dd HH:mm}");
                var t = plugin.Config.Tribes;
                Line($"Settings: goal {t.Goal?.ToString() ?? "automatic"}, left out [{string.Join(",", t.LeftOut)}], pause for cutscenes {t.PauseForCutscenes}, leave out /say {t.LeaveOutSay}");
                if (ready)
                {
                    var accepted = Integrations.Tribes.Accepted(tribes);
                    var slots = Integrations.Tribes.DailySlots();
                    Line($"Today's slots: [{string.Join(", ", slots.Select(x => $"{x.Row - 0x10000}{(x.Done ? " done" : "")}"))}]");
                    foreach (var a in accepted)
                        Line($"In the journal: {a.Quest.Number} \"{a.Quest.Name}\" ({a.Quest.Kind}), step {a.Sequence}, taken as {ClassJobs.Abbreviation(a.AcceptClassJob)}{(a.Leftover ? ", LEFT OVER from before the reset" : "")}");
                    foreach (var society in tribes.Tribes)
                    {
                        var day = Integrations.Tribes.Today(tribes, society, accepted, slots);
                        if (!day.Unlocked)
                        {
                            Line($"{society.Id} {society.Name}: locked");
                            continue;
                        }
                        Line($"{society.Id} {society.Name}: rank {day.Rank}{(day.RankedUpToday ? " (ranked up today)" : "")}, reputation {day.Value}/{day.Needed}{(day.BarFull ? ", bar full" : "")}{(day.Allied ? ", allied" : "")}, taken today {day.Taken}");
                        foreach (var g in day.Givers.Where(g => g.Offers.Count > 0))
                        {
                            var check = Integrations.Tribes.Matches(g.Giver.NpcId) switch { true => "matches the game", false => "DIFFERS from the game", _ => "not checked" };
                            Line($"  giver {g.Giver.NpcId}: offers [{string.Join(", ", g.Offers.Select(o => $"{o.Quest.Number} {o.State}{(o.AcceptedAs != 0 ? " " + ClassJobs.Abbreviation(o.AcceptedAs) : "")}"))}], taken {g.Taken}, {check}");
                        }
                    }
                }
            });

            Part("The Tribes run", () =>
            {
                // The run: its state, the plan it goes by (or would), the stepper, and its log.
                Section("Tribes run");
                var ready = Integrations.Tribes.Ready;
                var loop = plugin.TribeLoop;
                foreach (var l in loop.Describe()) Line(l);
                var tribePlan = loop.Active ? loop.Plan : ready ? plugin.TribePlanning.Build() : Core.Tribes.TribePlan.Empty;
                Line($"Plan{(loop.Active ? "" : " (if started now)")}: goal {tribePlan.Goal}, {tribePlan.Allowances} allowances, {tribePlan.ToAccept} to accept, {tribePlan.Count} in all"
                     + (tribePlan.Blocked != null ? $"; no new dailies: {tribePlan.Blocked}" : ""));
                foreach (var v in tribePlan.Visits)
                    Line($"- {v.Tribe.Name}, giver {v.Giver.NpcId}, as {ClassJobs.Abbreviation(v.ClassJob)}: "
                         + string.Join("; ", v.Quests.Select(q => $"{q.Quest.Number}{(q.Accepted ? $" in the journal as {ClassJobs.Abbreviation(q.ClassJob)}" : "")}{(q.Leftover ? " (left over)" : "")}"
                                                                  + (q.Crystals.Count > 0 ? $", crystals {string.Join("+", q.Crystals.Select(c => $"{c.Amount}x{c.ItemId}"))}" : ""))));
                foreach (var l in tribePlan.LeftOut) Line($"  left out {l.Quest.Number} ({l.Tribe.Name}): {l.Reason}");
                foreach (var n in tribePlan.Notices) Line($"  notice {n.Tribe.Name}: {n.Text}");
                foreach (var e in loop.Lines.Reverse().Take(25)) Line($"{e.At:HH:mm:ss} {(e.Problem ? "[!] " : "")}{e.Text}");
            });

            Part("The society quests' enemies", () =>
            {
                // The society quests in the journal that fight: their enemy data, and the mobs around right now with
                // what the run's enemy filter makes of each. A report made next to them shows whether the filter holds.
                var lines = QuestWatch.DescribeEnemies();
                if (lines.Count == 0) return;
                Section("Quest enemies (allied society quests in the journal)");
                foreach (var l in lines) Line(l);
            });
        }

        // ---------------------------------------------------------------- Cosmic Exploration
        Part("Cosmic Exploration", () =>
        {
            var cosmic = plugin.Config.Cosmic;
            if (cosmic.Classes.Count > 0 || plugin.CosmicLoop.Lines.Count > 0)
            {
                Section("Cosmic Exploration");
                Line($"Planet {(cosmic.Planet < 0 ? "where you are" : ((Core.Cosmic.Planet)cosmic.Planet).ToString())} (on {Integrations.Cosmic.CurrentPlanet?.ToString() ?? "none"}), "
                     + $"classes {string.Join("/", cosmic.Classes.Select(Core.Character.ClassJobs.Abbreviation))}, goal {cosmic.Goal} {(cosmic.Goal == CosmicGoal.Cosmocredits ? cosmic.CreditsTarget.ToString() : "")}, "
                     + $"time limit {cosmic.TimeLimitMinutes} min, extras {cosmic.Extras}, shopping {cosmic.Shopping.Count}, consumables {cosmic.UseConsumables}, missions learned {cosmic.Records.Count}");
                Line($"Loop: {(plugin.CosmicLoop.Active ? "running" : "idle")}, {plugin.CosmicLoop.Status}; mission under way {Integrations.Cosmic.ActiveMission}, score {Integrations.Cosmic.Score}, rank {Integrations.Cosmic.Rank}");
                foreach (var e in plugin.CosmicLoop.Lines.Reverse().Take(25)) Line($"{e.At:HH:mm:ss} {(e.Problem ? "[!] " : "")}{e.Text}");
            }
        });

        Part("The debug log", () =>
        {
            // The run going (or the last one) and the one before it, whole: a problem early in a long run still shows,
            // and after a crash the run that crashed comes along.
            var sessions = Integrations.DebugLog.Sessions();
            foreach (var (name, lines, total) in sessions)
            {
                Section($"Debug log: {name} ({(lines.Count < total ? $"the last {lines.Count} of {total} lines" : $"{total} lines")}, oldest first)");
                foreach (var l in lines) Line(l);
            }
            if (sessions.Count == 0 && Integrations.DebugLog.Recent(300) is { Count: > 0 } recent)
            {
                Section("Debug log (no run yet; the last lines, oldest first)");
                foreach (var l in recent) Line(l);
            }
        });

        // ---------------------------------------------------------------- settings and plugins
        Part("The settings", () =>
        {
            Section("Settings");
            var c = plugin.Config;
            var settings = new Dictionary<string, object?>
            {
                ["Stock"] = c.Stock, ["Buying"] = c.Buying, ["Crafting"] = c.Crafting, ["Run"] = c.Run, ["Fishing"] = c.Fishing,
                ["Combat"] = c.Combat, ["AfterOrder"] = c.AfterOrder, ["CraftingSpot"] = c.CraftingSpot, ["Security"] = c.Security,
                ["Consumables"] = c.Consumables, ["Import"] = c.Import, ["Browse"] = c.Browse, ["Window"] = c.Window, ["Timings"] = c.Timings,
            };
            foreach (var (name, value) in settings)
            {
                string json;
                try { json = JsonSerializer.Serialize(value, Json); }
                catch (Exception e) { json = "(couldn't be written: " + e.Message + ")"; }
                Line($"{name}: {json}");
            }
        });

        Part("The plugins", () =>
        {
            Section("Plugins loaded");
            Line(string.Join(", ", Services.PluginInterface.InstalledPlugins.Where(p => p.IsLoaded).OrderBy(p => p.InternalName).Select(p => $"{p.InternalName} {p.Version}")));
        });
        return sb.ToString();
    }

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private static string Xyz(Vector3 p) => $"X {p.X:0.0}, Y {p.Y:0.0}, Z {p.Z:0.0}";

    private static string EorzeaTime(DateTimeOffset now)
    {
        var hour = Core.Timing.Eorzea.Hour(now);
        var h = (int)hour;
        return $"{h:00}:{(int)((hour - h) * 60):00}";
    }
}
