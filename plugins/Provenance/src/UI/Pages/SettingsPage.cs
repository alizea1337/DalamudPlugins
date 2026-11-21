using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Provenance.UI.Theme;

namespace Provenance.UI.Pages;

/// <summary>The settings pages in the sidebar, one per part of a run.</summary>
internal enum SettingsSection
{
    General,
    /// <summary>Interface scale, text size, tooltip size, motion.</summary>
    Interface,
    Crafting,
    Gathering,
    Fishing,
    MonsterDrops,
    AfterOrder,
}

/// <summary>One of the settings pages; which one is fixed when it's created.</summary>
internal sealed class SettingsPage : Page
{
    private static readonly (string InternalName, string Name, string Role)[] Dependencies =
    {
        ("vnavmesh", "vnavmesh", "Walks and flies to vendors, summoning bells, nodes, fishing spots and monsters."),
    };

    private readonly SettingsSection section;

    public SettingsPage(Plugin plugin, SettingsSection section) : base(plugin) => this.section = section;

    public override string Title => section switch
    {
        SettingsSection.General => "General",
        SettingsSection.Interface => "Interface",
        SettingsSection.Crafting => "Crafting",
        SettingsSection.Gathering => "Gathering",
        SettingsSection.Fishing => "Fishing",
        SettingsSection.MonsterDrops => "Monster drops",
        _ => "After the order",
    };

    public override FontAwesomeIcon Icon => section switch
    {
        SettingsSection.General => FontAwesomeIcon.SlidersH,
        SettingsSection.Interface => FontAwesomeIcon.Desktop,
        SettingsSection.Crafting => FontAwesomeIcon.Hammer,
        SettingsSection.Gathering => FontAwesomeIcon.Leaf,
        SettingsSection.Fishing => FontAwesomeIcon.Fish,
        SettingsSection.MonsterDrops => FontAwesomeIcon.Paw,
        _ => FontAwesomeIcon.Home,
    };

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
    private static readonly string[] AfterPlaces = { "Stay", "Where it started", "Inn room", "Home", "Crafting spot" };
    private List<Integrations.Teleporter.Destination>? homes;
    private List<DropdownItem>? homeItems, innItems;

    /// <summary>Where to go once an order is finished: stay, back to the start, an inn room, or a house or apartment.</summary>
    private void DrawAfterOrder(float w)
    {
        var s = Ui.S;
        var a = Config.AfterOrder;
        Ui.Heading("After the order", "Where to go once an order is finished. A stopped run stays where it is.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        Ui.BeginCard(width: w);
        var place = (int)a.Place;
        if (Ui.Segmented("##afterplace", AfterPlaces, ref place, 560 * s)) { a.Place = (AfterOrderPlace)place; Config.Save(); }
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
            case AfterOrderPlace.CraftingSpot:
                if (Config.CraftingSpot.IsSet)
                    Ui.Wrapped($"Goes back to the crafting spot: {Config.CraftingSpot.Label}, through the door when it's in a house or apartment.", Palette.TextMuted);
                else
                    Ui.Wrapped("No crafting spot saved yet. Save one on the Crafting page.", Palette.Warn);
                break;
        }
        Ui.EndCard();
    }

    private string? spotProblem;

