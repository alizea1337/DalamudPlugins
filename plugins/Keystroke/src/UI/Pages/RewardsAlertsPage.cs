using System.Numerics;
using Dalamud.Bindings.ImGui;
using Keystroke.UI.Theme;

namespace Keystroke.UI.Pages;

internal sealed class RewardsAlertsPage : Page
{
    public RewardsAlertsPage(Plugin plugin) : base(plugin) { }

    public override string Title => "alerts";
    public override char Icon => Theme.Icon.Bell;

    public override void Draw()
    {
        var a = Config.Alerts;
        var changed = false;
        var s = Widgets.S;

        Widgets.BeginColumns(2);

        Widgets.BeginGroup("mgc cap");
        var v = a.MgcCapWarning;
        if (Widgets.Check("warn when mgc approaches the cap", ref v, "Keybound currency stops accumulating at the cap; spend it before then.")) { a.MgcCapWarning = v; changed = true; }
        if (a.MgcCapWarning)
        {
            var pct = a.MgcCapWarningPercent;
            if (Widgets.Slider("warn at", ref pct, 50, 100, "{0} % of cap")) { a.MgcCapWarningPercent = pct; changed = true; }
        }
        Widgets.EndGroup();

        Widgets.BeginGroup("channels");
        v = a.ToastEnabled;
        if (Widgets.Check("in-game toast", ref v)) { a.ToastEnabled = v; changed = true; }
        v = a.SoundEnabled;
        if (Widgets.Check("sound effect", ref v)) { a.SoundEnabled = v; changed = true; }
        if (a.SoundEnabled)
        {
            var se = a.SoundEffect;
            Widgets.Row("sound", 150 * s, () =>
            {
                var items = new string[16];
                for (var i = 0; i < 16; i++) items[i] = $"<se.{i + 1}>";
                var idx = se - 1;
                if (Widgets.Combo("##se", ref idx, items, 118 * s)) { a.SoundEffect = idx + 1; changed = true; }
                ImGui.SameLine(0, 4 * s);
                if (Widgets.Button("##play", Widgets.ButtonKind.Normal, new Vector2(Theme.Theme.ControlHeight * s), Theme.Icon.Play, "preview"))
                    Alerts.Notifier.PlaySound(a.SoundEffect);
            }, "Game sound effects 1–16, as in <se.N> chat macros.");
        }
        v = a.ChatEnabled;
        if (Widgets.Check("chat message", ref v)) { a.ChatEnabled = v; changed = true; }
        ImGui.BeginDisabled();
        var off = false;
        Widgets.Check("windows notification", ref off, "Not available yet.");
        ImGui.EndDisabled();
        Widgets.EndGroup();

        Widgets.Column(1);

        Widgets.BeginGroup("events");
        v = a.NotifyLoopFinished;
        if (Widgets.Check("loop finished or stop condition reached", ref v)) { a.NotifyLoopFinished = v; changed = true; }
        v = a.NotifyMgcWarning;
        if (Widgets.Check("mgc warning and cap", ref v)) { a.NotifyMgcWarning = v; changed = true; }
        v = a.NotifyErrors;
        if (Widgets.Check("automation stopped on an error", ref v)) { a.NotifyErrors = v; changed = true; }
        v = a.NotifyGameResult;
        if (Widgets.Check("every game result", ref v, "Noisy; off by default.")) { a.NotifyGameResult = v; changed = true; }
        Widgets.EndGroup();

        Widgets.EndColumns();
        if (changed) Save();
    }
}
