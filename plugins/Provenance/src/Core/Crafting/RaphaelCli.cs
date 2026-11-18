using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Provenance.Core.Crafting;

/// <summary>What Raphael needs to solve one craft.</summary>
/// <param name="TargetQuality">null: as much quality as possible. 0: progress only (fastest macro).</param>
public sealed record RaphaelRequest(
    uint RecipeId,
    int Level,
    int Craftsmanship,
    int Control,
    int Cp,
    bool Manipulation,
    bool Specialist,
    int? TargetQuality)
{
    public string CacheKey => $"{RecipeId}|{Level}|{Craftsmanship}|{Control}|{Cp}|{Manipulation}|{Specialist}|{TargetQuality?.ToString() ?? "max"}";
}

/// <summary>Command line for raphael-cli and parsing of its answer. No process handling here.</summary>
public static class RaphaelCli
{
    public static string Arguments(RaphaelRequest r)
    {
        var a = new StringBuilder();
        a.Append(CultureInfo.InvariantCulture, $"solve --recipe-id {r.RecipeId} --level {r.Level} --stats {r.Craftsmanship} {r.Control} {r.Cp}");
        if (r.Manipulation) a.Append(" --manipulation");
        if (r.Specialist) a.Append(" --heart-and-soul --quick-innovation");
        if (r.TargetQuality is { } q) a.Append(CultureInfo.InvariantCulture, $" --target-quality {Math.Max(0, q)}");
        a.Append(" --output-variables action_ids");
        return a.ToString();
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
