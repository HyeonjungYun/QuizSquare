using System.Net.Sockets;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using QuizMMO.Protocol;
using QuizMMO.Server.Core;

namespace QuizMMO.Server.Networking;

/// <summary>
/// TCP 연결 하나. 소켓, 수신 버퍼, 송신 큐를 소유한다.
///
/// 비동기 모델: <see cref="SocketAsyncEventArgs"/> 를 수신용 1개, 송신용 1개 재사용한다.
/// ReceiveAsync/SendAsync 는 Windows 에서 IOCP, Linux 에서 epoll 위에서 돌고 완료는 .NET ThreadPool(I/O 스레드)에서 온다.
/// C++ 버전의 WSARecv/WSASend + OverlappedEx 와 1:1 로 대응하며, 완료 포트 코드만 런타임이 대신한다.
///
/// 스레드 규칙:
/// - 수신 완료 처리는 한 번에 하나만 돈다 (수신 1개 규칙). 송신도 한 번에 SendAsync 하나만 진행하고 나머지는 큐에 모아 gather 로 보낸다.
/// - <see cref="Send(byte[])"/>, <see cref="Disconnect"/> 는 어느 스레드에서든 호출 가능 (락·원자값으로 보호).
/// - <see cref="Context"/> 는 로직 스레드가 쓰고 어디서든 읽는다.
/// </summary>
public sealed class Session : IMessageSink
{
    private readonly Socket _socket;
    private readonly PacketDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly RecvBuffer _recvBuffer;
    private readonly SocketAsyncEventArgs _recvArgs = new();
    private readonly SocketAsyncEventArgs _sendArgs = new();

    private readonly object _sendLock = new();
    private readonly Queue<byte[]> _sendQueue = new();
    private readonly List<ArraySegment<byte>> _sending = new();   // 진행 중인 SendAsync 가 참조하는 버퍼들
    private bool _sendInFlight;
    private bool _closeAfterFlush;
    private string _closeReason = "";

    private int _connected = 1;
    private object? _context;

    public Session(Socket socket, ulong id, PacketDispatcher dispatcher, int recvBufferBytes, ILogger logger)
    {
        _socket = socket;
        Id = id;
        _dispatcher = dispatcher;
        _logger = logger;
        _recvBuffer = new RecvBuffer(recvBufferBytes);
        RemoteAddress = socket.RemoteEndPoint?.ToString() ?? "unknown";

        // 비동기 완료(pending == true)일 때만 Completed 가 불린다. 동기 완료는 Post* 루프가 직접 처리한다.
        _recvArgs.Completed += (_, args) =>
        {
            if (HandleRecv(args)) PostRecv();
        };
        _sendArgs.Completed += (_, args) =>
        {
            if (HandleSend(args)) PostSend();
        };
    }

    public ulong Id { get; }
    public string RemoteAddress { get; }
    public bool IsConnected => Volatile.Read(ref _connected) == 1;

    /// <summary>상위 계층 데이터(gameplay 의 Player). networking 은 구체 타입을 모른다.</summary>
    public object? Context
    {
        get => Volatile.Read(ref _context);
        set => Volatile.Write(ref _context, value);
    }

    /// <summary>끊길 때 정확히 한 번, 어느 I/O 스레드에서든 발생한다. SessionManager 가 구독한다.</summary>
    public event Action<Session, string>? Disconnected;

    /// <summary>등록 뒤 한 번 호출한다. 첫 ReceiveAsync 를 건다.</summary>
    public void Start() => PostRecv();

    // ---------------------------------------------------------------- recv

    private void PostRecv()
    {
        while (IsConnected)
        {
            if (_recvBuffer.FreeSize == 0)
            {
                // MaxPacketSize < 버퍼 크기이고 Clean() 이 항상 선행하므로 도달하지 않아야 한다.
                Disconnect("recv buffer full");
                return;
            }

            _recvArgs.SetBuffer(_recvBuffer.Buffer, _recvBuffer.WritePos, _recvBuffer.FreeSize);
            bool pending;
            try
            {
                pending = _socket.ReceiveAsync(_recvArgs);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException e)
            {
                Disconnect($"ReceiveAsync failed: {e.SocketErrorCode}");
                return;
            }

            if (pending) return;                  // 완료되면 Completed 이벤트가 HandleRecv 를 부른다
            if (!HandleRecv(_recvArgs)) return;   // 동기 완료: 바로 처리하고 다시 건다 (재귀 대신 루프)
        }
    }

    /// <summary>수신 완료 처리. 계속 받아도 되면 true.</summary>
    private bool HandleRecv(SocketAsyncEventArgs args)
    {
        if (!IsConnected) return false;

        if (args.SocketError != SocketError.Success)
        {
            _logger.LogRateLimited(LogLevel.Debug, "recv-error", 1000, "[session {Id}] recv completed with {Error}", Id, args.SocketError);
            Disconnect("recv error");
            return false;
        }
        if (args.BytesTransferred == 0)
        {
            Disconnect("peer closed");
            return false;
        }
        if (!_recvBuffer.OnWrite(args.BytesTransferred))
        {
            Disconnect("recv overflow");
            return false;
        }

        if (!ProcessPackets()) return false;
        _recvBuffer.Clean();
        return true;
    }

