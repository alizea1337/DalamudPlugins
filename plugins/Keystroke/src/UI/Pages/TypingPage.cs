using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Keystroke.UI.Theme;

namespace Keystroke.UI.Pages;

internal sealed class TypingPage : Page
{
    private static readonly string[] ProfileLabels = { "relaxed", "normal", "skilled", "custom" };
    private static readonly TypingProfile[] ProfileOrder = { TypingProfile.Relaxed, TypingProfile.Normal, TypingProfile.Skilled, TypingProfile.Custom };

    public TypingPage(Plugin plugin) : base(plugin) { }

    public override string Title => "typing";
    public override char Icon => Theme.Icon.Keyboard;

    public override void Draw()
    {
        var t = Config.Typing;
        var changed = false;
        var s = Widgets.S;

        Widgets.BeginColumns(2);

        Widgets.BeginGroup("profile");
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
        ImGui.Dummy(new Vector2(0, 4 * s));
        Widgets.Wrapped(t.Profile switch
        {
            TypingProfile.Relaxed => "≈38 wpm. ~310 ms per key, uneven, frequent pauses, 0.5–0.8 s to react to a new word.",
            TypingProfile.Normal => "≈52 wpm, the measured everyday average. ~230 ms per key, natural variation, 0.4–0.6 s reaction.",
            TypingProfile.Skilled => "≈105 wpm, upper professional range. ~115 ms per key, steady, 0.25–0.4 s reaction.",
            _ => "Every timing below is yours to set.",
        });
        if (t.InstantMode) Widgets.Wrapped("Instant mode is on and overrides this profile.", Theme.Theme.Warn);
        Widgets.EndGroup();

        Widgets.BeginGroup("input");
        var v = t.BackgroundInput;
        if (Widgets.Check("type while the game is in the background", ref v, "Key messages go straight to the game window; you can use other apps meanwhile.")) { t.BackgroundInput = v; changed = true; }
        v = t.SkipOptionalPunctuation;
        if (Widgets.Check("skip optional punctuation", ref v, "Leaves out ? ! - and apostrophes when the game accepts the word without them.")) { t.SkipOptionalPunctuation = v; changed = true; }
        Widgets.EndGroup();

        Widgets.BeginGroup("instant mode");
        v = t.InstantMode;
        if (Widgets.Check("instant mode", ref v, "Sends every key of the word in one frame and submits. Easy to notice for other players."))
        {
            if (v) InstantModeSheet.Request(t, Config);
            else { t.InstantMode = false; changed = true; }
        }
        Widgets.EndGroup();

        Widgets.Column(1);

        var custom = t.Profile == TypingProfile.Custom;
        if (!custom) ImGui.BeginDisabled();
        Widgets.BeginGroup("timing");
        var median = t.MedianKeyDelayMs;
        if (Widgets.Slider("median key delay", ref median, 20, 400, "{0} ms", "Centre of the per-key delay distribution.")) { t.MedianKeyDelayMs = median; changed = true; }
        var jitter = t.KeyDelayJitter;
        if (Widgets.SliderF("variation", ref jitter, 0f, 1f, "{0:0.00}", "0 = every key identical, 1 = very uneven.")) { t.KeyDelayJitter = jitter; changed = true; }
        var hes = t.HesitationChance * 100f;
        if (Widgets.SliderF("hesitation chance", ref hes, 0f, 20f, "{0:0.0} %", "Chance that a key is preceded by a longer pause.", 0, 0.1f)) { t.HesitationChance = hes / 100f; changed = true; }
        var hmin = t.HesitationMinMs;
        var hmax = t.HesitationMaxMs;
        Widgets.Row("hesitation length (ms)", 124 * s, () =>
        {
            if (Widgets.IntField("##hmin", ref hmin, 58 * s)) { t.HesitationMinMs = Math.Clamp(hmin, 0, 5000); changed = true; }
            ImGui.SameLine(0, 8 * s);
            if (Widgets.IntField("##hmax", ref hmax, 58 * s)) { t.HesitationMaxMs = Math.Clamp(hmax, t.HesitationMinMs, 5000); changed = true; }
        });
        var bw = t.BetweenWordsMs;
        if (Widgets.Slider("pause between words", ref bw, 0, 2000, "{0} ms", "After a word is submitted, before the next one is picked.")) { t.BetweenWordsMs = bw; changed = true; }
        var rmin = t.ReactionMinMs;
        var rmax = t.ReactionMaxMs;
        Widgets.Row("reaction time (ms)", 124 * s, () =>
        {
            if (Widgets.IntField("##rmin", ref rmin, 58 * s)) { t.ReactionMinMs = Math.Clamp(rmin, 0, 5000); changed = true; }
            ImGui.SameLine(0, 8 * s);
            if (Widgets.IntField("##rmax", ref rmax, 58 * s)) { t.ReactionMaxMs = Math.Clamp(rmax, t.ReactionMinMs, 5000); changed = true; }
        }, "Noticing a new word and deciding to type it. Humans: ~250 ms simple, ~400–450 ms with a choice.");
        var rd = t.ReadMsPerChar;
        if (Widgets.Slider("reading time per character", ref rd, 0, 150, "{0} ms", "Added to the reaction time; 200–300 words per minute is about 40–60 ms per character.")) { t.ReadMsPerChar = rd; changed = true; }
        Widgets.EndGroup();
        if (!custom) ImGui.EndDisabled();

        Widgets.EndColumns();
        InstantModeSheet.Draw(Config);
        if (changed) Save();
    }
}
