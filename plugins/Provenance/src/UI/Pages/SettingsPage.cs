using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Provenance.UI.Theme;

namespace Provenance.UI.Pages;

internal sealed class SettingsPage : Page
{
    private static readonly (string InternalName, string Name, string Role)[] Dependencies =
    {
        ("vnavmesh", "vnavmesh", "Walks and flies to vendors, summoning bells and monsters."),
    };

    public SettingsPage(Plugin plugin) : base(plugin) { }

    public override string Title => "Settings";

    public override (string Text, Vector4 Color)? Status => ("Changes are saved as you make them", Palette.TextFaint);

    private List<Integrations.Teleporter.Destination> aetherytes = new();
    private List<DropdownItem>? aetheryteItems;

    private void BellPicker()
    {
        var s = Ui.S;
        var id = Config.Run.BellAetheryte;
        var name = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>().GetRowOrDefault(id)?.PlaceName.ValueNullable?.Name.ExtractText() ?? "Aetheryte " + id;
        if (Ui.DropdownField("##bell", name, "Pick an aetheryte", 340 * s, FontAwesomeIcon.MapMarkerAlt))
        {
            aetherytes = Integrations.Teleporter.Unlocked().OrderBy(d => d.Name).ToList();
            // The zone, when the aetheryte's own name doesn't say it (Wineport: Eastern La Noscea).
            aetheryteItems = aetherytes.Select(d => new DropdownItem(d.Name, ZoneName(d.TerritoryId) is { } zone && zone != d.Name ? zone : null)).ToList();
        }
        var pick = Ui.DropdownList("##bell", aetheryteItems, aetherytes.FindIndex(d => d.AetheryteId == id), "No aetheryte unlocked.");
        if (pick >= 0) { Config.Run.BellAetheryte = aetherytes[pick].AetheryteId; Config.Save(); }
    }

    private static string? ZoneName(uint territoryId)
        => Services.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().GetRowOrDefault(territoryId)?.PlaceName.ValueNullable?.Name.ExtractText() is { Length: > 0 } n ? n : null;

    private static readonly string[] Stances = { "Free", "Defender", "Attacker", "Healer" };
    private static readonly string[] AfterPlaces = { "Stay", "Where it started", "Inn room", "Home" };
    private List<Integrations.Teleporter.Destination>? homes;
    private List<DropdownItem>? homeItems, innItems;

