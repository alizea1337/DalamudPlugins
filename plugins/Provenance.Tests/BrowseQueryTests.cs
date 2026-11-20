using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Catalog;
using Xunit;

namespace Provenance.Tests;

public class BrowseQueryTests
{
    // A German client's job names, and a few categories in both languages.
    private static readonly BrowseVocabulary Vocabulary = new(
        new (string, uint)[] { ("Fischer", 18), ("FIS", 18), ("Gärtner", 17), ("GÄR", 17), ("Minenarbeiter", 16) },
        new[] { "Metall", "Metal", "Angel", "Fisher's Primary Tool", "Meeresfrüchte", "Seafood", "Lumber" });

    private static readonly BrowseEntry Ingot = new(BrowseKind.Recipe, 1, 100, "Mythril Ingot", 9, 50, 0, 0, 1, false, 0, false, CanHq: true,
        ItemLevel: 140, Category: "Metall", CategoryEnglish: "Metal");
    private static readonly BrowseEntry Rod = new(BrowseKind.Recipe, 2, 101, "Rarefied Claro Walnut Fishing Rod", 8, 100, 2, 0, 1, true, 77, false,
        ItemLevel: 690, Category: "Angel", CategoryEnglish: "Fisher's Primary Tool", Specialist: true);
    private static readonly BrowseEntry Palate = new(BrowseKind.Fish, 0, 102, "Purple Palate", 18, 93, 0, 0, 1, false, 0, false,
        Reduced: true, ItemLevel: 640, Category: "Meeresfrüchte", CategoryEnglish: "Seafood");
    private static readonly BrowseEntry Corn = new(BrowseKind.Gather, 0, 103, "Turali Corn", 17, 95, 0, 0, 1, false, 0, true,
        Tome: 900, Places: Place.OpenWorld);
    private static readonly BrowseEntry Diadem = new(BrowseKind.Gather, 0, 104, "Grade 4 Skybuilders' Log", 17, 80, 0, 0, 1, false, 0, false,
        Places: Place.Diadem);
    private static readonly BrowseEntry Silex = new(BrowseKind.Gather, 0, 105, "Raw Silex", 16, 60, 0, 0, 1, false, 0, false, Reduced: true);

    private static readonly BrowseEntry[] All = { Ingot, Rod, Palate, Corn, Diadem, Silex };

    private static IEnumerable<string> Find(string query, BrowseCharacter? character = null)
    {
        var q = BrowseQuery.Parse(query, Vocabulary);
        return All.Where(e => q.Matches(e, character)).Select(e => e.Name);
    }

    [Fact]
    public void Plain_words_search_names()
    {
        Assert.Equal(new[] { "Mythril Ingot" }, Find("ingot"));
        Assert.Equal(new[] { "Turali Corn" }, Find("turali corn"));
        Assert.Equal(new[] { "Turali Corn" }, Find("corn turali"));
        Assert.Empty(Find("\"corn turali\""));
        Assert.Equal(All.Select(e => e.Name), Find(""));
    }

    [Fact]
    public void Or_binds_tighter_than_and_like_the_example()
    {
        // (FSH or Fisher) and reduction and level 50-70: Purple Palate is FSH but level 93.
        Assert.Empty(Find("job:fsh or job:fisher AND aetherial.reduction AND level.range:50-70"));
        Assert.Equal(new[] { "Purple Palate" }, Find("job:fsh or job:fisher AND aetherial.reduction AND level.range:50-99"));
        // (MIN or FSH) and reduction: both reduction rows.
        Assert.Equal(new[] { "Purple Palate", "Raw Silex" }, Find("job:min or job:fsh is:reduction"));
        // Brackets change the grouping.
        Assert.Equal(new[] { "Purple Palate", "Raw Silex" }, Find("job:min or (job:fsh is:reduction)"));
        Assert.Equal(new[] { "Raw Silex" }, Find("job:min (job:fsh or is:reduction)"));
    }

