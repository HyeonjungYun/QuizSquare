using Google.Protobuf;
using Microsoft.Extensions.Logging;
using QuizMMO.Protocol;
using QuizMMO.Server.Core;

namespace QuizMMO.Server.Networking;

/// <summary>패킷 핸들러. I/O 스레드에서 호출된다. 본문이 의미상 잘못되었으면 false → 세션이 끊긴다.</summary>
public delegate bool PacketHandler(Session session, IMessage message);

/// <summary>
/// 패킷 ID → 핸들러 표. Register 는 서버 시작 전에만, Dispatch 는 어느 I/O 스레드에서든.
/// 핸들러는 검증만 하고 게임 상태가 필요한 일은 GameLoop 에 작업으로 넘긴다 — 여기서 룸을 직접 만지지 않는다.
/// </summary>
public sealed class PacketDispatcher
{
    private readonly Dictionary<ushort, PacketHandler> _handlers = new();
    private readonly ILogger _logger;

    public PacketDispatcher(ILogger logger)
    {
        _logger = logger;
    }

    public int Count => _handlers.Count;

    /// <summary>메시지 타입으로 핸들러를 등록한다. 시작 전에만 호출 (스레드 안전하지 않다).</summary>
    public void Register<T>(Func<Session, T, bool> handler) where T : class, IMessage<T>
    {
        var id = PacketRegistry.GetId<T>();
        _handlers[id] = (session, message) => handler(session, (T)message);
    }

    /// <summary>미등록 id, 깨진 본문, 핸들러가 false → false (레이트리밋 로그 포함). 호출자는 세션을 끊는다.</summary>
    public bool Dispatch(Session session, ushort id, ReadOnlySpan<byte> body)
    {
        if (!_handlers.TryGetValue(id, out var handler))
        {
            _logger.LogRateLimited(LogLevel.Warning, "dispatch-unknown", 1000,
                "[session {Id}] unknown packet id {PacketId} ({Bytes} bytes) from {Remote}", session.Id, id, body.Length, session.RemoteAddress);
            return false;
        }

        if (!PacketSerializer.TryDeserialize(id, body, out var message) || message is null)
        {
            _logger.LogRateLimited(LogLevel.Warning, "dispatch-malformed", 1000,
                "[session {Id}] malformed packet id {PacketId} ({Bytes} bytes) from {Remote}", session.Id, id, body.Length, session.RemoteAddress);
            return false;
        }

        if (!handler(session, message))
        {
            _logger.LogRateLimited(LogLevel.Warning, "dispatch-rejected", 1000,
                "[session {Id}] packet id {PacketId} rejected by handler from {Remote}", session.Id, id, session.RemoteAddress);
            return false;
        }
        return true;
    }
}
