using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Provenance.Integrations;

namespace Provenance.Combat;

/// <summary>
/// The basic single-target combo of each combat job (and its starting class): the 1-2-3 weaponskill
/// chain for melee and physical ranged jobs, the main nuke for casters and healers, plus one heal or
/// defensive action for low health. Upgrades (Royal Authority, Glare, …) come from the game itself
/// through the adjusted action id. Deliberately simple: it farms open-world monsters, not raids.
/// </summary>
internal static unsafe class BasicCombo
{
    /// <param name="Combo">Weaponskill chain, or a single spell.</param>
    /// <param name="Heal">Self-heal or defensive action for low health; 0 when the job has none.</param>
    /// <param name="Melee">Fights at melee range; otherwise from about 20 yalms.</param>
    public sealed record Kit(uint[] Combo, uint Heal, bool Melee)
    {
        /// <summary>How close to the target's edge the kit fights from, in yalms (its hitbox comes on top).</summary>
        public float Reach => Melee ? 2.6f : 20f;
    }

    private const uint SecondWind = 7541;
    private const uint Rampart = 7531;

    private static readonly Dictionary<uint, Kit> Kits = new()
    {
        // Tanks
        [19] = new(new uint[] { 9, 15, 3539, 21 }, Rampart, true),       // PLD: Fast Blade, Riot Blade, Royal Authority / Rage of Halone
        [21] = new(new uint[] { 31, 37, 42 }, Rampart, true),            // WAR: Heavy Swing, Maim, Storm's Path
        [32] = new(new uint[] { 3617, 3623, 3632 }, Rampart, true),      // DRK: Hard Slash, Syphon Strike, Souleater
        [37] = new(new uint[] { 16137, 16139, 16145 }, Rampart, true),   // GNB: Keen Edge, Brutal Shell, Solid Barrel
        // Melee
        [20] = new(new uint[] { 53, 54, 56 }, SecondWind, true),         // MNK: Bootshine, True Strike, Snap Punch (by form, see Next)
        [22] = new(new uint[] { 75, 78, 84 }, SecondWind, true),         // DRG: True Thrust, Vorpal Thrust, Full Thrust
        [30] = new(new uint[] { 2240, 2242, 2255 }, SecondWind, true),   // NIN: Spinning Edge, Gust Slash, Aeolian Edge
        [34] = new(new uint[] { 7477, 7478, 7481 }, SecondWind, true),   // SAM: Hakaze, Jinpu, Gekko
        [39] = new(new uint[] { 24373, 24374, 24375 }, SecondWind, true),// RPR: Slice, Waxing Slice, Infernal Slice
        [41] = new(new uint[] { 34606 }, SecondWind, true),              // VPR: Steel Fangs (the button itself turns into the next step)
        // Physical ranged
        [23] = new(new uint[] { 97 }, SecondWind, false),                // BRD: Heavy Shot
        [31] = new(new uint[] { 2866, 2868, 2873 }, SecondWind, false),  // MCH: Split Shot, Slug Shot, Clean Shot
        [38] = new(new uint[] { 15989, 15990 }, SecondWind, false),      // DNC: Cascade, Fountain
        // Casters
        [25] = new(new uint[] { 142 }, 0, false),                        // BLM: Blizzard (Fire while MP is high, see Next)
        [27] = new(new uint[] { 163 }, 16230, false),                    // SMN: Ruin, Physick
        [35] = new(new uint[] { 7503 }, 7514, false),                    // RDM: Jolt, Vercure
        [42] = new(new uint[] { 34650 }, 0, false),                      // PCT: Fire in Red
        // Healers
        [24] = new(new uint[] { 119 }, 120, false),                      // WHM: Stone, Cure
        [28] = new(new uint[] { 17869 }, 190, false),                    // SCH: Ruin, Physick
        [33] = new(new uint[] { 3596 }, 3594, false),                    // AST: Malefic, Benefic
        [40] = new(new uint[] { 24283 }, 24284, false),                  // SGE: Dosis, Diagnosis
    };

    /// <summary>Starting classes use their job's kit (their actions are the same).</summary>
    private static readonly Dictionary<uint, uint> ClassToJob = new()
    {
        [1] = 19, [3] = 21, [2] = 20, [4] = 22, [29] = 30, [5] = 23, [7] = 25, [26] = 27, [6] = 24,
    };

    public static IReadOnlyCollection<uint> Jobs => Kits.Keys;

    public static Kit? For(uint classJob) => Kits.GetValueOrDefault(ClassToJob.GetValueOrDefault(classJob, classJob));

    /// <summary>
    /// The next action for the target: the combo continuation when the last weaponskill started it,
    /// else the first step; skipping steps the character hasn't learned. 0 when nothing is usable now.
    /// </summary>
    public static uint Next(Kit kit, ulong targetId)
    {
        var am = ActionManager.Instance();
        if (am == null) return 0;
        var level = GameState.JobLevel(GameState.CurrentJob);
        var learned = kit.Combo.Where(a => Learned(a, level)).ToList();
        if (learned.Count == 0) return 0;

        // Black Mage: Fire while there is plenty of MP, Blizzard to get it back.
        if (kit.Combo[0] == 142 && Services.Objects.LocalPlayer is { } me && me.MaxMp > 0 && me.CurrentMp * 100 / me.MaxMp > 40 && Learned(141, level))
            return Ready(141, targetId);

        // Monk: forms instead of a combo. Raptor form (108) → True Strike, Coeurl form (109) → Snap Punch.
        if (kit.Combo[0] == 53 && Services.Objects.LocalPlayer is { } monk)
        {
            var form = monk.StatusList.FirstOrDefault(st => st.StatusId is 108 or 109)?.StatusId ?? 0;
            var step = form == 108 && Learned(54, level) ? 54u : form == 109 && Learned(56, level) ? 56u : 53u;
            return Ready(step, targetId);
        }

        var last = am->Combo.Timer > 0 ? am->Combo.Action : 0;
        for (var i = 0; i < learned.Count - 1; i++)
        {
            if (am->GetAdjustedActionId(learned[i]) != last) continue;
            // PLD lists Royal Authority before Rage of Halone: take the first learned follow-up.
            for (var j = i + 1; j < learned.Count; j++)
                if (Ready(learned[j], targetId) is var next && next != 0) return next;
        }
        return Ready(learned[0], targetId);
    }

    /// <summary>The adjusted action id when it can be used on the target now; 0 otherwise.</summary>
    public static uint Ready(uint action, ulong targetId)
    {
        var am = ActionManager.Instance();
        var adjusted = am->GetAdjustedActionId(action);
        return am->GetActionStatus(ActionType.Action, adjusted, targetId, true, true, null) == 0 ? adjusted : 0;
    }

    public static bool Use(uint adjustedAction, ulong targetId)
        => ActionManager.Instance()->UseAction(ActionType.Action, adjustedAction, targetId);

    /// <summary>The character's level is high enough and any unlock quest is done.</summary>
    public static bool Learned(uint action, int level)
    {
        if (Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(action) is not { } row) return false;
        if (row.ClassJobLevel > level) return false;
        return row.UnlockLink.RowId == 0 || UIState.Instance()->IsUnlockLinkUnlockedOrQuestCompleted(row.UnlockLink.RowId);
    }
}
