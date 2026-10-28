// QuizMMO.TestClient — 서버 검증용 콘솔 클라이언트 (TcpClient + async/await).
//
// 사용법: dotnet run --project tools/QuizMMO.TestClient -- [옵션]
//   --host H --port P          접속 대상 (기본 127.0.0.1:7777)
//   --clients N --duration S   동시 클라이언트 수, 실행 시간(초)
//   --version V                프로토콜 버전 (서버와 다르면 거절 시나리오로 판정)
//   --no-queue                 퀴즈 대기열에 참가하지 않음
//   --quiz                     SQuizQuestion 을 받으면 answer-delay 뒤 무작위 보기로 답함
//   --answer-delay-ms N        퀴즈 답안 지연 (기본 300)
//   --move                     로그인 뒤 스폰 주변을 200ms 간격으로 5번 이동
//   --bad-move                 이동 뒤 맵 밖 좌표를 보내 서버 보정(SMove corrected)을 유도
//   --trade                    0번 클라이언트가 처음 본 다른 플레이어에게 거래 요청, 상대는 수락, 양쪽 골드 1 제안 후 확정
//   --expect-quiz / --expect-trade / --expect-move / --expect-correction   합격 조건에 추가
//   --quiet                    패킷 로그 생략
// 종료 코드 0 = 모든 클라이언트가 합격 조건을 만족. 기본 조건: 로그인 성공 + 자기 채팅 echo + SPong + (대기열 참가 시) 대기열 상태.

using System.Diagnostics;
using System.Net.Sockets;
using Google.Protobuf;
using QuizMMO.Protocol;

var options = Options.Parse(args);
if (options is null) return 2;

var tasks = Enumerable.Range(0, options.Clients).Select(i => RunClientAsync(options, i)).ToArray();
var results = await Task.WhenAll(tasks);

var expectRejection = options.Version != ProtocolInfo.Version;
var passed = 0;
var totalPackets = 0;
foreach (var r in results)
{
    totalPackets += r.Packets;
    bool pass;
    if (expectRejection)
    {
        pass = r.Connected && r.LoginResult == LoginResult.VersionMismatch && r.ServerClosed;
    }
    else
    {
        pass = r.Connected && r.LoginOk && r.GotOwnChat && r.GotPong
               && (!options.JoinQueue || r.GotQueueStatus)
               && (!options.ExpectQuiz || r.GotQuizResult)
               && (!options.ExpectTrade || r.GotTradeCompleted)
               && (!options.ExpectMove || r.GotOtherMove)
               && (!options.ExpectCorrection || r.GotCorrection);
    }
    if (pass) passed++;
}

var criteria = expectRejection ? "expected version rejection" : "login+chat+ping" + (options.JoinQueue ? "+queue" : "")
    + (options.ExpectQuiz ? "+quiz" : "") + (options.ExpectTrade ? "+trade" : "") + (options.ExpectMove ? "+move" : "") + (options.ExpectCorrection ? "+correction" : "");
Console.WriteLine($"summary: {passed}/{options.Clients} clients passed ({criteria}), {totalPackets} packets received, errors={results.Sum(r => r.Errors)}, gold={string.Join(",", results.Select(r => r.Gold))}");
return passed == options.Clients ? 0 : 1;

