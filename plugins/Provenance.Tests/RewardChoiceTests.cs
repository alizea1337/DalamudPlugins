using System;
using Provenance.Core.Tribes;
using Xunit;

namespace Provenance.Tests;

public class RewardChoiceTests
{
    private static RewardOption Item(int slot, uint id, uint price, uint amount = 1, uint icon = 20204)
        => new(slot, id, false, icon, amount, $"item {id}", price);

    private static RewardOption Gil(int slot, uint amount, uint id = 0) => new(slot, id, false, RewardChoice.GilIcon, amount, "gil", 0);

    [Fact]
    public void NothingToChooseFrom()
        => Assert.Null(RewardChoice.Pick(Array.Empty<RewardOption>()));

    [Fact]
    public void GilComesFirstEvenWhenAnItemSellsForMore()
    {
        var options = new[] { Item(0, 5702, 5000, 3), Gil(1, 500), Item(2, 5707, 90) };
        var pick = RewardChoice.Pick(options)!;
        Assert.Equal(1, pick.Slot);
        Assert.Equal("gil comes first", RewardChoice.Why(pick, options));
    }

    [Fact]
    public void ItemOneIsGilWithoutTheSackIcon()
        => Assert.Equal(2, RewardChoice.Pick(new[] { Item(0, 5702, 90), Item(1, 5707, 90), new RewardOption(2, 1, false, 65002, 300, "gil", 0) })!.Slot);

    [Fact]
    public void TheLargerSumOfGilWins()
        => Assert.Equal(2, RewardChoice.Pick(new[] { Gil(0, 100), Item(1, 5702, 90), Gil(2, 300) })!.Slot);

    [Fact]
    public void ThenTheItemThatSellsForTheMost()
    {
        var options = new[] { Item(0, 5687, 90), Item(1, 5692, 120), Item(2, 5697, 100) };
        var pick = RewardChoice.Pick(options)!;
        Assert.Equal(1, pick.Slot);
        Assert.Equal("sells for the most (120 gil)", RewardChoice.Why(pick, options));
    }

    [Fact]
    public void TheAmountCounts()
        // Two at 50 sell for more than one at 90.
        => Assert.Equal(1, RewardChoice.Pick(new[] { Item(0, 5687, 90), Item(1, 5692, 50, 2) })!.Slot);

    [Fact]
    public void ATieGoesToTheFirstListed()
    {
        // The Ixal rewards (1523): three materia IV that all sell for 90.
        var options = new[] { Item(0, 5702, 90), Item(1, 5707, 90), Item(2, 5692, 90) };
        var pick = RewardChoice.Pick(options)!;
        Assert.Equal(0, pick.Slot);
        Assert.Contains("the first listed", RewardChoice.Why(pick, options));
    }

    [Fact]
    public void TheListedOrderIsTheSlotsNotTheArrays()
        => Assert.Equal(1, RewardChoice.Pick(new[] { Item(3, 5702, 90), Item(1, 5707, 90) })!.Slot);

    [Fact]
    public void AnAmountOfZeroCountsAsOne()
    {
        Assert.Equal(90ul, Item(0, 5702, 90, 0).VendorValue);
        Assert.Equal(180ul, Item(0, 5702, 90, 2).VendorValue);
    }
}
