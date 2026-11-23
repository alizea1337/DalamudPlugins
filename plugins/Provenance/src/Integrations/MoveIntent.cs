using System;

namespace Provenance.Integrations;

/// <summary>
/// Provenance's own trips (a teleport, the aethernet, an aetheryte, an innkeeper, a door, a ferry NPC): for a while
/// after one, a move of the character is expected, so the Security check doesn't count it as being moved.
/// </summary>
internal static class MoveIntent
{
    private static DateTime until;
    private static string lastWhy = "";

    /// <summary>A trip starts: moves in the next <paramref name="seconds"/> are Provenance's own.</summary>
    /// <param name="why">What the trip is, for the debug log (a new window or a different trip gets a line).</param>
    public static void Expect(double seconds = 30, string why = "")
    {
        var fresh = !Expected || why != lastWhy;
        var t = DateTime.UtcNow.AddSeconds(seconds);
        if (t > until) until = t;
        lastWhy = why;
        if (fresh) DebugLog.Write("travel", $"expecting a move for {seconds:0} s: {(why.Length > 0 ? why : "a trip")}");
    }

    public static bool Expected => DateTime.UtcNow < until;
}
