using System.Collections.Generic;
using System.Linq;

namespace Provenance.Core.Fishing;

/// <summary>
/// Spearfishing catches, told from the bags: the window never says what was caught, so every rise in the
/// count of a fish between two looks at the bags is a catch of that fish. Falls (a fish moved or used up)
/// are not catches. Follows AutoHook's SpearfishingUpdateModule.ProcessCatches (BSD-3, Puni.sh).
/// </summary>
public static class SpearCatches
{
    /// <summary>What rose between two counts (item id → amount in the bags), by item id; ids missing from a count are 0.</summary>
    public static List<(uint ItemId, int Amount)> Gains(IReadOnlyDictionary<uint, int> before, IReadOnlyDictionary<uint, int> now)
    {
        var result = new List<(uint, int)>();
        foreach (var (id, count) in now)
        {
            var was = before.TryGetValue(id, out var b) ? b : 0;
            if (count > was) result.Add((id, count - was));
        }
        return result.OrderBy(g => g.Item1).ToList();
    }
}
