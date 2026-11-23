using System;

namespace Provenance.Integrations;

/// <summary>
/// Provenance's own trips (a teleport, the aethernet, an aetheryte, an innkeeper, a door, a ferry NPC): for a while
/// after one, a move of the character is expected, so the Security check doesn't count it as being moved.
/// </summary>
internal static class MoveIntent
{
    private static DateTime until;

    /// <summary>A trip starts: moves in the next <paramref name="seconds"/> are Provenance's own.</summary>
    public static void Expect(double seconds = 30)
    {
        var t = DateTime.UtcNow.AddSeconds(seconds);
        if (t > until) until = t;
    }

    public static bool Expected => DateTime.UtcNow < until;
}
