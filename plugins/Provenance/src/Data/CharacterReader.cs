using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using Provenance.Core.Character;
using GamePlayerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState;

namespace Provenance.Data;

/// <summary>
/// Reads levels, gearsets, crafting and gathering stats, master books and folklore tomes of the
/// logged-in character. Framework thread only.
/// </summary>
internal static unsafe class CharacterReader
{
    private const int GearsetCount = 100;
    private const int SoulCrystalSlot = 13;
    private const uint WellFed = 48;
    private const uint Medicated = 49;

    public static CharacterProfile? Read(GameCatalog catalog, CharacterProfile? previous)
    {
        var ps = GamePlayerState.Instance();
        var gearsets = RaptureGearsetModule.Instance();
        if (!Services.PlayerState.IsLoaded || ps == null || gearsets == null) return null;

        var profile = new CharacterProfile
        {
            ContentId = Services.PlayerState.ContentId,
            Name = Services.PlayerState.CharacterName,
            World = Services.PlayerState.HomeWorld.ValueNullable?.Name.ExtractText() ?? "",
            CheckedAtUtc = DateTime.UtcNow,
            BaseStats = previous?.BaseStats != null ? new Dictionary<string, int[]>(previous.BaseStats) : new(),
        };

        // The player object is the reliable source for the job being played right now.
        var currentJob = Services.Objects.LocalPlayer?.ClassJob.RowId ?? (uint)ps->CurrentClassJobId;
        var buffed = Services.Objects.LocalPlayer?.StatusList.Any(st => st.StatusId is WellFed or Medicated) ?? false;
        var notes = new List<string>();

        // Measure base stats on the current job first, so the other jobs of its kind can use them.
        int[]? live = null;
        if (ClassJobs.IsCrafter(currentJob) || ClassJobs.IsGatherer(currentJob))
        {
            var d = ClassJobs.Of(currentJob);
            var ids = GearStats.Params(d);
            live = new[] { ps->Attributes[(int)ids[0]], ps->Attributes[(int)ids[1]], ps->Attributes[(int)ids[2]] };
            if (buffed)
            {
                live = null;
                notes.Add("Food or medicine is active, so this check didn't measure base stats. Check again without them for exact numbers.");
            }
            else
            {
                var gear = GearStats.Sum(d, EquippedPieces());
                profile.BaseStats[CharacterProfile.BaseKey(d, Level(currentJob))] = StatMath.BaseFrom(live, gear);
            }
        }
        else
        {
            notes.Add("Switch to a crafter or gatherer before checking to measure base stats; until then, other jobs show gear stats only.");
        }

        var owned = OwnedGear();
        foreach (var job in ClassJobs.Crafters.Concat(ClassJobs.Gatherers))
            profile.Jobs.Add(ReadJob(job, gearsets, currentJob, live, profile, catalog, owned));

        foreach (var book in catalog.MasterBooks)
            if (ps->IsSecretRecipeBookUnlocked(book.Id)) profile.MasterBooks.Add(book.Id);
        foreach (var tome in catalog.FolkloreTomes)
            if (ItemUnlocks.IsLearned(tome.ItemId)) profile.FolkloreTomes.Add(tome.ItemId);

        profile.Note = notes.Count > 0 ? string.Join(" ", notes) : null;
        return profile;
    }

    private static JobStats ReadJob(uint job, RaptureGearsetModule* module, uint currentJob, int[]? live, CharacterProfile profile, GameCatalog catalog,
        Dictionary<uint, List<GearStats.Piece>> owned)
    {
        var d = ClassJobs.Of(job);
        var stats = new JobStats { ClassJobId = job, Level = Level(job) };
        if (ClassJobs.IsCrafter(job) && catalog.ManipulationQuest.TryGetValue(job, out var quest))
            stats.Manipulation = QuestManager.IsQuestComplete(quest);

        // The highest item level gearset for the job is the one a run would equip.
        var best = -1;
        var count = 0;
        for (var i = 0; i < GearsetCount; i++)
        {
            var e = module->Entries[i];
            if ((e.Flags & RaptureGearsetModule.GearsetFlag.Exists) == 0 || e.ClassJob != job) continue;
            count++;
            // Highest item level wins; on a tie the gearset being worn wins, so it can be measured live.
            if (best < 0 || e.ItemLevel > module->Entries[best].ItemLevel
                || (e.ItemLevel == module->Entries[best].ItemLevel && i == module->CurrentGearsetIndex)) best = i;
        }
        if (best < 0) return stats;

        var entry = module->Entries[best];
        stats.GearsetNumber = best + 1;
        stats.GearsetName = entry.NameString;
        stats.ItemLevel = entry.ItemLevel;
        stats.OtherGearsets = count - 1;
        stats.Specialist = ClassJobs.IsCrafter(job) && entry.Items[SoulCrystalSlot].ItemId != 0;

        if (job == currentJob && live != null && module->CurrentGearsetIndex == best)
        {
            stats.Stats = live;
            stats.Source = StatSource.Measured;
            return stats;
        }

        var gear = GearStats.Sum(d, GearsetPieces(entry, owned));
        var (total, source) = StatMath.Combine(gear, profile.BaseStats.GetValueOrDefault(CharacterProfile.BaseKey(d, stats.Level)));
        stats.Stats = total;
        stats.Source = source;
        return stats;
    }

