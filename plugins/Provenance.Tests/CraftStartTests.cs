using Provenance.Core.Crafting;
using Xunit;

namespace Provenance.Tests;

public class CraftStartTests
{
    private static readonly RaphaelRequest Plan = new(1, 100, 5400, 5100, 620, true, false, null);

    [Theory]
    [InlineData(5400, 5100, 620, true, 2, CraftStart.Click)]
    [InlineData(5399, 5100, 620, true, 0, CraftStart.CheckConsumables)]
    [InlineData(5400, 5099, 620, false, 0, CraftStart.Solve)]
    [InlineData(5400, 5100, 619, false, 1, CraftStart.Solve)]
    [InlineData(5400, 5100, 630, true, 2, CraftStart.Stop)]
    [InlineData(0, 0, 0, true, 0, CraftStart.Stop)]
    public void Check_the_solved_stats_at_each_click(int craftsmanship, int control, int cp, bool mayRenew, int replans, CraftStart expected)
        => Assert.Equal(expected, CraftStartCheck.Decide(Plan, [craftsmanship, control, cp], mayRenew, replans));

    [Fact]
    public void Basic_synthesis_without_a_solved_rotation_does_not_need_a_buff()
        => Assert.Equal(CraftStart.Click, CraftStartCheck.Decide(null, [5300, 5000, 600], true, 2));
}
