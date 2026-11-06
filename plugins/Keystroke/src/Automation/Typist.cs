using System;

namespace Keystroke.Automation;

/// <summary>
/// Decides *when* the next key goes out. Humanized mode draws per-key delays from a log-normal
/// distribution around the configured median with occasional longer hesitations; Fast and Custom
/// use the same machinery with different parameters.
/// </summary>
internal sealed class Typist
{
    private readonly Random rng = new();
    private readonly Func<TypingSettings> settings;

    public Typist(Func<TypingSettings> settings) => this.settings = settings;

    /// <summary>Delay before the next character of a word.</summary>
    public TimeSpan NextKeyDelay()
    {
        var t = settings();
        var median = Math.Max(5, t.MedianKeyDelayMs);
        var sigma = Math.Clamp(t.KeyDelayJitter, 0f, 1.5f);
        // log-normal: median * exp(sigma * N(0,1)), clamped so a single key never stalls.
        var z = Gaussian();
        var ms = median * Math.Exp(sigma * z);
        ms = Math.Clamp(ms, median * 0.3, median * 4.0);

        if (t.HesitationChance > 0 && rng.NextDouble() < t.HesitationChance)
        {
            var lo = Math.Max(0, t.HesitationMinMs);
            var hi = Math.Max(lo, t.HesitationMaxMs);
            ms += lo + rng.NextDouble() * (hi - lo);
        }
        return TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>Delay between finishing one word (Enter sent) and starting the next.</summary>
    public TimeSpan BetweenWordsDelay()
    {
        var t = settings();
        var baseMs = Math.Max(0, t.BetweenWordsMs);
        var jitter = baseMs * 0.4 * (rng.NextDouble() - 0.5);
        return TimeSpan.FromMilliseconds(Math.Max(0, baseMs + jitter));
    }

    /// <summary>Short pause before pressing Enter, like a human confirming the word.</summary>
    public TimeSpan BeforeEnterDelay() => TimeSpan.FromMilliseconds(Math.Max(10, settings().MedianKeyDelayMs * (0.6 + rng.NextDouble() * 0.6)));

    private double Gaussian()
    {
        // Box-Muller
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