    private static int Level(uint classJob)
    {
        var row = Services.Data.GetExcelSheet<ClassJob>().GetRowOrDefault(classJob);
        return row is { } cj ? Services.PlayerState.GetClassJobLevel(cj) : 0;
    }

    /// <summary>
    /// A saved gearset only records item ids and materia; whether a piece is HQ isn't reliable there.
    /// So each piece is matched to the real item in the armoury chest or equipped slots (same materia
    /// preferred), and its HQ flag and materia are read from that item. Unmatched pieces fall back to
    /// the gearset's own record.
    /// </summary>
    private static GearStats.Piece[] GearsetPieces(RaptureGearsetModule.GearsetEntry entry, Dictionary<uint, List<GearStats.Piece>> owned)
    {
        var pieces = new List<GearStats.Piece>(14);
        var used = new HashSet<GearStats.Piece>(ReferenceEqualityComparer.Instance);
        foreach (var item in entry.Items)
        {
            if (item.ItemId == 0) continue;
            var baseId = item.ItemId % 1_000_000;
            var materia = item.Materia.ToArray();
            var grades = item.MateriaGrades.ToArray();
            GearStats.Piece? match = null;
            if (owned.TryGetValue(baseId, out var candidates))
            {
                var free = candidates.Where(c => !used.Contains(c)).ToList();
                match = free.FirstOrDefault(c => c.Materia.SequenceEqual(materia)) ?? free.FirstOrDefault();
            }
            if (match != null)
            {
                used.Add(match);
                pieces.Add(match);
            }
            else
            {
                Services.Log.Debug("Gearset {Name}: item {Raw} not found in armoury or equipped slots", entry.NameString, item.ItemId);
                pieces.Add(new GearStats.Piece(baseId, item.ItemId >= 1_000_000, materia, grades));
            }
        }
        return pieces.ToArray();
    }

    private static readonly InventoryType[] GearContainers =
    {
        InventoryType.EquippedItems,
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead, InventoryType.ArmoryBody,
        InventoryType.ArmoryHands, InventoryType.ArmoryWaist, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar, InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
        InventoryType.ArmorySoulCrystal,
    };

    /// <summary>Every piece of gear the character carries in equipped slots and the armoury chest, by item id.</summary>
    private static Dictionary<uint, List<GearStats.Piece>> OwnedGear()
    {
        var result = new Dictionary<uint, List<GearStats.Piece>>();
        var manager = InventoryManager.Instance();
        if (manager == null) return result;
        foreach (var type in GearContainers)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->GetBaseItemId() == 0) continue;
                var piece = PieceOf(slot);
                if (!result.TryGetValue(piece.ItemId, out var list)) result[piece.ItemId] = list = new List<GearStats.Piece>(1);
                list.Add(piece);
            }
        }
        return result;
    }

    private static GearStats.Piece PieceOf(InventoryItem* slot)
    {
        var materia = new ushort[5];
        var grades = new byte[5];
        for (var m = 0; m < 5; m++)
        {
            materia[m] = slot->Materia[m];
            grades[m] = slot->MateriaGrades[m];
        }
        return new GearStats.Piece(slot->GetBaseItemId(), slot->GetFlags().HasFlag(InventoryItem.ItemFlags.HighQuality), materia, grades);
    }

    private static GearStats.Piece[] EquippedPieces()
    {
        var pieces = new List<GearStats.Piece>(14);
        var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
        if (container == null) return pieces.ToArray();
        for (var i = 0; i < container->Size; i++)
        {
            var slot = container->GetInventorySlot(i);
            if (slot == null || slot->GetBaseItemId() == 0) continue;
            pieces.Add(PieceOf(slot));
        }
        return pieces.ToArray();
    }
}
