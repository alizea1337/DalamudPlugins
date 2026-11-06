using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Keystroke.UI.Theme;

namespace Keystroke.UI.Pages;

internal sealed class TypingPage : Page
{
    private static readonly string[] ProfileLabels = { "Relaxed", "Normal", "Skilled", "Custom" };
    private static readonly TypingProfile[] ProfileOrder = { TypingProfile.Relaxed, TypingProfile.Normal, TypingProfile.Skilled, TypingProfile.Custom };

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
            var p = Array.IndexOf(ProfileOrder, t.Profile);
            if (p < 0) p = 1;
            if (Widgets.Segmented("##profile", ProfileLabels, ref p))
            {
                t.Profile = ProfileOrder[p];
                t.ApplyPreset();
                changed = true;
                Services.Log.Information("Typing profile set to {Profile}: median {Median} ms/key, reaction {Min}-{Max} ms, read {Read} ms/char",
                    t.Profile, t.MedianKeyDelayMs, t.ReactionMinMs, t.ReactionMaxMs, t.ReadMsPerChar);
            }
            ImGui.Dummy(new Vector2(0, 8 * s));
            Widgets.Caption(t.Profile switch
            {
                TypingProfile.Relaxed => "≈38 WPM: ~310 ms per key, uneven, frequent pauses, 0.5–0.8 s to react to a new word. Someone glancing at the keyboard.",
                TypingProfile.Normal => "≈52 WPM, the measured everyday average: ~230 ms per key, natural variation, 0.4–0.6 s reaction. Recommended.",
                TypingProfile.Skilled => "≈85 WPM, professional range: ~140 ms per key, steady, 0.3–0.45 s reaction. Fast but still human-paced.",
                _ => "Tune every parameter yourself.",
            });
            if (t.InstantMode)
                Widgets.Caption("Instant mode is on and overrides this profile: whole words are inserted at once.", Theme.Theme.Orange);
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
            Widgets.Row("Pause between words", "After a word is submitted, before the next one is picked.", 220 * s, Widgets.FrameHeight,
                () => { if (Widgets.SliderInt("##bw", ref bw, 0, 2000, "%d ms", 220)) { t.BetweenWordsMs = bw; changed = true; } });
            Widgets.RowSeparator();
            var rmin = t.ReactionMinMs;
            var rmax = t.ReactionMaxMs;
            Widgets.Row("Reaction time", "Noticing a new word and deciding to type it. Humans: ~250 ms simple, ~400–450 ms with a choice.", 220 * s, Widgets.FrameHeight, () =>
            {
                ImGui.SetNextItemWidth(104 * s);
                if (ImGui.InputInt("##rmin", ref rmin, 0, 0)) { t.ReactionMinMs = Math.Clamp(rmin, 0, 5000); changed = true; }
                ImGui.SameLine(0, 6 * s);
                ImGui.SetNextItemWidth(104 * s);
                if (ImGui.InputInt("##rmax", ref rmax, 0, 0)) { t.ReactionMaxMs = Math.Clamp(rmax, t.ReactionMinMs, 5000); changed = true; }
            });
            Widgets.RowSeparator();
            var rd = t.ReadMsPerChar;
            Widgets.Row("Reading time per character", "Added to the reaction time; 200–300 words per minute is about 40–60 ms per character.", 220 * s, Widgets.FrameHeight,
                () => { if (Widgets.SliderInt("##rd", ref rd, 0, 150, "%d ms", 220)) { t.ReadMsPerChar = rd; changed = true; } });
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
}
