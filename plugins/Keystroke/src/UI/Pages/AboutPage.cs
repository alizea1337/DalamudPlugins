using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Keystroke.UI.Theme;

namespace Keystroke.UI.Pages;

internal sealed class AboutPage : Page
{
    private const string RepoUrl = "https://github.com/alizea1337/DalamudPlugins";

    public AboutPage(Plugin plugin) : base(plugin) { }

    public override string Title => Strings.About;
    public override char Icon => Theme.Icon.Info;

    public override void Draw()
    {
        var s = Widgets.Scale;

        ImGui.Dummy(new Vector2(0, 10 * s));
        using (Widgets.F.LargeTitle.Push()) ImGui.TextUnformatted(Strings.PluginName);
        Widgets.Secondary($"Version {Plugin.Version}");
        ImGui.Dummy(new Vector2(0, 14 * s));

        if (Widgets.BeginCard("about"))
        {
            Widgets.ValueRow("Author", "alizea1337");
            Widgets.RowSeparator();
            Widgets.ValueRow("License", "MIT");
            Widgets.RowSeparator();
            Widgets.Row("Source", RepoUrl, 100 * s, Widgets.FrameHeight, () =>
            {
                if (Widgets.Button("GitHub##src", Widgets.ButtonStyle.Default, null, Theme.Icon.Github))
                    Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true });
            }, Theme.Icon.ExternalLink);
            Widgets.EndCard();
        }

        Widgets.SectionLabel("THIRD-PARTY");
        if (Widgets.BeginCard("thirdparty"))
        {
            Widgets.ValueRow("Inter", "SIL Open Font License 1.1", description: "Typeface by Rasmus Andersson.");
            Widgets.RowSeparator();
            Widgets.ValueRow("Lucide", "ISC License", description: "Icon set by Lucide Contributors.");
            Widgets.RowSeparator();
            Widgets.ValueRow("FFXIVClientStructs", "MIT License", description: "Game structure definitions by aers and contributors.");
            Widgets.EndCard();
        }

        Widgets.SectionLabel("A NOTE");
        Widgets.Caption("Keystroke is an independent project and is not affiliated with Square Enix. Automating gameplay is against the FFXIV terms of service; use it at your own discretion.");
    }
}
