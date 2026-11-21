using System;

namespace Provenance.Core.Fishing;

/// <summary>
/// What the bait for one fish really came to over the runs so far: the bait used up while fishing for it with its
/// own bait, and how many of it were landed meanwhile. Same-tug fish hooked in its place make this far more than
/// the data's guess for some holes (Purple Palate shares its tug and bite time with two others).
/// </summary>
[Serializable]
public sealed class BaitUse
{
    public double Bait { get; set; }
    public int Catches { get; set; }

    /// <summary>How many catches the data's own figure counts as, next to the ones seen.</summary>
    public const int PriorCatches = 3;

    /// <summary>
    /// The bait per catch to plan with: the data's figure, moved toward what the runs saw as catches come in (bait
    /// used up without a catch raises it too).
    /// </summary>
    public static double Blend(double fromData, BaitUse? seen)
        => seen is { } s && (s.Bait > 0 || s.Catches > 0)
            ? (fromData * PriorCatches + s.Bait) / (PriorCatches + s.Catches)
            : fromData;
}