    [Fact]
    public void Comma_means_one_of_and_minus_leaves_out()
    {
        Assert.Equal(new[] { "Purple Palate", "Raw Silex" }, Find("job:fsh,min"));
        Assert.Equal(new[] { "Raw Silex" }, Find("is:reduction -job:fsh"));
        Assert.Equal(new[] { "Raw Silex" }, Find("is:reduction not job:fsh"));
        Assert.Equal(new[] { "Mythril Ingot", "Purple Palate", "Turali Corn", "Grade 4 Skybuilders' Log", "Raw Silex" }, Find("-rod"));
        Assert.Equal(new[] { "Mythril Ingot" }, Find("is:recipe && -is:expert"));
        Assert.Equal(new[] { "Mythril Ingot", "Rarefied Claro Walnut Fishing Rod" }, Find("is:recipe || is:nothing"));
    }

    [Fact]
    public void Jobs_by_abbreviation_name_client_language_and_group()
    {
        Assert.Equal(new[] { "Purple Palate" }, Find("job:FSH"));
        Assert.Equal(new[] { "Purple Palate" }, Find("job:Fischer"));
        Assert.Equal(new[] { "Turali Corn", "Grade 4 Skybuilders' Log" }, Find("job:gartner"));
        Assert.Equal(new[] { "Turali Corn", "Grade 4 Skybuilders' Log" }, Find("job:GÄR"));
        Assert.Equal(new[] { "Mythril Ingot", "Rarefied Claro Walnut Fishing Rod" }, Find("job:crafter"));
        Assert.Equal(new[] { "Purple Palate", "Turali Corn", "Grade 4 Skybuilders' Log", "Raw Silex" }, Find("job:gatherer"));
    }

    [Theory]
    [InlineData("level:50", new[] { "Mythril Ingot" })]
    [InlineData("level:90+", new[] { "Rarefied Claro Walnut Fishing Rod", "Purple Palate", "Turali Corn" })]
    [InlineData("level:>=93", new[] { "Rarefied Claro Walnut Fishing Rod", "Purple Palate", "Turali Corn" })]
    [InlineData("level:>93", new[] { "Rarefied Claro Walnut Fishing Rod", "Turali Corn" })]
    [InlineData("level:<60", new[] { "Mythril Ingot" })]
    [InlineData("level:<=60", new[] { "Mythril Ingot", "Raw Silex" })]
    [InlineData("lvl:60..80", new[] { "Grade 4 Skybuilders' Log", "Raw Silex" })]
    [InlineData("level:80-60", new[] { "Grade 4 Skybuilders' Log", "Raw Silex" })]
    [InlineData("ilvl:600+", new[] { "Rarefied Claro Walnut Fishing Rod", "Purple Palate" })]
    [InlineData("stars:1+", new[] { "Rarefied Claro Walnut Fishing Rod" })]
    public void Numbers_and_ranges(string query, string[] expected) => Assert.Equal(expected, Find(query));

    [Fact]
    public void Categories_match_by_contains_in_both_languages()
    {
        Assert.Equal(new[] { "Mythril Ingot" }, Find("cat:metal"));
        Assert.Equal(new[] { "Mythril Ingot" }, Find("cat:metall"));
        Assert.Equal(new[] { "Rarefied Claro Walnut Fishing Rod" }, Find("cat:\"primary tool\""));
        Assert.Equal(new[] { "Purple Palate" }, Find("category:meeresfruchte"));
    }

    [Fact]
    public void Content_and_flags()
    {
        Assert.Equal(new[] { "Grade 4 Skybuilders' Log" }, Find("in:diadem"));
        Assert.Equal(new[] { "Turali Corn" }, Find("is:timed in:world"));
        Assert.Equal(new[] { "Purple Palate" }, Find("is:fish"));
        Assert.Equal(new[] { "Rarefied Claro Walnut Fishing Rod" }, Find("is:specialist"));
        Assert.Equal(new[] { "Mythril Ingot" }, Find("is:hq"));
        Assert.Equal(new[] { "Rarefied Claro Walnut Fishing Rod", "Turali Corn" }, Find("is:book"));
        Assert.Equal(new[] { "Rarefied Claro Walnut Fishing Rod" }, Find("is:expert"));
    }

