using System.Text;
using QuizMMO.Protocol;
using QuizMMO.Server.Core;
using QuizMMO.Server.Networking;

namespace QuizMMO.Server.Gameplay;

/// <summary>
/// C_ 패킷 핸들러 등록. 핸들러는 I/O 스레드에서 필드 범위만 검증하고(서버 권위), 상태가 필요한 일은 GameLoop 에 작업으로 넘긴다.
/// protobuf 가 구조를 보장하므로 여기서는 의미 범위(길이 등)만 본다. 범위 위반은 false → 세션이 끊긴다.
/// </summary>
public static class GameHandlers
{
    public static void Register(GameServer server, PacketDispatcher dispatcher)
    {
        dispatcher.Register<CLogin>((session, msg) =>
        {
            if (Encoding.UTF8.GetByteCount(msg.Nickname) > ProtocolInfo.NicknameMaxBytes) return false;
            server.Loop.Post(() => server.OnLogin(session, msg.ProtocolVersion, msg.Nickname));
            return true;
        });

        dispatcher.Register<CChat>((session, msg) =>
        {
            var bytes = Encoding.UTF8.GetByteCount(msg.Text);
            if (bytes == 0 || bytes > ProtocolInfo.ChatMaxBytes) return false;
            server.Loop.Post(() => server.OnChat(session, msg.Text));
            return true;
        });

        // 상태가 없으므로 I/O 스레드에서 바로 응답한다.
        dispatcher.Register<CPing>((session, msg) =>
        {
            session.Send(new SPong { ClientTimeMs = msg.ClientTimeMs, ServerTimeMs = Clock.UnixTimeMs });
            return true;
        });

        dispatcher.Register<CQuizQueueJoin>((session, _) =>
        {
            server.Loop.Post(() => server.OnQuizQueueJoin(session));
            return true;
        });

        dispatcher.Register<CQuizQueueLeave>((session, _) =>
        {
            server.Loop.Post(() => server.OnQuizQueueLeave(session));
            return true;
        });
    }
}
