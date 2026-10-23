using QuizMMO.Server.Gameplay;
using Xunit;

namespace QuizMMO.Server.Tests;

public class QuizQueueTests
{
    private static Player NewPlayer(ulong id) => new(id, $"p{id}", new FakeSink());

    [Fact]
    public void Join_SetsFlagAndCounts()
    {
        var queue = new QuizQueue();
        var player = NewPlayer(1);

        Assert.True(queue.Join(player));

        Assert.True(player.InQuizQueue);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void JoinTwice_SecondIsRejected()
    {
        var queue = new QuizQueue();
        var player = NewPlayer(1);
        queue.Join(player);

        Assert.False(queue.Join(player));
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void Leave_ClearsFlagAndRemoves()
    {
        var queue = new QuizQueue();
        var player = NewPlayer(1);
        queue.Join(player);

        Assert.True(queue.Leave(player.Id));

        Assert.False(player.InQuizQueue);
        Assert.Equal(0, queue.Count);
        Assert.False(queue.Leave(player.Id));
    }

    [Fact]
    public void TryTake_NotEnoughWaiting_ReturnsFalseAndTakesNobody()
    {
        var queue = new QuizQueue();
        queue.Join(NewPlayer(1));
        var taken = new List<Player> { NewPlayer(99) };   // 이전 내용은 비워져야 한다

        Assert.False(queue.TryTake(2, taken));

        Assert.Empty(taken);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void TryTake_TakesFirstComersInOrderAndClearsTheirFlag()
    {
        var queue = new QuizQueue();
        var players = Enumerable.Range(1, 5).Select(i => NewPlayer((ulong)i)).ToList();
        foreach (var p in players) queue.Join(p);
        var taken = new List<Player>();

        Assert.True(queue.TryTake(3, taken));

        Assert.Equal(new ulong[] { 1, 2, 3 }, taken.Select(p => p.Id));
        Assert.All(taken, p => Assert.False(p.InQuizQueue));
        Assert.Equal(2, queue.Count);
        Assert.True(players[3].InQuizQueue);
    }
}