    /// <summary>Where to go once an order is finished: stay, back to the start, an inn room, or a house or apartment.</summary>
    private void DrawAfterOrder(float w)
    {
        var s = Ui.S;
        var a = Config.AfterOrder;
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("After the order");
        Ui.Wrapped("Where to go once an order is finished. A stopped run stays where it is.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        var place = (int)a.Place;
        if (Ui.Segmented("##afterplace", AfterPlaces, ref place, 440 * s)) { a.Place = (AfterOrderPlace)place; Config.Save(); }
        ImGui.Dummy(new Vector2(0, 4 * s));
        var catalog = Plans.Catalog;
        switch (a.Place)
        {
            case AfterOrderPlace.Stay:
                Ui.Wrapped("The character stays wherever the last step ended.", Palette.TextMuted);
                break;
            case AfterOrderPlace.WhereStarted:
                Ui.Wrapped("Teleports and walks back to the spot where you pressed Start. Started in an inn room: back into that inn.", Palette.TextMuted);
                break;
            case AfterOrderPlace.Inn:
            {
                var inns = catalog?.Inns ?? Array.Empty<Data.GameCatalog.Inn>();
                var current = inns.FirstOrDefault(i => i.NpcId == a.InnNpc);
                if (Ui.DropdownField("##inn", current?.City, "Pick an inn", 380 * s, FontAwesomeIcon.Bed, detail: current?.NpcName))
                    innItems = inns.Select(i => new DropdownItem(i.City, i.NpcName)).ToList();
                var pick = Ui.DropdownList("##inn", innItems, inns.ToList().FindIndex(i => i.NpcId == a.InnNpc), "No inns found in the game data.");
                if (pick >= 0) { a.InnNpc = inns[pick].NpcId; Config.Save(); }
                ImGui.Dummy(new Vector2(0, 2 * s));
                Ui.Wrapped("Travels to the innkeeper and takes a room.", Palette.TextMuted);
                break;
            }
            case AfterOrderPlace.Home:
            {
                if (Ui.DropdownField("##home", a.HomeAetheryte == 0 ? null : a.HomeLabel, "Pick a house or apartment", 380 * s, FontAwesomeIcon.Home))
                {
                    homes = Integrations.Teleporter.Homes();
                    homeItems = homes.Select(h => new DropdownItem(h.Estate ?? h.Name)).ToList();
                }
                var pick = Ui.DropdownList("##home", homeItems, homes?.FindIndex(h => h.AetheryteId == a.HomeAetheryte && h.SubIndex == a.HomeSubIndex) ?? -1,
                    "No house or apartment in your teleport list.");
                if (pick >= 0 && homes != null)
                {
                    var h = homes[pick];
                    a.HomeAetheryte = h.AetheryteId;
                    a.HomeSubIndex = h.SubIndex;
                    a.HomeLabel = h.Estate ?? h.Name;
                    Config.Save();
                }
                ImGui.Dummy(new Vector2(0, 2 * s));
                Ui.Wrapped("Teleports to the house or apartment entrance (any house or apartment in your teleport list).", Palette.TextMuted);
                break;
            }
        }
        Ui.EndCard();
    }
    private List<uint> combatJobs = new();
    private List<DropdownItem>? combatJobItems;

    /// <summary>A field with the picked job; it opens a searchable list of combat jobs with a saved gearset.</summary>
    private void CombatJobPicker()
    {
        var s = Ui.S;
        var c = Config.Combat;
        var picked = c.ClassJob != 0;
        if (Ui.DropdownField("##combatjob", picked ? JobName(c.ClassJob) : null, "Pick a combat job", 340 * s, FontAwesomeIcon.FistRaised,
                dot: picked ? Palette.ClassJobColor(c.ClassJob) : null, detail: picked ? $"level {Data.PlanService.JobLevel(c.ClassJob)}" : null))
        {
            combatJobs = Combat.BasicCombo.Jobs.Where(j => Integrations.GameState.BestGearset(j) >= 0)
                .OrderBy(j => Core.Character.ClassJobs.RoleOf(j)).ThenBy(JobName).ToList();
            combatJobItems = combatJobs.Select(j => new DropdownItem(JobName(j), $"level {Data.PlanService.JobLevel(j)}",
                Dot: Palette.ClassJobColor(j), Keywords: Core.Character.ClassJobs.Abbreviation(j))).ToList();
        }
        var pick = Ui.DropdownList("##combatjob", combatJobItems, combatJobs.IndexOf(c.ClassJob), "Save a gearset for a combat job first.");
        if (pick >= 0) { c.ClassJob = combatJobs[pick]; Config.Save(); }
    }

    private static string JobName(uint classJob)
    {
        var name = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>().GetRowOrDefault(classJob)?.Name.ExtractText() ?? "";
        return name.Length == 0 ? Core.Character.ClassJobs.Abbreviation(classJob)
            : string.Join(' ', name.Split(' ').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
    }

    /// <summary>Combat job for monster drops (jobs with a saved gearset), the chocobo, and when to heal.</summary>
    private void DrawCombat(float w)
    {
        var s = Ui.S;
        var c = Config.Combat;
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("Monster drops");
        Ui.Wrapped("Items only monsters drop are farmed with a basic combo on this job, in its highest item level gearset. One monster at a time; whatever attacks you is fought first.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        CombatJobPicker();
        ImGui.Dummy(new Vector2(0, 2 * s));
        var v = c.Chocobo;
        var greens = Integrations.GameState.BagCount(Combat.Chocobo.GysahlGreens);
        if (Ui.Checkbox("Call the chocobo", ref v, greens > 0
                ? $"Uses Gysahl Greens from your bags ({greens} there) and puts the chocobo in the stance below."
                : "Needs Gysahl Greens in your bags; without them the run fights alone."))
        { c.Chocobo = v; Config.Save(); }
        if (c.Chocobo)
        {
            var stance = (int)Math.Clamp(c.ChocoboStance, 4, 7) - 4;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 30 * s);
            if (Ui.Segmented("##stance", Stances, ref stance, 320 * s)) { c.ChocoboStance = (uint)(stance + 4); Config.Save(); }
        }
        ImGui.Dummy(new Vector2(0, 2 * s));
        Ui.Text("Heal below");
        ImGui.SameLine(0, 10 * s);
        var heal = c.HealBelow;
        ImGui.SetNextItemWidth(160 * s);
        if (ImGui.SliderInt("##healbelow", ref heal, 10, 90, "%d%% health")) { c.HealBelow = heal; Config.Save(); }
        Ui.Wrapped("The job's own heal (or Second Wind, or Rampart for tanks) when health drops under this.", Palette.TextMuted);
        Ui.EndCard();
    }

    private static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        return span.TotalHours < 1 ? $"{Math.Max(1, (int)span.TotalMinutes)} min ago" : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago" : utc.ToLocalTime().ToString("d MMM");
    }

    public override void Draw()
    {
        var s = Ui.S;
        var w = MathF.Min(ImGui.GetContentRegionAvail().X, 720 * s);
        Ui.Heading("Settings");
        ImGui.Dummy(new Vector2(0, 2 * s));

        // Owned items
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("Items you already have");
        Ui.Wrapped("Owned items are used before anything is gathered, bought or crafted. The armoury chest is never counted.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        var st = Config.Stock;
        var v = st.Inventory;
        if (Ui.Checkbox("Inventory and crystals", ref v)) { st.Inventory = v; Changed(); }
        v = st.Saddlebag;
        if (Ui.Checkbox("Saddlebag", ref v, "Counted once you've opened the saddlebag this session.")) { st.Saddlebag = v; Changed(); }
        v = st.Retainers;
        var known = Services.PlayerState.IsLoaded ? Data.RetainerCache.For(Config, Services.PlayerState.ContentId) : null;
        var retainerHint = known is { Count: > 0 }
            ? $"Counts what your {known.Count} known retainers held when last opened; a run takes it out at a summoning bell."
            : "Open each retainer once at a summoning bell so Provenance knows what they hold; a run then takes it out.";
        if (Ui.Checkbox("Retainers", ref v, retainerHint)) { st.Retainers = v; Changed(); }
        if (st.Retainers && known is { Count: > 0 })
        {
            foreach (var r in known.Values.OrderBy(r => r.Name))
            {
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 30 * s);
                using (Ui.F.Small.Push()) Ui.Muted($"{r.Name}: {r.Items.Count} kinds of items, seen {Ago(r.SeenUtc)}");
            }
        }
        v = st.CountFinishedItems;
        if (Ui.Checkbox("Count ordered items I already have", ref v, "Off: ordered items are crafted or gathered in full, on top of what's already in your bags. Materials always use what you have.")) { st.CountFinishedItems = v; Changed(); }
        Ui.EndCard();

        // Vendors
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("Vendors");
        Ui.Wrapped("Materials sold for gil are bought up to this price per item. Anything more expensive becomes an item to get yourself.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        var price = (int)Config.Buying.VendorMaxPrice;
        ImGui.SetNextItemWidth(160 * s);
        if (ImGui.InputInt("##vendorcap", ref price, 100, 1000)) { Config.Buying.VendorMaxPrice = (uint)Math.Clamp(price, 0, 9_999_999); Changed(); }
        ImGui.SameLine(0, 10 * s);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 6 * s);
        Ui.Muted(Config.Buying.VendorMaxPrice == 0 ? "gil per item, no limit" : "gil per item");
        v = Config.Buying.PreferVendor;
        if (Ui.Checkbox("Buy instead of gathering when a vendor sells it", ref v, "Faster for common materials such as shards and basic ores.")) { Config.Buying.PreferVendor = v; Changed(); }
        Ui.EndCard();

        // Crafting
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("Crafting");
        Ui.Wrapped("Rotations come from Raphael and are worked out for your stats right before each recipe, with the food and medicine from the Character page.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        var cr = Config.Crafting;
        v = cr.AimForHq;
        if (Ui.Checkbox("High quality by default", ref v, "New items in the order start with HQ on; switch it per item in Browse or on the plan. Intermediate materials always get the fastest rotation.")) { cr.AimForHq = v; Config.Save(); }
        v = cr.UseSpecialistActions;
        if (Ui.Checkbox("Use specialist actions", ref v, "Heart and Soul and Quick Innovation, when a specialist soul is equipped.")) { cr.UseSpecialistActions = v; Config.Save(); }
        Ui.Text("Repair gear below");
        ImGui.SameLine(0, 10 * s);
        var repair = cr.RepairBelow;
        ImGui.SetNextItemWidth(160 * s);
        if (ImGui.SliderInt("##repair", ref repair, 0, 99, repair == 0 ? "never" : "%d%%")) { cr.RepairBelow = repair; Config.Save(); }
        Ui.Wrapped("Self-repair needs Dark Matter of a high enough grade in your bags.", Palette.TextMuted);
        Ui.EndCard();

        // Run
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("Gathering");
        Ui.Wrapped("When only timed nodes are left and none is up, the run goes to where the next one spawns, crafts what it already has materials for, and hands back to gathering shortly before the window opens.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        var rs = Config.Run;
        v = rs.CraftWhileWaiting;
        if (Ui.Checkbox("Craft while waiting for timed nodes", ref v)) { rs.CraftWhileWaiting = v; Config.Save(); }
        v = rs.TravelAhead;
        if (Ui.Checkbox("Go to the next timed node while waiting", ref v, "Teleports to its zone and rides to where it spawns, then waits or crafts there.")) { rs.TravelAhead = v; Config.Save(); }
        Ui.Text("Back to gathering");
        ImGui.SameLine(0, 10 * s);
        var lead = rs.TimedLeadMinutes;
        ImGui.SetNextItemWidth(160 * s);
        if (ImGui.SliderInt("##lead", ref lead, 1, 10, "%d min before")) { rs.TimedLeadMinutes = lead; Config.Save(); }
        Ui.Wrapped("Real minutes before a window opens; enough for the craft under way and the trip to the node.", Palette.TextMuted);
        ImGui.Dummy(new Vector2(0, 4 * s));
        v = rs.DiscardExtras;
        if (Ui.Checkbox("Throw away extra gathered items", ref v, "A node is always gathered to the end, so the last one usually brings in a few more than the order needs. With this on, the run throws those away once gathering is done (normal quality only, never what you had before).")) { rs.DiscardExtras = v; Config.Save(); }
        if (rs.DiscardExtras)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 30 * s);
            v = rs.DiscardTimedExtras;
            if (Ui.Checkbox("Also from timed nodes", ref v, "Unspoiled, legendary and ephemeral nodes often bring in far more than the order needs. Off keeps those extras.")) { rs.DiscardTimedExtras = v; Config.Save(); }
        }
        v = rs.ExtractMateria;
        if (Ui.Checkbox("Extract materia", ref v, "Between nodes, extracts materia from gear at 100% spiritbond (needs the materia extraction quest).")) { rs.ExtractMateria = v; Config.Save(); }
        v = rs.ReduceCollectables;
        if (Ui.Checkbox("Reduce spare collectables", ref v, "When the bags run low on space, aetherially reduces collectables the order doesn't need. Never runs while a collectable the order or the scrip earning needs is in the bags.")) { rs.ReduceCollectables = v; Config.Save(); }
        Ui.EndCard();

        // After the order
        DrawAfterOrder(w);

        // Monster drops
        DrawCombat(w);

        // Books and scrips
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("Books and scrips");
        Ui.Wrapped("Master books and folklore tomes the order needs are bought at a scrip exchange. When they cost more scrips than you have, the run can earn them first.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        v = rs.EarnScrips;
        if (Ui.Checkbox("Earn scrips for books", ref v, "Makes or gathers the quickest collectables your jobs can handle (fewest items for the scrips missing), turns them in at a collectable appraiser, buys the book, then runs the order.")) { rs.EarnScrips = v; Config.Save(); Plans.Invalidate(); }
        Ui.EndCard();

        // Summoning bell
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("Summoning bell");
        Ui.Wrapped("When no summoning bell is nearby, the run teleports to this aetheryte and uses the bell there. Pick one with a bell close by (the Limsa Lominsa Lower Decks plaza has several).");
        ImGui.Dummy(new Vector2(0, 2 * s));
        BellPicker();
        Ui.EndCard();

        // Plugins
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("Plugins Provenance works with");
        Ui.Wrapped("A run needs these installed and enabled. Planning works without them.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        foreach (var dep in Dependencies)
        {
            var installed = Services.PluginInterface.InstalledPlugins.FirstOrDefault(p => p.InternalName == dep.InternalName);
            var (label, color) = installed == null ? ("not installed", Palette.Danger)
                : installed.IsLoaded ? ($"loaded, {installed.Version}", Palette.Accent)
                : ("installed but disabled", Palette.Warn);
            ImGui.BeginGroup();
            using (Ui.F.Strong.Push()) Ui.Text(dep.Name);
            ImGui.SameLine(0, 10 * s);
            Ui.Chip(label, color);
            using (Ui.F.Small.Push()) Ui.Muted(dep.Role);
            ImGui.EndGroup();
        }
        Ui.EndCard();

        using (Ui.F.Small.Push()) Ui.Muted($"Provenance {Plugin.Version}");
    }
}
