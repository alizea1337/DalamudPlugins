namespace Provenance.Core.Crafting;

public enum CraftSource { None, Self, Other }
public enum CraftOutcome { Unknown, Created, Failed }
public enum ResultStep { Wait, Count, Failed, Unconfirmed }

internal sealed class CraftResult(uint expectedItem, uint expectedRecipe)
{
    public bool MatchesRecipe(uint activeRecipe) => activeRecipe != 0 && activeRecipe == expectedRecipe;

    public CraftOutcome Outcome { get; private set; }
    public ResultStep Resolve(bool busy, bool notebookSettled, bool timedOut)
    {
        if (Outcome == CraftOutcome.Failed && (!busy || timedOut)) return ResultStep.Failed;
        if (!busy && notebookSettled && Outcome == CraftOutcome.Created) return ResultStep.Count;
        return timedOut ? ResultStep.Unconfirmed : ResultStep.Wait;
    }

    // LogMessage: 1156/7 also announce other players' crafts; 1158/9 are the
    // first-person result. IntegerParameter(1) is the item in all four rows.
    public void Observe(uint row, uint item, CraftSource source)
    {
        if (source == CraftSource.Other || Outcome == CraftOutcome.Failed) return;
        var matches = expectedItem != 0 && item == expectedItem;
        switch (row)
        {
            case 1156 or 1157 when matches && source == CraftSource.Self:
            case 1158 or 1159 when matches:
                Outcome = CraftOutcome.Created;
                break;
            case 1160 or 1161 when source == CraftSource.Self: // failure or cancellation
            case 1177:
            case 5914 when matches: // insufficient quality, despite completing progress
            case 5915 when matches && source == CraftSource.Self:
                Outcome = CraftOutcome.Failed;
                break;
        }
    }
}
