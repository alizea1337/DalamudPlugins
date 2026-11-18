using System;
using System.Collections.Generic;

namespace Provenance.Core.Character;

/// <summary>Disciples of the Hand (crafters) and of the Land (gatherers) have different stats.</summary>
public enum Discipline { Hand, Land }

/// <summary>Where a job's numbers came from.</summary>
public enum StatSource
{
    /// <summary>No gearset saved for the job.</summary>
    None,
    /// <summary>Read from the game while on that job in that gearset.</summary>
    Measured,
    /// <summary>Gearset items and materia plus base stats measured on a job of the same kind and level.</summary>
    GearAndBase,
    /// <summary>Gearset items and materia only; base stats unknown until a job of this kind is measured at this level.</summary>
    GearOnly,
}

public static class ClassJobs
{
    public static readonly uint[] Crafters = { 8, 9, 10, 11, 12, 13, 14, 15 };
    public static readonly uint[] Gatherers = { 16, 17, 18 };

    public static bool IsCrafter(uint classJob) => classJob is >= 8 and <= 15;
    public static bool IsGatherer(uint classJob) => classJob is >= 16 and <= 18;

    public static Discipline Of(uint classJob) => IsCrafter(classJob) ? Discipline.Hand : Discipline.Land;

    public static string Abbreviation(uint classJob) => classJob switch
    {
        8 => "CRP", 9 => "BSM", 10 => "ARM", 11 => "GSM", 12 => "LTW", 13 => "WVR", 14 => "ALC", 15 => "CUL",
        16 => "MIN", 17 => "BTN", 18 => "FSH",
        1 => "GLA", 2 => "PGL", 3 => "MRD", 4 => "LNC", 5 => "ARC", 6 => "CNJ", 7 => "THM", 26 => "ACN", 29 => "ROG",
        19 => "PLD", 20 => "MNK", 21 => "WAR", 22 => "DRG", 23 => "BRD", 24 => "WHM", 25 => "BLM", 27 => "SMN", 28 => "SCH",
        30 => "NIN", 31 => "MCH", 32 => "DRK", 33 => "AST", 34 => "SAM", 35 => "RDM", 36 => "BLU", 37 => "GNB", 38 => "DNC",
        39 => "RPR", 40 => "SGE", 41 => "VPR", 42 => "PCT",
        _ => "?",
    };

    public enum Role { Tank, Healer, Dps, Other }

    /// <summary>Combat role of a job or starting class.</summary>
    public static Role RoleOf(uint classJob) => classJob switch
    {
        1 or 3 or 19 or 21 or 32 or 37 => Role.Tank,
        6 or 24 or 28 or 33 or 40 => Role.Healer,
        2 or 4 or 5 or 7 or 26 or 29 or 20 or 22 or 23 or 25 or 27 or 30 or 31 or 34 or 35 or 36 or 38 or 39 or 41 or 42 => Role.Dps,
        _ => Role.Other,
    };

    public static string StatName(Discipline d, int index) => (d, index) switch
    {
        (Discipline.Hand, 0) => "Craftsmanship",
        (Discipline.Hand, 1) => "Control",
        (Discipline.Hand, 2) => "CP",
        (Discipline.Land, 0) => "Gathering",
        (Discipline.Land, 1) => "Perception",
        (Discipline.Land, 2) => "GP",
        _ => "?",
    };
}

/// <summary>One crafter or gatherer as last checked.</summary>
[Serializable]
public sealed class JobStats
{
    public uint ClassJobId { get; set; }
    public int Level { get; set; }

    /// <summary>Gearset number as the game shows it (1-based); 0 when none is saved.</summary>
    public int GearsetNumber { get; set; }
    public string GearsetName { get; set; } = "";
    public int ItemLevel { get; set; }
    /// <summary>Further saved gearsets for this job (the best one by item level is used).</summary>
    public int OtherGearsets { get; set; }

    /// <summary>Craftsmanship, Control, CP — or Gathering, Perception, GP.</summary>
    public int[] Stats { get; set; } = new int[3];
    public StatSource Source { get; set; }

    public bool Specialist { get; set; }
    /// <summary>Crafters only: the Manipulation action is learned.</summary>
    public bool Manipulation { get; set; }
}

[Serializable]
public sealed class CharacterProfile
{
    public ulong ContentId { get; set; }
    public string Name { get; set; } = "";
    public string World { get; set; } = "";
    public DateTime CheckedAtUtc { get; set; }

    public List<JobStats> Jobs { get; set; } = new();
    /// <summary>SecretRecipeBook rows the character has learned.</summary>
    public List<uint> MasterBooks { get; set; } = new();
    /// <summary>Folklore tomes read, by the tome's item id.</summary>
    public List<uint> FolkloreTomes { get; set; } = new();

    /// <summary>Base stats per discipline and level ("Hand:100"), kept between checks.</summary>
    public Dictionary<string, int[]> BaseStats { get; set; } = new();

    /// <summary>Something the user should know about the last check (food active, not on a crafter…).</summary>
    public string? Note { get; set; }

    public JobStats? Job(uint classJob) => Jobs.Find(j => j.ClassJobId == classJob);

    public static string BaseKey(Discipline d, int level) => $"{d}:{level}";
}

public static class StatMath
{
    /// <summary>Base stats are what the live numbers have beyond the equipped gear.</summary>
    public static int[] BaseFrom(int[] live, int[] gear)
    {
        var b = new int[3];
        for (var i = 0; i < 3; i++) b[i] = Math.Max(0, live[i] - gear[i]);
        return b;
    }

    public static (int[] Stats, StatSource Source) Combine(int[] gear, int[]? baseStats)
    {
        if (baseStats == null) return ((int[])gear.Clone(), StatSource.GearOnly);
        var total = new int[3];
        for (var i = 0; i < 3; i++) total[i] = gear[i] + baseStats[i];
        return (total, StatSource.GearAndBase);
    }
}
