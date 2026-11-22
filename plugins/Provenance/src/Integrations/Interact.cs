using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Provenance.Integrations;

/// <summary>Finding NPCs and objects nearby, talking to them, and the menus that follow.</summary>
internal static unsafe class Interact
{
    public static IGameObject? Nearest(ObjectKind kind, Func<uint, bool> baseId)
    {
        var me = Services.Objects.LocalPlayer?.Position ?? Vector3.Zero;
        return Services.Objects
            .Where(o => o.ObjectKind == kind && baseId(o.BaseId) && o.IsTargetable)
            .OrderBy(o => Vector3.DistanceSquared(o.Position, me))
            .FirstOrDefault();
    }

    /// <summary>Event object names of an aethernet shard, in the client's language ("Aethernet shard", "aetheryte shard").</summary>
    private static readonly Lazy<HashSet<string>> ShardNames = new(() =>
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sheet = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.EObjName>();
        foreach (var id in new uint[] { 2000151, 2014665, 2003395 })
            if (sheet.GetRowOrDefault(id)?.Singular.ExtractText() is { Length: > 0 } name) names.Add(name);
        return names;
    });

    /// <summary>
    /// The crystal of an Aetheryte row, to talk to for the aethernet. A city's main aetheryte is an aetheryte
    /// object with the row as its id. An aethernet shard is an event object named "Aethernet shard" with an id of
    /// its own, so it is found by where the game's data puts that shard.
    /// </summary>
    public static IGameObject? Crystal(uint aetheryteId)
    {
        if (Nearest(ObjectKind.Aetheryte, id => id == aetheryteId) is { } main) return main;
        if (Teleporter.Position(aetheryteId) is not { } at) return null;
        var spot = new Vector2(at.X, at.Z);
        return Services.Objects
            .Where(o => o.IsTargetable && (o.ObjectKind == ObjectKind.Aetheryte
                                           || (o.ObjectKind == ObjectKind.EventObj && ShardNames.Value.Contains(o.Name.TextValue))))
            .Select(o => (Object: o, Away: Vector2.Distance(new Vector2(o.Position.X, o.Position.Z), spot)))
            .Where(x => x.Away <= CrystalWithin)
            .OrderBy(x => x.Away)
            .Select(x => x.Object)
            .FirstOrDefault();
    }

    /// <summary>A crystal this close to where the data puts it (a map marker, so not exact) is the one meant.</summary>
    private const float CrystalWithin = 15f;

    public static float Distance(IGameObject o)
        => Services.Objects.LocalPlayer is { } p ? Vector3.Distance(p.Position, o.Position) : float.MaxValue;

    public static bool With(IGameObject o)
    {
        var ts = TargetSystem.Instance();
        if (ts == null) return false;
        ts->Target = (GameObjectStruct*)o.Address;
        return ts->InteractWithObject((GameObjectStruct*)o.Address, true) != 0;
    }

    public static AtkUnitBase* Ready(string name)
        => GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var a) && GenericHelpers.IsAddonReady(a) ? a : null;

    public static bool IsOpen(string name)
        => GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var a) && a->IsVisible;

    public static void Close(string name)
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var a) && a->IsVisible) a->Close(true);
    }

    /// <summary>Clicks through a Talk window if one is open.</summary>
    public static bool SkipTalk()
    {
        if (Ready("Talk") is var talk && talk != null)
        {
            new AddonMaster.Talk((nint)talk).Click();
            return true;
        }
        return false;
    }

    /// <summary>Confirms a Yes/No dialog if one is open.</summary>
    public static bool Yes()
    {
        if (Ready("SelectYesno") is var yn && yn != null)
        {
            new AddonMaster.SelectYesno((nint)yn).Yes();
            return true;
        }
        return false;
    }

    /// <summary>Declines a Yes/No dialog if one is open.</summary>
    public static bool No()
    {
        if (Ready("SelectYesno") is var yn && yn != null)
        {
            new AddonMaster.SelectYesno((nint)yn).No();
            return true;
        }
        return false;
    }

    /// <summary>Entries of the open SelectString or SelectIconString menu, or null when none is open.</summary>
    public static List<(string Text, Action Select)>? Menu()
    {
        if (Ready("SelectIconString") is var icon && icon != null)
            return new AddonMaster.SelectIconString((nint)icon).Entries.Select(e => (e.Text, (Action)(() => e.Select()))).ToList();
        if (Ready("SelectString") is var str && str != null)
            return new AddonMaster.SelectString((nint)str).Entries.Select(e => (e.Text, (Action)(() => e.Select()))).ToList();
        return null;
    }

    /// <summary>Selects the first menu entry whose text contains any of the labels (ignoring case).</summary>
    public static bool Choose(IEnumerable<string> labels)
    {
        var menu = Menu();
        if (menu == null) return false;
        foreach (var label in labels.Where(l => l.Length > 0))
        foreach (var (text, select) in menu)
            if (text.Contains(label, StringComparison.CurrentCultureIgnoreCase)) { select(); return true; }
        return false;
    }
}
