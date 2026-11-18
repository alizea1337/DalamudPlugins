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

        foreach (var job in ClassJobs.Crafters.Concat(ClassJobs.Gatherers))
            profile.Jobs.Add(ReadJob(job, gearsets, currentJob, live, profile, catalog));

        foreach (var book in catalog.MasterBooks)
            if (ps->IsSecretRecipeBookUnlocked(book.Id)) profile.MasterBooks.Add(book.Id);
        foreach (var tome in catalog.FolkloreTomes)
            if (ItemUnlocks.IsLearned(tome.ItemId)) profile.FolkloreTomes.Add(tome.ItemId);

        profile.Note = notes.Count > 0 ? string.Join(" ", notes) : null;
        return profile;
    }

    private static JobStats ReadJob(uint job, RaptureGearsetModule* module, uint currentJob, int[]? live, CharacterProfile profile, GameCatalog catalog)
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

        var gear = GearStats.Sum(d, GearsetPieces(entry));
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

    private static GearStats.Piece[] GearsetPieces(RaptureGearsetModule.GearsetEntry entry)
    {
        var pieces = new List<GearStats.Piece>(14);
        foreach (var item in entry.Items)
        {
            if (item.ItemId == 0) continue;
            // Gearsets store HQ items as id + 1,000,000.
            pieces.Add(new GearStats.Piece(item.ItemId % 1_000_000, item.ItemId >= 1_000_000, item.Materia.ToArray(), item.MateriaGrades.ToArray()));
        }
        return pieces.ToArray();
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
            var materia = new ushort[5];
            var grades = new byte[5];
            for (var m = 0; m < 5; m++)
            {
                materia[m] = slot->Materia[m];
                grades[m] = slot->MateriaGrades[m];
            }
            pieces.Add(new GearStats.Piece(slot->GetBaseItemId(), slot->GetFlags().HasFlag(InventoryItem.ItemFlags.HighQuality), materia, grades));
        }
        return pieces.ToArray();
    }
}