    /// <summary>The crafting spot: saved where you stand, gone to before the run crafts.</summary>
    private void DrawCraftingSpot(float w)
    {
        var s = Ui.S;
        var spot = Config.CraftingSpot;
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("Crafting spot");
        Ui.Wrapped("Where the run goes before it crafts: a corner of your house or apartment, an inn room, or anywhere a teleport and a walk reach. "
                   + "Stand there and save it. Crafting while waiting for timed nodes goes there too when there's time to come back; "
                   + "otherwise it happens by an aetheryte in the node's zone, away from monsters.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        if (spot.IsSet)
        {
            var icon = spot.Kind switch
            {
                CraftingSpotKind.House => FontAwesomeIcon.Home,
                CraftingSpotKind.Apartment => FontAwesomeIcon.Building,
                CraftingSpotKind.InnRoom => FontAwesomeIcon.Bed,
                _ => FontAwesomeIcon.MapMarkerAlt,
            };
            Ui.Icon(icon, Palette.Accent);
            ImGui.SameLine(0, 10 * s);
            Ui.Text(spot.Label);
            var v = spot.Enabled;
            if (Ui.Checkbox("Go there before crafting", ref v, "Off keeps the spot saved, and the run crafts wherever it is.")) { spot.Enabled = v; Config.Save(); }
        }
        else Ui.Muted("No spot saved yet.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        var runs = Plugin.Runs;
        if (Ui.Button("Save where I'm standing", Ui.Kind.Secondary, FontAwesomeIcon.MapMarkerAlt, enabled: !runs.TestingSpot, id: "##savespot"))
        {
            var (captured, why) = Run.CraftingSpots.Capture(Plans.Catalog);
            spotProblem = why;
            if (captured != null) { Config.CraftingSpot = captured; Config.Save(); }
        }
        if (spot.IsSet)
        {
            // Trying it out: the trip alone, the way a run makes it before crafting.
            ImGui.SameLine(0, 10 * s);
            if (runs.TestingSpot)
            {
                if (Ui.Button("Stop", Ui.Kind.Danger, FontAwesomeIcon.Stop, id: "##stopspot")) runs.StopSpotTest();
            }
            else if (Ui.Button("Go there now", Ui.Kind.Secondary, FontAwesomeIcon.Play, enabled: !runs.Active,
                         tooltip: runs.Active ? "A run is going." : "Makes the trip a run makes before crafting, so you can watch it.", id: "##testspot"))
                spotProblem = runs.TestSpot();
            ImGui.SameLine(0, 10 * s);
            if (Ui.Button("Forget it", Ui.Kind.Ghost, enabled: !runs.TestingSpot, id: "##forgetspot")) { Config.CraftingSpot = new CraftingSpotSettings(); spotProblem = null; Config.Save(); }
        }
        if (runs.TestingSpot || runs.SpotTestStatus.Length > 0)
        {
            var color = runs.TestingSpot ? Palette.Accent : runs.SpotTestFailed ? Palette.Warn : Palette.TextMuted;
            Ui.Icon(runs.TestingSpot ? FontAwesomeIcon.Route : runs.SpotTestFailed ? FontAwesomeIcon.ExclamationTriangle : FontAwesomeIcon.CheckCircle, color, small: true);
            ImGui.SameLine(0, 8 * s);
            Ui.Wrapped(runs.SpotTestStatus.Length > 0 ? runs.SpotTestStatus : "Setting off", runs.TestingSpot ? Palette.Text : color);
        }
        if (spotProblem != null)
        {
            Ui.Icon(FontAwesomeIcon.ExclamationTriangle, Palette.Warn, small: true);
            ImGui.SameLine(0, 8 * s);
            Ui.Wrapped(spotProblem, Palette.Warn);
        }
        Ui.EndCard();
    }
    private List<uint> combatJobs = new();
    private List<DropdownItem>? combatJobItems;

    private static readonly int[] Percentages = { 75, 90, 100, 110, 125, 150 };
    private static readonly List<DropdownItem> PercentageItems = Percentages
        .Select(p => new DropdownItem($"{p}%", p == 100 ? "default" : null)).ToList();

    /// <summary>A size in percent from a drop-down (interface scale, text size).</summary>
    private void PercentPicker(string id, int value, Action<int> set, FontAwesomeIcon icon)
    {
        var s = Ui.S;
        var current = Array.IndexOf(Percentages, value);
        Ui.DropdownField(id, $"{value}%", "Pick a size", 220 * s, icon);
        var pick = Ui.DropdownList(id, PercentageItems, current, "");
        if (pick >= 0) { set(Percentages[pick]); Config.Save(); }
    }

    /// <summary>How big the window and its text are, the tooltips, and motion.</summary>
    private void DrawInterface(float w)
    {
        var s = Ui.S;
        var win = Config.Window;
        Ui.Heading("Interface", "How big the window and its text are, the tooltips, and how much moves.");
        ImGui.Dummy(new Vector2(0, 2 * s));

        Ui.BeginCard(width: w);
        // Header: "Size" on the left; on the right, while anything here isn't at its default, a way back to it.
        const string resetLabel = "Reset to defaults";
        var resetSize = Ui.LinkButtonSize(resetLabel, FontAwesomeIcon.Undo);
        var head = ImGui.GetCursorScreenPos();
        using (Ui.F.Strong.Push())
        {
            ImGui.SetCursorScreenPos(head + new Vector2(0, MathF.Max(0, (resetSize.Y - ImGui.GetTextLineHeight()) / 2)));
            Ui.Text("Size");
        }
        if (Ui.F.Building)
        {
            // The sharp fonts for a new size are being built; the text is stretched to it meanwhile.
            ImGui.SameLine(0, 10 * s);
            using (Ui.F.Small.Push()) Ui.Muted("sharpening the text…");
        }
        var below = ImGui.GetCursorScreenPos();
        if (win.Scale != 100 || win.TextScale != 100 || win.TooltipSize != TooltipSize.Normal || !Plugin.Main.AtDefaultSize)
        {
            ImGui.SetCursorScreenPos(new Vector2(head.X + Ui.CardInnerWidth - resetSize.X, head.Y));
            if (Ui.LinkButton(resetLabel, Palette.TextMuted, FontAwesomeIcon.Undo,
                    "Interface scale and text size back to 100%, tooltips to Normal, and the window to its first size. Reduce motion stays as it is.", "##resetinterface"))
            {
                win.Scale = 100;
                win.TextScale = 100;
                win.TooltipSize = TooltipSize.Normal;
                Plugin.Main.ResetSize();
                Config.Save();
            }
        }
        ImGui.SetCursorScreenPos(new Vector2(head.X, MathF.Max(below.Y, head.Y + resetSize.Y)));
        ImGui.Dummy(new Vector2(0, 2 * s));
        Ui.Text("Interface scale");
        PercentPicker("##uiscale", win.Scale, v => win.Scale = v, FontAwesomeIcon.ExpandArrowsAlt);
        using (Ui.F.Small.Push()) Ui.Wrapped("Spacing, buttons, icons and the window itself, on top of Dalamud's own scale. The text keeps its own size.", Palette.TextMuted);
        ImGui.Dummy(new Vector2(0, 4 * s));
        Ui.Text("Text size");
        PercentPicker("##textscale", win.TextScale, v => win.TextScale = v, FontAwesomeIcon.Font);
        using (Ui.F.Small.Push()) Ui.Wrapped("All the text in the window: item names, page names, headings and buttons.", Palette.TextMuted);
        ImGui.Dummy(new Vector2(0, 4 * s));
        Ui.Text("Tooltip size");
        TooltipSizePicker();
        Ui.EndCard();

        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("Motion");
        var v = win.ReduceMotion;
        if (Ui.Checkbox("Reduce motion", ref v, "Pages, the sidebar and switches change in place instead of sliding. Fades stay, shorter.")) { win.ReduceMotion = v; Config.Save(); }
        Ui.EndCard();
    }

    private static readonly string[] TooltipSizeNames = { "Small", "Normal", "Large", "Larger", "Largest" };
    private static readonly List<DropdownItem> TooltipSizeItems = TooltipSizeNames
        .Select(n => new DropdownItem(n, n == "Normal" ? "default" : null)).ToList();

    /// <summary>The tooltip size field, with a sample to hover next to it.</summary>
    private void TooltipSizePicker()
    {
        var s = Ui.S;
        var current = (int)Config.Window.TooltipSize;
        Ui.DropdownField("##tooltipsize", TooltipSizeNames[current], "Pick a size", 220 * s, FontAwesomeIcon.CommentAlt);
        ImGui.SameLine(0, 14 * s);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (34 * s - ImGui.GetTextLineHeight()) / 2);
        ImGui.BeginGroup();
        Ui.Icon(FontAwesomeIcon.InfoCircle, Palette.TextFaint, small: true);
        ImGui.SameLine(0, 6 * s);
        Ui.Muted("Hover for a sample");
        ImGui.EndGroup();
        Ui.Tooltip("Tooltips look like this. They explain the chips, buttons and plan rows you hover across Provenance.");
        var pick = Ui.DropdownList("##tooltipsize", TooltipSizeItems, current, "");
        if (pick >= 0) { Config.Window.TooltipSize = (TooltipSize)pick; Config.Save(); }
    }

    /// <summary>A field with the picked job; it opens a searchable list of combat jobs with a saved gearset.</summary>
    private void CombatJobPicker()
    {
        var s = Ui.S;
        var c = Config.Combat;
        var picked = c.ClassJob != 0;
        if (Ui.DropdownField("##combatjob", picked ? JobName(c.ClassJob) : null, "Pick a combat job", 340 * s, FontAwesomeIcon.FistRaised,
                job: c.ClassJob, detail: picked ? $"level {Data.PlanService.JobLevel(c.ClassJob)}" : null))
        {
            combatJobs = Combat.BasicCombo.Jobs.Where(j => Integrations.GameState.BestGearset(j) >= 0)
                .OrderBy(j => Core.Character.ClassJobs.RoleOf(j)).ThenBy(JobName).ToList();
            combatJobItems = combatJobs.Select(j => new DropdownItem(JobName(j), $"level {Data.PlanService.JobLevel(j)}",
                Keywords: Core.Character.ClassJobs.Abbreviation(j), Job: j)).ToList();
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
        Ui.Heading("Monster drops", "Items only monsters drop are farmed with a basic combo on the job picked here, in its highest item level gearset. One monster at a time; whatever attacks you is fought first.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        Ui.BeginCard(width: w);
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

    /// <summary>Rod fishing and spearfishing in a run: buying bait, the Versatile Lure stand-in, buffs and cordials, fish windows, by-catch.</summary>
    private void DrawFishing(float w)
    {
        var s = Ui.S;
        var f = Config.Fishing;
        Ui.Heading("Fishing", "Fish are caught from recorded places to stand, with the bait each one bites on. Only the fish the order needs, and the ones that lead to them (mooching, Fisher's Intuition), are hooked; anything else is let go.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        Ui.BeginCard(width: w);
        var v = f.BuyBait;
        if (Ui.Checkbox("Buy bait", ref v, "Buys the bait each fish needs before fishing: from a gil vendor (up to the vendor price cap under General), "
                + "or at a scrip exchange when only that sells it, with your scrips or ones the run earns first. More from a gil vendor if it runs out during the run.")) { f.BuyBait = v; Changed(); }
        if (f.BuyBait)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 30 * s);
            Ui.Text("Spare bait");
            ImGui.SameLine(0, 10 * s);
            var spare = f.SpareBait;
            ImGui.SetNextItemWidth(160 * s);
            if (ImGui.InputInt("##sparebait", ref spare, 5, 20)) { f.SpareBait = Math.Clamp(spare, 0, 999); Changed(); }
            ImGui.SameLine(0, 10 * s);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 6 * s);
            Ui.Muted("per kind, on top of what the catches need");
        }
        v = f.VersatileLureFallback;
        if (Ui.Checkbox("Versatile Lure when the bait can't be bought", ref v, "Some bait has to be crafted, or is sold for scrips you don't have and the run won't earn. With this on, those fish are caught with Versatile Lure from Limsa Lominsa Lower Decks instead: every fish there bites on it, so the one you want comes much more slowly. Off: that bait has to be in your bags.")) { f.VersatileLureFallback = v; Changed(); }
        v = f.UseBuffs;
        if (Ui.Checkbox("Use fishing buffs", ref v, "Patience, Prize Catch, Makeshift Bait, Chum, Surface Slap and Identical Cast, when the catch needs them and there's GP. "
                + "Lures are always used for fish that only come to a lure, and Mooch II and Thaliak's Favor whenever a catch needs them.")) { f.UseBuffs = v; Config.Save(); }
        v = f.UseCordials;
        if (Ui.Checkbox("Drink cordials for GP", ref v, "Uses a Hi-Cordial, Cordial or Watered Cordial from your bags when a buff the catch needs is short of GP. Never buys any.")) { f.UseCordials = v; Config.Save(); }
        Ui.Text("Be at the fishing hole");
        ImGui.SameLine(0, 10 * s);
        var lead = f.WindowLeadMinutes;
        ImGui.SetNextItemWidth(160 * s);
        if (ImGui.SliderInt("##fishlead", ref lead, 0, 30, "%d min before")) { f.WindowLeadMinutes = lead; Config.Save(); }
        Ui.Wrapped("Real minutes before a fish's time or weather window opens. Catching the fish for Fisher's Intuition comes on top. "
                   + "Crafting while waiting stops at the longer of this and \"Back to gathering\" under Gathering.", Palette.TextMuted);
        ImGui.Dummy(new Vector2(0, 4 * s));
        v = f.DiscardByCatch;
        if (Ui.Checkbox("Throw away fish caught on the side", ref v, "Fish the order doesn't need are kept while fishing and thrown away once gathering is done: main bags only, never anything the order uses, never what you had before.")) { f.DiscardByCatch = v; Config.Save(); }
        v = f.FishForReduction;
        if (Ui.Checkbox("Fish for aetherial reduction", ref v, "Many items from aetherial reduction come from a fish as well as from gathered collectables. "
                + "On: the run also fishes for them, with their bait, while no node of theirs is up. Off: only gathered collectables are reduced, and the run waits for their nodes (crafting meanwhile); "
                + "an item that only comes from fish is then one to get yourself.")) { f.FishForReduction = v; Changed(); }
        Ui.EndCard();
    }

    private static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        return span.TotalHours < 1 ? $"{Math.Max(1, (int)span.TotalMinutes)} min ago" : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago" : utc.ToLocalTime().ToString("d MMM");
    }

    public override void Draw()
    {
        var w = MathF.Min(ImGui.GetContentRegionAvail().X, 720 * Ui.S);
        switch (section)
        {
            case SettingsSection.General: DrawGeneral(w); break;
            case SettingsSection.Interface: DrawInterface(w); break;
            case SettingsSection.Crafting: DrawCrafting(w); break;
            case SettingsSection.Gathering: DrawGathering(w); break;
            case SettingsSection.Fishing: DrawFishing(w); break;
            case SettingsSection.MonsterDrops: DrawCombat(w); break;
            default: DrawAfterOrder(w); break;
        }
    }

    /// <summary>Owned items, vendors, scrips, the summoning bell, the plugins a run needs, and the bug report.</summary>
    private void DrawGeneral(float w)
    {
        var s = Ui.S;
        Ui.Heading("General", "What a run counts as yours, what it may buy, and what it works with.");
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

        // Scrips
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("Scrips");
        Ui.Wrapped("Master books, folklore tomes and materials the order needs that are sold at a scrip exchange (white, purple or orange scrips) are bought there. "
                   + "When they cost more scrips than you have, the run can earn them first.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        var rs = Config.Run;
        v = rs.EarnScrips;
        if (Ui.Checkbox("Earn scrips when short", ref v, "Makes or gathers the quickest collectables your jobs can handle (fewest items for the scrips missing), turns them in at a collectable appraiser, buys what the order needs, then runs the order.")) { rs.EarnScrips = v; Config.Save(); Plans.Invalidate(); }
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

        DrawBugReport(w);
    }

    /// <summary>The last copy of the bug report: when, how long, and whether the GitHub form was opened with it.</summary>
    private (DateTime At, int Lines, bool Opened)? reportCopied;

    /// <summary>Copies what Provenance knows right now, to paste into a bug report.</summary>
    private void CopyBugReport(bool opened)
    {
        var text = Data.BugReport.Build(Plugin);
        ImGui.SetClipboardText(text);
        reportCopied = (DateTime.UtcNow, text.Split('\n').Length, opened);
    }

    private void DrawBugReport(float w)
    {
        var s = Ui.S;
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("Bug report");
        Ui.Wrapped("Copies what Provenance knows right now: the settings, the order and its plan, what the run is doing and its log, "
                   + "the gathering nodes around you and where you stand. Your character's name, world and retainers aren't in it. "
                   + "Open on GitHub also opens a new bug report with the plugin and version filled in, to paste it into. "
                   + "For Discord, copying is enough: a long one becomes a text file.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        var lineStart = ImGui.GetCursorScreenPos().X;
        if (Ui.Button("Copy bug report", Ui.Kind.Secondary, FontAwesomeIcon.Bug, id: "##bugreport"))
            CopyBugReport(opened: false);
        ImGui.SameLine(0, 8 * s);
        if (Ui.Button("Open on GitHub", Ui.Kind.Secondary, FontAwesomeIcon.ExternalLinkAlt, id: "##bugreportgithub",
                tooltip: "Copies the report and opens a new bug report on GitHub in your browser. Paste it into the \"Provenance bug report\" box there."))
        {
            CopyBugReport(opened: true);
            Dalamud.Utility.Util.OpenLink(Data.BugReport.IssueFormUrl());
        }
        if (reportCopied is { } copied && DateTime.UtcNow - copied.At < TimeSpan.FromSeconds(copied.Opened ? 12 : 6))
        {
            var note = copied.Opened ? $"Copied, {copied.Lines} lines. Paste it into the page that just opened." : $"Copied, {copied.Lines} lines";
            // Beside the buttons when it fits; on the line below when the card is narrow or the text large.
            var width = 18 * s + ImGui.CalcTextSize(note).X;
            if (ImGui.GetItemRectMax().X - lineStart + 12 * s + width <= Ui.CardInnerWidth)
            {
                ImGui.SameLine(0, 12 * s);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (34 * s - ImGui.GetTextLineHeight()) / 2);
            }
            else ImGui.Dummy(new Vector2(0, 2 * s));
            Ui.Icon(FontAwesomeIcon.Check, Palette.Accent, small: true);
            ImGui.SameLine(0, 6 * s);
            Ui.Text(note, Palette.Accent);
        }
        using (Ui.F.Small.Push())
            Ui.Wrapped("Copy it right when something goes wrong, while the run still shows it. In the middle of a run, /prov bugreport does the same without opening this page.", Palette.TextMuted);
        Ui.EndCard();
    }

    private void DrawCrafting(float w)
    {
        var s = Ui.S;
        Ui.Heading("Crafting", "Rotations come from Raphael and are worked out for your stats right before each recipe, with the food and medicine from the Character page.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        Ui.BeginCard(width: w);
        var cr = Config.Crafting;
        var v = cr.AimForHq;
        if (Ui.Checkbox("High quality by default", ref v, "New items in the order start with HQ on; switch it per item in Browse or on the plan. Crafted materials that go into an HQ item are made HQ too; everything else gets the fastest rotation.")) { cr.AimForHq = v; Config.Save(); }
        v = cr.UseSpecialistActions;
        if (Ui.Checkbox("Use specialist actions", ref v, "Heart and Soul and Quick Innovation, when a specialist soul is equipped.")) { cr.UseSpecialistActions = v; Config.Save(); }
        Ui.Text("Repair gear below");
        ImGui.SameLine(0, 10 * s);
        var repair = cr.RepairBelow;
        ImGui.SetNextItemWidth(160 * s);
        if (ImGui.SliderInt("##repair", ref repair, 0, 99, repair == 0 ? "never" : "%d%%")) { cr.RepairBelow = repair; Config.Save(); }
        Ui.Wrapped("Self-repair needs Dark Matter of a high enough grade in your bags.", Palette.TextMuted);
        Ui.EndCard();
        DrawCraftingSpot(w);
    }

    /// <summary>Timed nodes and waiting, extras, collectables, materia and reduction.</summary>
    private void DrawGathering(float w)
    {
        var s = Ui.S;
        Ui.Heading("Gathering", "When only timed nodes and fish with time or weather windows are left and none is open, the run crafts what it already has materials for "
                   + "and hands back to gathering or fishing shortly before the next window opens. For a timed node it first goes to where the node spawns.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        Ui.BeginCard(width: w);
        var rs = Config.Run;
        var v = rs.CraftWhileWaiting;
        if (Ui.Checkbox("Craft while waiting for timed nodes and fish windows", ref v)) { rs.CraftWhileWaiting = v; Config.Save(); }
        v = rs.TravelAhead;
        if (Ui.Checkbox("Go to the next timed node while waiting", ref v, "Teleports to its zone and rides to where it spawns, then waits there on the mount. "
                + "When there's crafting to do meanwhile, it's done somewhere safe from monsters instead: the crafting spot, or an aetheryte in the node's zone.")) { rs.TravelAhead = v; Config.Save(); }
        Ui.Text("Back to gathering");
        ImGui.SameLine(0, 10 * s);
        var lead = rs.TimedLeadMinutes;
        ImGui.SetNextItemWidth(160 * s);
        if (ImGui.SliderInt("##lead", ref lead, 1, 10, "%d min before")) { rs.TimedLeadMinutes = lead; Config.Save(); }
        Ui.Wrapped("Real minutes before a timed node or fish window opens; enough for the craft under way and the trip there. "
                   + "For fish, \"Be at the fishing hole\" under Fishing counts instead when it is longer.", Palette.TextMuted);
        ImGui.Dummy(new Vector2(0, 4 * s));
        v = rs.DiscardExtras;
        if (Ui.Checkbox("Throw away extra gathered items", ref v, "A node is always gathered to the end, so the last one usually brings in a few more than the order needs. With this on, the run throws those away once gathering is done (normal quality only, never what you had before).")) { rs.DiscardExtras = v; Config.Save(); }
        if (rs.DiscardExtras)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 30 * s);
            v = rs.DiscardTimedExtras;
            if (Ui.Checkbox("Also from timed nodes and rare fish", ref v, "Unspoiled, legendary and ephemeral nodes often bring in far more than the order needs. Rare fish are the ones that only bite at some hours or in some weather. Off keeps those extras.")) { rs.DiscardTimedExtras = v; Config.Save(); }
        }
        v = rs.WaitForCollectableGp;
        if (Ui.Checkbox("Wait for GP before collectables", ref v, "The top reward needs the full collectable rotation (Scrutiny, Solid Reason), which takes 600 GP. On regular nodes the run waits next to the node until the GP is back; timed nodes are never waited for.")) { rs.WaitForCollectableGp = v; Config.Save(); }
        if (rs.WaitForCollectableGp)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 30 * s);
            v = rs.UseCordials;
            if (Ui.Checkbox("Drink cordials instead of waiting", ref v, "Uses a Hi-Cordial, Cordial or Watered Cordial from your bags, the smallest that fills the gap, whenever one is off cooldown. Never buys any.")) { rs.UseCordials = v; Config.Save(); }
        }
        v = rs.ExtractMateria;
        if (Ui.Checkbox("Extract materia", ref v, "Between nodes, extracts materia from gear at 100% spiritbond (needs the materia extraction quest).")) { rs.ExtractMateria = v; Config.Save(); }
        v = rs.ReduceCollectables;
        if (Ui.Checkbox("Reduce spare collectables", ref v, "When the bags run low on space, aetherially reduces collectables the order doesn't need. Never runs while a reducible collectable the order or the scrip earning needs is in the bags.")) { rs.ReduceCollectables = v; Config.Save(); }
        Ui.EndCard();
    }
}
