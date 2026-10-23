using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using QuizMMO.Server.Core;

namespace QuizMMO.Server.Networking;

/// <summary>
/// 리스너. AcceptAsync 를 N개 미리 걸어두고(C++ 의 AcceptEx 선행 게시와 같다), 하나가 완료되면 세션을 만들고 다시 건다.
/// Start/Stop 은 호스트 스레드에서, 완료 처리는 I/O 스레드에서.
/// </summary>
public sealed class Listener
{
    private readonly SessionManager _sessions;
    private readonly int _acceptCount;
    private readonly ILogger _logger;
    private Socket? _listenSocket;
    private volatile bool _running;

    public Listener(SessionManager sessions, int acceptCount, ILogger logger)
    {
        _sessions = sessions;
        _acceptCount = acceptCount;
        _logger = logger;
    }

    public void Start(IPAddress bindAddress, int port)
    {
        _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        // Windows 에서 SO_REUSEADDR 는 포트 가로채기를 허용한다. 배타 점유로 중복 실행을 막는다.
        _listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
        _listenSocket.Bind(new IPEndPoint(bindAddress, port));
        _listenSocket.Listen(512);
        _running = true;

        for (var i = 0; i < _acceptCount; i++)
        {
            var args = new SocketAsyncEventArgs();
            args.Completed += (_, completed) =>
            {
                if (HandleAccept(completed)) PostAccept(completed);
            };
            PostAccept(args);
        }
        _logger.LogInformation("listening on {Address}:{Port} ({Accepts} accepts posted)", bindAddress, port, _acceptCount);
    }

    /// <summary>리슨 소켓을 닫는다. 걸려 있던 AcceptAsync 는 오류로 완료되고 조용히 정리된다.</summary>
    public void Stop()
    {
        _running = false;
        _listenSocket?.Close();
    }

    private void PostAccept(SocketAsyncEventArgs args)
    {
        while (_running)
        {
            args.AcceptSocket = null;   // 재사용 전 반드시 비운다
            bool pending;
            try
            {
                pending = _listenSocket!.AcceptAsync(args);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            if (pending) return;
            if (!HandleAccept(args)) return;   // 동기 완료: 바로 처리하고 다시 건다
        }
    }

    /// <summary>수락 완료 처리. 계속 수락해도 되면 true.</summary>
    private bool HandleAccept(SocketAsyncEventArgs args)
    {
        if (!_running)
        {
            args.AcceptSocket?.Close();
            return false;
        }
        if (args.SocketError != SocketError.Success)
        {
            _logger.LogRateLimited(LogLevel.Warning, "accept-error", 1000, "accept completed with {Error}", args.SocketError);
            args.AcceptSocket?.Close();
            return true;
        }

        var socket = args.AcceptSocket!;
        socket.NoDelay = true;   // 게임 패킷은 작고 잦다. Nagle 지연을 끈다.
        _sessions.Accept(socket);   // 소켓 소유권 이전 (정원 초과면 안에서 닫는다)
        return true;
    }
}