static async Task<ClientResult> RunClientAsync(Options options, int index)
{
    var result = new ClientResult();
    var state = new ClientState(index, $"tester{index}");

    using var client = new TcpClient();
    try
    {
        await client.ConnectAsync(options.Host, options.Port);
    }
    catch (SocketException e)
    {
        Output.Print(options, index, $"connect to {options.Host}:{options.Port} failed: {e.SocketErrorCode}");
        return result;
    }
    client.NoDelay = true;
    result.Connected = true;
    var stream = client.GetStream();

    await SendAsync(stream, new CLogin { ProtocolVersion = options.Version, Nickname = state.Nickname });

    var inbox = new byte[PacketHeader.MaxPacketSize * 4];
    var inboxLength = 0;
    var chunk = new byte[8192];
    var clock = Stopwatch.StartNew();

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(options.DurationSec));
    while (!cts.IsCancellationRequested)
    {
        // 100ms 마다 깨어나 예약된 행동(이동, 답안, 거래 요청)을 처리한다.
        using var tick = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        tick.CancelAfter(100);
        var received = 0;
        var gotData = false;
        try
        {
            received = await stream.ReadAsync(chunk, tick.Token);
            gotData = true;
        }
        catch (OperationCanceledException)
        {
            if (cts.IsCancellationRequested) break;
        }
        catch (IOException)
        {
            result.ServerClosed = true;
            Output.Print(options, index, "connection dropped");
            break;
        }

        if (gotData)
        {
            if (received == 0)
            {
                result.ServerClosed = true;
                Output.Print(options, index, "server closed the connection");
                break;
            }
            if (inboxLength + received > inbox.Length)
            {
                Output.Print(options, index, "inbox overflow - giving up");
                break;
            }
            Buffer.BlockCopy(chunk, 0, inbox, inboxLength, received);
            inboxLength += received;

            var offset = 0;
            var broken = false;
            while (inboxLength - offset >= PacketHeader.Size)
            {
                var header = PacketHeader.Read(inbox.AsSpan(offset));
                if (!header.IsValid)
                {
                    Output.Print(options, index, $"bad header size {header.TotalSize} - giving up");
                    broken = true;
                    break;
                }
                if (inboxLength - offset < header.TotalSize) break;

                var body = inbox.AsSpan(offset + PacketHeader.Size, header.BodySize);
                result.Packets++;
                if (PacketSerializer.TryDeserialize(header.Id, body, out var message) && message is not null)
                {
                    HandleMessage(options, state, result, message, clock.ElapsedMilliseconds);
                }
                else
                {
                    Output.Print(options, index, $"unknown or malformed packet id {header.Id} ({header.TotalSize} bytes)");
                }
                offset += header.TotalSize;
            }
            if (broken) break;
            if (offset > 0)
            {
                Buffer.BlockCopy(inbox, offset, inbox, 0, inboxLength - offset);
                inboxLength -= offset;
            }
        }

        try
        {
            await RunScheduledActionsAsync(stream, options, state, result, clock.ElapsedMilliseconds);
            foreach (var outgoing in state.Outbox) await SendAsync(stream, outgoing);
            state.Outbox.Clear();
        }
        catch (IOException)
        {
            result.ServerClosed = true;
            break;
        }
    }

    return result;
}

