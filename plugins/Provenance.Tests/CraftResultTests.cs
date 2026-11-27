using Provenance.Core.Crafting;
using Xunit;

namespace Provenance.Tests;

public class CraftResultTests
{
    [Fact]
    public void An_attempt_only_accepts_its_dispatched_recipe_even_for_the_same_product()
    {
        var attempt = new CraftResult(42, 123);
        Assert.False(attempt.MatchesRecipe(0));
        Assert.False(attempt.MatchesRecipe(124));
        Assert.True(attempt.MatchesRecipe(123));
    }

    [Theory]
    [InlineData(1156, 42, CraftSource.Self, CraftOutcome.Created)]
    [InlineData(1157, 42, CraftSource.Other, CraftOutcome.Unknown)]
    [InlineData(1156, 42, CraftSource.None, CraftOutcome.Unknown)]
    [InlineData(1158, 42, CraftSource.None, CraftOutcome.Created)]
    [InlineData(1159, 42, CraftSource.Self, CraftOutcome.Created)]
    [InlineData(1159, 43, CraftSource.Self, CraftOutcome.Unknown)]
    [InlineData(1159, 0, CraftSource.Self, CraftOutcome.Unknown)]
    [InlineData(1154, 42, CraftSource.Self, CraftOutcome.Unknown)] // an action succeeded
    [InlineData(5902, 42, CraftSource.Self, CraftOutcome.Unknown)] // a trial, no item
    [InlineData(1160, 0, CraftSource.Self, CraftOutcome.Failed)]
    [InlineData(1161, 0, CraftSource.Self, CraftOutcome.Failed)]
    [InlineData(1177, 0, CraftSource.None, CraftOutcome.Failed)]
    [InlineData(5914, 42, CraftSource.None, CraftOutcome.Failed)]
    [InlineData(5915, 42, CraftSource.Other, CraftOutcome.Unknown)]
    public void Only_this_crafters_result_for_the_expected_product_counts(uint row, uint item, CraftSource source, CraftOutcome expected)
    {
        var attempt = new CraftResult(42, 123);
        attempt.Observe(row, item, source);
        Assert.Equal(expected, attempt.Outcome);
    }

    [Theory]
    [InlineData(0, false, true, false, ResultStep.Wait)] // notebook is back, result may still arrive
    [InlineData(0, false, true, true, ResultStep.Unconfirmed)]
    [InlineData(1158, true, true, false, ResultStep.Wait)]
    [InlineData(1158, true, true, true, ResultStep.Unconfirmed)] // never credit an unsettled craft
    [InlineData(1158, false, false, false, ResultStep.Wait)]
    [InlineData(1158, false, true, false, ResultStep.Count)]
    [InlineData(1177, false, true, false, ResultStep.Failed)]
    public void Closing_the_window_is_not_a_result(uint row, bool busy, bool notebookSettled, bool timedOut, ResultStep expected)
    {
        var attempt = new CraftResult(42, 123);
        attempt.Observe(row, 42, CraftSource.None);
        Assert.Equal(expected, attempt.Resolve(busy, notebookSettled, timedOut));
    }

    [Fact]
    public void A_duplicate_success_does_not_override_failure_or_carry_to_the_next_attempt()
    {
        var attempt = new CraftResult(42, 123);
        attempt.Observe(1158, 42, CraftSource.None);
        attempt.Observe(5914, 42, CraftSource.None);
        attempt.Observe(1158, 42, CraftSource.None);
        Assert.Equal(ResultStep.Failed, attempt.Resolve(false, true, false));
        Assert.Equal(ResultStep.Unconfirmed, new CraftResult(42, 123).Resolve(false, true, true));
    }
}
