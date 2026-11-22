using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Provenance.Core.Appearance;
using Provenance.Core.Character;
using Provenance.Core.Orders;

namespace Provenance;

/// <summary>Persisted settings and the active order. One class per area.</summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public Order Order { get; set; } = new();
    /// <summary>Orders saved to use again (Browse, Favourites), newest first.</summary>
    public List<Core.Orders.FavouriteOrder> Favourites { get; set; } = new();
    public StockSettings Stock { get; set; } = new();
    public BuyingSettings Buying { get; set; } = new();
    public ImportSettings Import { get; set; } = new();
    public BrowseSettings Browse { get; set; } = new();
    public WindowSettings Window { get; set; } = new();
    /// <summary>The Cosmic Exploration loop: planet, classes, goal, extras, shopping list, and what runs of each mission came to.</summary>
    public CosmicSettings Cosmic { get; set; } = new();
    public ConsumableSettings Consumables { get; set; } = new();
    public CraftingSettings Crafting { get; set; } = new();
    public RunSettings Run { get; set; } = new();
    public FishingSettings Fishing { get; set; } = new();
    public CombatSettings Combat { get; set; } = new();
    public AfterOrderSettings AfterOrder { get; set; } = new();
    public CraftingSpotSettings CraftingSpot { get; set; } = new();
    public SecuritySettings Security { get; set; } = new();
    /// <summary>Timing figures for estimates, adjusted by every run.</summary>
    public Core.Estimating.Timings Timings { get; set; } = new();
    /// <summary>Last "Check character" result per character (content id).</summary>
    public Dictionary<ulong, CharacterProfile> Characters { get; set; } = new();
    /// <summary>Remembered retainer inventories per character (content id), then per retainer.</summary>
    public Dictionary<ulong, Dictionary<ulong, Data.RetainerStock>> Retainers { get; set; } = new();

    public void Save() => Services.PluginInterface.SavePluginConfig(this);

    public static Configuration Load()
    {
        var c = Services.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        // Sections added in later versions are null in older files.
        c.Order ??= new();
        c.Order.Lines ??= new();
        c.Stock ??= new();
        c.Buying ??= new();
        c.Import ??= new();
        c.Browse ??= new();
        // "Quests & other" is no longer a filter; a saved pick of it would hide everything.
        c.Browse.PlaceMask &= (int)(Core.Catalog.Place.OpenWorld | Core.Catalog.Place.Diadem | Core.Catalog.Place.CosmicExploration | Core.Catalog.Place.OceanFishing);
        c.Window ??= new();
        if (c.Window.TextScale <= 0) c.Window.TextScale = c.Window.Scale;
        c.Window.Colors ??= new();
        c.Cosmic ??= new();
        c.Cosmic.Classes ??= new();
        c.Cosmic.Shopping ??= new();
        c.Cosmic.Records ??= new();
        // Before 0.19.5 a mission stopped after the crafts its objective names, so what it learned undersold it.
        if (c.Cosmic.RecordsVersion < 2)
        {
            c.Cosmic.Records.Clear();
            c.Cosmic.RecordsVersion = 2;
        }
        c.Characters ??= new();
        c.Consumables ??= new();
        c.Crafting ??= new();
        c.Run ??= new();
        c.Fishing ??= new();
        c.Fishing.SpareBait = Math.Clamp(c.Fishing.SpareBait, 0, 999);
        c.Fishing.WindowLeadMinutes = Math.Clamp(c.Fishing.WindowLeadMinutes, 0, 30);
        c.Combat ??= new();
        c.AfterOrder ??= new();
        c.CraftingSpot ??= new();
        c.Favourites ??= new();
        c.Security ??= new();
        c.Retainers ??= new();
        c.Timings ??= new();
        c.Consumables.CraftFood ??= new();
        c.Consumables.CraftMedicine ??= new();
        c.Consumables.GatherFood ??= new();
        c.Consumables.GatherMedicine ??= new();
        return c;
    }
}

