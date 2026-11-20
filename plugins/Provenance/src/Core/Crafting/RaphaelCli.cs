using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Provenance.Core.Crafting;

/// <summary>What Raphael needs to solve one craft.</summary>
/// <param name="TargetQuality">null: as much quality as possible. 0: progress only (fastest macro).</param>
/// <param name="HqIngredients">
/// HQ ingredients going in, per ingredient of the recipe in its order (crystals left out); they start the craft
/// with quality. Null or all 0 for none.
/// </param>
public sealed record RaphaelRequest(
    uint RecipeId,
    int Level,
    int Craftsmanship,
    int Control,
    int Cp,
    bool Manipulation,
    bool Specialist,
    int? TargetQuality,
    IReadOnlyList<int>? HqIngredients = null)
{
    public bool AnyHqIngredients => HqIngredients?.Any(n => n > 0) == true;

    public string CacheKey => $"{RecipeId}|{Level}|{Craftsmanship}|{Control}|{Cp}|{Manipulation}|{Specialist}|{TargetQuality?.ToString() ?? "max"}"
                              + (AnyHqIngredients ? "|hq " + string.Join(",", HqIngredients!) : "");
}

/// <summary>A rotation, and the quality it ends at (counting what HQ ingredients start it with) out of the recipe's most.</summary>
/// <param name="FinalQuality">-1 when raphael-cli didn't say.</param>
/// <param name="MaxQuality">-1 when raphael-cli didn't say.</param>
public sealed record RaphaelSolution(IReadOnlyList<uint> Actions, int FinalQuality, int MaxQuality)
{
    /// <summary>Ends at the recipe's most quality, so the item comes out HQ for certain.</summary>
    public bool ReachesMax => MaxQuality > 0 && FinalQuality >= MaxQuality;
}

/// <summary>Command line for raphael-cli and parsing of its answer. No process handling here.</summary>
public static class RaphaelCli
{
    public static string Arguments(RaphaelRequest r)
    {
        var a = new StringBuilder();
        a.Append(CultureInfo.InvariantCulture, $"solve --recipe-id {r.RecipeId} --level {r.Level} --stats {r.Craftsmanship} {r.Control} {r.Cp}");
        if (r.Manipulation) a.Append(" --manipulation");
        // Heart and Soul / Quick Innovation only add quality; with no quality wanted raphael-cli even
        // fails with an internal error when they are offered, so leave them out.
        if (r.Specialist && r.TargetQuality != 0) a.Append(" --heart-and-soul --quick-innovation");
        if (r.TargetQuality is { } q) a.Append(CultureInfo.InvariantCulture, $" --target-quality {Math.Max(0, q)}");
        // HQ ingredients start the craft with quality (raphael-cli works out how much from the recipe).
        if (r.TargetQuality != 0 && r.AnyHqIngredients)
            a.Append(" --hq-ingredients ").Append(string.Join(" ", r.HqIngredients!.Take(6).Select(n => Math.Clamp(n, 0, 255).ToString(CultureInfo.InvariantCulture))));
        a.Append(" --output-variables action_ids final_quality recipe_max_quality");
        return a.ToString();
    }

    /// <summary>
    /// Reads <c>"[100379, 4639]",14087,21200</c>: the actions, then the quality the rotation ends at and the
    /// recipe's most. Null when the output isn't a solution.
    /// </summary>
    public static RaphaelSolution? ParseSolution(string output)
    {
        var ids = ParseActionIds(output);
        if (ids.Count == 0) return null;
        var text = output.Trim();
        var rest = text[(text.IndexOf(']') + 1)..].Trim('"', ' ', '\r', '\n')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int Number(int i) => i < rest.Length && int.TryParse(rest[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1;
        return new RaphaelSolution(ids, Number(0), Number(1));
    }

    /// <summary>Reads <c>"[100379, 4639, 19297]"</c> into action ids. Empty when the output isn't a solution.</summary>
    public static List<uint> ParseActionIds(string output)
    {
        var ids = new List<uint>();
        var text = output.Trim();
        var open = text.IndexOf('[');
        var close = text.IndexOf(']', open + 1);
        if (open < 0 || close < 0) return ids;
        foreach (var part in text[(open + 1)..close].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0) return new List<uint>();
            ids.Add(id);
        }
        return ids;
    }
}
