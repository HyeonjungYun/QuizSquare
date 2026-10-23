using Microsoft.Extensions.Logging.Abstractions;
using QuizMMO.Server.Gameplay;
using Xunit;

namespace QuizMMO.Server.Tests;

public class GameLoopTests
{
    [Fact]
    public async Task Post_RunsJobOnTheLogicThread()
    {
        var loop = new GameLoop(tickHz: 50, NullLogger<GameLoop>.Instance);
        var ranOnLogicThread = new TaskCompletionSource<bool>();
        loop.Start();

        loop.Post(() => ranOnLogicThread.SetResult(loop.IsOnLogicThread));

        Assert.True(await ranOnLogicThread.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(loop.IsOnLogicThread);   // 테스트 스레드는 로직 스레드가 아니다
        loop.Stop();
    }

    [Fact]
    public async Task TickHandler_IsCalledWithPositiveDeltaTime()
    {
        var loop = new GameLoop(tickHz: 100, NullLogger<GameLoop>.Instance);
        var firstTick = new TaskCompletionSource<float>();
        loop.TickHandler = dt => firstTick.TrySetResult(dt);

        loop.Start();
        var dt = await firstTick.Task.WaitAsync(TimeSpan.FromSeconds(2));
        loop.Stop();

        Assert.True(dt > 0f);
        Assert.False(loop.IsRunning);
    }

    [Fact]
    public void Stop_RunsJobsPostedBeforeStopping()
    {
        var loop = new GameLoop(tickHz: 20, NullLogger<GameLoop>.Instance);
        var ran = 0;
        loop.Start();
        for (var i = 0; i < 5; i++) loop.Post(() => Interlocked.Increment(ref ran));

        loop.Stop();

        Assert.Equal(5, ran);
    }
}
