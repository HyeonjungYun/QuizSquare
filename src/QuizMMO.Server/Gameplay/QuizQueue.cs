namespace QuizMMO.Server.Gameplay;

/// <summary>
/// 퀴즈 참가 대기열. 선착순(FIFO). <b>로직 스레드 전용.</b>
/// <see cref="Player.InQuizQueue"/> 플래그를 이 클래스가 관리한다 — 밖에서 직접 바꾸지 않는다.
/// 확장 지점: MMR 매칭은 브리프 Out of scope. 필요해지면 TryTake 의 선택 규칙만 바꾼다.
/// </summary>
public sealed class QuizQueue
{
    private readonly List<Player> _waiting = new();

    public int Count => _waiting.Count;
    public IReadOnlyList<Player> Waiting => _waiting;

    /// <summary>이미 대기 중이면 false.</summary>
    public bool Join(Player player)
    {
        if (player.InQuizQueue) return false;
        player.InQuizQueue = true;
        _waiting.Add(player);
        return true;
    }

    /// <summary>대기 중이 아니면 false.</summary>
    public bool Leave(ulong playerId)
    {
        var index = _waiting.FindIndex(p => p.Id == playerId);
        if (index < 0) return false;
        _waiting[index].InQuizQueue = false;
        _waiting.RemoveAt(index);
        return true;
    }

    /// <summary>count 명이 모였으면 앞에서부터 꺼내 담고 true. 모자라면 비우고 false.</summary>
    public bool TryTake(int count, List<Player> into)
    {
        into.Clear();
        if (count <= 0 || _waiting.Count < count) return false;
        into.AddRange(_waiting.GetRange(0, count));
        _waiting.RemoveRange(0, count);
        foreach (var player in into) player.InQuizQueue = false;
        return true;
    }
}
