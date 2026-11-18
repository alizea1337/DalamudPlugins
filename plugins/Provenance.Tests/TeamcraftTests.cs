using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Provenance.Core.Teamcraft;
using Xunit;
using static Provenance.Tests.FakeCatalog;

namespace Provenance.Tests;

public class TeamcraftTests
{
    private static readonly FakeCatalog Catalog = new();

    private static Task<string> NoNetwork(string url, CancellationToken ct) => throw new InvalidOperationException("no network in this test");

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    [Theory]
    [InlineData("https://ffxivteamcraft.com/list/AbCdEf123456", InputKind.ListLink, "AbCdEf123456")]
    [InlineData("ffxivteamcraft.com/de/list/XyZ-789_abc", InputKind.ListLink, "XyZ-789_abc")]
    [InlineData("", InputKind.Empty, "")]
    [InlineData("hello there", InputKind.Unknown, "hello there")]
    public void Detects_links(string input, InputKind kind, string payload)
    {
        var (k, p) = TeamcraftFormats.Detect(input);
        Assert.Equal(kind, k);
        Assert.Equal(payload, p);
    }

    [Fact]
    public void Detects_import_links_and_bare_strings()
    {
        var s = B64("200,null,2;101,7,3");
        Assert.Equal(InputKind.ImportString, TeamcraftFormats.Detect("https://ffxivteamcraft.com/import/" + Uri.EscapeDataString(s)).Kind);
        Assert.Equal(InputKind.ImportString, TeamcraftFormats.Detect(s).Kind);
    }

    [Fact]
    public void Parses_import_string()
    {
        Assert.True(TeamcraftFormats.TryParseImportString(Uri.EscapeDataString(B64("200,null,2;101,7,3")), out var lines));
        Assert.Equal(2, lines.Count);
        Assert.Equal((200u, 2, 0u), (lines[0].ItemId, lines[0].Quantity, lines[0].RecipeId));
        Assert.Equal((101u, 3, 7u), (lines[1].ItemId, lines[1].Quantity, lines[1].RecipeId));
    }

    [Theory]
    [InlineData("200,null")]
    [InlineData("abc,null,2")]
    [InlineData("200,null,0")]
    public void Rejects_bad_import_strings(string raw)
    {
        Assert.False(TeamcraftFormats.TryParseImportString(B64(raw), out _));
    }

    [Fact]
    public async Task Text_import_keeps_only_final_items()
    {
        // A whole-list copy: crystals, materials and the finals all pasted together.
        const string text = """
            Crystals :
            7x Wind Shard

            Final items :
            2x Maple Shortbow
            1x Maple Round Shield

            Pre-crafts :
            5x maple lumber
            3x Copper Rivets

            Gathering :
            15x Maple Log
            1x Totally Unknown Item
            """;
        var r = await TeamcraftImporter.ImportAsync(text, false, Catalog, NoNetwork);

        Assert.Equal(InputKind.Text, r.Kind);
        Assert.True(r.Ok);
        Assert.Equal(new[] { Bow, Shield }, r.Lines.Select(l => l.ItemId).OrderBy(x => x).ToArray());
        Assert.Equal(2, r.Lines.Single(l => l.ItemId == Bow).Quantity);
        Assert.Equal(new[] { "Totally Unknown Item" }, r.Unmatched);
        Assert.Equal(4, r.MaterialsSkipped);
    }

    [Fact]
    public async Task List_link_uses_remaining_amounts_unless_progress_is_ignored()
    {
        const string json = """
            {"name":"projects/ffxivteamcraft/databases/(default)/documents/lists/abcdef123",
             "fields":{
               "name":{"stringValue":"Bows for the FC"},
               "finalItems":{"arrayValue":{"values":[
                 {"mapValue":{"fields":{"id":{"integerValue":"200"},"amount":{"integerValue":"5"},"done":{"integerValue":"2"},"recipeId":{"stringValue":"4"}}}},
                 {"mapValue":{"fields":{"id":{"integerValue":"201"},"amount":{"integerValue":"1"},"done":{"integerValue":"1"}}}}
               ]}}}}
            """;
        string? requested = null;
        Task<string> Fetch(string url, CancellationToken ct) { requested = url; return Task.FromResult(json); }

        var r = await TeamcraftImporter.ImportAsync("https://ffxivteamcraft.com/list/abcdef123", false, Catalog, Fetch);
        Assert.EndsWith("/documents/lists/abcdef123", requested);
        Assert.Equal("Bows for the FC", r.ListName);
        var bow = Assert.Single(r.Lines);
        Assert.Equal((200u, 3, 4u), (bow.ItemId, bow.Quantity, bow.RecipeId));
        Assert.Equal(1, r.AlreadyDone);

        var all = await TeamcraftImporter.ImportAsync("https://ffxivteamcraft.com/list/abcdef123", true, Catalog, Fetch);
        Assert.Equal(new[] { 5, 1 }, all.Lines.Select(l => l.Quantity).ToArray());
    }

    [Fact]
    public async Task List_link_reports_network_and_api_errors()
    {
        var failed = await TeamcraftImporter.ImportAsync("https://ffxivteamcraft.com/list/abcdef123", false, Catalog, NoNetwork);
        Assert.False(failed.Ok);
        Assert.Contains("Couldn't load", failed.Error);

        Task<string> NotFound(string url, CancellationToken ct) => Task.FromResult("""{"error":{"code":404,"message":"Document not found"}}""");
        var missing = await TeamcraftImporter.ImportAsync("https://ffxivteamcraft.com/list/abcdef123", false, Catalog, NotFound);
        Assert.Contains("Document not found", missing.Error);
    }
}
