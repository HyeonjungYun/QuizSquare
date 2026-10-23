using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuizMMO.Protocol;
using QuizMMO.Server.Core;
using QuizMMO.Server.Networking;

namespace QuizMMO.Server.Gameplay;

/// <summary>
/// 서버 전체를 조립한다.
///
///   [I/O 스레드: .NET ThreadPool]  ── 소켓 완료, 패킷 파싱, 핸들러(검증) ──▶ GameLoop.Post(작업)
///   [로직 스레드 1개: GameLoop]   ── 작업 실행 + 틱: WorldRoom, QuizRoom들, QuizQueue (락 없음)
///
/// 스레드 모델 결정: 비동기 소켓은 런타임이 IOCP/epoll 위에서 돌린다. 룸 격리는 작업 큐로 얻는다 (C++ 버전과 같다).
/// 20명 퀴즈방도 2000명 월드도 Room 하나다.
///
/// On* 메서드는 로직 스레드 전용이다. GameHandlers 가 Loop.Post 로 넘겨 부른다.
/// </summary>
public sealed class GameServer : IHostedService
{
    private readonly ServerOptions _options;
    private readonly ILogger<GameServer> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly PacketDispatcher _dispatcher;
    private readonly GameLoop _loop;

    private SessionManager? _sessions;
    private Listener? _listener;
    private int _stopped;

    // ---- 로직 스레드 전용 상태 ----
    private WorldRoom? _world;
    private readonly Dictionary<uint, QuizRoom> _quizRooms = new();
    private readonly QuizQueue _quizQueue = new();
    private readonly List<uint> _finishedRooms = new();     // Tick 이 재사용하는 버퍼
    private readonly List<Player> _matched = new();         // TryMatchQuiz 가 재사용하는 버퍼
    private uint _nextRoomId = 1;
    private ulong _nextPlayerId = 1;
    private float _matchTimer;
    private float _statusTimer;

    public GameServer(IOptions<ServerOptions> options, ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<GameServer>();
        _options.Normalize(_logger);
        _dispatcher = new PacketDispatcher(loggerFactory.CreateLogger<PacketDispatcher>());
        _loop = new GameLoop(_options.Game.TickHz, loggerFactory.CreateLogger<GameLoop>());
    }

    public GameLoop Loop => _loop;

    // ---------------------------------------------------------------- IHostedService

    public Task StartAsync(CancellationToken cancellationToken)
    {
        GameHandlers.Register(this, _dispatcher);

        _sessions = new SessionManager(_dispatcher, _options.Net.MaxSessions, _options.Net.RecvBufferBytes,
            _loggerFactory.CreateLogger<SessionManager>());
        _sessions.SessionClosed += (session, _) => _loop.Post(() => OnSessionClosed(session));

        _world = new WorldRoom(_nextRoomId++, _options.Game.WorldName);

        _loop.TickHandler = Tick;
        _loop.Start();

        if (!IPAddress.TryParse(_options.Net.BindAddress, out var bind))
        {
            _logger.LogWarning("Server:Net:BindAddress '{Address}' is not a valid IPv4 address - using 0.0.0.0", _options.Net.BindAddress);
            bind = IPAddress.Any;
        }
        _listener = new Listener(_sessions, _options.Net.AcceptBacklog, _loggerFactory.CreateLogger<Listener>());
        _listener.Start(bind, _options.Net.Port);

        _logger.LogInformation("QuizServer started - port {Port}, tick {Tick} Hz, world '{World}', quiz room size {RoomSize}, {Handlers} handlers, press Ctrl+C to stop",
            _options.Net.Port, _options.Game.TickHz, _options.Game.WorldName, _options.Quiz.RoomSize, _dispatcher.Count);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Stop();
        return Task.CompletedTask;
    }

