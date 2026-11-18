using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Provenance.Core.Catalog;
using Provenance.Core.Orders;

namespace Provenance.Core.Teamcraft;

public sealed class ImportResult
{
    public InputKind Kind { get; init; }
    public string ListName { get; init; } = "";
    public List<OrderLine> Lines { get; init; } = new();
    /// <summary>Names from copied text that matched no item in the client language.</summary>
    public List<string> Unmatched { get; init; } = new();
    /// <summary>Pasted lines dropped because another pasted item already consumes them.</summary>
    public int MaterialsSkipped { get; init; }
    /// <summary>Final items Teamcraft already marks as done.</summary>
    public int AlreadyDone { get; init; }
    public string? Error { get; init; }

    public bool Ok => Error == null && Lines.Count > 0;
}

/// <summary>Turns any of the three Teamcraft formats into order lines.</summary>
public static class TeamcraftImporter
{
    /// <param name="fetch">Downloads a URL and returns the body; the plugin passes an HttpClient call.</param>
    public static async Task<ImportResult> ImportAsync(
        string input, bool ignoreProgress, IGameCatalog catalog,
        Func<string, CancellationToken, Task<string>> fetch, CancellationToken ct = default)
    {
        var (kind, payload) = TeamcraftFormats.Detect(input);
        switch (kind)
        {
            case InputKind.Empty:
                return new ImportResult { Kind = kind, Error = "Paste a Teamcraft link, import string or copied list first." };

            case InputKind.ListLink:
            {
                TeamcraftFormats.ListDocument doc;
                try
                {
                    doc = TeamcraftFormats.ParseFirestoreList(await fetch(TeamcraftFormats.FirestoreListUrl + payload, ct).ConfigureAwait(false));
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    return new ImportResult { Kind = kind, Error = "Couldn't load that list from Teamcraft: " + e.Message };
                }
                var lines = new List<OrderLine>();
                var done = 0;
                foreach (var e in doc.FinalItems)
                {
                    var qty = ignoreProgress ? e.Amount : e.Amount - e.Done;
                    if (!ignoreProgress && e.Done >= e.Amount) done++;
                    if (qty > 0) lines.Add(new OrderLine(e.ItemId, qty, e.RecipeId));
                }
                return lines.Count == 0
                    ? new ImportResult { Kind = kind, ListName = doc.Name, AlreadyDone = done, Error = done > 0 ? "Every item on that list is already marked done in Teamcraft." : "That list has no final items." }
                    : new ImportResult { Kind = kind, ListName = doc.Name, Lines = lines, AlreadyDone = done };
            }

            case InputKind.ImportString:
                return TeamcraftFormats.TryParseImportString(payload, out var parsed)
                    ? new ImportResult { Kind = kind, Lines = parsed }
                    : new ImportResult { Kind = kind, Error = "That import string couldn't be read. Copy it again from Teamcraft." };

            case InputKind.Text:
            {
                var entries = new List<(uint Id, int Qty)>();
                var unmatched = new List<string>();
                foreach (var (qty, name) in TeamcraftFormats.ParseText(payload))
                {
                    if (catalog.TryFindItem(name, out var id)) entries.Add((id, qty));
                    else unmatched.Add(name);
                }
                var finals = SelectFinalItems(entries, catalog);
                var lines = finals.GroupBy(e => e.Id).Select(g => new OrderLine(g.Key, g.Sum(x => x.Qty))).ToList();
                return new ImportResult
                {
                    Kind = kind,
                    Lines = lines,
                    Unmatched = unmatched,
                    MaterialsSkipped = entries.Count - finals.Count,
                    Error = lines.Count == 0 ? (unmatched.Count > 0 ? "None of the pasted item names matched your game language." : "No items found in the pasted text.") : null,
                };
            }

            default:
                return new ImportResult { Kind = kind, Error = "That doesn't look like a Teamcraft link, import string or copied list." };
        }
    }

    /// <summary>
    /// A whole-list text copy also contains the intermediate and raw materials. Keep only the pasted
    /// items that no other pasted item needs somewhere down its recipe tree; those are the final items.
    /// </summary>
    public static List<(uint Id, int Qty)> SelectFinalItems(List<(uint Id, int Qty)> entries, IGameCatalog catalog)
    {
        var consumed = new HashSet<uint>();
        foreach (var root in entries.Select(e => e.Id).Distinct())
        {
            var stack = new Stack<(uint Id, int Depth)>();
            stack.Push((root, 0));
            var seen = new HashSet<uint> { root };
            while (stack.Count > 0)
            {
                var (id, depth) = stack.Pop();
                if (depth > 12) continue;
                foreach (var recipe in catalog.RecipesFor(id))
                foreach (var ing in recipe.Ingredients)
                {
                    if (ing.ItemId == root || !seen.Add(ing.ItemId)) continue;
                    consumed.Add(ing.ItemId);
                    stack.Push((ing.ItemId, depth + 1));
                }
            }
        }
        var finals = entries.Where(e => !consumed.Contains(e.Id)).ToList();
        return finals.Count > 0 ? finals : entries;
    }
}
