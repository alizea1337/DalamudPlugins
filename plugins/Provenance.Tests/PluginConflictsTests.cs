using System.Linq;
using Provenance.Core.Run;
using Xunit;

namespace Provenance.Tests;

public class PluginConflictsTests
{
    private static readonly OtherPluginState Nothing = default;
    private static readonly StopListHolds NoneHeld = default;

    /// <summary>All three loaded, TextAdvance switched on, Questionable idle.</summary>
    private static readonly OtherPluginState AllLoaded = new(true, true, true, true, false);

    private static OtherPluginState Questionable(bool? running) => new(false, false, null, true, running);

    // ------------------------------------------------------------------ the stop lists

    [Fact]
    public void AWorkingRunHoldsBothListsWhenTheSettingIsOn()
        => Assert.Equal(new StopListHolds(true, true), PluginConflicts.Holds(pauseOthers: true, runWorking: true, handingIn: false));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void NothingIsHeldWithoutBothTheSettingAndAWorkingRun(bool pauseOthers, bool runWorking)
        => Assert.Equal(new StopListHolds(false, false), PluginConflicts.Holds(pauseOthers, runWorking, handingIn: false));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AHandInHoldsYesAlreadyWhateverTheSetting(bool pauseOthers)
        => Assert.True(PluginConflicts.Holds(pauseOthers, runWorking: false, handingIn: true).YesAlready);

    [Fact]
    public void AHandInAloneLeavesTextAdvanceBe()
        => Assert.Equal(new StopListHolds(true, false), PluginConflicts.Holds(pauseOthers: false, runWorking: true, handingIn: true));

    [Fact]
    public void YesAlreadyStaysHeldWhileEitherReasonIsLeft()
    {
        // The hand-in ends while the run still works with the setting on...
        Assert.True(PluginConflicts.Holds(pauseOthers: true, runWorking: true, handingIn: false).YesAlready);
        // ...the setting is switched off mid hand-in...
        Assert.True(PluginConflicts.Holds(pauseOthers: false, runWorking: true, handingIn: true).YesAlready);
        // ...and only with neither left is it let go.
        Assert.False(PluginConflicts.Holds(pauseOthers: false, runWorking: true, handingIn: false).YesAlready);
        Assert.False(PluginConflicts.Holds(pauseOthers: true, runWorking: false, handingIn: false).YesAlready);
    }

    // ------------------------------------------------------------------ Questionable

    [Fact]
    public void ARunWontStartWhileQuestionableRuns()
        => Assert.Equal("Questionable is running its own quests; stop it first.", PluginConflicts.StartBlocker(Questionable(true)));

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void ARunStartsWhileQuestionableIsIdleOrCantBeAsked(bool? running)
        => Assert.Null(PluginConflicts.StartBlocker(Questionable(running)));

    [Fact]
    public void AnAnswerFromBeforeQuestionableWasUnloadedDoesNotCount()
    {
        var gone = new OtherPluginState(false, false, null, Questionable: false, QuestionableRunning: true);
        Assert.False(gone.QuestionableDrives);
        Assert.Null(PluginConflicts.StartBlocker(gone));
        Assert.Null(PluginConflicts.StopReason(gone, runWorking: true));
        Assert.Empty(PluginConflicts.Notices(gone, pauseOthers: true));
    }

    [Fact]
    public void QuestionableStartingStopsAWorkingRun()
        => Assert.Equal("Questionable started running; two plugins can't drive the character at once.",
            PluginConflicts.StopReason(Questionable(true), runWorking: true));

    [Fact]
    public void QuestionableLeavesARunThatIsNotWorkingAlone()
    {
        Assert.Null(PluginConflicts.StopReason(Questionable(true), runWorking: false));
        Assert.Null(PluginConflicts.StopReason(Questionable(false), runWorking: true));
    }

    // ------------------------------------------------------------------ the run pages' warnings