/// <summary>수신 메시지 처리. 응답이 필요하면 state.Outbox 에 넣고, 루프가 보낸다.</summary>
static void HandleMessage(Options options, ClientState state, ClientResult result, IMessage message, long nowMs)
{
    var index = state.Index;
    switch (message)
    {
        case SLogin m:
            result.LoginResult = m.Result;
            result.LoginOk = m.Result == LoginResult.Ok;
            if (result.LoginOk)
            {
                state.MyPlayerId = m.PlayerId;
                state.LoginAtMs = nowMs;
            }
            Output.Print(options, index, $"SLogin result={m.Result} playerId={m.PlayerId} roomId={m.RoomId}");
            break;
        case SInventory m:
            result.Gold = m.Gold;
            Output.Print(options, index, $"SInventory gold={m.Gold} items=[{string.Join(", ", m.Items.Select(i => $"{i.ItemId}x{i.Count}"))}]");
            break;
        case SChat m:
            if (m.Text == state.ChatText) result.GotOwnChat = true;
            Output.Print(options, index, $"SChat [{m.PlayerId} {m.Nickname}] {m.Text}");
            break;
        case SPong m:
            result.GotPong = true;
            Output.Print(options, index, $"SPong rtt={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - m.ClientTimeMs} ms");
            break;
        case SQuizQueueStatus m:
            result.GotQueueStatus = true;
            Output.Print(options, index, $"SQuizQueueStatus state={m.State} waiting={m.Waiting} required={m.Required}");
            break;
        case SRoomChanged m:
            state.X = m.X;
            state.Y = m.Y;
            state.InWorld = m.RoomType == RoomType.World;
            Output.Print(options, index, $"SRoomChanged room={m.RoomId} type={m.RoomType} name={m.RoomName} pos=({m.X:F0},{m.Y:F0})");
            break;
        case SPlayerEnter m:
            if (m.PlayerId != state.MyPlayerId && !state.OtherPlayerIds.Contains(m.PlayerId)) state.OtherPlayerIds.Add(m.PlayerId);
            Output.Print(options, index, $"SPlayerEnter {m.PlayerId} {m.Nickname} pos=({m.X:F0},{m.Y:F0})");
            break;
        case SPlayerLeave m:
            state.OtherPlayerIds.Remove(m.PlayerId);
            Output.Print(options, index, $"SPlayerLeave {m.PlayerId}");
            break;
        case SMove m:
            if (m.Corrected) { result.GotCorrection = true; state.X = m.X; state.Y = m.Y; }
            else if (m.PlayerId != state.MyPlayerId) result.GotOtherMove = true;
            Output.Print(options, index, $"SMove {m.PlayerId} ({m.X:F0},{m.Y:F0}){(m.Corrected ? " CORRECTED" : "")}");
            break;
        case SQuizStart m:
            Output.Print(options, index, $"SQuizStart room={m.RoomId} questions={m.QuestionCount} players={m.PlayerCount} startsIn={m.StartsInSec:F1}s");
            break;
        case SQuizQuestion m:
            Output.Print(options, index, $"SQuizQuestion {m.Index + 1}/{m.Total} '{m.Text}' choices={m.Choices.Count} limit={m.TimeLimitSec:F0}s");
            if (options.Quiz)
            {
                state.PendingAnswerIndex = m.Index;
                state.PendingAnswerChoice = (uint)Random.Shared.Next(m.Choices.Count);
                state.PendingAnswerDueMs = nowMs + options.AnswerDelayMs;
            }
            break;
        case SQuizAnswerAck m:
            Output.Print(options, index, $"SQuizAnswerAck index={m.Index} accepted={m.Accepted}");
            break;
        case SQuizReveal m:
            var mine = m.Scores.FirstOrDefault(s => s.PlayerId == state.MyPlayerId);
            Output.Print(options, index, $"SQuizReveal index={m.Index} correct={m.CorrectChoice} myScore={mine?.Score ?? 0} myCorrect={mine?.Correct ?? false}");
            break;
        case SQuizResult m:
            result.GotQuizResult = true;
            var me = m.Ranking.FirstOrDefault(r => r.PlayerId == state.MyPlayerId);
            Output.Print(options, index, $"SQuizResult players={m.Ranking.Count} myRank={me?.Rank ?? 0} myScore={me?.Score ?? 0} gold+{me?.GoldReward ?? 0} items=[{string.Join(",", me?.ItemRewards.Select(i => i.ItemId) ?? Array.Empty<uint>())}] returnsIn={m.ReturnsInSec:F0}s");
            break;
        case STradeRequested m:
            Output.Print(options, index, $"STradeRequested trade={m.TradeId} from={m.FromPlayerId} {m.FromNickname}");
            if (options.Trade)
            {
                state.TradeId = m.TradeId;
                state.Outbox.Add(new CTradeRespond { TradeId = m.TradeId, Accept = true });
            }
            break;
        case STradeStarted m:
            Output.Print(options, index, $"STradeStarted trade={m.TradeId} partner={m.PartnerPlayerId} {m.PartnerNickname}");
            if (options.Trade)
            {
                state.TradeId = m.TradeId;
                state.Outbox.Add(new CTradeOffer { TradeId = m.TradeId, Gold = 1 });
                state.OfferSent = true;
            }
            break;
        case STradeOffer m:
            Output.Print(options, index, $"STradeOffer trade={m.TradeId} by={m.PlayerId} gold={m.Gold} items={m.Items.Count}");
            if (options.Trade && m.PlayerId != state.MyPlayerId && state.OfferSent && !state.ConfirmSent)
            {
                state.ConfirmSent = true;
                state.Outbox.Add(new CTradeConfirm { TradeId = m.TradeId });
            }
            break;
        case STradeConfirmed m:
            Output.Print(options, index, $"STradeConfirmed trade={m.TradeId} by={m.PlayerId}");
            break;
        case STradeCompleted m:
            result.GotTradeCompleted = true;
            Output.Print(options, index, $"STradeCompleted trade={m.TradeId}");
            break;
        case STradeCancelled m:
            Output.Print(options, index, $"STradeCancelled trade={m.TradeId} reason={m.Reason}");
            break;
        case SError m:
            result.Errors++;
            Output.Print(options, index, $"SError {m.Code}: {m.Detail}");
            break;
        default:
            Output.Print(options, index, $"unhandled message {message.GetType().Name}");
            break;
    }
}

