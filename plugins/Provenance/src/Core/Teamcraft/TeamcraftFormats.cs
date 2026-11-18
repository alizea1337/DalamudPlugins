using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Provenance.Core.Orders;

namespace Provenance.Core.Teamcraft;

public enum InputKind
{
    Empty,
    /// <summary>ffxivteamcraft.com/list/&lt;id&gt; — fetched from Teamcraft's public database.</summary>
    ListLink,
    /// <summary>ffxivteamcraft.com/import/&lt;base64&gt; or the bare base64 string.</summary>
    ImportString,
    /// <summary>"Copy as text" output: lines like "12x Iron Ingot".</summary>
    Text,
    Unknown,
}

/// <summary>Recognises what the user pasted and decodes the Teamcraft formats. No game data, no network.</summary>
public static partial class TeamcraftFormats
{
    public const string FirestoreListUrl = "https://firestore.googleapis.com/v1/projects/ffxivteamcraft/databases/(default)/documents/lists/";

    [GeneratedRegex(@"ffxivteamcraft\.com/(?:[a-z]{2}/)?list/([A-Za-z0-9_-]{6,})", RegexOptions.IgnoreCase)]
    private static partial Regex ListLinkRegex();

    [GeneratedRegex(@"ffxivteamcraft\.com/import/([^\s?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ImportLinkRegex();

    [GeneratedRegex(@"^\s*(\d+)\s*x\s+(.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TextLineRegex();

    public static (InputKind Kind, string Payload) Detect(string? input)
    {
        var text = input?.Trim() ?? "";
        if (text.Length == 0) return (InputKind.Empty, "");

        var list = ListLinkRegex().Match(text);
        if (list.Success) return (InputKind.ListLink, list.Groups[1].Value);

        var import = ImportLinkRegex().Match(text);
        if (import.Success) return (InputKind.ImportString, import.Groups[1].Value);

        if (!text.Contains('\n') && !text.Contains(' ') && TryParseImportString(text, out _))
            return (InputKind.ImportString, text);

        foreach (var line in text.Split('\n'))
            if (TextLineRegex().IsMatch(line)) return (InputKind.Text, text);

        return (InputKind.Unknown, text);
    }

    /// <summary>Decodes "itemId,recipeId|null,quantity;…" wrapped in base64 (Teamcraft's /import/ route).</summary>
    public static bool TryParseImportString(string payload, out List<OrderLine> lines)
    {
        lines = new List<OrderLine>();
        string decoded;
        try
        {
            var b64 = Uri.UnescapeDataString(payload.Trim()).Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
        catch (FormatException)
        {
            return false;
        }

        foreach (var entry in decoded.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(',');
            if (parts.Length != 3) return false;
            if (!uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var item) || item == 0) return false;
            uint recipe = 0;
            if (parts[1] != "null" && parts[1].Length > 0 && !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out recipe)) return false;
            if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var qty) || qty <= 0) return false;
            lines.Add(new OrderLine(item, qty, recipe));
        }
        return lines.Count > 0;
    }

    /// <summary>Lines of "Copy as text"; panel headers ("Final items :") and blank lines are skipped.</summary>
    public static List<(int Quantity, string Name)> ParseText(string text)
    {
        var result = new List<(int, string)>();
        foreach (var raw in text.Split('\n'))
        {
            var m = TextLineRegex().Match(raw);
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var qty) || qty <= 0) continue;
            result.Add((qty, m.Groups[2].Value.Trim()));
        }
        return result;
    }

    public sealed record ListEntry(uint ItemId, int Amount, int Done, uint RecipeId);

    public sealed record ListDocument(string Name, List<ListEntry> FinalItems);

    /// <summary>Reads the final items out of a Firestore REST document (typed values like {"integerValue":"3"}).</summary>
    public static ListDocument ParseFirestoreList(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err))
        {
            var msg = err.TryGetProperty("message", out var m) ? m.GetString() : "unknown error";
            throw new FormatException("Teamcraft returned an error: " + msg);
        }
        if (!root.TryGetProperty("fields", out var fields)) throw new FormatException("Not a Teamcraft list document");

        var name = fields.TryGetProperty("name", out var n) && n.TryGetProperty("stringValue", out var s) ? s.GetString() ?? "" : "";
        var finals = new List<ListEntry>();
        if (fields.TryGetProperty("finalItems", out var fi)
            && fi.TryGetProperty("arrayValue", out var arr)
            && arr.TryGetProperty("values", out var values))
        {
            foreach (var v in values.EnumerateArray())
            {
                if (!v.TryGetProperty("mapValue", out var map) || !map.TryGetProperty("fields", out var f)) continue;
                var id = (uint)Number(f, "id");
                if (id == 0) continue;
                finals.Add(new ListEntry(id, (int)Number(f, "amount"), (int)Number(f, "done"), (uint)Number(f, "recipeId")));
            }
        }
        return new ListDocument(name, finals);
    }

    private static long Number(JsonElement fields, string key)
    {
        if (!fields.TryGetProperty(key, out var v)) return 0;
        if (v.TryGetProperty("integerValue", out var i) && long.TryParse(i.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
        if (v.TryGetProperty("doubleValue", out var d) && d.TryGetDouble(out var dbl)) return (long)dbl;
        if (v.TryGetProperty("stringValue", out var str) && long.TryParse(str.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sl)) return sl;
        return 0;
    }
}
