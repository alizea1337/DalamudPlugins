using System;
using System.Collections.Generic;

namespace Provenance.Core.Character;

public enum ConsumableKind { Food, Medicine }

/// <summary>One stat a food or medicine raises: a percentage of the stat up to a cap (or a flat amount).</summary>
public readonly record struct ConsumableParam(int StatIndex, bool Relative, int Value, int Max, int ValueHq, int MaxHq);

/// <summary>A food or medicine that raises crafting or gathering stats.</summary>
public sealed record ConsumableInfo(
    uint ItemId,
    string Name,
    uint Icon,
    ConsumableKind Kind,
    Discipline Discipline,
    int ItemLevel,
    bool CanBeHq,
    IReadOnlyList<ConsumableParam> Params)
{
    /// <summary>What it adds to [primary, secondary, points] on top of the given stats.</summary>
    public int[] Bonus(int[] stats, bool hq)
    {
        var bonus = new int[3];
        foreach (var p in Params)
        {
            if (p.StatIndex is < 0 or > 2) continue;
            var useHq = hq && CanBeHq;
            var value = useHq ? p.ValueHq : p.Value;
            var max = useHq ? p.MaxHq : p.Max;
            bonus[p.StatIndex] += p.Relative ? Math.Min(max, stats[p.StatIndex] * value / 100) : value;
        }
        return bonus;
    }
}

/// <summary>The food and medicine picked for one discipline. ItemId 0 means none.</summary>
[Serializable]
public sealed class ConsumableChoice
{
    public uint ItemId { get; set; }
    public bool Hq { get; set; } = true;
}
