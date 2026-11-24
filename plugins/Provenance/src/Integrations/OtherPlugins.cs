using System;
using System.Collections.Generic;
using Provenance.Core.Run;

namespace Provenance.Integrations;

/// <summary>
/// The automation plugins that get in a run's way. YesAlready and TextAdvance are asked to stand aside through the
/// stop list each of them shares with other plugins: while a list has a name in it, its plugin does nothing, and a
/// plugin puts its own name in and takes it out again. Provenance's is in both while a run works and the Security
/// tab's setting is on, and in YesAlready's while custom deliveries are handed in, setting or not. Questionable can't
/// be paused from outside without stopping its work, so it's only asked whether it runs: a run won't start then
/// (<see cref="StartBlocker"/>), and everything stops when it starts while a run works (<see cref="Run.Guard"/>).
/// What follows from what is in <see cref="PluginConflicts"/>.
/// </summary>
/// <remarks>
/// Facts only, own code: the lists' names and how they're used (YesAlready's from xiv-mcp, MIT; TextAdvance has no
/// licence), and the two questions asked, TextAdvance.IsEnabled and Questionable.IsRunning (Questionable is AGPL).
/// The plugins are looked at about once a second, and a plugin that isn't loaded is never asked (that throws); a
/// question or a list that fails isn't tried again for a few seconds.
/// </remarks>
internal static class OtherPlugins
{
    /// <summary>The name Provenance goes by in the stop lists.</summary>
    private const string Me = "Provenance";
    /// <summary>How often the plugins are looked at, and an entry a plugin cleared is put back.</summary>
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(1);
    /// <summary>A question or a list that failed isn't tried again for this long.</summary>
    private static readonly TimeSpan Unavailable = TimeSpan.FromSeconds(5);

    private static readonly StopList YesAlreadyList = new("YesAlready.StopRequests");
    private static readonly StopList TextAdvanceList = new("TextAdvance.StopRequests");
    private static readonly Question TextAdvanceIsEnabled = new("TextAdvance.IsEnabled");
    private static readonly Question QuestionableIsRunning = new("Questionable.IsRunning");

    private static DateTime nextLook;
    /// <summary>The setting, and whether a run works, as of the last frame.</summary>
    private static bool pauseOthers, runWorking;
    /// <summary>Custom deliveries are being handed in (<see cref="HoldForHandIn"/>).</summary>
    private static bool handingIn;

    /// <summary>The three plugins as last looked at.</summary>
    public static OtherPluginState State { get; private set; }

    /// <summary>The stop lists "Provenance" is in right now.</summary>
    public static StopListHolds Held => new(YesAlreadyList.Held, TextAdvanceList.Held);

    /// <summary>
    /// Every frame, before the runs: "Provenance" goes into the stop lists, or out of them, the moment a run starts or
    /// stops working or the setting changes. About once a second the three plugins are looked at again, and an entry
    /// a plugin cleared meanwhile is put back (TextAdvance empties its list when it loads).
    /// </summary>
    public static void Tick(Plugin plugin)
    {
        try
        {
            var now = DateTime.UtcNow;
            var setting = plugin.Config.Security.PauseOtherPlugins;
            var working = plugin.Runs.Working || plugin.CosmicLoop.Working || plugin.DeliveryLoop.Working || plugin.TribeLoop.Working;
            var due = now >= nextLook;
            if (!due && setting == pauseOthers && working == runWorking) return;
            pauseOthers = setting;
            runWorking = working;
            if (due)
            {
                nextLook = now + Every;
                Look(now);
            }
            Sync(now);
        }
        catch (Exception e)
        {
            // Never into the frame: the runs tick right after this.
            Services.Log.Debug("Other plugins: {Message}", e.Message);
        }
    }

    /// <summary>
    /// Looks at the three plugins now instead of going by the last look: for the moment Start is pressed, never
    /// every frame.
    /// </summary>
    public static void Look() => Look(DateTime.UtcNow);

    /// <summary>Which of the three are loaded, whether TextAdvance is switched on, and whether Questionable runs.</summary>
    private static void Look(DateTime now)
    {
        try
        {
            var yesAlready = Ipc.IsLoaded(PluginConflicts.YesAlready);
            var textAdvance = Ipc.IsLoaded(PluginConflicts.TextAdvance);
            var questionable = Ipc.IsLoaded(PluginConflicts.Questionable);
            State = new OtherPluginState(yesAlready, textAdvance, TextAdvanceIsEnabled.Ask(textAdvance, now),
                questionable, QuestionableIsRunning.Ask(questionable, now));
        }
        catch (Exception e)
        {
            // What was known stays; the stop lists are kept up all the same.
            Services.Log.Debug("Other plugins: {Message}", e.Message);
        }
    }

