using QuizMMO.Protocol;
using QuizMMO.Server.Gameplay;
using Xunit;

namespace QuizMMO.Server.Tests;

public class RoomTests
{
    private static (Player player, FakeSink sink) NewPlayer(ulong id)
    {
        var sink = new FakeSink();
        return (new Player(id, $"p{id}", sink), sink);
    }

    [Fact]
    public void Enter_SendsRoomChangedAndExistingPlayers_AndNotifiesOthers()
    {
        var room = new WorldRoom(7, "Plaza");
        var (first, firstSink) = NewPlayer(1);
        var (second, secondSink) = NewPlayer(2);
        room.Enter(first);

        room.Enter(second);

        // 두 번째 입장자: 룸 정보 + 기존 인원(1번)
        var changed = Assert.Single(secondSink.Of<SRoomChanged>());
        Assert.Equal(7u, changed.RoomId);
        Assert.Equal(RoomType.World, changed.RoomType);
        Assert.Equal("Plaza", changed.RoomName);
        var seen = Assert.Single(secondSink.Of<SPlayerEnter>());
        Assert.Equal(1UL, seen.PlayerId);
        // 기존 인원(1번): 2번이 들어왔다는 알림
        var notified = Assert.Single(firstSink.Of<SPlayerEnter>());
        Assert.Equal(2UL, notified.PlayerId);
        Assert.Equal(7u, second.RoomId);
        Assert.Equal(2, room.PlayerCount);
    }

    [Fact]
    public void EnterTwice_IsIgnored()
    {
        var room = new WorldRoom(7, "Plaza");
        var (player, sink) = NewPlayer(1);
        room.Enter(player);

        room.Enter(player);

        Assert.Equal(1, room.PlayerCount);
        Assert.Single(sink.Of<SRoomChanged>());
    }

    [Fact]
    public void Leave_BroadcastsPlayerLeaveToRemaining_AndClearsRoomId()
    {
        var room = new WorldRoom(7, "Plaza");
        var (first, firstSink) = NewPlayer(1);
        var (second, _) = NewPlayer(2);
        room.Enter(first);
        room.Enter(second);

        room.Leave(second.Id);

        var left = Assert.Single(firstSink.Of<SPlayerLeave>());
        Assert.Equal(2UL, left.PlayerId);
        Assert.Equal(0u, second.RoomId);
        Assert.Equal(1, room.PlayerCount);
        Assert.Null(room.FindPlayer(2));
    }

    [Fact]
    public void Chat_ReachesEveryoneIncludingSender()
    {
        var room = new WorldRoom(7, "Plaza");
        var (first, firstSink) = NewPlayer(1);
        var (second, secondSink) = NewPlayer(2);
        room.Enter(first);
        room.Enter(second);

        room.Chat(first, "hello");

        foreach (var sink in new[] { firstSink, secondSink })
        {
            var chat = Assert.Single(sink.Of<SChat>());
            Assert.Equal(1UL, chat.PlayerId);
            Assert.Equal("p1", chat.Nickname);
            Assert.Equal("hello", chat.Text);
        }
    }

    [Fact]
    public void QuizRoom_FinishesAfterDuration()
    {
        var room = new QuizRoom(9, "Quiz-9", durationSec: 1.0f);

        room.Update(0.4f);
        Assert.False(room.IsFinished);
        room.Update(0.7f);

        Assert.True(room.IsFinished);
        Assert.Equal(0f, room.RemainingSec);
        Assert.Equal(RoomType.Quiz, room.Type);
    }
}
