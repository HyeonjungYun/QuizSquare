using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace QuizMMO.Server.Core;

/// <summary>
/// 같은 key 로 intervalMs 당 최대 1회만 기록한다. 네트워크 오류처럼 폭주할 수 있는 로그는 반드시 이걸 쓴다.
/// 어느 스레드에서든 안전.
/// </summary>
public static class RateLimitedLog
{
    private static readonly ConcurrentDictionary<string, long> LastByKey = new();

    /// <summary>key 로 지금 기록해도 되는지. 허용되면 마지막 기록 시각을 갱신한다.</summary>
    public static bool Allow(string key, long intervalMs)
    {
        var now = Clock.NowMs;
        var previous = LastByKey.GetOrAdd(key, long.MinValue);
        if (previous != long.MinValue && now - previous < intervalMs) return false;
        return LastByKey.TryUpdate(key, now, previous);
    }
}

public static class LoggerRateLimitExtensions
{
    /// <summary>같은 호출 지점(key)에서 intervalMs 당 최대 1회만 기록한다.</summary>
    public static void LogRateLimited(this ILogger logger, LogLevel level, string key, long intervalMs, string message, params object?[] args)
    {
        if (!logger.IsEnabled(level)) return;
        if (!RateLimitedLog.Allow(key, intervalMs)) return;
        logger.Log(level, message, args);
    }
}
