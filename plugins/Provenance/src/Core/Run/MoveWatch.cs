using System;
using System.Numerics;

namespace Provenance.Core.Run;

/// <summary>One frame of where the character is and whether anything could explain a move.</summary>
/// <param name="Territory">The zone.</param>
/// <param name="Position">Where the character stands.</param>
/// <param name="Loading">Between areas (a zone change under way), or no character to read.</param>
/// <param name="Explained">
/// Something this frame accounts for a move: a cast (Teleport, Return), an interaction or its menu (aetheryte,
/// aethernet, a door, a ferry), a cutscene, a knock-out, a duty, or one of Provenance's own trips.
/// </param>
public readonly record struct MoveSample(DateTime At, uint Territory, Vector3 Position, bool Loading, bool Explained);

/// <summary>A move nothing explains: another zone, or a jump within the zone.</summary>
/// <param name="Yalms">How far, on the ground (across zones, between the two zones' coordinates).</param>
public sealed record UnexpectedMove(bool ZoneChanged, uint From, uint To, float Yalms);

/// <summary>
/// Watches for the character being moved by something other than the player or Provenance (a game master can
/// summon a character, for example): a zone change, or a jump within the zone faster than anything travels, with
/// nothing shortly before it (or during the loading) that explains it. Fed every frame while a run works.
/// </summary>
public sealed class MoveWatch
{
    /// <summary>How long an explanation still counts: a teleport's cast and the loading that follows take a few seconds.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(15);
    /// <summary>A jump within the zone is at least this far on the ground...</summary>
    public const float JumpYalms = 50f;
    /// <summary>...at a speed nothing travels at (the fastest mounts fly about 20 yalms a second).</summary>
    public const float JumpSpeed = 100f;

    private MoveSample? settled;
    private DateTime explainedAt = DateTime.MinValue;
    private bool loading;
    private bool loadExplained;

    /// <summary>Starts afresh (a run starts or resumes): where the character is now is where it's meant to be.</summary>
    public void Reset()
    {
        settled = null;
        explainedAt = DateTime.MinValue;
        loading = false;
        loadExplained = false;
    }

    /// <summary>One frame; an unexpected move when this frame shows one.</summary>
    public UnexpectedMove? Feed(MoveSample s)
    {
        if (s.Explained) explainedAt = s.At;
        var recent = s.At - explainedAt <= Grace;
        if (s.Loading)
        {
            // The explanation has to come before the loading starts, or during it (a long load is still one move).
            if (!loading) { loading = true; loadExplained = recent; }
            else if (s.Explained) loadExplained = true;
            return null;
        }

        var before = settled;
        settled = s;
        if (loading)
        {
            loading = false;
            if (before is not { } b || loadExplained || recent) return null;
            var zone = b.Territory != s.Territory;
            var away = Flat(b.Position, s.Position);
            // A loading screen that left the character where it was (same zone, close by) moved nothing.
            return zone || away >= JumpYalms ? new UnexpectedMove(zone, b.Territory, s.Territory, away) : null;
        }
        if (before is not { } p || recent) return null;
        // Another zone without a loading screen seen in between (a missed frame): judged the same way.
        if (p.Territory != s.Territory) return new UnexpectedMove(true, p.Territory, s.Territory, Flat(p.Position, s.Position));
        var d = Flat(p.Position, s.Position);
        var seconds = Math.Max((s.At - p.At).TotalSeconds, 0.001);
        return d >= JumpYalms && d / seconds >= JumpSpeed ? new UnexpectedMove(false, p.Territory, s.Territory, d) : null;
    }

    private static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();
}