/// <summary>시간 조건이 맞은 예약 행동을 보낸다: 로그인 직후 패킷, 이동, 퀴즈 답안, 거래 요청.</summary>
static async Task RunScheduledActionsAsync(NetworkStream stream, Options options, ClientState state, ClientResult result, long nowMs)
{
    if (!result.LoginOk) return;

    if (!state.SentAfterLogin)
    {
        state.SentAfterLogin = true;
        await SendAsync(stream, new CChat { Text = state.ChatText });
        await SendAsync(stream, new CPing { ClientTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        if (options.JoinQueue) await SendAsync(stream, new CQuizQueueJoin());
    }

    if (options.Move && state.InWorld && state.MovesSent < 5 && nowMs - state.LastMoveAtMs >= 200)
    {
        state.MovesSent++;
        state.LastMoveAtMs = nowMs;
        state.X += 20f;   // 300 units/s 제한 안에서 200ms 당 20 units
        await SendAsync(stream, new CMove { X = state.X, Y = state.Y });
    }
    else if (options.BadMove && state.InWorld && state.MovesSent == 5 && !state.BadMoveSent && nowMs - state.LastMoveAtMs >= 200)
    {
        state.BadMoveSent = true;
        await SendAsync(stream, new CMove { X = -100f, Y = -100f });
    }

    if (options.Quiz && state.PendingAnswerIndex.HasValue && nowMs >= state.PendingAnswerDueMs)
    {
        var answerIndex = state.PendingAnswerIndex.Value;
        state.PendingAnswerIndex = null;
        await SendAsync(stream, new CQuizAnswer { Index = answerIndex, Choice = state.PendingAnswerChoice });
    }

    if (options.Trade && state.Index == 0 && !state.TradeRequested && state.OtherPlayerIds.Count > 0 && nowMs - state.LoginAtMs >= 500)
    {
        state.TradeRequested = true;
        await SendAsync(stream, new CTradeRequest { TargetPlayerId = state.OtherPlayerIds[0] });
    }
}

static async Task SendAsync(NetworkStream stream, IMessage message)
{
    var packet = PacketSerializer.Serialize(message);
    await stream.WriteAsync(packet);
}

sealed class Options
{
    public string Host { get; private set; } = "127.0.0.1";
    public int Port { get; private set; } = 7777;
    public int Clients { get; private set; } = 1;
    public int DurationSec { get; private set; } = 3;
    public uint Version { get; private set; } = ProtocolInfo.Version;
    public bool JoinQueue { get; private set; } = true;
    public bool Quiz { get; private set; }
    public int AnswerDelayMs { get; private set; } = 300;
    public bool Move { get; private set; }
    public bool BadMove { get; private set; }
    public bool Trade { get; private set; }
    public bool ExpectQuiz { get; private set; }
    public bool ExpectTrade { get; private set; }
    public bool ExpectMove { get; private set; }
    public bool ExpectCorrection { get; private set; }
    public bool Quiet { get; private set; }

    public static Options? Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var hasValue = i + 1 < args.Length;
            switch (args[i])
            {
                case "--host" when hasValue: o.Host = args[++i]; break;
                case "--port" when hasValue: o.Port = int.Parse(args[++i]); break;
                case "--clients" when hasValue: o.Clients = Math.Max(1, int.Parse(args[++i])); break;
                case "--duration" when hasValue: o.DurationSec = int.Parse(args[++i]); break;
                case "--version" when hasValue: o.Version = uint.Parse(args[++i]); break;
                case "--answer-delay-ms" when hasValue: o.AnswerDelayMs = int.Parse(args[++i]); break;
                case "--no-queue": o.JoinQueue = false; break;
                case "--quiz": o.Quiz = true; break;
                case "--move": o.Move = true; break;
                case "--bad-move": o.BadMove = true; o.Move = true; break;
                case "--trade": o.Trade = true; break;
                case "--expect-quiz": o.ExpectQuiz = true; o.Quiz = true; break;
                case "--expect-trade": o.ExpectTrade = true; o.Trade = true; break;
                case "--expect-move": o.ExpectMove = true; o.Move = true; break;
                case "--expect-correction": o.ExpectCorrection = true; o.BadMove = true; o.Move = true; break;
                case "--quiet": o.Quiet = true; break;
                case "--help":
                case "-h":
                    PrintUsage();
                    return null;
                default:
                    Console.Error.WriteLine($"unknown argument: {args[i]}");
                    PrintUsage();
                    return null;
            }
        }
        return o;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("usage: QuizMMO.TestClient [--host H] [--port P] [--clients N] [--duration S] [--version V] [--no-queue]");
        Console.WriteLine("                          [--quiz] [--answer-delay-ms N] [--move] [--bad-move] [--trade]");
        Console.WriteLine("                          [--expect-quiz] [--expect-trade] [--expect-move] [--expect-correction] [--quiet]");
    }
}

