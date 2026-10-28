using QuizMMO.Server.Gameplay;
using Xunit;

namespace QuizMMO.Server.Tests;

public class PlayerTests
{
    [Fact]
    public void TryChat_FirstChatRightAfterServerStart_IsAllowed()
    {
        // 서버 기동 직후에는 단조 시계가 0 에 가깝다. 첫 채팅이 쿨다운에 걸리면 안 된다 (회귀 테스트).
        var player = new Player(1, "p", new FakeSink());

        Assert.True(player.TryChat(nowMs: 3, cooldownMs: 500));
    }

    [Fact]
    public void TryChat_WithinCooldown_IsRejected_ThenAllowedAgain()
    {
        var player = new Player(1, "p", new FakeSink());
        Assert.True(player.TryChat(1000, 500));

        Assert.False(player.TryChat(1200, 500));
        Assert.False(player.TryChat(1499, 500));
        Assert.True(player.TryChat(1500, 500));
        Assert.False(player.TryChat(1600, 500));   // 마지막 허용 시각(1500) 기준으로 다시 센다
    }

    [Fact]
    public void TryChat_ZeroCooldown_AlwaysAllows()
    {
        var player = new Player(1, "p", new FakeSink());

        Assert.True(player.TryChat(10, 0));
        Assert.True(player.TryChat(10, 0));
    }

    [Fact]
    public void NewPlayer_StartsWithEmptyInventoryAndNoRoomOrTrade()
    {
        var player = new Player(7, "p7", new FakeSink());

        Assert.Equal(0, player.Inventory.Gold);
        Assert.Empty(player.Inventory.Items);
        Assert.Equal(0u, player.RoomId);
        Assert.Equal(0u, player.TradeId);
        Assert.False(player.InQuizQueue);
        Assert.True(player.IsConnected);
    }
}
