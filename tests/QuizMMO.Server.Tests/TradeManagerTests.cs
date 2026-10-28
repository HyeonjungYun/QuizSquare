using Microsoft.Extensions.Logging.Abstractions;
using QuizMMO.Protocol;
using QuizMMO.Server.Core;
using QuizMMO.Server.Gameplay;
using QuizMMO.Server.Gameplay.Data;
using Xunit;

namespace QuizMMO.Server.Tests;

public class TradeManagerTests
{
    private const uint WorldId = 1;

    private static ItemCatalog Catalog() => ItemCatalog.FromItems(new[]
    {
        new ItemDefinition { Id = 1001, Name = "trophy", Tradable = true },
        new ItemDefinition { Id = 2001, Name = "badge", Tradable = false },
    });

    private static TradeManager NewManager() =>
        new(new ServerOptions.TradeOptions { RequestTimeoutSec = 5, IdleTimeoutSec = 10 }, Catalog(), WorldId, NullLogger.Instance);

    private static (Player player, FakeSink sink) NewPlayer(ulong id, long gold, uint trophies = 0)
    {
        var sink = new FakeSink();
        var player = new Player(id, $"p{id}", sink) { RoomId = WorldId };
        player.Inventory.AddGold(gold);
        if (trophies > 0) player.Inventory.AddItem(1001, trophies);
        return (player, sink);
    }

    private static uint OpenTrade(TradeManager trades, Player a, FakeSink sinkA, Player b, FakeSink sinkB)
    {
        Assert.True(trades.Request(a, b));
        var requested = Assert.Single(sinkB.Of<STradeRequested>());
        Assert.True(trades.Respond(b, requested.TradeId, accept: true));
        Assert.Single(sinkA.Of<STradeStarted>());
        Assert.Single(sinkB.Of<STradeStarted>());
        return requested.TradeId;
    }

    [Fact]
    public void FullFlow_SwapsGoldAndItems_AndSendsInventories()
    {
        var trades = NewManager();
        var (a, sinkA) = NewPlayer(1, gold: 100, trophies: 2);
        var (b, sinkB) = NewPlayer(2, gold: 50);
        var tradeId = OpenTrade(trades, a, sinkA, b, sinkB);

        Assert.True(trades.SetOffer(a, tradeId, 10, new[] { new ItemStack { ItemId = 1001, Count = 1 } }));
        Assert.True(trades.SetOffer(b, tradeId, 20, Array.Empty<ItemStack>()));
        Assert.Equal(2, sinkA.Of<STradeOffer>().Count());
        Assert.True(trades.Confirm(a, tradeId));
        Assert.Equal(1, trades.ActiveCount);                 // 한쪽만 확정: 아직
        Assert.True(trades.Confirm(b, tradeId));

        Assert.Equal(0, trades.ActiveCount);
        Assert.Single(sinkA.Of<STradeCompleted>());
        Assert.Single(sinkB.Of<STradeCompleted>());
        Assert.Equal(110, a.Inventory.Gold);
        Assert.Equal(1u, a.Inventory.Count(1001));
        Assert.Equal(40, b.Inventory.Gold);
        Assert.Equal(1u, b.Inventory.Count(1001));
        Assert.Equal(110, Assert.Single(sinkA.Of<SInventory>()).Gold);
        Assert.Equal(40, Assert.Single(sinkB.Of<SInventory>()).Gold);
        Assert.Equal(0u, a.TradeId);
        Assert.Equal(0u, b.TradeId);
    }

    [Fact]
    public void Request_RejectsSelfMissingNotInWorldAndBusyTargets()
    {
        var trades = NewManager();
        var (a, sinkA) = NewPlayer(1, 10);
        var (b, sinkB) = NewPlayer(2, 10);
        var (c, _) = NewPlayer(3, 10);

        Assert.False(trades.Request(a, a));
        Assert.Equal(ErrorCode.SelfTarget, sinkA.Of<SError>().Last().Code);

        Assert.False(trades.Request(a, null));
        Assert.Equal(ErrorCode.TargetNotFound, sinkA.Of<SError>().Last().Code);

        b.RoomId = 99;
        Assert.False(trades.Request(a, b));
        Assert.Equal(ErrorCode.NotInWorld, sinkA.Of<SError>().Last().Code);
        b.RoomId = WorldId;

        Assert.True(trades.Request(a, b));                   // a-b 대기 중
        Assert.False(trades.Request(c, b));
        Assert.Equal(ErrorCode.TargetBusy, ((FakeSink)c.Link).Of<SError>().Last().Code);
        Assert.False(trades.Request(a, c));
        Assert.Equal(ErrorCode.AlreadyInTrade, sinkA.Of<SError>().Last().Code);
        Assert.Single(sinkB.Of<STradeRequested>());
    }

    [Fact]
    public void Decline_CancelsForBoth()
    {
        var trades = NewManager();
        var (a, sinkA) = NewPlayer(1, 10);
        var (b, sinkB) = NewPlayer(2, 10);
        trades.Request(a, b);
        var tradeId = Assert.Single(sinkB.Of<STradeRequested>()).TradeId;

        Assert.True(trades.Respond(b, tradeId, accept: false));

        Assert.Equal(TradeCancelReason.Declined, Assert.Single(sinkA.Of<STradeCancelled>()).Reason);
        Assert.Single(sinkB.Of<STradeCancelled>());
        Assert.Equal(0, trades.ActiveCount);
        Assert.Equal(0u, a.TradeId);
        Assert.Equal(0u, b.TradeId);
    }