/// <summary>Which owned items count against the order. The armoury chest never does.</summary>
[Serializable]
public sealed class StockSettings
{
    public bool Inventory { get; set; } = true;
    public bool Saddlebag { get; set; } = true;
    public bool Retainers { get; set; } = true;
    /// <summary>Count finished items already owned against the order.</summary>
    public bool CountFinishedItems { get; set; } = false;
}

[Serializable]
public sealed class BuyingSettings
{
    /// <summary>Most gil per unit the run may pay a vendor. 0 means no limit.</summary>
    public uint VendorMaxPrice { get; set; } = 2000;
    /// <summary>Buy vendor-sold materials instead of gathering or fishing them.</summary>
    public bool PreferVendor { get; set; } = true;
}

[Serializable]
public sealed class ImportSettings
{
    public bool IgnoreTeamcraftProgress { get; set; } = false;
    /// <summary>Imports replace the order (true) or add to it (false).</summary>
    public bool Replace { get; set; } = true;
}

[Serializable]
public sealed class BrowseSettings
{
    /// <summary>Bit per crafter (Carpenter = bit 0). 0 shows every job.</summary>
    public int JobMask { get; set; } = 0;
    /// <summary>Bits of <see cref="Core.Catalog.Place"/>: the open world, the Diadem and so on. 0 shows everything.</summary>
    public int PlaceMask { get; set; } = 0;
    public int MinLevel { get; set; } = 1;
    public int MaxLevel { get; set; } = 100;
    /// <summary>The Favourites chip is on: the saved orders show instead of the items.</summary>
    public bool ShowFavourites { get; set; }
    /// <summary>The search as last typed (words and filters), kept between sessions like the chips.</summary>
    public string Query { get; set; } = "";
}

[Serializable]
public sealed class RunSettings
{
    /// <summary>Craft what already can be crafted while only closed timed nodes are left.</summary>
    public bool CraftWhileWaiting { get; set; } = true;
    /// <summary>Real minutes before a timed node opens to hand back to gathering.</summary>
    public int TimedLeadMinutes { get; set; } = 2;
    /// <summary>While waiting for a timed node, travel to where it spawns (and craft or wait there).</summary>
    public bool TravelAhead { get; set; } = true;
    /// <summary>After gathering, throw away what came in over the order's amount (normal quality, main bags).</summary>
    public bool DiscardExtras { get; set; } = true;
    /// <summary>With <see cref="DiscardExtras"/>: also throw away extras of items that only grow on timed nodes.</summary>
    public bool DiscardTimedExtras { get; set; } = true;
    /// <summary>Before a collectable on a regular node, wait next to it until there's GP for the full rotation.</summary>
    public bool WaitForCollectableGp { get; set; } = true;
    /// <summary>With <see cref="WaitForCollectableGp"/>: drink a cordial from the bags instead of waiting.</summary>
    public bool UseCordials { get; set; } = true;
    /// <summary>When a needed master book or folklore tome costs more scrips than you have, earn them first with collectables.</summary>
    public bool EarnScrips { get; set; } = true;
    /// <summary>Extract materia from fully spiritbonded gear between nodes.</summary>
    public bool ExtractMateria { get; set; } = true;
    /// <summary>Aetherially reduce spare collectables when the bags fill up (never ones the order needs).</summary>
    public bool ReduceCollectables { get; set; } = true;
    /// <summary>Aetheryte to teleport to for a summoning bell when none is nearby (8 = Limsa Lominsa Lower Decks).</summary>
    public uint BellAetheryte { get; set; } = 8;
}

