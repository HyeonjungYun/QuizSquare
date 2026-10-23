using System.Diagnostics;

namespace QuizMMO.Server.Core;

/// <summary>시계 유틸. 타임아웃·레이트리밋은 단조 시계, 클라이언트와 주고받는 값은 유닉스 시각.</summary>
public static class Clock
{
    private static readonly Stopwatch Monotonic = Stopwatch.StartNew();

    /// <summary>단조 증가 ms. 뒤로 가지 않는다.</summary>
    public static long NowMs => Monotonic.ElapsedMilliseconds;

    /// <summary>유닉스 시각(ms). 핑/퐁 등 클라이언트와 공유하는 타임스탬프.</summary>
    public static long UnixTimeMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
