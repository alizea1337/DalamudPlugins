using System.Collections.Generic;
using System.Linq;

namespace Provenance.Core.Catalog;

/// <summary>Which collectables a run turns into an item by aetherial reduction.</summary>
public static class Reduction
{
    /// <summary>
    /// The collectables a run gets an item from by reduction: gathered ones always, fished ones only while fishing
    /// for reductions is on (Settings, Fishing).
    /// </summary>
    public static IReadOnlyList<uint> Sources(IGameCatalog catalog, uint itemId, bool fishing)
    {
        var all = catalog.ReducedFrom(itemId);
        return fishing ? all : all.Where(s => !IsFished(catalog, s)).ToList();
    }

    /// <summary>A collectable the run fishes rather than gathers.</summary>
    public static bool IsFished(IGameCatalog catalog, uint source)
    {
        var src = catalog.Sources(source);
        return src.Has(Obtain.Fish) && !src.Has(Obtain.Gather | Obtain.GatherTimed);
    }
}
