// QuizMMO.TestClient — 서버 검증용 콘솔 클라이언트 (TcpClient + async/await).
//
// 사용법: dotnet run --project tools/QuizMMO.TestClient -- [--host 127.0.0.1] [--port 7777] [--clients 1]
//                                                          [--duration 3] [--version 1] [--no-queue] [--quiet]
// 각 클라이언트: 접속 → CLogin → (SLogin ok) → CChat, CPing, CQuizQueueJoin → duration 동안 수신 출력.
// 종료 코드 0 = 모든 클라이언트가 로그인 성공 + 자기 채팅 echo + SPong + 대기열 상태를 받음.
//   --version 이 서버와 다르면: SLogin(VersionMismatch) 를 받고 서버가 끊어야 0.
//   --no-queue: 대기열 참가와 그 상태 수신 검사를 생략한다.

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
    var pass = expectRejection
        ? r.Connected && r.LoginResult == LoginResult.VersionMismatch && r.ServerClosed
        : r.Connected && r.LoginOk && r.GotOwnChat && r.GotPong && (!options.JoinQueue || r.GotQueueStatus);
    if (pass) passed++;
}

Console.WriteLine($"summary: {passed}/{options.Clients} clients passed ({(expectRejection ? "expected version rejection" : "login+chat+ping+queue")}), {totalPackets} packets received");
return passed == options.Clients ? 0 : 1;

static async Task<ClientResult> RunClientAsync(Options options, int index)
{
    var result = new ClientResult();
    var nickname = $"tester{index}";
    var chatText = $"hello from {nickname}";

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

    await SendAsync(stream, new CLogin { ProtocolVersion = options.Version, Nickname = nickname });

    // 프레이밍: 서버와 같은 PacketHeader. 수신 바이트를 모아 완전한 패킷만 꺼낸다.
    var inbox = new byte[PacketHeader.MaxPacketSize * 4];
    var inboxLength = 0;
    var chunk = new byte[8192];
    var sentAfterLogin = false;

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(options.DurationSec));
    while (!cts.IsCancellationRequested)
    {
        int received;
        try
        {
            received = await stream.ReadAsync(chunk, cts.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (IOException)
        {
            result.ServerClosed = true;
            Output.Print(options, index, "connection dropped");
            break;
        }
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
        while (inboxLength - offset >= PacketHeader.Size)
        {
            var header = PacketHeader.Read(inbox.AsSpan(offset));
            if (!header.IsValid)
            {
                Output.Print(options, index, $"bad header size {header.TotalSize} - giving up");
                cts.Cancel();
                break;
            }
            if (inboxLength - offset < header.TotalSize) break;

            var body = inbox.AsSpan(offset + PacketHeader.Size, header.BodySize);
            result.Packets++;
            if (PacketSerializer.TryDeserialize(header.Id, body, out var message) && message is not null)
            {
                HandleMessage(options, index, result, chatText, message);
            }
            else
            {
                Output.Print(options, index, $"unknown or malformed packet id {header.Id} ({header.TotalSize} bytes)");
            }
            offset += header.TotalSize;
        }
        if (offset > 0)
        {
            Buffer.BlockCopy(inbox, offset, inbox, 0, inboxLength - offset);
            inboxLength -= offset;
        }

        if (result.LoginOk && !sentAfterLogin)
        {
            sentAfterLogin = true;
            try
            {
                await SendAsync(stream, new CChat { Text = chatText });
                await SendAsync(stream, new CPing { ClientTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                if (options.JoinQueue) await SendAsync(stream, new CQuizQueueJoin());
            }
            catch (IOException)
            {
                result.ServerClosed = true;
                break;
            }
        }
    }

    return result;
}

static void HandleMessage(Options options, int index, ClientResult result, string chatText, IMessage message)
{
    switch (message)
    {
        case SLogin m:
            result.LoginResult = m.Result;
            result.LoginOk = m.Result == LoginResult.Ok;
            Output.Print(options, index, $"SLogin result={m.Result} playerId={m.PlayerId} roomId={m.RoomId}");
            break;
        case SChat m:
            if (m.Text == chatText) result.GotOwnChat = true;
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
            Output.Print(options, index, $"SRoomChanged room={m.RoomId} type={m.RoomType} name={m.RoomName}");
            break;
        case SPlayerEnter m:
            Output.Print(options, index, $"SPlayerEnter {m.PlayerId} {m.Nickname}");
            break;
        case SPlayerLeave m:
            Output.Print(options, index, $"SPlayerLeave {m.PlayerId}");
            break;
        default:
            Output.Print(options, index, $"unhandled message {message.GetType().Name}");
            break;
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
                case "--no-queue": o.JoinQueue = false; break;
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
        Console.WriteLine("usage: QuizMMO.TestClient [--host H] [--port P] [--clients N] [--duration S] [--version V] [--no-queue] [--quiet]");
    }
}

sealed class ClientResult
{
    public bool Connected;
    public bool LoginOk;
    public LoginResult LoginResult = (LoginResult)255;
    public bool GotOwnChat;
    public bool GotPong;
    public bool GotQueueStatus;
    public bool ServerClosed;
    public int Packets;
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
