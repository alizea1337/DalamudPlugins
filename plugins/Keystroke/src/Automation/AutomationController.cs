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

/// <summary>
/// What the UI talks to. The real engine lands in later milestones; the UI never reaches
/// past this surface.
/// </summary>
internal interface IAutomationController
{
    AutomationState State { get; }
    string StatusDetail { get; }
    string CurrentPrompt { get; }
    string CurrentAnswer { get; }
    int LoopRemaining { get; }
    SessionStats Session { get; }
    string? LastError { get; }

    void Start();
    void StartLoop(int games);
    void Pause();
    void Resume();
    void Stop();
    void Tick();
}

/// <summary>Milestone-1 placeholder: tracks requested state so the UI can be exercised without the engine.</summary>
internal sealed class StubAutomationController : IAutomationController
{
    public AutomationState State { get; private set; } = AutomationState.Idle;
    public string StatusDetail { get; private set; } = "Engine not implemented yet";
    public string CurrentPrompt => "";
    public string CurrentAnswer => "";
    public int LoopRemaining { get; private set; }
    public SessionStats Session { get; } = new();
    public string? LastError => null;

    public void Start()
    {
        State = AutomationState.WaitingForDuty;
        StatusDetail = "Waiting for the Keybound Brawler duty";
        Services.Log.Information("Start requested");
    }

    public void StartLoop(int games)
    {
        LoopRemaining = games;
        Start();
    }

    public void Pause()
    {
        if (State is AutomationState.Playing or AutomationState.WaitingForDuty) State = AutomationState.Paused;
    }

    public void Resume()
    {
        if (State == AutomationState.Paused) State = AutomationState.WaitingForDuty;
    }

    public void Stop()
    {
        State = AutomationState.Idle;
        LoopRemaining = 0;
        StatusDetail = "Engine not implemented yet";
    }

    public void Tick() { }
}