    /// <summary>Each stop list gets the entry it should have now, or loses it; a change goes into the debug log.</summary>
    private static void Sync(DateTime now)
    {
        var want = PluginConflicts.Holds(pauseOthers, runWorking, handingIn);
        // Both, whatever the first one did.
        var changed = YesAlreadyList.Set(want.YesAlready, now) | TextAdvanceList.Set(want.TextAdvance, now);
        if (changed)
            DebugLog.Write("plugins", $"stop lists now: YesAlready {(want.YesAlready ? "held" : "not held")}, TextAdvance {(want.TextAdvance ? "held" : "not held")}"
                                      + $" (run working {runWorking}, handing in {handingIn}, setting {pauseOthers})");
    }

    /// <summary>
    /// Custom deliveries are being handed in, or no longer: YesAlready stands aside for that whatever the setting
    /// (its own turn-in races the run's). At once, not at the next look; the entry stays while a run's pause wants it.
    /// </summary>
    public static void HoldForHandIn(bool hold)
    {
        if (handingIn == hold) return;
        handingIn = hold;
        Sync(DateTime.UtcNow);
    }

    /// <summary>Why a run can't start because of another plugin (Questionable is running its quests), or null.</summary>
    /// <param name="lookFirst">Ask now instead of going by the last look (see <see cref="Look()"/>).</param>
    public static string? StartBlocker(bool lookFirst = false)
    {
        if (lookFirst) Look();
        return PluginConflicts.StartBlocker(State);
    }

    /// <summary>Why everything stops because of another plugin (Questionable runs while a run works), or null.</summary>
    public static string? StopReason(bool working) => PluginConflicts.StopReason(State, working);

    /// <summary>The three plugins, the entries held and the setting in one line, for a bug report.</summary>
    public static string Describe(bool setting) => PluginConflicts.Describe(State, setting, Held);

    /// <summary>Unload: out of both stop lists, whatever was going on.</summary>
    public static void Dispose()
    {
        runWorking = handingIn = false;
        var now = DateTime.UtcNow;
        YesAlreadyList.Set(false, now);
        TextAdvanceList.Set(false, now);
    }

    /// <summary>A yes-or-no question to another plugin over IPC.</summary>
    private sealed class Question(string name)
    {
        private DateTime retryAt;

        /// <summary>The answer; null when the plugin isn't loaded, or didn't answer (then or a moment ago).</summary>
        public bool? Ask(bool loaded, DateTime now)
        {
            if (!loaded)
            {
                retryAt = default;
                return null;
            }
            if (now < retryAt) return null;
            if (Ipc.TryFunc<bool>(name, out var answer)) return answer;
            retryAt = now + Unavailable;
            return null;
        }
    }

    /// <summary>
    /// One plugin's stop list in Dalamud's data share. It's fetched when the entry goes in and let go of when the
    /// entry comes out, as the data share counts who uses it.
    /// </summary>
    private sealed class StopList(string tag)
    {
        private HashSet<string>? list;
        private DateTime retryAt;

        /// <summary>"Provenance" is in the list right now.</summary>
        public bool Held
        {
            get
            {
                if (list is not { } names) return false;
                lock (names) return names.Contains(Me);
            }
        }

        /// <summary>
        /// Puts the entry in (again, when the plugin cleared its list meanwhile), or takes it out and lets go of the
        /// list. True when that changed the list.
        /// </summary>
        public bool Set(bool hold, DateTime now)
        {
            try
            {
                if (hold)
                {
                    if (list == null)
                    {
                        if (now < retryAt) return false;
                        list = Services.PluginInterface.GetOrCreateData(tag, () => new HashSet<string>());
                    }
                    lock (list) return list.Add(Me);
                }
                if (list is not { } names) return false;
                list = null;
                bool removed;
                lock (names) removed = names.Remove(Me);
                Services.PluginInterface.RelinquishData(tag);
                return removed;
            }
            catch (Exception e)
            {
                retryAt = now + Unavailable;
                Services.Log.Debug("{List}: {Message}", tag, e.Message);
                return false;
            }
        }
    }
}
