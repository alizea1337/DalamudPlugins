using Provenance.Core.Crafting;
using Xunit;

namespace Provenance.Tests;

public class RaphaelTests
{
    [Fact]
    public void Builds_the_solve_command_line()
    {
        var full = RaphaelCli.Arguments(new RaphaelRequest(38104, 100, 5844, 5182, 572, true, false, null));
        Assert.Equal("solve --recipe-id 38104 --level 100 --stats 5844 5182 572 --manipulation --output-variables action_ids final_quality recipe_max_quality", full);

        // No quality wanted: the specialist actions are left out (raphael-cli fails with them, and they only add quality).
        var fast = RaphaelCli.Arguments(new RaphaelRequest(153, 100, 5844, 5182, 572, false, true, 0));
        Assert.Equal("solve --recipe-id 153 --level 100 --stats 5844 5182 572 --target-quality 0 --output-variables action_ids final_quality recipe_max_quality", fast);

        var hq = RaphaelCli.Arguments(new RaphaelRequest(153, 100, 5844, 5182, 572, false, true, null));
        Assert.Equal("solve --recipe-id 153 --level 100 --stats 5844 5182 572 --heart-and-soul --quick-innovation --output-variables action_ids final_quality recipe_max_quality", hq);
    }

    [Fact]
    public void The_game_s_own_recipe_numbers_replace_the_recipe_id()
    {
        var sheet = new RaphaelRequest(36771, 100, 5844, 5182, 572, true, false, 0);
        var live = sheet with { Live = new LiveRecipe(690, 6600, 12000, 80) };
        Assert.Equal("solve --custom-recipe 690 6600 12000 80 --level 100 --stats 5844 5182 572 --manipulation --target-quality 0 --output-variables action_ids final_quality recipe_max_quality",
            RaphaelCli.Arguments(live));
        Assert.StartsWith("solve --recipe-id 36771 ", RaphaelCli.Arguments(sheet));
        Assert.NotEqual(sheet.CacheKey, live.CacheKey);
    }

    [Fact]
    public void Hq_ingredients_start_the_craft_with_quality()
    {
        var withHq = new RaphaelRequest(37825, 100, 5700, 5300, 645, true, false, null, new[] { 2, 1, 0 });
        Assert.Equal("solve --recipe-id 37825 --level 100 --stats 5700 5300 645 --manipulation --hq-ingredients 2 1 0 --output-variables action_ids final_quality recipe_max_quality",
            RaphaelCli.Arguments(withHq));
        // None HQ, or no quality wanted: not passed.
        Assert.DoesNotContain("--hq-ingredients", RaphaelCli.Arguments(withHq with { HqIngredients = new[] { 0, 0, 0 } }));
        Assert.DoesNotContain("--hq-ingredients", RaphaelCli.Arguments(withHq with { TargetQuality = 0 }));
        // A different HQ count is a different rotation.
        Assert.NotEqual(withHq.CacheKey, (withHq with { HqIngredients = new[] { 1, 1, 0 } }).CacheKey);
        Assert.Equal((withHq with { HqIngredients = null }).CacheKey, (withHq with { HqIngredients = new[] { 0, 0, 0 } }).CacheKey);
    }

    [Fact]
    public void Reads_the_quality_a_rotation_reaches()
    {
        var s = RaphaelCli.ParseSolution("\"[100379, 4639, 100323]\",14087,21200\n");
        Assert.NotNull(s);
        Assert.Equal(new uint[] { 100379, 4639, 100323 }, s!.Actions);
        Assert.Equal(14087, s.FinalQuality);
        Assert.Equal(21200, s.MaxQuality);
        Assert.False(s.ReachesMax);
        Assert.True(RaphaelCli.ParseSolution("\"[100323]\",21293,21200")!.ReachesMax);
        // Only the actions: the quality is unknown, never "reached".
        var bare = RaphaelCli.ParseSolution("\"[100323]\"");
        Assert.Equal(-1, bare!.FinalQuality);
        Assert.False(bare.ReachesMax);
        Assert.Null(RaphaelCli.ParseSolution(""));
    }

    [Fact]
    public void Parses_real_output()
    {
        Assert.Equal(new uint[] { 100379, 4639, 19297, 100403, 100323 },
            RaphaelCli.ParseActionIds("\"[100379, 4639, 19297, 100403, 100323]\"\n"));
        Assert.Equal(new uint[] { 100323 }, RaphaelCli.ParseActionIds("\"[100323]\""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("thread 'main' panicked at raphael-cli\\src\\commands\\solve.rs:199:32:\nUnable to find Recipe with ID: 999999")]
    [InlineData("\"[]\"")]
    [InlineData("\"[100323, oops]\"")]
    public void Anything_else_is_no_solution(string output)
    {
        Assert.Empty(RaphaelCli.ParseActionIds(output));
    }

    [Fact]
    public void Cache_key_changes_with_stats_and_target()
    {
        var a = new RaphaelRequest(1, 100, 10, 20, 30, false, false, null);
        Assert.NotEqual(a.CacheKey, (a with { Cp = 31 }).CacheKey);
        Assert.NotEqual(a.CacheKey, (a with { TargetQuality = 0 }).CacheKey);
        Assert.Equal(a.CacheKey, (a with { }).CacheKey);
    }
}
