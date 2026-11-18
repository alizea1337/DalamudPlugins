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
        if (Ui.Checkbox("Count finished items I already have", ref v, "Off: the order always makes its full amount, even if some are already in your bags.")) { st.CountFinishedItems = v; Changed(); }
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