/// <summary>Fishing in a run: bait, buffs and what happens to fish caught on the side.</summary>
[Serializable]
public sealed class FishingSettings
{
    /// <summary>Buy the bait a fish needs from a gil vendor before fishing, and again when it runs out.</summary>
    public bool BuyBait { get; set; } = true;
    /// <summary>Bait bought on top of the estimate, per kind.</summary>
    public int SpareBait { get; set; } = 20;
    /// <summary>When a fish's own bait can't be bought for gil (scrips, crafted), fish with Versatile Lure instead.</summary>
    public bool VersatileLureFallback { get; set; } = true;
    /// <summary>Use Chum, Patience, Surface Slap, Identical Cast and the lures when the catch plan calls for them.</summary>
    public bool UseBuffs { get; set; } = true;
    /// <summary>Drink cordials for fishing GP.</summary>
    public bool UseCordials { get; set; } = true;
    /// <summary>Real minutes before a fish's window opens to be at the spot (catching the fish for Fisher's Intuition comes on top).</summary>
    public int WindowLeadMinutes { get; set; } = 3;
    /// <summary>After fishing, throw away fish caught on the side that the order doesn't need (main bags only).</summary>
    public bool DiscardByCatch { get; set; } = true;
    /// <summary>
    /// Fish for items from aetherial reduction too, not only gather for them. Off: only gathered collectables are
    /// reduced, and an item that only comes from fish is one to get yourself.
    /// </summary>
    public bool FishForReduction { get; set; } = true;
}

/// <summary>The Security tab: getting unstuck, trades, and limits for a run.</summary>
[Serializable]
public sealed class SecuritySettings
{
    /// <summary>Jump, back off and side-step before teleporting when a walk gets stuck.</summary>
    public bool AdvancedUnstuck { get; set; } = true;
    /// <summary>Seconds without real progress that count as stuck.</summary>
    public int StuckSeconds { get; set; } = 6;
    /// <summary>Cancel trade windows other players open.</summary>
    public bool DeclineTrades { get; set; } = true;
    /// <summary>Only while a run is going (otherwise always).</summary>
    public bool TradesOnlyDuringRuns { get; set; } = true;
    /// <summary>Most gil a run may spend at vendors. 0: no limit (besides the price cap per item).</summary>
    public int MaxGilPerRun { get; set; }
    /// <summary>Stop a run after this many minutes (after the craft under way). 0: no limit.</summary>
    public int MaxRunMinutes { get; set; }
    /// <summary>Knock-outs a run gets back up from before it stops.</summary>
    public int MaxKnockOuts { get; set; } = 3;
    /// <summary>A run stops when only this many inventory slots are free.</summary>
    public int MinFreeSlots { get; set; } = 2;
}

public enum AfterOrderPlace { Stay, WhereStarted, Inn, Home, CraftingSpot }

/// <summary>What kind of place a crafting spot is in, which decides how a run gets there.</summary>
public enum CraftingSpotKind { OpenWorld, InnRoom, House, Apartment }

