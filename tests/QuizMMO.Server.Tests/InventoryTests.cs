using QuizMMO.Server.Gameplay;
using Xunit;

namespace QuizMMO.Server.Tests;

public class InventoryTests
{
    [Fact]
    public void AddAndRemoveGold_RespectBalance()
    {
        var inventory = new Inventory();

        Assert.True(inventory.AddGold(100));
        Assert.False(inventory.AddGold(-1));
        Assert.True(inventory.TryRemoveGold(30));
        Assert.False(inventory.TryRemoveGold(71));
        Assert.False(inventory.TryRemoveGold(-5));

        Assert.Equal(70, inventory.Gold);
    }

    [Fact]
    public void AddAndRemoveItems_TrackCountsAndDropEmptyStacks()
    {
        var inventory = new Inventory();

        inventory.AddItem(1001, 2);
        inventory.AddItem(1001, 1);
        inventory.AddItem(1002, 0);   // 0개는 무시

        Assert.Equal(3u, inventory.Count(1001));
        Assert.False(inventory.Items.ContainsKey(1002));
        Assert.True(inventory.Has(1001, 3));
        Assert.False(inventory.Has(1001, 4));
        Assert.True(inventory.Has(9999, 0));

        Assert.False(inventory.TryRemoveItem(1001, 4));
        Assert.True(inventory.TryRemoveItem(1001, 3));
        Assert.False(inventory.Items.ContainsKey(1001));
    }

    [Fact]
    public void ToMessage_ListsGoldAndEveryStack()
    {
        var inventory = new Inventory();
        inventory.AddGold(42);
        inventory.AddItem(1001, 1);
        inventory.AddItem(2001, 5);

        var message = inventory.ToMessage();

        Assert.Equal(42, message.Gold);
        Assert.Equal(2, message.Items.Count);
        Assert.Equal(5u, message.Items.Single(i => i.ItemId == 2001).Count);
    }
}