    /// <summary>리스너 → 세션 → 로직 스레드 순으로 멈춘다. 두 번 불러도 안전.</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1) return;
        _logger.LogInformation("QuizServer stopping");
        _listener?.Stop();
        _sessions?.DisconnectAll("server shutdown");
        _loop.Stop();   // 남은 퇴장 작업을 실행하고 멈춘다
        _logger.LogInformation("QuizServer stopped");
    }

    // ---------------------------------------------------------------- logic thread

    private static Player? PlayerOf(Session session) => session.Context as Player;

    private Room? FindRoom(uint roomId)
    {
        if (roomId == 0) return null;
        if (_world is not null && _world.Id == roomId) return _world;
        return _quizRooms.TryGetValue(roomId, out var room) ? room : null;
    }

    private void MovePlayer(Player player, Room to)
    {
        FindRoom(player.RoomId)?.Leave(player.Id);
        to.Enter(player);
    }

    public void OnLogin(Session session, uint protocolVersion, string nickname)
    {
        if (!session.IsConnected) return;

        if (PlayerOf(session) is not null)
        {
            session.Send(new SLogin { Result = LoginResult.AlreadyLoggedIn });
            return;
        }
        if (protocolVersion != ProtocolInfo.Version)
        {
            _logger.LogWarning("[session {Id}] protocol version {Client} rejected (server {Server})", session.Id, protocolVersion, ProtocolInfo.Version);
            session.Send(new SLogin { Result = LoginResult.VersionMismatch });
            session.DisconnectAfterFlush("protocol version mismatch");
            return;
        }
        var nicknameBytes = System.Text.Encoding.UTF8.GetByteCount(nickname);
        if (nicknameBytes < ProtocolInfo.NicknameMinBytes || nicknameBytes > ProtocolInfo.NicknameMaxBytes)
        {
            session.Send(new SLogin { Result = LoginResult.BadNickname });
            session.DisconnectAfterFlush("bad nickname");
            return;
        }

        var player = new Player(_nextPlayerId++, nickname, session);
        session.Context = player;

        session.Send(new SLogin { Result = LoginResult.Ok, PlayerId = player.Id, RoomId = _world!.Id });
        _world.Enter(player);
        _logger.LogInformation("[player {PlayerId}] '{Nickname}' logged in from {Remote} (world {Count}) [t{Thread}]",
            player.Id, player.Nickname, session.RemoteAddress, _world.PlayerCount, Environment.CurrentManagedThreadId);
    }

    public void OnChat(Session session, string text)
    {
        var player = PlayerOf(session);
        if (player is null)
        {
            session.Disconnect("chat before login");
            return;
        }
        FindRoom(player.RoomId)?.Chat(player, text);
    }

    public void OnQuizQueueJoin(Session session)
    {
        var player = PlayerOf(session);
        if (player is null)
        {
            session.Disconnect("queue join before login");
            return;
        }
        if (player.RoomId != _world!.Id)
        {
            // 퀴즈방 안에서는 다시 줄을 설 수 없다.
            player.Send(new SQuizQueueStatus { State = QueueState.None, Waiting = 0, Required = (uint)_options.Quiz.RoomSize });
            return;
        }
        _quizQueue.Join(player);   // 이미 대기 중이어도 현재 상태를 다시 알려준다
        BroadcastQueueStatus();
    }

    public void OnQuizQueueLeave(Session session)
    {
        var player = PlayerOf(session);
        if (player is null)
        {
            session.Disconnect("queue leave before login");
            return;
        }
        if (_quizQueue.Leave(player.Id))
        {
            player.Send(new SQuizQueueStatus { State = QueueState.None, Waiting = 0, Required = (uint)_options.Quiz.RoomSize });
            BroadcastQueueStatus();
        }
    }

    public void OnSessionClosed(Session session)
    {
        var player = PlayerOf(session);
        if (player is null)
        {
            _logger.LogDebug("[session {Id}] closed before login", session.Id);
            return;
        }
        if (player.InQuizQueue && _quizQueue.Leave(player.Id)) BroadcastQueueStatus();
        FindRoom(player.RoomId)?.Leave(player.Id);
        _logger.LogInformation("[player {PlayerId}] '{Nickname}' left (world {Count})", player.Id, player.Nickname, _world!.PlayerCount);
    }

    private void BroadcastQueueStatus()
    {
        if (_quizQueue.Count == 0) return;
        var status = PacketSerializer.Serialize(new SQuizQueueStatus
        {
            State = QueueState.Waiting,
            Waiting = (uint)_quizQueue.Count,
            Required = (uint)_options.Quiz.RoomSize,
        });
        foreach (var player in _quizQueue.Waiting) player.Send(status);
    }

    private void Tick(float dt)
    {
        _matchTimer += dt;
        if (_matchTimer >= _options.Quiz.MatchCheckIntervalSec)
        {
            _matchTimer = 0f;
            TryMatchQuiz();
        }

        _finishedRooms.Clear();
        foreach (var (id, room) in _quizRooms)
        {
            room.Update(dt);
            if (room.IsFinished || room.PlayerCount == 0) _finishedRooms.Add(id);
        }
        foreach (var id in _finishedRooms) FinishQuizRoom(id);

        if (_options.StatusIntervalSec > 0)
        {
            _statusTimer += dt;
            if (_statusTimer >= _options.StatusIntervalSec)
            {
                _statusTimer = 0f;
                _logger.LogInformation("status: sessions={Sessions} world={World} queue={Queue} quizRooms={Rooms}",
                    _sessions?.Count ?? 0, _world?.PlayerCount ?? 0, _quizQueue.Count, _quizRooms.Count);
            }
        }
    }

    private void TryMatchQuiz()
    {
        var anyMatched = false;
        while (_quizQueue.TryTake(_options.Quiz.RoomSize, _matched))
        {
            anyMatched = true;
            var roomId = _nextRoomId++;
            var room = new QuizRoom(roomId, $"Quiz-{roomId}", (float)_options.Quiz.RoomDurationSec);
            var status = PacketSerializer.Serialize(new SQuizQueueStatus { State = QueueState.Matched, Waiting = 0, Required = (uint)_options.Quiz.RoomSize });
            foreach (var player in _matched)
            {
                player.Send(status);
                MovePlayer(player, room);
            }
            _logger.LogInformation("quiz room {RoomId} created with {Count} players ({Duration} s)", roomId, _matched.Count, _options.Quiz.RoomDurationSec);
            _quizRooms[roomId] = room;
        }
        if (anyMatched) BroadcastQueueStatus();   // 남은 대기자에게 갱신된 인원을 알린다
    }

    private void FinishQuizRoom(uint roomId)
    {
        if (!_quizRooms.Remove(roomId, out var room)) return;
        var players = room.Players();
        foreach (var player in players) MovePlayer(player, _world!);
        _logger.LogInformation("quiz room {RoomId} finished - {Count} players returned to '{World}'", roomId, players.Count, _world!.Name);
    }
}