/// <summary>
/// A spot saved where you stood (in your house, apartment, an inn room or out in the world) that the run walks
/// to before it crafts. Houses and apartments are found again through the teleport list.
/// </summary>
[Serializable]
public sealed class CraftingSpotSettings
{
    /// <summary>Go there before crafting.</summary>
    public bool Enabled { get; set; }
    public CraftingSpotKind Kind { get; set; }
    /// <summary>The zone the spot is in (for a house or apartment, its inside); 0 while nothing is saved.</summary>
    public uint Territory { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    /// <summary>"Inside Mist, ward 5, plot 12 (free company house)", "Inn room in Limsa Lominsa", or the zone.</summary>
    public string Label { get; set; } = "";
    /// <summary>House or apartment: the housing district outside, the ward and plot (0-based), the apartment's room, the world.</summary>
    public uint District { get; set; }
    public int Ward { get; set; }
    public int Plot { get; set; }
    public int Room { get; set; }
    public uint World { get; set; }
    /// <summary>House or apartment: its entry in the teleport list.</summary>
    public uint Aetheryte { get; set; }
    public byte SubIndex { get; set; }

    public bool IsSet => Territory != 0;
}

/// <summary>Text size of the tooltips (12, 14, 16, 18 and 20 px).</summary>
public enum TooltipSize { Small, Normal, Large, Larger, Largest }

/// <summary>Where to go once an order is finished.</summary>
[Serializable]
public sealed class AfterOrderSettings
{
    public AfterOrderPlace Place { get; set; } = AfterOrderPlace.Stay;
    /// <summary>Innkeeper (ENpcBase) of the inn picked.</summary>
    public uint InnNpc { get; set; }
    /// <summary>House or apartment from the teleport list: its aetheryte and sub index.</summary>
    public uint HomeAetheryte { get; set; }
    public byte HomeSubIndex { get; set; }
    public string HomeLabel { get; set; } = "";
    /// <summary>The Cosmic Exploration loop goes there too when it finishes on its own (goal, time limit, shopping list).</summary>
    public bool AfterCosmic { get; set; }
}

/// <summary>Farming monster drops with a basic combo.</summary>
[Serializable]
public sealed class CombatSettings
{
    /// <summary>Combat job (ClassJob row) to fight with, in its highest item level gearset. 0: none picked yet.</summary>
    public uint ClassJob { get; set; }
    /// <summary>Summon the chocobo companion with Gysahl Greens from the bags.</summary>
    public bool Chocobo { get; set; } = true;
    /// <summary>Chocobo stance (BuddyAction row): 4 free, 5 defender, 6 attacker, 7 healer.</summary>
    public uint ChocoboStance { get; set; } = 6;
    /// <summary>Use the job's heal or defensive action below this much health (percent).</summary>
    public int HealBelow { get; set; } = 50;
}

[Serializable]
public sealed class CraftingSettings
{
    /// <summary>Self-repair when any equipped piece drops below this percentage. 0 turns repairing off.</summary>
    public int RepairBelow { get; set; } = 20;
    /// <summary>Default HQ wish for items added to the order (per item it can be changed); intermediates always get the fastest rotation.</summary>
    public bool AimForHq { get; set; } = true;
    /// <summary>Let rotations use Heart and Soul and Quick Innovation when a specialist soul is equipped.</summary>
    public bool UseSpecialistActions { get; set; } = true;
}

/// <summary>Food and medicine to use while crafting and while gathering. None picked: no consumables.</summary>
[Serializable]
public sealed class ConsumableSettings
{
    public ConsumableChoice CraftFood { get; set; } = new();
    public ConsumableChoice CraftMedicine { get; set; } = new();
    public ConsumableChoice GatherFood { get; set; } = new();
    public ConsumableChoice GatherMedicine { get; set; } = new();

    public (ConsumableChoice Food, ConsumableChoice Medicine) For(Discipline d)
        => d == Discipline.Hand ? (CraftFood, CraftMedicine) : (GatherFood, GatherMedicine);
}

[Serializable]
public sealed class WindowSettings
{
    /// <summary>Title of the page that was open when the window closed.</summary>
    public string LastPage { get; set; } = "";
    /// <summary>Show the small run strip instead of the full window.</summary>
    public bool Compact { get; set; } = false;
    /// <summary>Size of the full window, restored when leaving the compact strip.</summary>
    public float FullWidth { get; set; } = 1100;
    public float FullHeight { get; set; } = 720;
    /// <summary>Pages, the sidebar and switches change in place instead of sliding; fades stay, shorter.</summary>
    public bool ReduceMotion { get; set; } = false;
    /// <summary>How big the text in tooltips is.</summary>
    public TooltipSize TooltipSize { get; set; } = TooltipSize.Normal;
    /// <summary>Interface scale in percent (75 to 150): spacing, buttons, icons and the window, on top of Dalamud's own scale.</summary>
    public int Scale { get; set; } = 100;
    /// <summary>
    /// Text size in percent (75 to 150): the window's text, apart from the interface scale. Unset (-1) in files from
    /// before it existed, where the interface size set the text too; it starts at that size then (see Load).
    /// </summary>
    public int TextScale { get; set; } = -1;
    /// <summary>The window's colours (accent, background, cards, text).</summary>
    public ColorSettings Colors { get; set; } = new();