    [Fact]
    public void Respond_ByInitiatorOrBeforeRequest_IsRejected()
    {
        var trades = NewManager();
        var (a, sinkA) = NewPlayer(1, 10);
        var (b, _) = NewPlayer(2, 10);
        trades.Request(a, b);

        Assert.False(trades.Respond(a, a.TradeId, accept: true));    // 요청한 쪽은 응답할 수 없다
        Assert.Equal(ErrorCode.InvalidRequest, sinkA.Of<SError>().Last().Code);
        Assert.False(trades.Respond(b, 999, accept: true));          // 없는 거래
        Assert.Equal(1, trades.ActiveCount);
    }

    [Fact]
    public void Offer_MoreThanOwned_OrNonTradableItem_IsRejectedWithoutChangingOffer()
    {
        var trades = NewManager();
        var (a, sinkA) = NewPlayer(1, gold: 10, trophies: 1);
        var (b, sinkB) = NewPlayer(2, gold: 10);
        a.Inventory.AddItem(2001, 1);
        var tradeId = OpenTrade(trades, a, sinkA, b, sinkB);

        Assert.False(trades.SetOffer(a, tradeId, 11, Array.Empty<ItemStack>()));
        Assert.Equal(ErrorCode.InsufficientItems, sinkA.Of<SError>().Last().Code);
        Assert.False(trades.SetOffer(a, tradeId, 0, new[] { new ItemStack { ItemId = 1001, Count = 2 } }));
        Assert.Equal(ErrorCode.InsufficientItems, sinkA.Of<SError>().Last().Code);
        Assert.False(trades.SetOffer(a, tradeId, 0, new[] { new ItemStack { ItemId = 2001, Count = 1 } }));
        Assert.Equal(ErrorCode.InsufficientItems, sinkA.Of<SError>().Last().Code);

        Assert.Empty(sinkB.Of<STradeOffer>());
        Assert.Equal(1, trades.ActiveCount);
    }

    [Fact]
    public void ChangingAnOffer_ResetsBothConfirmations()
    {
        var trades = NewManager();
        var (a, sinkA) = NewPlayer(1, 100);
        var (b, sinkB) = NewPlayer(2, 100);
        var tradeId = OpenTrade(trades, a, sinkA, b, sinkB);
        trades.SetOffer(a, tradeId, 10, Array.Empty<ItemStack>());
        trades.SetOffer(b, tradeId, 10, Array.Empty<ItemStack>());
        trades.Confirm(a, tradeId);

        trades.SetOffer(b, tradeId, 20, Array.Empty<ItemStack>());   // b 가 제안을 바꿈 → a 의 확정 무효
        trades.Confirm(b, tradeId);

        Assert.Equal(1, trades.ActiveCount);                           // a 가 다시 확정해야 한다
        Assert.Empty(sinkA.Of<STradeCompleted>());
        trades.Confirm(a, tradeId);
        Assert.Equal(0, trades.ActiveCount);
        Assert.Equal(110, a.Inventory.Gold);
        Assert.Equal(90, b.Inventory.Gold);
    }

    [Fact]
    public void Execute_WhenOfferNoLongerBacked_CancelsAsInvalidOffer()
    {
        var trades = NewManager();
        var (a, sinkA) = NewPlayer(1, 100);
        var (b, sinkB) = NewPlayer(2, 100);
        var tradeId = OpenTrade(trades, a, sinkA, b, sinkB);
        trades.SetOffer(a, tradeId, 100, Array.Empty<ItemStack>());
        trades.SetOffer(b, tradeId, 0, Array.Empty<ItemStack>());
        trades.Confirm(a, tradeId);
        a.Inventory.TryRemoveGold(50);                                 // 확정 사이에 재화가 사라졌다 (방어 코드 검증)

        trades.Confirm(b, tradeId);

        Assert.Equal(TradeCancelReason.InvalidOffer, Assert.Single(sinkB.Of<STradeCancelled>()).Reason);
        Assert.Equal(50, a.Inventory.Gold);
        Assert.Equal(100, b.Inventory.Gold);
    }

    [Fact]
    public void PendingRequest_TimesOut()
    {
        var trades = NewManager();
        var (a, sinkA) = NewPlayer(1, 10);
        var (b, _) = NewPlayer(2, 10);
        trades.Request(a, b);

        trades.Update(4.9f);
        Assert.Equal(1, trades.ActiveCount);
        trades.Update(0.2f);

        Assert.Equal(0, trades.ActiveCount);
        Assert.Equal(TradeCancelReason.Timeout, Assert.Single(sinkA.Of<STradeCancelled>()).Reason);
    }

    [Fact]
    public void PlayerLeaving_CancelsTradeWithReason()
    {
        var trades = NewManager();
        var (a, sinkA) = NewPlayer(1, 10);
        var (b, sinkB) = NewPlayer(2, 10);
        OpenTrade(trades, a, sinkA, b, sinkB);

        trades.OnPlayerLeft(a, TradeCancelReason.Disconnected);

        Assert.Equal(TradeCancelReason.Disconnected, Assert.Single(sinkB.Of<STradeCancelled>()).Reason);
        Assert.Equal(0, trades.ActiveCount);
        Assert.Equal(0u, b.TradeId);
    }

    [Fact]
    public void Cancel_ByEitherSide_CancelsForBoth()
    {
        var trades = NewManager();
        var (a, sinkA) = NewPlayer(1, 10);
        var (b, sinkB) = NewPlayer(2, 10);
        var tradeId = OpenTrade(trades, a, sinkA, b, sinkB);

        Assert.True(trades.Cancel(b, tradeId));

        Assert.Equal(TradeCancelReason.Cancelled, Assert.Single(sinkA.Of<STradeCancelled>()).Reason);
        Assert.Single(sinkB.Of<STradeCancelled>());
        Assert.Equal(0, trades.ActiveCount);
    }
}
