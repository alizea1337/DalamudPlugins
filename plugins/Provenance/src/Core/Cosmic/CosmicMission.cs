using System;
using System.Collections.Generic;

namespace Provenance.Core.Cosmic;

/// <summary>Cosmic Exploration's planets, in the game's own order (WKSManager's planet index).</summary>
public enum Planet : byte
{
    SinusArdorum = 0,
    Phaenna = 1,
    Oizys = 2,
    Auxesia = 3,
}

public static class Planets
{
    public static readonly Planet[] All = { Planet.SinusArdorum, Planet.Phaenna, Planet.Oizys, Planet.Auxesia };

    /// <summary>Cosmocredits: earned on every planet, spent at the Cosmocredit Exchange on every planet.</summary>
    public const uint Cosmocredit = 45690;
    public const int CosmocreditCap = 30000;
    /// <summary>Each planet's own credits cap here; they only buy Cosmic Fortunes draws, on their own planet.</summary>
    public const int PlanetCreditCap = 10000;

    public static string Name(this Planet p) => p switch
    {
        Planet.SinusArdorum => "Sinus Ardorum",
        Planet.Phaenna => "Phaenna",
        Planet.Oizys => "Oizys",
        _ => "Auxesia",
    };

    /// <summary>TerritoryType row of the planet.</summary>
    public static uint Territory(this Planet p) => p switch
    {
        Planet.SinusArdorum => 1237,
        Planet.Phaenna => 1291,
        Planet.Oizys => 1310,
        _ => 1319,
    };

    /// <summary>The planet's own credit (Lunar Credit on Sinus Ardorum).</summary>
    public static uint CreditItem(this Planet p) => p switch
    {
        Planet.SinusArdorum => 45691,
        Planet.Phaenna => 48146,
        Planet.Oizys => 48147,
        _ => 48148,
    };

    public static Planet? OfTerritory(uint territory)
    {
        foreach (var p in All)
            if (p.Territory() == territory) return p;
        return null;
    }

    /// <summary>The planet a WKSMissionUnit row belongs to: missions sit in one block of rows per planet.</summary>
    public static Planet OfMission(uint missionId) => missionId switch
    {
        <= 544 => Planet.SinusArdorum,
        <= 1039 => Planet.Phaenna,
        <= 1369 => Planet.Oizys,
        _ => Planet.Auxesia,
    };
}

/// <summary>A mission's class (the game's LevelGroup): D to A, and A's harder EX and EX+.</summary>
public enum MissionRank : byte
{
    D = 1,
    C = 2,
    B = 3,
    A = 4,
    EX = 5,
    EXPlus = 6,
}

public static class MissionRanks
{
    public static string Label(this MissionRank r) => r switch
    {
        MissionRank.EX => "EX",
        MissionRank.EXPlus => "EX+",
        _ => r.ToString(),
    };

    /// <summary>The class level a rank opens at (it also needs missions of the rank below, see <see cref="CosmicPicker.RankOpen"/>).</summary>
    public static int Level(this MissionRank r) => r switch
    {
        MissionRank.D => 10,
        MissionRank.C => 50,
        MissionRank.B => 90,
        _ => 100,
    };
}

/// <summary>What sets a mission apart from a basic one; the extras on the Cosmic Exploration page pick which the loop takes.</summary>
[Flags]
public enum MissionKind
{
    None = 0,
    /// <summary>Only during a red alert.</summary>
    Critical = 1 << 0,
    /// <summary>Opens once the one before it in its chain is done.</summary>
    Chained = 1 << 1,
    /// <summary>Only in certain weather.</summary>
    Weather = 1 << 2,
    /// <summary>Only at certain Eorzea times.</summary>
    Time = 1 << 3,
    /// <summary>Tool Mastery (Auxesia).</summary>
    Master = 1 << 4,
    /// <summary>A gatherer collects what a crafter then makes (Sinus Ardorum).</summary>
    TwoClass = 1 << 5,
}

/// <summary>The special action a mission lends its crafter.</summary>
public enum StellarAction : byte
{
    None,
    MaterialMiracle,
    SteadyHand,
}

/// <param name="IngredientId">The one ingredient a mission recipe takes (the mission's Cosmic Containers, or an earlier step's result).</param>
public sealed record MissionStep(uint RecipeId, uint ItemId, uint IngredientId, int IngredientAmount, int Yield);

/// <summary>An item the objective asks for, and how many.</summary>
public sealed record MissionTarget(uint ItemId, int Count);

/// <param name="Cosmocredits">Base amounts: a better score pays more (learned per mission, see <see cref="MissionRecord"/>).</param>
/// <param name="TokenItem">Exploration Tokens on later planets' harder missions, or 0.</param>
public sealed record MissionReward(int Cosmocredits, int PlanetCredits, int Dronebits, uint TokenItem = 0, int Tokens = 0);

/// <summary>A stellar mission (WKSMissionUnit row) with what the loop needs to judge and do it.</summary>
/// <param name="ClassJob">ClassJob row (8 Carpenter … 18 Fisher); <paramref name="SecondClassJob"/> on two-class missions.</param>
/// <param name="Silver">Score for silver; <paramref name="Gold"/> for gold. Bronze is finishing at all.</param>
/// <param name="Steps">The recipes in crafting order, the ones in between first; empty for gatherers.</param>
/// <param name="Targets">What the objective asks for: one item mostly, three different ones on some missions.</param>
/// <param name="Previous">The mission before it in its chain, or 0.</param>
public sealed record CosmicMission(
    uint Id,
    string Name,
    Planet Planet,
    uint ClassJob,
    uint SecondClassJob,
    MissionRank Rank,
    MissionKind Kinds,
    int TimeLimitSeconds,
    int Silver,
    int Gold,
    IReadOnlyList<MissionStep> Steps,
    uint SuppliedItem,
    int SuppliedCount,
    IReadOnlyList<MissionTarget> Targets,
    StellarAction Action,
    int ActionCharges,
    MissionReward Reward,
    uint Previous = 0)
{
    public bool IsCraft => Steps.Count > 0;

    /// <summary>The recipe whose result the objective counts.</summary>
    public MissionStep? Final => Steps.Count == 0 ? null : Steps[^1];

    public bool Is(MissionKind kind) => (Kinds & kind) != 0;

    /// <summary>"B: Data Entry Paper".</summary>
    public string Label => $"{Rank.Label()}: {Name}";
}
