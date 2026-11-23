using System.Collections.Generic;
using System.Linq;

namespace Provenance.Core.Catalog;

/// <summary>Which collectables a run turns into an item by aetherial reduction.</summary>
public static class Reduction
{
    /// <summary>
    /// The collectables a run gets an item from by reduction: gathered ones always, fished ones only while fishing
    /// for reductions is on (Settings, Fishing), and never a timed fish (a time window or weather): waiting for its
    /// window would hold the run up for the sake of a reduction.
    /// </summary>
    public static IReadOnlyList<uint> Sources(IGameCatalog catalog, uint itemId, bool fishing)
        => catalog.ReducedFrom(itemId).Where(s => !IsFished(catalog, s) || fishing && !catalog.TimedFish(s)).ToList();

    /// <summary>A collectable the run fishes rather than gathers.</summary>
    public static bool IsFished(IGameCatalog catalog, uint source)
    {
        var src = catalog.Sources(source);
        return src.Has(Obtain.Fish) && !src.Has(Obtain.Gather | Obtain.GatherTimed);
    }
}
