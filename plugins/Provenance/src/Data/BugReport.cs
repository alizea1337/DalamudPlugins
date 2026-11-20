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

        var catalog = plugin.Plans.Catalog;
        string Name(uint id) => catalog?.ItemName(id) ?? "item " + id;
        var now = DateTimeOffset.UtcNow;

        Line("Provenance bug report");
        Line($"Version {Plugin.Version}, game language {Services.ClientState.ClientLanguage}, made {DateTime.Now:yyyy-MM-dd HH:mm} local ({now:HH:mm} UTC), Eorzea {EorzeaTime(now)}");
        Line("No character name, world or retainer names are in this report.");
        Line($"Interface size {plugin.Config.Window.Scale}%, Dalamud scale {Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale:0.00}; fonts: {UI.Ui.F.Describe()}");

        // ---------------------------------------------------------------- where and what
        Section("Character");
        var me = Services.Objects.LocalPlayer;
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

        // ---------------------------------------------------------------- the order and its plan
        var order = plugin.Config.Order;
        Section($"Order \"{order.Name}\"");
        if (order.IsEmpty) Line("Empty.");
        foreach (var l in order.Lines.Where(l => l.Quantity > 0))
            Line($"- {l.Quantity}x {Name(l.ItemId)} ({l.ItemId}){(l.Hq ? ", HQ" : "")}{(l.RecipeId != 0 ? $", recipe {l.RecipeId}" : "")}");
        Line($"Favourite orders saved: {plugin.Config.Favourites.Count}");

        Section("Plan");
        var plan = plugin.Plans.Plan;
        if (plugin.Plans.LoadError is { } loadError) Line("Game data couldn't be read: " + loadError);
        foreach (var l in plan.Lines.OrderBy(l => l.Supply).ThenBy(l => Name(l.ItemId)))
        {
            var extra = new List<string>();
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

        // ---------------------------------------------------------------- the run
        var runs = plugin.Runs;
        Section("Run");
        foreach (var l in runs.Describe()) Line(l);
        Section("Gatherer");
        foreach (var l in runs.Gather.Describe()) Line(l);
        Section("Fisher");
        foreach (var l in runs.Fish.Describe()) Line(l);
        Section("Run log (newest first)");
        if (runs.Log.Count == 0) Line("Empty.");
        foreach (var e in runs.Log.Take(40)) Line($"{e.Time:HH:mm:ss} {(e.Problem ? "[!] " : "")}{e.Text}");

        // ---------------------------------------------------------------- settings and plugins
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

        Section("Plugins loaded");
        Line(string.Join(", ", Services.PluginInterface.InstalledPlugins.Where(p => p.IsLoaded).OrderBy(p => p.InternalName).Select(p => $"{p.InternalName} {p.Version}")));
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
