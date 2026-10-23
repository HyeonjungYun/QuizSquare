using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using QuizMMO.Server.Core;

namespace QuizMMO.Server.Networking;

/// <summary>살아 있는 세션의 소유자. 수락된 소켓으로 세션을 만들고, 끊기면 표에서 지운다. 모든 메서드는 스레드 안전.</summary>
public sealed class SessionManager
{
    private readonly PacketDispatcher _dispatcher;
    private readonly int _maxSessions;
    private readonly int _recvBufferBytes;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<ulong, Session> _sessions = new();
    private long _nextId;

    public SessionManager(PacketDispatcher dispatcher, int maxSessions, int recvBufferBytes, ILogger logger)
    {
        _dispatcher = dispatcher;
        _maxSessions = maxSessions;
        _recvBufferBytes = recvBufferBytes;
        _logger = logger;
    }

    public int Count => _sessions.Count;

    /// <summary>세션이 표에서 제거된 직후 (I/O 스레드). 게임 계층이 퇴장 처리를 로직 스레드로 넘긴다.</summary>
    public event Action<Session, string>? SessionClosed;

    /// <summary>수락된 소켓을 세션으로 감싸 수신을 시작한다. 정원 초과면 소켓을 닫고 null. 소켓 소유권은 항상 이쪽으로 넘어온다.</summary>
    public Session? Accept(Socket socket)
    {
        if (_sessions.Count >= _maxSessions)
        {
            _logger.LogRateLimited(LogLevel.Warning, "session-limit", 1000, "session limit {Max} reached - rejecting a connection", _maxSessions);
            socket.Close();
            return null;
        }

        var id = (ulong)Interlocked.Increment(ref _nextId);
        var session = new Session(socket, id, _dispatcher, _recvBufferBytes, _logger);
        _sessions[id] = session;
        session.Disconnected += OnDisconnected;
        session.Start();

        _logger.LogDebug("[session {Id}] connected from {Remote} ({Online} online) [t{Thread}]", id, session.RemoteAddress, _sessions.Count, Environment.CurrentManagedThreadId);
        return session;
    }

    public void DisconnectAll(string reason)
    {
        foreach (var session in _sessions.Values.ToArray()) session.Disconnect(reason);
    }

    private void OnDisconnected(Session session, string reason)
    {
        _sessions.TryRemove(session.Id, out _);
        SessionClosed?.Invoke(session, reason);
    }
}