    [Fact]
    public void NoWarningWhenNothingIsInTheWay()
    {
        Assert.Empty(PluginConflicts.Notices(Nothing, pauseOthers: true));
        Assert.Empty(PluginConflicts.Notices(Nothing, pauseOthers: false));
        // Everything loaded, the setting on, Questionable idle: all taken care of.
        Assert.Empty(PluginConflicts.Notices(AllLoaded, pauseOthers: true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QuestionableRunningWarnsWhateverTheSetting(bool pauseOthers)
    {
        var notice = Assert.Single(PluginConflicts.Notices(Questionable(true), pauseOthers));
        Assert.Equal("Questionable is running", notice.Title);
        Assert.Equal("Two plugins can't drive the character at once, so a run won't start until Questionable is stopped.", notice.Text);
        Assert.True(notice.BlocksStart);
        Assert.False(notice.InSettings);
    }

    [Fact]
    public void TheSettingOffWarnsAboutBothWhenBothAct()
    {
        var notice = Assert.Single(PluginConflicts.Notices(AllLoaded, pauseOthers: false));
        Assert.Equal("YesAlready and TextAdvance aren't paused", notice.Title);
        Assert.Equal("While a run is going they still answer the game's windows and click through its dialogue by themselves, including the ones the run "
                     + "is working with. Turn on \"Pause YesAlready and TextAdvance while a run is going\" on the Security page, or switch them off "
                     + "yourself for the run.", notice.Text);
        Assert.False(notice.BlocksStart);
        Assert.True(notice.InSettings);
    }

    [Fact]
    public void TheSettingOffWarnsAboutYesAlreadyAlone()
    {
        var notice = Assert.Single(PluginConflicts.Notices(new OtherPluginState(true, false, null, false, null), pauseOthers: false));
        Assert.Equal("YesAlready isn't paused", notice.Title);
        Assert.StartsWith("While a run is going it still answers the game's windows by itself", notice.Text);
        Assert.EndsWith("or switch it off yourself for the run.", notice.Text);
        Assert.True(notice.InSettings);
    }

    [Theory]
    [InlineData(true)]
    // TextAdvance loaded but not answering: taken as switched on.
    [InlineData(null)]
    public void TheSettingOffWarnsAboutTextAdvanceWhenItIsSwitchedOn(bool? enabled)
    {
        var notice = Assert.Single(PluginConflicts.Notices(new OtherPluginState(false, true, enabled, false, null), pauseOthers: false));
        Assert.Equal("TextAdvance isn't paused", notice.Title);
        Assert.StartsWith("While a run is going it still clicks through dialogue and quest windows by itself", notice.Text);
        Assert.Contains($"\"{PluginConflicts.PauseSetting}\"", notice.Text);
    }

    [Fact]
    public void TextAdvanceSwitchedOffIsNotInTheWay()
    {
        Assert.Empty(PluginConflicts.Notices(new OtherPluginState(false, true, false, false, null), pauseOthers: false));
        // Next to YesAlready, only YesAlready is named.
        var notice = Assert.Single(PluginConflicts.Notices(new OtherPluginState(true, true, false, false, null), pauseOthers: false));
        Assert.Equal("YesAlready isn't paused", notice.Title);
    }

    [Fact]
    public void BothWarningsCanApplyAndQuestionableComesFirst()
    {
        var notices = PluginConflicts.Notices(AllLoaded with { QuestionableRunning = true }, pauseOthers: false);
        Assert.Equal(new[] { "Questionable is running", "YesAlready and TextAdvance aren't paused" }, notices.Select(n => n.Title));
    }

    // ------------------------------------------------------------------ the Security tab's lines

    [Fact]
    public void OnlyLoadedPluginsGetALine()
    {
        Assert.Empty(PluginConflicts.Statuses(Nothing, pauseOthers: true, NoneHeld));
        Assert.Equal(new[] { "TextAdvance" }, PluginConflicts.Statuses(new OtherPluginState(false, true, true, false, null), true, NoneHeld).Select(s => s.Plugin));
        Assert.Equal(new[] { "YesAlready", "TextAdvance", "Questionable" }, PluginConflicts.Statuses(AllLoaded, true, NoneHeld).Select(s => s.Plugin));
    }

    [Fact]
    public void WithTheSettingOnBothArePausedWhileARunIsGoing()
    {
        var lines = PluginConflicts.Statuses(AllLoaded, pauseOthers: true, NoneHeld);
        Assert.Equal(new PluginStatus("YesAlready", "paused while a run is going", StatusTone.Fine), lines[0]);
        Assert.Equal(new PluginStatus("TextAdvance", "paused while a run is going", StatusTone.Fine), lines[1]);
        Assert.Equal(new PluginStatus("Questionable", "can't be paused", StatusTone.Plain,
            "A run won't start while it's running. If it starts during a run, the run stops."), lines[2]);
    }

    [Fact]
    public void WhileARunWorksTheLinesSayPausedNow()
    {
        var lines = PluginConflicts.Statuses(AllLoaded, pauseOthers: true, new StopListHolds(true, true));
        Assert.Equal(new PluginStatus("YesAlready", "paused now", StatusTone.Fine, "Until the run pauses, finishes or stops."), lines[0]);
        Assert.Equal(new PluginStatus("TextAdvance", "paused now", StatusTone.Fine, "Until the run pauses, finishes or stops."), lines[1]);
    }

    [Fact]
    public void WithTheSettingOffTheLinesSayWhatStaysActive()
    {
        var lines = PluginConflicts.Statuses(AllLoaded, pauseOthers: false, NoneHeld);
        Assert.Equal(new PluginStatus("YesAlready", "not paused", StatusTone.Warning,
            "It stays active while a run is going, except while custom deliveries are handed in."), lines[0]);
        Assert.Equal(new PluginStatus("TextAdvance", "not paused", StatusTone.Warning, "It stays active while a run is going."), lines[1]);
    }

    [Fact]
    public void AHandInWithTheSettingOffShowsYesAlreadyPausedForIt()
    {
        var lines = PluginConflicts.Statuses(AllLoaded, pauseOthers: false, new StopListHolds(true, false));
        Assert.Equal(new PluginStatus("YesAlready", "paused now", StatusTone.Fine, "While custom deliveries are handed in."), lines[0]);
        Assert.Equal("not paused", lines[1].State);
    }

    [Fact]
    public void TextAdvanceSwitchedOffIsNoWarningWithTheSettingOff()
    {
        var line = Assert.Single(PluginConflicts.Statuses(new OtherPluginState(false, true, false, false, null), pauseOthers: false, NoneHeld));
        Assert.Equal(new PluginStatus("TextAdvance", "not paused", StatusTone.Plain, "It's switched off right now, so it isn't in a run's way."), line);
    }

    [Fact]
    public void QuestionableRunningIsAWarning()
    {
        var line = Assert.Single(PluginConflicts.Statuses(Questionable(true), pauseOthers: true, NoneHeld));
        Assert.Equal(new PluginStatus("Questionable", "running now", StatusTone.Warning, "A run won't start until it's stopped."), line);
    }

    [Fact]
    public void QuestionableNotAnsweringPromisesNothing()
    {
        var line = Assert.Single(PluginConflicts.Statuses(Questionable(null), pauseOthers: true, NoneHeld));
        Assert.Equal(new PluginStatus("Questionable", "not answering", StatusTone.Plain,
            "Provenance can't tell whether it's running, so stop it yourself before a run."), line);
    }

    [Fact]
    public void ANoticeBlocksTheStartExactlyWhenTheStartIsRefused()
    {
        // The Run page leaves that notice out before a run: its own card says why the run can't start.
        foreach (var running in new bool?[] { null, false, true })
        foreach (var pauseOthers in new[] { false, true })
        {
            var state = AllLoaded with { QuestionableRunning = running };
            Assert.Equal(PluginConflicts.StartBlocker(state) != null, PluginConflicts.Notices(state, pauseOthers).Any(n => n.BlocksStart));
        }
    }

    [Fact]
    public void EveryWarningLineHasANoticeOnTheRunPages()
    {
        // The Security tab and the run pages agree on what's in the way.
        foreach (var yesAlready in new[] { false, true })
        foreach (var textAdvance in new[] { false, true })
        foreach (var enabled in new bool?[] { null, false, true })
        foreach (var questionable in new[] { false, true })
        foreach (var running in new bool?[] { null, false, true })
        foreach (var pauseOthers in new[] { false, true })
        {
            var state = new OtherPluginState(yesAlready, textAdvance, enabled, questionable, running);
            var warned = PluginConflicts.Statuses(state, pauseOthers, NoneHeld).Any(s => s.Tone == StatusTone.Warning);
            Assert.Equal(warned, PluginConflicts.Notices(state, pauseOthers).Count > 0);
        }
    }

    // ------------------------------------------------------------------ the bug report's line

    [Fact]
    public void TheBugReportLineNamesAllThreeAndTheSetting()
    {
        Assert.Equal("Other plugins: YesAlready loaded, held by Provenance; TextAdvance loaded, enabled, held by Provenance; "
                     + "Questionable loaded, not running; pause while a run is going on",
            PluginConflicts.Describe(AllLoaded, pauseOthers: true, new StopListHolds(true, true)));
        Assert.Equal("Other plugins: YesAlready not loaded, not held; TextAdvance not loaded, not held; Questionable not loaded; "
                     + "pause while a run is going off",
            PluginConflicts.Describe(Nothing, pauseOthers: false, NoneHeld));
    }

    [Fact]
    public void TheBugReportLineSaysWhatCouldNotBeAsked()
    {
        var line = PluginConflicts.Describe(new OtherPluginState(false, true, null, true, null), pauseOthers: true, new StopListHolds(true, false));
        Assert.Equal("Other plugins: YesAlready not loaded, held by Provenance; TextAdvance loaded, enabled unknown, not held; "
                     + "Questionable loaded, running unknown; pause while a run is going on", line);
        Assert.Contains("TextAdvance loaded, disabled", PluginConflicts.Describe(AllLoaded with { TextAdvanceEnabled = false }, true, NoneHeld));
        Assert.Contains("Questionable loaded, running;", PluginConflicts.Describe(AllLoaded with { QuestionableRunning = true }, true, NoneHeld));
    }
}
