using System.Collections.Generic;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Provenance.Integrations;

namespace Provenance.Run.Gathering;

/// <summary>One item slot of the gathering window.</summary>
internal sealed record GatherSlot(int Index, uint ItemId, int Yield, int BoonChance, bool Bonus, bool GivingLand, bool Collectable, bool Hidden, bool Rare);

/// <summary>
/// The "Gathering" window: eight item slots (AtkValues from 5, eleven values each: item id at +1,
/// flags at +5, yield and boon chance at +7, Giving Land at +9, collectable at +10), hidden/rare flags
/// at 98, integrity at 109/110. Layout as read by GatherBuddy Reborn's GatheringReader (Apache-2.0).
/// </summary>
internal sealed unsafe class GatheringWindow
{
    private readonly AtkUnitBase* addon;

    private GatheringWindow(AtkUnitBase* addon) => this.addon = addon;

    public static GatheringWindow? Open() => Interact.Ready("Gathering") is var a && a != null ? new GatheringWindow(a) : null;

    public int Integrity => (int)U(109);
    public int MaxIntegrity => (int)U(110);
    public bool Touched => Integrity != MaxIntegrity;

    public List<GatherSlot> Slots()
    {
        var flags = U(98);
        var list = new List<GatherSlot>(8);
        for (var i = 0; i < 8; i++)
        {
            var b = 5 + i * 11;
            var item = U(b + 1);
            if (item == 0) continue;
            var raw = U(b + 5);
            var buffs = U(b + 7);
            list.Add(new GatherSlot(i, item, (sbyte)(buffs & 0xff), (sbyte)((buffs >> 8) & 0xff), (raw & 4) != 0, B(b + 9), U(b + 10) == 2,
                (flags & (1u << i)) != 0, (flags & (1u << (16 + i))) != 0));
        }
        return list;
    }

    public bool AnyUnhidden() => Slots().Exists(s => s.Hidden);

    public void Gather(int slot) => Callback.Fire(addon, true, slot, 0);

    private uint U(int i)
    {
        if (i < 0 || i >= addon->AtkValuesCount) return 0;
        var v = addon->AtkValues[i];
        return v.Type switch { AtkValueType.UInt => v.UInt, AtkValueType.Int => (uint)v.Int, AtkValueType.Bool => v.Byte, _ => 0 };
    }

    private bool B(int i) => U(i) != 0;
}

/// <summary>
/// The collectable window ("GatheringMasterpiece"): item at 2, collectability 13/14, the gains of Scour
/// (48), Brazen (49–50) and Meticulous (51), integrity 62/63, reward thresholds 65–67. Layout as read
/// by GatherBuddy Reborn's GatheringMasterpieceReader (Apache-2.0).
/// </summary>
internal sealed unsafe class CollectableWindow
{
    private readonly AtkUnitBase* addon;

    private CollectableWindow(AtkUnitBase* addon) => this.addon = addon;

    public static CollectableWindow? Open() => Interact.Ready("GatheringMasterpiece") is var a && a != null ? new CollectableWindow(a) : null;

    public uint ItemId => U(2);
    public int Collectability => (int)U(13);
    public int Integrity => (int)U(62);
    public int MaxIntegrity => (int)U(63);
    public int ScourGain => (int)U(48);
    public int BrazenGainMax => (int)U(50);
    public int MeticulousGain => (int)U(51);
    public int Low => (int)U(65);
    public int Mid => (int)U(66);
    public int High => (int)U(67);

    private uint U(int i)
    {
        if (i < 0 || i >= addon->AtkValuesCount) return 0;
        var v = addon->AtkValues[i];
        return v.Type switch { AtkValueType.UInt => v.UInt, AtkValueType.Int => (uint)v.Int, _ => 0 };
    }
}