sealed class ClientState
{
    public ClientState(int index, string nickname)
    {
        Index = index;
        Nickname = nickname;
        ChatText = $"hello from {nickname}";
    }

    public int Index { get; }
    public string Nickname { get; }
    public string ChatText { get; }
    public ulong MyPlayerId;
    public long LoginAtMs;
    public bool SentAfterLogin;
    public bool InWorld;
    public float X, Y;
    public int MovesSent;
    public long LastMoveAtMs;
    public bool BadMoveSent;
    public List<ulong> OtherPlayerIds { get; } = new();
    public uint? PendingAnswerIndex;
    public uint PendingAnswerChoice;
    public long PendingAnswerDueMs;
    public uint TradeId;
    public bool TradeRequested;
    public bool OfferSent;
    public bool ConfirmSent;
    public List<IMessage> Outbox { get; } = new();
}

sealed class ClientResult
{
    public bool Connected;
    public bool LoginOk;
    public LoginResult LoginResult = (LoginResult)255;
    public bool GotOwnChat;
    public bool GotPong;
    public bool GotQueueStatus;
    public bool GotQuizResult;
    public bool GotTradeCompleted;
    public bool GotOtherMove;
    public bool GotCorrection;
    public bool ServerClosed;
    public int Packets;
    public int Errors;
    public long Gold = -1;
}

static class Output
{
    private static readonly object Lock = new();

    public static void Print(Options options, int index, string text)
    {
        if (options.Quiet) return;
        lock (Lock)
        {
            Console.WriteLine($"[client {index}] {text}");
        }
    }
}