    [Fact]
    public void Character_filters_use_the_live_character()
    {
        // Level 60 on every job, master book 77 learned, tome 900 not, a CRP specialist.
        var me = new BrowseCharacter(_ => 60, book => book == 77, _ => false, job => job == 8);
        var mine = BrowseQuery.Parse("level:mine", Vocabulary);
        Assert.True(mine.UsesCharacter);
        Assert.Equal(new[] { "Mythril Ingot", "Raw Silex" }, Find("level:mine", me));
        Assert.Equal(new[] { "Turali Corn" }, Find("is:unlearned", me));
        Assert.Equal(new[] { "Mythril Ingot", "Raw Silex" }, Find("is:ready", me));
        var high = new BrowseCharacter(_ => 100, _ => true, _ => true, _ => false);
        // Level 100, everything learned, but no specialist: the rod still isn't ready.
        Assert.DoesNotContain("Rarefied Claro Walnut Fishing Rod", Find("is:ready", high));
        Assert.Contains("Turali Corn", Find("is:ready", high));
    }

    [Fact]
    public void Typos_are_left_out_with_a_suggestion()
    {
        var q = BrowseQuery.Parse("job:fishr is:reduction", Vocabulary);
        var p = Assert.Single(q.Problems);
        Assert.Equal("job:fisher", p.Fix);
        Assert.Equal(0, p.Start);
        Assert.Equal("job:fishr".Length, p.Length);
        // The rest still filters.
        Assert.Equal(new[] { "Purple Palate", "Raw Silex" }, Find("job:fishr is:reduction"));

        Assert.Equal("level:50", Assert.Single(BrowseQuery.Parse("levle:50", Vocabulary).Problems).Fix);
        Assert.Equal("is:timed", Assert.Single(BrowseQuery.Parse("is:timd", Vocabulary).Problems).Fix);
        Assert.Equal("-in:diadem", Assert.Single(BrowseQuery.Parse("-in:diadm", Vocabulary).Problems).Fix);
        Assert.Equal("cat:metal", Assert.Single(BrowseQuery.Parse("cat:metl", Vocabulary).Problems).Fix);
        Assert.Equal("job:fsh,min", Assert.Single(BrowseQuery.Parse("job:fsh,mni", Vocabulary).Problems).Fix);
        // Not a number: left out, no guess.
        var bad = Assert.Single(BrowseQuery.Parse("level:fifty", Vocabulary).Problems);
        Assert.Null(bad.Fix);
        // Nothing close at all: no guess either.
        Assert.Null(Assert.Single(BrowseQuery.Parse("job:xyzzyq", Vocabulary).Problems).Fix);
    }

    [Fact]
    public void Half_typed_and_broken_queries_never_fail()
    {
        Assert.Empty(BrowseQuery.Parse("job:", Vocabulary).Problems);
        Assert.Equal(All.Select(e => e.Name), Find("job:"));
        Assert.Equal(new[] { "Purple Palate" }, Find("(job:fsh"));
        Assert.Equal(new[] { "Purple Palate" }, Find("job:fsh)"));
        Assert.Equal(new[] { "Purple Palate" }, Find("or job:fsh or"));
        Assert.Equal(new[] { "Purple Palate" }, Find("and and job:fsh"));
        Assert.Equal(All.Select(e => e.Name), Find("not"));
        Assert.Equal(All.Select(e => e.Name), Find("- ( )"));
    }

    [Fact]
    public void Words_for_favourites_and_what_the_search_uses()
    {
        var q = BrowseQuery.Parse("corn -oil \"mythril ingot\" job:btn", Vocabulary);
        Assert.Equal(new[] { "corn", "mythril ingot" }, q.Words);
        Assert.True(q.HasFilters);
        Assert.False(q.UsesCharacter);
        Assert.False(BrowseQuery.Parse("corn", Vocabulary).HasFilters);
        Assert.True(BrowseQuery.Parse("  ", Vocabulary).IsEmpty);
    }
}
