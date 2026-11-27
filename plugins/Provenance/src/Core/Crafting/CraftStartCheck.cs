namespace Provenance.Core.Crafting;

public enum CraftStart { Click, CheckConsumables, Solve, Stop }

internal static class CraftStartCheck
{
    public static CraftStart Decide(RaphaelRequest? plan, int[] stats, bool mayRenew, int replans)
    {
        if (plan == null || (stats[0], stats[1], stats[2]) == (plan.Craftsmanship, plan.Control, plan.Cp)) return CraftStart.Click;
        if (stats[0] <= 0 || replans >= 2) return CraftStart.Stop;
        return mayRenew ? CraftStart.CheckConsumables : CraftStart.Solve;
    }
}
