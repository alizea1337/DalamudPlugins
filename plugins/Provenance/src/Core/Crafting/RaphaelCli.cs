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
/// <param name="SteadyHand">Stellar Steady Hand uses a Cosmic Exploration mission lends (0: none).</param>
/// <param name="Food">Food the rotation may count on (item id, 0: none); <paramref name="Potion"/> the same for medicine.</param>
/// <param name="Live">
/// The recipe's numbers as the game has them right now, when they differ from the sheet's (an allied society quest
/// recipe the game adjusts to the crafter's level); null: the recipe as the sheet has it.
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
    IReadOnlyList<int>? HqIngredients = null,
    int SteadyHand = 0,
    uint Food = 0,
    bool FoodHq = false,
    uint Potion = 0,
    bool PotionHq = false,
    LiveRecipe? Live = null)
{
    public bool AnyHqIngredients => HqIngredients?.Any(n => n > 0) == true;

    public string CacheKey => $"{RecipeId}|{Level}|{Craftsmanship}|{Control}|{Cp}|{Manipulation}|{Specialist}|{TargetQuality?.ToString() ?? "max"}"
                              + (AnyHqIngredients ? "|hq " + string.Join(",", HqIngredients!) : "")
                              + (SteadyHand > 0 ? "|ssh " + SteadyHand : "")
                              + (Food != 0 ? $"|food {Food}{(FoodHq ? "hq" : "")}" : "")
                              + (Potion != 0 ? $"|potion {Potion}{(PotionHq ? "hq" : "")}" : "")
                              + (Live is { } l ? $"|live {l.RecipeLevel},{l.Progress},{l.Quality},{l.Durability}" : "");
}

/// <summary>A recipe's numbers as the game shows them while crafting: its recipe level, progress, quality and durability.</summary>
public readonly record struct LiveRecipe(int RecipeLevel, int Progress, int Quality, int Durability)
{
    public override string ToString() => $"recipe level {RecipeLevel}, progress {Progress}, quality {Quality}, durability {Durability}";
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
        // The game's own numbers for the recipe, when they differ from the sheet's (raphael-cli takes one or the other).
        if (r.Live is { } live)
            a.Append(CultureInfo.InvariantCulture, $"solve --custom-recipe {live.RecipeLevel} {live.Progress} {live.Quality} {live.Durability}");
        else a.Append(CultureInfo.InvariantCulture, $"solve --recipe-id {r.RecipeId}");
        a.Append(CultureInfo.InvariantCulture, $" --level {r.Level} --stats {r.Craftsmanship} {r.Control} {r.Cp}");
        if (r.Manipulation) a.Append(" --manipulation");
        // Heart and Soul / Quick Innovation only add quality; with no quality wanted raphael-cli even
        // fails with an internal error when they are offered, so leave them out.
        if (r.Specialist && r.TargetQuality != 0) a.Append(" --heart-and-soul --quick-innovation");
        if (r.TargetQuality is { } q) a.Append(CultureInfo.InvariantCulture, $" --target-quality {Math.Max(0, q)}");
        // HQ ingredients start the craft with quality (raphael-cli works out how much from the recipe).
        if (r.TargetQuality != 0 && r.AnyHqIngredients)
            a.Append(" --hq-ingredients ").Append(string.Join(" ", r.HqIngredients!.Take(6).Select(n => Math.Clamp(n, 0, 255).ToString(CultureInfo.InvariantCulture))));
        if (r.SteadyHand > 0) a.Append(CultureInfo.InvariantCulture, $" --stellar-steady-hand {Math.Min(r.SteadyHand, 3)}");
        if (r.Food != 0) a.Append(CultureInfo.InvariantCulture, $" --food {r.Food}{(r.FoodHq ? ",HQ" : "")}");
        if (r.Potion != 0) a.Append(CultureInfo.InvariantCulture, $" --potion {r.Potion}{(r.PotionHq ? ",HQ" : "")}");
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
