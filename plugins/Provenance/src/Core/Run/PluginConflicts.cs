using System;
using System.Collections.Generic;

namespace Provenance.Core.Run;

/// <summary>
/// What's known about the three automation plugins that get in a run's way, as last looked at (about once a second).
/// </summary>
/// <param name="YesAlready">YesAlready is loaded.</param>
/// <param name="TextAdvance">TextAdvance is loaded.</param>
/// <param name="TextAdvanceEnabled">TextAdvance says it's switched on; null when it couldn't be asked.</param>
/// <param name="Questionable">Questionable is loaded.</param>
/// <param name="QuestionableRunning">Questionable says it's running its quests; null when it couldn't be asked.</param>
public readonly record struct OtherPluginState(bool YesAlready, bool TextAdvance, bool? TextAdvanceEnabled, bool Questionable, bool? QuestionableRunning)
{
    /// <summary>Questionable drives the character right now.</summary>
    public bool QuestionableDrives => Questionable && QuestionableRunning == true;

    /// <summary>TextAdvance acts on what the game shows, as far as can be told: loaded, and not known to be switched off.</summary>
    public bool TextAdvanceActs => TextAdvance && TextAdvanceEnabled != false;
}

/// <summary>The stop lists "Provenance" is in (or belongs in): YesAlready's and TextAdvance's.</summary>
public readonly record struct StopListHolds(bool YesAlready, bool TextAdvance);

/// <summary>A warning for the top of the run pages while a plugin is in a run's way.</summary>
/// <param name="BlocksStart">A run won't start while it lasts (a page that says why a run can't start says it already).</param>
/// <param name="InSettings">Turning the Security tab's setting on settles it.</param>
public sealed record PluginNotice(string Title, string Text, bool BlocksStart, bool InSettings);

/// <summary>How a plugin's line on the Security tab reads: all is taken care of, a plain fact, or something in a run's way.</summary>
public enum StatusTone { Fine, Plain, Warning }

/// <summary>One plugin's line on the Security tab: its state in a few words, and a sentence more where that helps.</summary>
public sealed record PluginStatus(string Plugin, string State, StatusTone Tone, string? Note = null);

/// <summary>
/// The rules for the automation plugins that get in a run's way. YesAlready and TextAdvance can be asked to stand
/// aside (each has a stop list other plugins put their name in): they are while a run works and the setting is on, and
/// YesAlready also while custom deliveries are handed in, setting or not. Questionable can't be paused from outside:
/// a run doesn't start while it runs its quests, and stops when it starts.
/// </summary>
public static class PluginConflicts
{
    public const string YesAlready = "YesAlready";
    public const string TextAdvance = "TextAdvance";
    public const string Questionable = "Questionable";

    /// <summary>The setting's name, the same wherever it's shown.</summary>
    public const string PauseSetting = "Pause YesAlready and TextAdvance while a run is going";

    /// <summary>Why a run doesn't start while Questionable runs.</summary>
    public const string QuestionableBlocksStart = "Questionable is running its own quests; stop it first.";

    /// <summary>Why a run that works stops once Questionable runs.</summary>
    public const string QuestionableStarted = "Questionable started running; two plugins can't drive the character at once.";

    /// <summary>
    /// Whose stop list "Provenance" belongs in. Either reason keeps the entry in YesAlready's: the hand-in's goes
    /// when the hand-in is over, the run's when no run works any more, and the entry only when neither is left.
    /// </summary>
    /// <param name="pauseOthers">The setting.</param>
    /// <param name="runWorking">A run acts on its own right now (not paused, finished or stopped).</param>
    /// <param name="handingIn">Custom deliveries are being handed in.</param>
    public static StopListHolds Holds(bool pauseOthers, bool runWorking, bool handingIn)
    {
        var forRun = pauseOthers && runWorking;
        return new StopListHolds(forRun || handingIn, forRun);
    }

    /// <summary>Why a run can't start because of another plugin, or null.</summary>
    public static string? StartBlocker(OtherPluginState s) => s.QuestionableDrives ? QuestionableBlocksStart : null;

    /// <summary>Why everything stops because of another plugin, or null: Questionable runs while a run works.</summary>
    public static string? StopReason(OtherPluginState s, bool runWorking) => runWorking && s.QuestionableDrives ? QuestionableStarted : null;

    /// <summary>
    /// The warnings for the run pages, only for what's in the way right now: Questionable running, and YesAlready or
    /// TextAdvance (when it's switched on) loaded while the setting is off. None otherwise.
    /// </summary>
    public static IReadOnlyList<PluginNotice> Notices(OtherPluginState s, bool pauseOthers)
    {
        var notPaused = pauseOthers ? null : NotPaused(s.YesAlready, s.TextAdvanceActs);
        if (!s.QuestionableDrives && notPaused == null) return Array.Empty<PluginNotice>();
        var list = new List<PluginNotice>(2);
        if (s.QuestionableDrives) list.Add(QuestionableRuns);
        if (notPaused != null) list.Add(notPaused);
        return list;
    }

