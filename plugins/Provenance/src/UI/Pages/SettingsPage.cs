using System;
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
        ("GatherBuddyReborn", "GatherBuddy Reborn", "Gathers materials, including timed nodes, and fishes through AutoHook."),
        ("vnavmesh", "vnavmesh", "Walks and flies to vendors, summoning bells and monsters."),
        ("AutoHook", "AutoHook", "Used by GatherBuddy Reborn for fishing."),
    };

    public SettingsPage(Plugin plugin) : base(plugin) { }

    public override string Title => "Settings";

    public override (string Text, Vector4 Color)? Status => ("Changes are saved as you make them", Palette.TextFaint);

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
        if (Ui.Checkbox("Retainers", ref v, "Takes items out at a summoning bell during a run. Arrives together with retainer withdrawal.", enabled: false)) { }
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
        if (Ui.Checkbox("Aim for high quality on ordered items", ref v, "Intermediate materials always get the fastest rotation, since their quality doesn't matter.")) { cr.AimForHq = v; Config.Save(); }
        v = cr.UseSpecialistActions;
        if (Ui.Checkbox("Use specialist actions", ref v, "Heart and Soul and Quick Innovation, when a specialist soul is equipped.")) { cr.UseSpecialistActions = v; Config.Save(); }
        Ui.Text("Repair gear below");
        ImGui.SameLine(0, 10 * s);
        var repair = cr.RepairBelow;
        ImGui.SetNextItemWidth(160 * s);
        if (ImGui.SliderInt("##repair", ref repair, 0, 99, repair == 0 ? "never" : "%d%%")) { cr.RepairBelow = repair; Config.Save(); }
        Ui.Wrapped("Self-repair needs Dark Matter of a high enough grade in your bags.", Palette.TextMuted);
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