    /// <summary>수신 버퍼에서 완전한 패킷을 모두 꺼내 디스패치한다. 끊겼으면 false.</summary>
    private bool ProcessPackets()
    {
        while (_recvBuffer.DataSize >= PacketHeader.Size)
        {
            var header = PacketHeader.Read(_recvBuffer.Data);
            if (!header.IsValid)
            {
                _logger.LogRateLimited(LogLevel.Warning, "bad-size", 1000,
                    "[session {Id}] bad packet size {Size} (id {PacketId}) from {Remote}", Id, header.TotalSize, header.Id, RemoteAddress);
                Disconnect("bad packet size");
                return false;
            }
            if (_recvBuffer.DataSize < header.TotalSize) break;   // 나머지는 다음 수신에서

            var body = _recvBuffer.Data.Slice(PacketHeader.Size, header.BodySize);
            if (!_dispatcher.Dispatch(this, header.Id, body))
            {
                Disconnect("malformed packet");
                return false;
            }
            if (!IsConnected) return false;   // 핸들러가 끊었다
            _recvBuffer.OnRead(header.TotalSize);
        }
        return true;
    }

    // ---------------------------------------------------------------- send

    public void Send(byte[] packet)
    {
        if (packet.Length == 0 || !IsConnected) return;

        bool start;
        lock (_sendLock)
        {
            _sendQueue.Enqueue(packet);
            start = !_sendInFlight;
            if (start) _sendInFlight = true;
        }
        if (start) PostSend();
    }

    public void Send(IMessage message) => Send(PacketSerializer.Serialize(message));

    private void PostSend()
    {
        // 선행 조건: _sendInFlight == true 이고, 이 스레드만 _sending 을 만진다.
        while (true)
        {
            lock (_sendLock)
            {
                _sending.Clear();
                while (_sendQueue.Count > 0) _sending.Add(new ArraySegment<byte>(_sendQueue.Dequeue()));
                if (_sending.Count == 0)
                {
                    _sendInFlight = false;
                    return;
                }
            }

            // 리스트 내용이 바뀌었으므로 다시 지정한다 (SAEA 가 내부에 복사해 둔다).
            _sendArgs.BufferList = null;
            _sendArgs.BufferList = _sending;

            bool pending;
            try
            {
                pending = _socket.SendAsync(_sendArgs);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException e)
            {
                lock (_sendLock) _sendInFlight = false;
                Disconnect($"SendAsync failed: {e.SocketErrorCode}");
                return;
            }

            if (pending) return;
            if (!HandleSend(_sendArgs)) return;   // 동기 완료: 다음 묶음이 있으면 루프가 이어 보낸다
        }
    }

    /// <summary>송신 완료 처리. 큐에 더 보낼 것이 있으면 true (호출자가 PostSend 를 이어간다).</summary>
    private bool HandleSend(SocketAsyncEventArgs args)
    {
        if (args.SocketError != SocketError.Success || args.BytesTransferred == 0)
        {
            lock (_sendLock) _sendInFlight = false;
            if (IsConnected)
            {
                _logger.LogRateLimited(LogLevel.Debug, "send-error", 1000, "[session {Id}] send completed with {Error}", Id, args.SocketError);
            }
            Disconnect("send error");
            return false;
        }

        var expected = 0;
        foreach (var segment in _sending) expected += segment.Count;
        if (args.BytesTransferred != expected)
        {
            // 스트림 소켓의 비동기 송신은 전부 보내고 완료되어야 한다. 아니면 프레이밍이 깨지므로 끊는다.
            _logger.LogWarning("[session {Id}] partial send {Sent}/{Expected} bytes - disconnecting", Id, args.BytesTransferred, expected);
            lock (_sendLock) _sendInFlight = false;
            Disconnect("partial send");
            return false;
        }

        bool more;
        var closeNow = false;
        var reason = "";
        lock (_sendLock)
        {
            _sending.Clear();
            more = _sendQueue.Count > 0;
            if (!more)
            {
                _sendInFlight = false;
                closeNow = _closeAfterFlush;
                reason = _closeReason;
            }
        }

        if (more) return true;
        if (closeNow) Disconnect(reason);
        return false;
    }

    // ---------------------------------------------------------------- lifecycle

    /// <summary>소켓을 닫고 <see cref="Disconnected"/> 를 정확히 한 번 올린다. 여러 번, 어느 스레드에서 불러도 안전.</summary>
    public void Disconnect(string reason)
    {
        if (Interlocked.Exchange(ref _connected, 0) == 0) return;

        _logger.LogDebug("[session {Id}] disconnect ({Remote}): {Reason} [t{Thread}]", Id, RemoteAddress, reason, Environment.CurrentManagedThreadId);
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        // Close 가 걸려 있는 비동기 작업을 취소한다. 취소된 완료는 OperationAborted 로 돌아와 조용히 무시된다.
        _socket.Close();

        Disconnected?.Invoke(this, reason);
    }

    /// <summary>큐에 있는 송신을 모두 보낸 뒤 끊는다 (거절 응답을 보내고 끊을 때).</summary>
    public void DisconnectAfterFlush(string reason)
    {
        bool now;
        lock (_sendLock)
        {
            _closeAfterFlush = true;
            _closeReason = reason;
            now = !_sendInFlight;
        }
        if (now) Disconnect(reason);
    }
}
