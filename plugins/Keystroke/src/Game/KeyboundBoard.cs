using System;
using System.Collections.Generic;
using System.Text;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Keystroke.Game;

/// <summary>One enemy/word pair on the board, as read from the Atk arrays.</summary>
public sealed record WordSlot(int Index, uint EntityId, string Word, int ScreenX, int ScreenY, int HpPercent, bool Visible);

/// <summary>What the Keybound Brawler duty currently shows. Immutable; produced once per framework tick.</summary>
public sealed record KeyboundBoard
{
    public static readonly KeyboundBoard Empty = new();

    public bool InDuty { get; init; }
    public bool InputPresent { get; init; }
    public bool InputVisible { get; init; }
    public string TypedText { get; init; } = "";
    public IReadOnlyList<WordSlot> Slots { get; init; } = Array.Empty<WordSlot>();
    public bool ResultVisible { get; init; }
    public bool FeverVisible { get; init; }
    public string Todo { get; init; } = "";
    public int Score { get; init; }
}

/// <summary>Reads the board from the game. Framework thread only.</summary>
internal static unsafe class KeyboundReader
{
    private const int RecordSize = 13;
    private const int MaxSlots = 20;
    private const uint InputComponentNodeId = 7;
    private const uint InputTextNodeId = 16;

    public static KeyboundBoard Read()
    {
        var territory = (ushort)Services.ClientState.TerritoryType;
        if (!Keybound.IsDuty(territory)) return KeyboundBoard.Empty;

        var slots = ReadSlots();
        var (inputPresent, inputVisible, typed) = ReadInput();
        var result = Services.GameGui.GetAddonByName(Keybound.ResultAddon);
        var fever = Services.GameGui.GetAddonByName("KTGScreenTextFever");

        return new KeyboundBoard
        {
            InDuty = true,
            InputPresent = inputPresent,
            InputVisible = inputVisible,
            TypedText = typed,
            Slots = slots,
            ResultVisible = !result.IsNull && result.IsVisible,
            FeverVisible = !fever.IsNull && fever.IsVisible,
            Todo = ReadText("KTGHudTodo", 3),
            Score = int.TryParse(ReadText("KTGHudScore", 3).Replace(",", ""), out var sc) ? sc : 0,
        };
    }

    private static List<WordSlot> ReadSlots()
    {
        var list = new List<WordSlot>();
        try
        {
            var stage = AtkStage.Instance();
            var numbers = stage->GetNumberArrayData()[Keybound.NumberArrayIndex];
            var strings = stage->GetStringArrayData()[Keybound.StringArrayIndex];
            if (numbers == null || strings == null) return list;

            var nSize = numbers->AtkArrayData.Size;
            var sSize = strings->AtkArrayData.Size;
            if (nSize < 1) return list;
            var count = Math.Clamp(numbers->IntArray[0], 0, MaxSlots);

            for (var i = 0; i < count; i++)
            {
                var baseIdx = 1 + i * RecordSize;
                if (baseIdx + RecordSize > nSize || i * 3 >= sSize) break;
                var entityId = (uint)numbers->IntArray[baseIdx];
                var x = numbers->IntArray[baseIdx + 1];
                var y = numbers->IntArray[baseIdx + 2];
                var visible = numbers->IntArray[baseIdx + 4] != 0;
                var hp = numbers->IntArray[baseIdx + 5];
                byte* p = strings->StringArray[i * 3];
                var word = p == null ? "" : ReadCString(p, 256);
                if (word.Length == 0) continue;
                list.Add(new WordSlot(i, entityId, word, x, y, hp, visible));
            }
        }
        catch (Exception e)
        {
            Services.Log.Verbose(e, "ReadSlots failed");
        }
        return list;
    }

    private static (bool present, bool visible, string typed) ReadInput()
    {
        try
        {
            var ptr = Services.GameGui.GetAddonByName(Keybound.InputAddon);
            if (ptr.IsNull) return (false, false, "");
            var addon = (AtkUnitBase*)ptr.Address;
            var compNode = addon->GetComponentNodeById(InputComponentNodeId);
            if (compNode == null || compNode->Component == null) return (true, ptr.IsVisible, "");
            var textNode = compNode->Component->UldManager.SearchNodeById(InputTextNodeId);
            var typed = textNode == null || textNode->Type != NodeType.Text ? "" : ((AtkTextNode*)textNode)->NodeText.ToString();
            return (true, ptr.IsVisible, typed);
        }
        catch (Exception e)
        {
            Services.Log.Verbose(e, "ReadInput failed");
            return (false, false, "");
        }
    }

    private static string ReadText(string addonName, uint nodeId)
    {
        try
        {
            var ptr = Services.GameGui.GetAddonByName(addonName);
            if (ptr.IsNull) return "";
            var addon = (AtkUnitBase*)ptr.Address;
            var node = addon->GetTextNodeById(nodeId);
            return node == null ? "" : node->NodeText.ToString();
        }
        catch
        {
            return "";
        }
    }

    private static string ReadCString(byte* p, int max)
    {
        var len = 0;
        while (len < max && p[len] != 0) len++;
        return Encoding.UTF8.GetString(p, len);
    }
}