    private static readonly PluginNotice QuestionableRuns = new("Questionable is running",
        "Two plugins can't drive the character at once, so a run won't start until Questionable is stopped.", BlocksStart: true, InSettings: false);

    private static readonly string TurnOn = $"Turn on \"{PauseSetting}\" on the Security page, or switch ";

    private static readonly PluginNotice BothNotPaused = new("YesAlready and TextAdvance aren't paused",
        "While a run is going they still answer the game's windows and click through its dialogue by themselves, including the ones the run is working with. "
        + TurnOn + "them off yourself for the run.", BlocksStart: false, InSettings: true);

    private static readonly PluginNotice YesAlreadyNotPaused = new("YesAlready isn't paused",
        "While a run is going it still answers the game's windows by itself, including the ones the run is working with. "
        + TurnOn + "it off yourself for the run.", BlocksStart: false, InSettings: true);

    private static readonly PluginNotice TextAdvanceNotPaused = new("TextAdvance isn't paused",
        "While a run is going it still clicks through dialogue and quest windows by itself, including the ones the run is working with. "
        + TurnOn + "it off yourself for the run.", BlocksStart: false, InSettings: true);

    private static PluginNotice? NotPaused(bool yesAlready, bool textAdvance)
        => yesAlready && textAdvance ? BothNotPaused : yesAlready ? YesAlreadyNotPaused : textAdvance ? TextAdvanceNotPaused : null;

    /// <summary>
    /// The Security tab's lines, one for each of the three that's loaded (none for the others): what Provenance does
    /// about it, and what it's doing right now.
    /// </summary>
    /// <param name="held">The stop lists "Provenance" is in right now.</param>
    public static IReadOnlyList<PluginStatus> Statuses(OtherPluginState s, bool pauseOthers, StopListHolds held)
    {
        var list = new List<PluginStatus>(3);
        if (s.YesAlready)
            list.Add(held.YesAlready ? new PluginStatus(YesAlready, "paused now", StatusTone.Fine,
                    pauseOthers ? UntilTheRunRests : "While custom deliveries are handed in.")
                : pauseOthers ? new PluginStatus(YesAlready, PausedForRuns, StatusTone.Fine)
                : new PluginStatus(YesAlready, "not paused", StatusTone.Warning,
                    "It stays active while a run is going, except while custom deliveries are handed in."));
        if (s.TextAdvance)
            list.Add(held.TextAdvance ? new PluginStatus(TextAdvance, "paused now", StatusTone.Fine, UntilTheRunRests)
                : pauseOthers ? new PluginStatus(TextAdvance, PausedForRuns, StatusTone.Fine)
                : s.TextAdvanceActs ? new PluginStatus(TextAdvance, "not paused", StatusTone.Warning, "It stays active while a run is going.")
                : new PluginStatus(TextAdvance, "not paused", StatusTone.Plain, "It's switched off right now, so it isn't in a run's way."));
        if (s.Questionable)
            list.Add(s.QuestionableRunning switch
            {
                true => new PluginStatus(Questionable, "running now", StatusTone.Warning, "A run won't start until it's stopped."),
                false => new PluginStatus(Questionable, "can't be paused", StatusTone.Plain,
                    "A run won't start while it's running. If it starts during a run, the run stops."),
                // Loaded, but it doesn't say (an older version, or still starting up): nothing is promised then.
                null => new PluginStatus(Questionable, "not answering", StatusTone.Plain,
                    "Provenance can't tell whether it's running, so stop it yourself before a run."),
            });
        return list;
    }

    private const string PausedForRuns = "paused while a run is going";
    private const string UntilTheRunRests = "Until the run pauses, finishes or stops.";

    /// <summary>The three plugins and the setting in one line, for a bug report.</summary>
    public static string Describe(OtherPluginState s, bool pauseOthers, StopListHolds held)
    {
        static string Held(bool inList) => inList ? "held by Provenance" : "not held";
        static string Asked(bool? answer, string yes, string no) => answer switch { true => yes, false => no, _ => yes + " unknown" };
        var yesAlready = $"{YesAlready} {(s.YesAlready ? "loaded" : "not loaded")}, {Held(held.YesAlready)}";
        var textAdvance = $"{TextAdvance} {(s.TextAdvance ? "loaded, " + Asked(s.TextAdvanceEnabled, "enabled", "disabled") : "not loaded")}, {Held(held.TextAdvance)}";
        var questionable = $"{Questionable} {(s.Questionable ? "loaded, " + Asked(s.QuestionableRunning, "running", "not running") : "not loaded")}";
        return $"Other plugins: {yesAlready}; {textAdvance}; {questionable}; pause while a run is going {(pauseOthers ? "on" : "off")}";
    }
}
