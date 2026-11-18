// Stat maths adapted from GatherBuddy Reborn's Vulcan (GatherBuddy/Crafting/GearsetStatsReader.cs),
// Copyright the GatherBuddy Reborn contributors, Apache License 2.0. See NOTICE.md.
using System;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Provenance.Core.Character;

namespace Provenance.Data;

/// <summary>Sums crafting or gathering stats over a set of gear pieces, materia included, capped like the game does.</summary>
internal static class GearStats
{
    private const int MaxEquipSlotCategory = 22;

    /// <summary>BaseParam ids for [primary, secondary, points].</summary>
    public static uint[] Params(Discipline d) => d == Discipline.Hand ? new uint[] { 70, 71, 11 } : new uint[] { 72, 73, 10 };

    public readonly record struct Piece(uint ItemId, bool Hq, ushort[] Materia, byte[] Grades);

    public static int[] Sum(Discipline discipline, ReadOnlySpan<Piece> pieces)
    {
        var ids = Params(discipline);
        var total = new int[3];
        var items = Services.Data.GetExcelSheet<Item>();
        var materiaSheet = Services.Data.GetExcelSheet<Materia>();
        foreach (var piece in pieces)
        {
            if (piece.ItemId == 0 || items.GetRowOrDefault(piece.ItemId) is not { } item) continue;
            var baseStats = new int[3];
            var meld = new int[3];

            for (var i = 0; i < item.BaseParam.Count && i < item.BaseParamValue.Count; i++)
                Add(baseStats, ids, item.BaseParam[i].RowId, item.BaseParamValue[i]);
            if (piece.Hq)
                for (var i = 0; i < item.BaseParamSpecial.Count && i < item.BaseParamValueSpecial.Count; i++)
                    Add(baseStats, ids, item.BaseParamSpecial[i].RowId, item.BaseParamValueSpecial[i]);

            for (var m = 0; m < piece.Materia.Length && m < piece.Grades.Length; m++)
            {
                if (piece.Materia[m] == 0 || materiaSheet.GetRowOrDefault(piece.Materia[m]) is not { } materia) continue;
                var grade = piece.Grades[m];
                if (grade < materia.Value.Count) Add(meld, ids, materia.BaseParam.RowId, materia.Value[grade]);
            }

            for (var s = 0; s < 3; s++) total[s] += Effective(item, ids[s], s, baseStats[s], meld[s], discipline);
        }
        return total;
    }

    private static void Add(int[] stats, uint[] ids, uint param, int value)
    {
        if (value == 0) return;
        var i = Array.IndexOf(ids, param);
        if (i >= 0) stats[i] += value;
    }

    /// <summary>Materia can't push a stat past the item's cap (item level stat × slot modifier).</summary>
    private static int Effective(Item item, uint param, int index, int baseValue, int melded, Discipline d)
    {
        var uncapped = baseValue + melded;
        if (uncapped == 0) return 0;
        var slot = (int)item.EquipSlotCategory.RowId;
        if (slot <= 0 || slot > MaxEquipSlotCategory) return uncapped;
        var level = item.LevelItem.ValueNullable;
        if (level is not { } lvl) return uncapped;
        int levelStat = (d, index) switch
        {
            (Discipline.Hand, 0) => lvl.Craftsmanship,
            (Discipline.Hand, 1) => lvl.Control,
            (Discipline.Hand, 2) => lvl.CP,
            (Discipline.Land, 0) => lvl.Gathering,
            (Discipline.Land, 1) => lvl.Perception,
            _ => lvl.GP,
        };
        if (levelStat <= 0) return Math.Min(uncapped, baseValue);
        var baseParams = Services.Data.GetExcelSheet<RawRow>(name: "BaseParam");
        if (baseParams.GetRowOrDefault(param) is not { } row) return uncapped;
        var modifier = row.ReadInt16Column(slot + 3);
        if (modifier <= 0) return uncapped;
        var cap = Math.Max(baseValue, (int)Math.Round(levelStat * modifier / 1000d, MidpointRounding.AwayFromZero));
        return Math.Min(uncapped, cap);
    }
}
