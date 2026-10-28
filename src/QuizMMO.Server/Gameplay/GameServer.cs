using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuizMMO.Protocol;
using QuizMMO.Server.Core;
using QuizMMO.Server.Gameplay.Data;
using QuizMMO.Server.Networking;

namespace QuizMMO.Server.Gameplay;

/// <summary>
/// 서버 전체를 조립한다.
///
///   [I/O 스레드: .NET ThreadPool]  ── 소켓 완료, 패킷 파싱, 핸들러(검증) ──▶ GameLoop.Post(작업)
///   [로직 스레드 1개: GameLoop]   ── 작업 실행 + 틱: WorldRoom, QuizRoom들, QuizQueue, TradeManager (락 없음)
///
/// 스레드 모델 결정: 비동기 소켓은 런타임이 IOCP/epoll 위에서 돌린다. 룸 격리는 작업 큐로 얻는다.
/// 20명 퀴즈방도 2000명 월드도 Room 하나다. 게임 데이터(문제, 아이템)와 수치는 파일과 설정에서 읽는다.
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
    private QuestionBank? _questions;
    private ItemCatalog _items = ItemCatalog.Empty();
    private WorldRoom? _world;
    private TradeManager? _trades;
    private readonly Dictionary<uint, QuizRoom> _quizRooms = new();
    private readonly QuizQueue _quizQueue = new();
    private readonly Dictionary<string, Player> _playersByNickname = new(StringComparer.OrdinalIgnoreCase);
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

        // 게임 데이터. 없거나 깨졌으면 여기서 예외 → 호스트가 시작 실패를 보고한다 (조용히 빈 서버로 뜨지 않는다).
        _questions = QuestionBank.LoadFile(ResolveDataPath(_options.Quiz.QuestionsFile));
        _items = ItemCatalog.LoadFile(ResolveDataPath(_options.Quiz.ItemsFile));
        WarnUnknownRewardItems();

        _sessions = new SessionManager(_dispatcher, _options.Net.MaxSessions, _options.Net.RecvBufferBytes,
            _loggerFactory.CreateLogger<SessionManager>());
        _sessions.SessionClosed += (session, _) => _loop.Post(() => OnSessionClosed(session));

        _world = new WorldRoom(_nextRoomId++, _options.Game.WorldName, _options.World);
        _trades = new TradeManager(_options.Trade, _items, _world.Id, _loggerFactory.CreateLogger<TradeManager>());

        _loop.TickHandler = Tick;
        _loop.Start();

        if (!IPAddress.TryParse(_options.Net.BindAddress, out var bind))
        {
            _logger.LogWarning("Server:Net:BindAddress '{Address}' is not a valid IPv4 address - using 0.0.0.0", _options.Net.BindAddress);
            bind = IPAddress.Any;
        }
        _listener = new Listener(_sessions, _options.Net.AcceptBacklog, _loggerFactory.CreateLogger<Listener>());
        _listener.Start(bind, _options.Net.Port);

        _logger.LogInformation(
            "QuizServer started - port {Port}, tick {Tick} Hz, world '{World}' {Width}x{Height}, quiz room size {RoomSize}, {Questions} questions, {Items} items, {Handlers} handlers, press Ctrl+C to stop",
            _options.Net.Port, _options.Game.TickHz, _options.Game.WorldName, _options.World.Width, _options.World.Height,
            _options.Quiz.RoomSize, _questions.Count, _items.Count, _dispatcher.Count);
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

    private static string ResolveDataPath(string relative)
    {
        if (Path.IsPathRooted(relative)) return relative;
        var besideExecutable = Path.Combine(AppContext.BaseDirectory, relative);
        return File.Exists(besideExecutable) ? besideExecutable : Path.GetFullPath(relative);
    }

    private void WarnUnknownRewardItems()
    {
        var ids = _options.Rewards.RankItemIds.Append(_options.Rewards.ParticipationItemId).Where(id => id != 0);
        foreach (var id in ids)
        {
            if (!_items.Contains(id)) _logger.LogWarning("Server:Rewards references item {ItemId} which is not in {File} - it will not be granted", id, _options.Quiz.ItemsFile);
        }
    }

    // ---------------------------------------------------------------- logic thread: helpers

    private static Player? PlayerOf(Session session) => session.Context as Player;

    /// <summary>로그인 전 세션이 게임 명령을 보내면 프로토콜 위반으로 끊는다.</summary>
    private static Player? RequirePlayer(Session session, string action)
    {
        var player = PlayerOf(session);
        if (player is null) session.Disconnect($"{action} before login");
        return player;
    }

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

    private void SendError(Player player, ErrorCode code, string detail) => player.Send(new SError { Code = code, Detail = detail });

    // ---------------------------------------------------------------- logic thread: login / chat / move

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
        var nicknameBytes = Encoding.UTF8.GetByteCount(nickname);
        if (nicknameBytes < ProtocolInfo.NicknameMinBytes || nicknameBytes > ProtocolInfo.NicknameMaxBytes || nickname.Trim().Length == 0)
        {
            session.Send(new SLogin { Result = LoginResult.BadNickname });   // 연결은 유지: 다른 닉네임으로 다시 시도 가능
            return;
        }
        if (_playersByNickname.ContainsKey(nickname))
        {
            session.Send(new SLogin { Result = LoginResult.NicknameTaken });
            return;
        }

        var player = new Player(_nextPlayerId++, nickname, session);
        player.Inventory.AddGold(_options.Game.StartingGold);
        session.Context = player;
        _playersByNickname[nickname] = player;

        session.Send(new SLogin { Result = LoginResult.Ok, PlayerId = player.Id, RoomId = _world!.Id });
        session.Send(player.Inventory.ToMessage());
        _world.Enter(player);
        _logger.LogInformation("[player {PlayerId}] '{Nickname}' logged in from {Remote} (world {Count}) [t{Thread}]",
            player.Id, player.Nickname, session.RemoteAddress, _world.PlayerCount, Environment.CurrentManagedThreadId);
    }

    public void OnChat(Session session, string text)
    {
        var player = RequirePlayer(session, "chat");
        if (player is null) return;

        if (!player.TryChat(Clock.NowMs, _options.Game.ChatCooldownMs))
        {
            SendError(player, ErrorCode.ChatTooFast, "chat cooldown");
            _logger.LogRateLimited(LogLevel.Debug, "chat-cooldown", 1000, "[player {PlayerId}] chat dropped by cooldown", player.Id);
            return;
        }
        FindRoom(player.RoomId)?.Chat(player, text);
    }

    public void OnMove(Session session, float x, float y)
    {
        var player = RequirePlayer(session, "move");
        if (player is null) return;
        if (player.RoomId != _world!.Id) return;   // 퀴즈방에서는 이동이 없다. 조용히 무시
        _world.TryMove(player, x, y, Clock.NowMs);
    }

    // ---------------------------------------------------------------- logic thread: quiz

    public void OnQuizQueueJoin(Session session)
    {
        var player = RequirePlayer(session, "queue join");
        if (player is null) return;
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
        var player = RequirePlayer(session, "queue leave");
        if (player is null) return;
        if (_quizQueue.Leave(player.Id))
        {
            player.Send(new SQuizQueueStatus { State = QueueState.None, Waiting = 0, Required = (uint)_options.Quiz.RoomSize });
            BroadcastQueueStatus();
        }
    }

    public void OnQuizAnswer(Session session, uint questionIndex, uint choice)
    {
        var player = RequirePlayer(session, "quiz answer");
        if (player is null) return;
        if (FindRoom(player.RoomId) is QuizRoom room) room.TryAnswer(player, questionIndex, choice);
        else SendError(player, ErrorCode.NotInQuiz, "not in a quiz room");
    }

    // ---------------------------------------------------------------- logic thread: trade

    public void OnTradeRequest(Session session, ulong targetPlayerId)
    {
        var player = RequirePlayer(session, "trade request");
        if (player is null) return;
        _trades!.Request(player, _world!.FindPlayer(targetPlayerId));
    }

    public void OnTradeRespond(Session session, uint tradeId, bool accept)
    {
        var player = RequirePlayer(session, "trade respond");
        if (player is null) return;
        _trades!.Respond(player, tradeId, accept);
    }

    public void OnTradeOffer(Session session, uint tradeId, long gold, IReadOnlyList<ItemStack> items)
    {
        var player = RequirePlayer(session, "trade offer");
        if (player is null) return;
        _trades!.SetOffer(player, tradeId, gold, items);
    }

    public void OnTradeConfirm(Session session, uint tradeId)
    {
        var player = RequirePlayer(session, "trade confirm");
        if (player is null) return;
        _trades!.Confirm(player, tradeId);
    }

    public void OnTradeCancel(Session session, uint tradeId)
    {
        var player = RequirePlayer(session, "trade cancel");
        if (player is null) return;
        _trades!.Cancel(player, tradeId);
    }

    // ---------------------------------------------------------------- logic thread: lifecycle

    public void OnSessionClosed(Session session)
    {
        var player = PlayerOf(session);
        if (player is null)
        {
            _logger.LogDebug("[session {Id}] closed before login", session.Id);
            return;
        }
        if (_playersByNickname.TryGetValue(player.Nickname, out var registered) && ReferenceEquals(registered, player))
        {
            _playersByNickname.Remove(player.Nickname);
        }
        _trades?.OnPlayerLeft(player, TradeCancelReason.Disconnected);
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

        _trades?.Update(dt);

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
                _logger.LogInformation("status: sessions={Sessions} world={World} queue={Queue} quizRooms={Rooms} trades={Trades}",
                    _sessions?.Count ?? 0, _world?.PlayerCount ?? 0, _quizQueue.Count, _quizRooms.Count, _trades?.ActiveCount ?? 0);
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
            var questions = _questions!.Sample(_options.Quiz.QuestionsPerRound, Random.Shared);
            var room = new QuizRoom(roomId, $"Quiz-{roomId}", _options.Quiz, questions) { OnResult = GrantRewards };

            var status = PacketSerializer.Serialize(new SQuizQueueStatus { State = QueueState.Matched, Waiting = 0, Required = (uint)_options.Quiz.RoomSize });
            foreach (var player in _matched)
            {
                player.Send(status);
                _trades?.OnPlayerLeft(player, TradeCancelReason.LeftRoom);   // 월드를 떠나므로 진행 중 거래는 취소
                MovePlayer(player, room);
            }
            _quizRooms[roomId] = room;
            room.Begin();
            _logger.LogInformation("quiz room {RoomId} created with {Count} players, {Questions} questions", roomId, _matched.Count, questions.Count);
        }
        if (anyMatched) BroadcastQueueStatus();   // 남은 대기자에게 갱신된 인원을 알린다
    }

    /// <summary>퀴즈방이 결과 단계에 들어갈 때. 보상을 계산해 인벤토리에 넣고 바뀐 인벤토리를 보낸다.</summary>
    private void GrantRewards(QuizRoom room, List<QuizRank> ranking)
    {
        var changed = RewardCalculator.Apply(ranking, _options.Rewards, _items, id => room.FindPlayer(id));
        foreach (var player in changed) player.Send(player.Inventory.ToMessage());

        var winner = ranking.FirstOrDefault();
        _logger.LogInformation("quiz room {RoomId} result: {Count} players, winner '{Winner}' with {Score} points, rewards granted to {Rewarded}",
            room.Id, ranking.Count, winner?.Nickname ?? "-", winner?.Score ?? 0, changed.Count);
    }

    private void FinishQuizRoom(uint roomId)
    {
        if (!_quizRooms.Remove(roomId, out var room)) return;
        var players = room.Players();
        foreach (var player in players) MovePlayer(player, _world!);
        _logger.LogInformation("quiz room {RoomId} finished - {Count} players returned to '{World}'", roomId, players.Count, _world!.Name);
    }
}
