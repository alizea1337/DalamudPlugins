using System;

namespace Keystroke.Automation;

public enum AutomationState
{
    Idle,
    WaitingForDuty,
    Queued,
    Playing,
    Paused,
    Finishing,
    Error,
}

/// <summary>Per-session counters shown on the dashboard and overlay.</summary>
public sealed class SessionStats
{
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public int GamesCompleted { get; set; }
    public long MgpGained { get; set; }
    public long MgcGained { get; set; }
    public int KeysTyped { get; set; }
    public int WordsTyped { get; set; }
    public int Mistakes { get; set; }
    public TimeSpan TypingTime { get; set; }

    public double WordsPerMinute => TypingTime.TotalMinutes <= 0 ? 0 : KeysTyped / 5.0 / TypingTime.TotalMinutes;
    public double Accuracy => KeysTyped == 0 ? 1 : 1 - Math.Min(1, (double)Mistakes / KeysTyped);
    public double GamesPerHour => (DateTime.Now - StartedAt).TotalHours is var h && h > 0.01 ? GamesCompleted / h : 0;
    public double MgpPerHour => (DateTime.Now - StartedAt).TotalHours is var h && h > 0.01 ? MgpGained / h : 0;

    public void Reset()
    {
        StartedAt = DateTime.Now;
        GamesCompleted = 0;
        MgpGained = 0;
        MgcGained = 0;
        KeysTyped = 0;
        WordsTyped = 0;
        Mistakes = 0;
        TypingTime = TimeSpan.Zero;
    }
}

/// <summary>What the UI talks to. The UI never reaches past this surface.</summary>
internal interface IAutomationController
{
    AutomationState State { get; }
    string StatusDetail { get; }
    string CurrentPrompt { get; }
    string CurrentAnswer { get; }
    /// <summary>Games left in a counted loop; -1 while repeating without a limit; 0 when not looping.</summary>
    int LoopRemaining { get; }
    SessionStats Session { get; }
    string? LastError { get; }
    CofferOpener Coffers { get; }
    /// <summary>Measured interval between the last keystrokes, in ms; 0 when nothing recent.</summary>
    double MeasuredMsPerKey { get; }

    /// <summary>Play the current/next game once (queues first if in the lobby and auto-queue is on).</summary>
    void Start();
    /// <summary>Queue and play this many games, then stop.</summary>
    void StartLoop(int games);
    /// <summary>Queue and play until stopped or a stop condition is met.</summary>
    void StartUnlimited();
    void Pause();
    void Resume();
    void Stop();
    void Tick();
}
