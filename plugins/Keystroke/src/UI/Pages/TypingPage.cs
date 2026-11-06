using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Keystroke.UI.Theme;

namespace Keystroke.UI.Pages;

internal sealed class TypingPage : Page
{
    private static readonly string[] Profiles = { "Humanized", "Fast", "Custom" };

    public TypingPage(Plugin plugin) : base(plugin) { }

    public override string Title => Strings.Typing;
    public override char Icon => Theme.Icon.Keyboard;

    public override void Draw()
    {
        var t = Config.Typing;
        var changed = false;
        var s = Widgets.Scale;

        Widgets.PageHeader(Strings.Typing, "How answers are entered. Humanized timing is the default for a reason.");

        Widgets.SectionLabel("PROFILE");
        if (Widgets.BeginCard("profile"))
        {
            ImGui.Dummy(new Vector2(0, 6 * s));
            var p = (int)t.Profile;
            if (Widgets.Segmented("##profile", Profiles, ref p))
            {
                t.Profile = (TypingProfile)p;
                ApplyPreset(t);
                changed = true;
            }
            ImGui.Dummy(new Vector2(0, 8 * s));
            Widgets.Caption(t.Profile switch
            {
                TypingProfile.Humanized => "Per-key delays follow a log-normal distribution around 80 ms with occasional hesitations. Looks like a quick typist.",
                TypingProfile.Fast => "Short, slightly varied delays. Clearly faster than a human; use at your own discretion.",
                _ => "Tune every parameter yourself.",
            });
            ImGui.Dummy(new Vector2(0, 6 * s));
            Widgets.EndCard();
        }

        var custom = t.Profile == TypingProfile.Custom;
        Widgets.SectionLabel("TIMING");
        if (!custom) ImGui.BeginDisabled();
        if (Widgets.BeginCard("timing"))
        {
            var median = t.MedianKeyDelayMs;
            Widgets.Row("Median key delay", "Centre of the per-key delay distribution.", 220 * s, Widgets.FrameHeight,
                () => { if (Widgets.SliderInt("##median", ref median, 20, 400, "%d ms", 220)) { t.MedianKeyDelayMs = median; changed = true; } });
            Widgets.RowSeparator();
            var jitter = t.KeyDelayJitter;
            Widgets.Row("Variation", "0 = every key identical, 1 = very uneven.", 220 * s, Widgets.FrameHeight,
                () => { if (Widgets.SliderFloat("##jitter", ref jitter, 0f, 1f, "%.2f", 220)) { t.KeyDelayJitter = jitter; changed = true; } });
            Widgets.RowSeparator();
            var hes = t.HesitationChance * 100f;
            Widgets.Row("Hesitation chance", "Chance that a key is preceded by a longer pause.", 220 * s, Widgets.FrameHeight,
                () => { if (Widgets.SliderFloat("##hes", ref hes, 0f, 20f, "%.1f %%", 220)) { t.HesitationChance = hes / 100f; changed = true; } });
            Widgets.RowSeparator();
            var hmin = t.HesitationMinMs;
            var hmax = t.HesitationMaxMs;
            Widgets.Row("Hesitation length", null, 220 * s, Widgets.FrameHeight, () =>
            {
                ImGui.SetNextItemWidth(104 * s);
                if (ImGui.InputInt("##hmin", ref hmin, 0, 0)) { t.HesitationMinMs = Math.Clamp(hmin, 0, 5000); changed = true; }
                ImGui.SameLine(0, 6 * s);
                ImGui.SetNextItemWidth(104 * s);
                if (ImGui.InputInt("##hmax", ref hmax, 0, 0)) { t.HesitationMaxMs = Math.Clamp(hmax, t.HesitationMinMs, 5000); changed = true; }
            });
            Widgets.RowSeparator();
            var bw = t.BetweenWordsMs;
            Widgets.Row("Pause between words", null, 220 * s, Widgets.FrameHeight,
                () => { if (Widgets.SliderInt("##bw", ref bw, 0, 2000, "%d ms", 220)) { t.BetweenWordsMs = bw; changed = true; } });
            Widgets.EndCard();
        }
        if (!custom) ImGui.EndDisabled();

        Widgets.SectionLabel("INPUT");
        if (Widgets.BeginCard("input"))
        {
            var v = t.BackgroundInput;
            if (Widgets.ToggleRow("Type while the game is in the background", ref v, "Sends key messages straight to the game window so you can use other apps meanwhile.", Theme.Icon.AppWindow)) { t.BackgroundInput = v; changed = true; }
            Widgets.RowSeparator();
            v = t.SkipOptionalPunctuation;
            if (Widgets.ToggleRow("Skip optional punctuation", ref v, "Leaves out ? ! - and apostrophes when the game accepts the answer without them.", Theme.Icon.Type)) { t.SkipOptionalPunctuation = v; changed = true; }
            Widgets.EndCard();
        }

        Widgets.SectionLabel("INSTANT MODE");
        if (Widgets.BeginCard("instant"))
        {
            var v = t.InstantMode;
            if (Widgets.ToggleRow("Instant mode", ref v, "Inserts the whole answer and submits it. Detectable as non-human; not recommended outside premade groups.", Theme.Icon.Zap, Theme.Theme.Orange))
            {
                if (v) InstantModeSheet.Request(t, Config);
                else { t.InstantMode = false; changed = true; }
            }
            Widgets.EndCard();
        }

        InstantModeSheet.Draw(Config);
        if (changed) Save();
    }

    private static void ApplyPreset(TypingSettings t)
    {
        switch (t.Profile)
        {
            case TypingProfile.Humanized:
                t.MedianKeyDelayMs = 80; t.KeyDelayJitter = 0.35f; t.HesitationChance = 0.02f;
                t.HesitationMinMs = 300; t.HesitationMaxMs = 700; t.BetweenWordsMs = 150;
                break;
            case TypingProfile.Fast:
                t.MedianKeyDelayMs = 35; t.KeyDelayJitter = 0.15f; t.HesitationChance = 0f;
                t.HesitationMinMs = 0; t.HesitationMaxMs = 0; t.BetweenWordsMs = 60;
                break;
        }
    }
}