    /// <summary>The interface scale as a factor (1 = 100%), kept within what the settings offer.</summary>
    public float ScaleFactor => Math.Clamp(Scale, 50, 200) / 100f;
    /// <summary>The text size as a factor (1 = 100%), kept within what the settings offer.</summary>
    public float TextScaleFactor => Math.Clamp(TextScale, 50, 200) / 100f;
}

/// <summary>The four colours picked on the Interface page, as 0xRRGGBB; the other shades are worked out from them.</summary>
public sealed class ColorSettings
{
    /// <summary>Primary buttons, switches, the open page, progress.</summary>
    public uint Accent { get; set; } = ThemeColors.DefaultAccent;
    /// <summary>The window behind the cards.</summary>
    public uint Background { get; set; } = ThemeColors.DefaultBackground;
    /// <summary>Cards, the sidebar and the bottom bar; fields and buttons are a shade of it.</summary>
    public uint Cards { get; set; } = ThemeColors.DefaultCards;
    /// <summary>The text; muted and faint text are shades of it.</summary>
    public uint Text { get; set; } = ThemeColors.DefaultText;

    public bool IsDefault => Accent == ThemeColors.DefaultAccent && Background == ThemeColors.DefaultBackground
                             && Cards == ThemeColors.DefaultCards && Text == ThemeColors.DefaultText;

    public void Reset()
    {
        Accent = ThemeColors.DefaultAccent;
        Background = ThemeColors.DefaultBackground;
        Cards = ThemeColors.DefaultCards;
        Text = ThemeColors.DefaultText;
    }
}

/// <summary>What the Cosmic Exploration loop works toward.</summary>
public enum CosmicGoal
{
    /// <summary>Earn a set number of Cosmocredits.</summary>
    Cosmocredits,
    /// <summary>Earn what the shopping list costs, then buy it.</summary>
    ShoppingList,
    /// <summary>Keep going until stopped (or the time limit).</summary>
    UntilStopped,
}

/// <summary>The extras the loop may take on, picked on the Cosmic Exploration page.</summary>
[Flags]
public enum CosmicExtra
{
    None = 0,
    CriticalMissions = 1 << 0,
    RedAlerts = 1 << 1,
    ChainedMissions = 1 << 2,
    WeatherTimeMissions = 1 << 3,
    TwoClassMissions = 1 << 4,
    MasterMissions = 1 << 5,
    CosmicFortunes = 1 << 6,
    MechOps = 1 << 7,
    MateriaAtCap = 1 << 8,
}

/// <summary>Something on the Cosmic Exploration shopping list.</summary>
[Serializable]
public sealed class CosmicWish
{
    public uint ItemId { get; set; }
    public int Count { get; set; } = 1;
}

[Serializable]
public sealed class CosmicSettings
{
    /// <summary>The planet the loop works on (Core.Cosmic.Planet), or -1: the one you're on, else the newest unlocked.</summary>
    public int Planet { get; set; } = -1;
    /// <summary>ClassJob rows the loop may use.</summary>
    public List<uint> Classes { get; set; } = new();
    public CosmicGoal Goal { get; set; } = CosmicGoal.Cosmocredits;
    /// <summary>For the Cosmocredits goal: stop once you have this many.</summary>
    public int CreditsTarget { get; set; } = 10000;
    /// <summary>Stop after this many minutes, whatever the goal (0: no limit).</summary>
    public int TimeLimitMinutes { get; set; }
    /// <summary>Take nothing above class A: no EX or EX+ missions, even when they'd reach gold.</summary>
    public bool NoExMissions { get; set; }
    public CosmicExtra Extras { get; set; } = CosmicExtra.CriticalMissions | CosmicExtra.RedAlerts | CosmicExtra.ChainedMissions | CosmicExtra.WeatherTimeMissions;
    public List<CosmicWish> Shopping { get; set; } = new();
    /// <summary>The plan's food and medicine suggestion was taken: use the crafting food and medicine while they last.</summary>
    public bool UseConsumables { get; set; }
    /// <summary>Which way <see cref="Records"/> were learned; older ones are dropped on load (they stopped after one craft).</summary>
    public int RecordsVersion { get; set; }
    /// <summary>What runs of each mission came to (keyed by WKSMissionUnit row).</summary>
    public Dictionary<uint, Core.Cosmic.MissionRecord> Records { get; set; } = new();

    public bool Has(CosmicExtra extra) => (Extras & extra) != 0;
}
