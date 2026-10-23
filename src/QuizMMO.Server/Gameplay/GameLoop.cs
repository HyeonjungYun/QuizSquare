using System.Diagnostics;
using Microsoft.Extensions.Logging;
using QuizMMO.Server.Core;

namespace QuizMMO.Server.Gameplay;

/// <summary>
/// 게임 로직 스레드. 모든 룸/플레이어/대기열 상태는 이 스레드만 만진다.
/// I/O 스레드는 <see cref="Post"/> 로 작업을 넘기고, 이 스레드가 순서대로 실행한 뒤 틱 간격마다 <see cref="TickHandler"/> 를 부른다.
/// 그래서 게임 코드에는 락이 없다. 룸이 많아져 한 스레드가 모자라면 룸 묶음별로 GameLoop 를 늘린다.
/// Start/Stop 은 호스트 스레드에서. Stop 을 로직 스레드 안에서 부르면 교착한다.
/// </summary>
public sealed class GameLoop
{
    private readonly TimeSpan _tickInterval;
    private readonly ILogger _logger;
    private readonly JobQueue _queue = new();
    private List<Action> _pending = new();   // Drain 이 재사용하는 버퍼
    private Thread? _thread;
    private volatile bool _running;
    private int _threadId = -1;

    public GameLoop(int tickHz, ILogger logger)
    {
        _tickInterval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, tickHz));
        _logger = logger;
    }

    /// <summary>틱마다 호출된다 (dt 는 초). Start 전에 설정한다.</summary>
    public Action<float>? TickHandler { get; set; }

    public bool IsRunning => _running;
    public bool IsOnLogicThread => Environment.CurrentManagedThreadId == _threadId;

    public void Post(Action job) => _queue.Post(job);

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Run) { Name = "GameLoop", IsBackground = true };
        _thread.Start();
        _logger.LogInformation("game loop started (tick {Ms} ms)", (long)_tickInterval.TotalMilliseconds);
    }

    /// <summary>남은 작업을 한 번 더 비운 뒤 스레드를 끝낸다. 두 번 불러도 안전.</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _queue.Wake();
        _thread?.Join();
        _logger.LogInformation("game loop stopped");
    }

    private void Run()
    {
        _threadId = Environment.CurrentManagedThreadId;
        _logger.LogInformation("game loop running on thread t{Thread}", _threadId);

        var clock = Stopwatch.StartNew();
        var lastTick = clock.Elapsed;
        var nextTick = lastTick + _tickInterval;

        while (_running)
        {
            var now = clock.Elapsed;
            var wait = nextTick > now ? nextTick - now : TimeSpan.Zero;
            _queue.Drain(ref _pending, wait);
            RunPending();

            var afterJobs = clock.Elapsed;
            if (afterJobs >= nextTick)
            {
                var dt = (float)(afterJobs - lastTick).TotalSeconds;
                lastTick = afterJobs;
                RunTick(dt);
                nextTick += _tickInterval;
                // 틱이 심하게 밀렸으면 따라잡기를 포기하고 지금부터 다시 센다 (연쇄 지연 방지).
                if (nextTick < afterJobs) nextTick = afterJobs + _tickInterval;
            }
        }

        // 종료 직전: 쌓인 작업(퇴장 처리 등)을 한 번 더 비운다.
        _queue.Drain(ref _pending, TimeSpan.Zero);
        RunPending();
    }

    private void RunPending()
    {
        foreach (var job in _pending)
        {
            try
            {
                job();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "game loop job threw");
            }
        }
        _pending.Clear();
    }

    private void RunTick(float dt)
    {
        try
        {
            TickHandler?.Invoke(dt);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "game loop tick threw");
        }
    }
}
