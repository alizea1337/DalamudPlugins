using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Keystroke.UI.Theme;

namespace Keystroke.UI.Pages;

internal sealed class AboutPage : Page
{
    private const string RepoUrl = "https://github.com/alizea1337/DalamudPlugins";

    public AboutPage(Plugin plugin) : base(plugin) { }

    public override string Title => "about";
    public override char Icon => Theme.Icon.Info;

    public override void Draw()
    {
        var s = Widgets.S;
        Widgets.BeginColumns(2);

        Widgets.BeginGroup("keystroke");
        using (Widgets.F.Display.Push()) Widgets.Text("keystroke", Theme.Theme.AccentText);
        Widgets.Small($"version {Plugin.Version}");
        ImGui.Dummy(new Vector2(0, 6 * s));
        Widgets.ValueRow("author", "alizea1337");
        Widgets.ValueRow("license", "MIT");
        ImGui.Dummy(new Vector2(0, 4 * s));
        if (Widgets.Button("source on github##src", Widgets.ButtonKind.Normal, null, Theme.Icon.Github, RepoUrl))
            Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true });
        Widgets.EndGroup();

        Widgets.BeginGroup("a note");
        Widgets.Wrapped("Keystroke is an independent project and is not affiliated with Square Enix. Automating gameplay is against the FFXIV terms of service; use it at your own discretion.");
        Widgets.EndGroup();

        Widgets.Column(1);
        Widgets.BeginGroup("third-party");
        Widgets.ValueRow("DejaVu Sans", "Bitstream Vera / DejaVu license", hint: "Typeface.");
        Widgets.ValueRow("Lucide", "ISC", hint: "Icon set by Lucide Contributors.");
        Widgets.ValueRow("FFXIVClientStructs", "MIT", hint: "Game structure definitions by aers and contributors.");
        Widgets.EndGroup();
        Widgets.EndColumns();
    }
}
