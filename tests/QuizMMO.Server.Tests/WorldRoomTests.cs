using QuizMMO.Protocol;
using QuizMMO.Server.Core;
using QuizMMO.Server.Gameplay;
using Xunit;

namespace QuizMMO.Server.Tests;

public class WorldRoomTests
{
    private static ServerOptions.WorldOptions Options() => new()
    {
        Width = 1000, Height = 1000, SpawnX = 500, SpawnY = 500, SpawnRadius = 10,
        MaxSpeed = 100, SpeedTolerance = 1.0f, MoveMinIntervalMs = 100, MaxMoveDeltaSec = 1.0,
    };

    private static (WorldRoom room, Player mover, FakeSink moverSink, Player other, FakeSink otherSink) Setup()
    {
        var room = new WorldRoom(1, "Plaza", Options(), new Random(1234));
        var moverSink = new FakeSink();
        var otherSink = new FakeSink();
        var mover = new Player(1, "mover", moverSink);
        var other = new Player(2, "other", otherSink);
        room.Enter(mover);
        room.Enter(other);
        moverSink.Received.Clear();
        otherSink.Received.Clear();
        return (room, mover, moverSink, other, otherSink);
    }

    [Fact]
    public void Enter_PlacesPlayerWithinSpawnRadius()
    {
        var (_, mover, _, _, _) = Setup();

        var distance = Math.Sqrt(Math.Pow(mover.X - 500, 2) + Math.Pow(mover.Y - 500, 2));

        Assert.True(distance <= 10.0001, $"spawned {distance} away from spawn point");
        Assert.True(mover.LastMoveAtMs >= 0);
    }

    [Fact]
    public void TryMove_WithinSpeedAndBounds_UpdatesAndBroadcastsToOthersOnly()
    {
        var (room, mover, moverSink, _, otherSink) = Setup();
        var targetX = mover.X + 50;   // 1초에 100 까지 허용
        var now = mover.LastMoveAtMs + 1000;

        var accepted = room.TryMove(mover, targetX, mover.Y, now);

        Assert.True(accepted);
        Assert.Equal(targetX, mover.X);
        Assert.Equal(now, mover.LastMoveAtMs);
        var move = Assert.Single(otherSink.Of<SMove>());
        Assert.Equal(1UL, move.PlayerId);
        Assert.Equal(targetX, move.X);
        Assert.False(move.Corrected);
        Assert.Empty(moverSink.Of<SMove>());
    }

    [Fact]
    public void TryMove_TooFast_SendsCorrectionToMoverOnly()
    {
        var (room, mover, moverSink, _, otherSink) = Setup();
        var originalX = mover.X;

        var accepted = room.TryMove(mover, mover.X + 500, mover.Y, mover.LastMoveAtMs + 1000);

        Assert.False(accepted);
        Assert.Equal(originalX, mover.X);
        var correction = Assert.Single(moverSink.Of<SMove>());
        Assert.True(correction.Corrected);
        Assert.Equal(originalX, correction.X);
        Assert.Empty(otherSink.Of<SMove>());
    }

    [Fact]
    public void TryMove_LongIdle_DoesNotAllowTeleport()
    {
        var (room, mover, _, _, _) = Setup();

        // 60초 쉬었어도 MaxMoveDeltaSec(1초) 분량인 100 까지만 허용된다.
        var accepted = room.TryMove(mover, mover.X + 150, mover.Y, mover.LastMoveAtMs + 60_000);

        Assert.False(accepted);
    }

    [Fact]
    public void TryMove_OutOfBounds_IsRejected()
    {
        var (room, mover, moverSink, _, _) = Setup();

        var accepted = room.TryMove(mover, -1f, mover.Y, mover.LastMoveAtMs + 1000);

        Assert.False(accepted);
        Assert.Single(moverSink.Of<SMove>());
    }

    [Fact]
    public void TryMove_NotInRoom_IsIgnored()
    {
        var (room, mover, moverSink, _, _) = Setup();
        room.Leave(mover.Id);
        moverSink.Received.Clear();

        var accepted = room.TryMove(mover, mover.X + 1, mover.Y, mover.LastMoveAtMs + 1000);

        Assert.False(accepted);
        Assert.Empty(moverSink.Received);
    }
}
