using Provenance.Core.Crafting;
using Xunit;

namespace Provenance.Tests;

public class RaphaelTests
{
    [Fact]
    public void Builds_the_solve_command_line()
    {
        var full = RaphaelCli.Arguments(new RaphaelRequest(38104, 100, 5844, 5182, 572, true, false, null));
        Assert.Equal("solve --recipe-id 38104 --level 100 --stats 5844 5182 572 --manipulation --output-variables action_ids", full);

        // No quality wanted: the specialist actions are left out (raphael-cli fails with them, and they only add quality).
        var fast = RaphaelCli.Arguments(new RaphaelRequest(153, 100, 5844, 5182, 572, false, true, 0));
        Assert.Equal("solve --recipe-id 153 --level 100 --stats 5844 5182 572 --target-quality 0 --output-variables action_ids", fast);

        var hq = RaphaelCli.Arguments(new RaphaelRequest(153, 100, 5844, 5182, 572, false, true, null));
        Assert.Equal("solve --recipe-id 153 --level 100 --stats 5844 5182 572 --heart-and-soul --quick-innovation --output-variables action_ids", hq);
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
