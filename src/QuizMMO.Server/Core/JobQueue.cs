namespace QuizMMO.Server.Core;

/// <summary>
/// 여러 스레드가 Post 하고 한 소유자 스레드가 Drain 하는 작업 큐 (C++ 버전의 JobQueue 와 같은 역할).
/// Drain 은 내부 리스트를 호출자의 리스트와 맞바꾸므로 소비자는 락 밖에서 작업을 실행하고,
/// 리스트를 재사용하면 정상 상태에서 추가 할당이 없다.
/// </summary>
public sealed class JobQueue
{
    private readonly object _lock = new();
    private List<Action> _jobs = new();

    /// <summary>어느 스레드에서든 호출 가능.</summary>
    public void Post(Action job)
    {
        lock (_lock)
        {
            _jobs.Add(job);
            Monitor.Pulse(_lock);
        }
    }

    /// <summary>작업이 생기거나 timeout 이 지날 때까지 기다린 뒤, 쌓인 작업을 모두 into 로 옮긴다 (swap).</summary>
    public int Drain(ref List<Action> into, TimeSpan timeout)
    {
        into.Clear();
        lock (_lock)
        {
            if (_jobs.Count == 0 && timeout > TimeSpan.Zero)
            {
                Monitor.Wait(_lock, timeout);
            }
            (into, _jobs) = (_jobs, into);
        }
        return into.Count;
    }

    /// <summary>대기 중인 소비자를 깨운다 (종료 시).</summary>
    public void Wake()
    {
        lock (_lock)
        {
            Monitor.PulseAll(_lock);
        }
    }

    public int Count
    {
        get
        {
            lock (_lock) return _jobs.Count;
        }
    }
}
