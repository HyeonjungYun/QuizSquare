using System.Text;
using QuizMMO.Protocol;
using QuizMMO.Server.Core;
using QuizMMO.Server.Networking;

namespace QuizMMO.Server.Gameplay;

/// <summary>
/// C_ 패킷 핸들러 등록. 핸들러는 I/O 스레드에서 필드 범위만 검증하고(서버 권위), 상태가 필요한 일은 GameLoop 에 작업으로 넘긴다.
/// protobuf 가 구조를 보장하므로 여기서는 의미 범위(길이, 개수, 유효한 수)만 본다. 범위 위반은 false → 세션이 끊긴다.
/// 의미상 거절(상대 없음, 골드 부족 등)은 로직 스레드에서 SError 로 알린다.
/// </summary>
public static class GameHandlers
{
    public static void Register(GameServer server, PacketDispatcher dispatcher)
    {
        // ---- 로그인 / 채팅 / 핑 ----
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

        // ---- 이동 ----
        dispatcher.Register<CMove>((session, msg) =>
        {
            if (!float.IsFinite(msg.X) || !float.IsFinite(msg.Y)) return false;
            server.Loop.Post(() => server.OnMove(session, msg.X, msg.Y));
            return true;
        });

        // ---- 퀴즈 ----
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

        dispatcher.Register<CQuizAnswer>((session, msg) =>
        {
            if (msg.Choice >= ProtocolInfo.MaxQuizChoices) return false;
            server.Loop.Post(() => server.OnQuizAnswer(session, msg.Index, msg.Choice));
            return true;
        });

        // ---- 거래 ----
        dispatcher.Register<CTradeRequest>((session, msg) =>
        {
            if (msg.TargetPlayerId == 0) return false;
            server.Loop.Post(() => server.OnTradeRequest(session, msg.TargetPlayerId));
            return true;
        });

        dispatcher.Register<CTradeRespond>((session, msg) =>
        {
            if (msg.TradeId == 0) return false;
            server.Loop.Post(() => server.OnTradeRespond(session, msg.TradeId, msg.Accept));
            return true;
        });

        dispatcher.Register<CTradeOffer>((session, msg) =>
        {
            if (msg.TradeId == 0 || msg.Gold < 0 || msg.Items.Count > ProtocolInfo.MaxTradeItemStacks) return false;
            foreach (var stack in msg.Items)
            {
                if (stack.ItemId == 0 || stack.Count == 0) return false;
            }
            var items = msg.Items.ToList();
            server.Loop.Post(() => server.OnTradeOffer(session, msg.TradeId, msg.Gold, items));
            return true;
        });

        dispatcher.Register<CTradeConfirm>((session, msg) =>
        {
            if (msg.TradeId == 0) return false;
            server.Loop.Post(() => server.OnTradeConfirm(session, msg.TradeId));
            return true;
        });

        dispatcher.Register<CTradeCancel>((session, msg) =>
        {
            if (msg.TradeId == 0) return false;
            server.Loop.Post(() => server.OnTradeCancel(session, msg.TradeId));
            return true;
        });
    }
}
