#include "gameplay/GameHandlers.h"

#include <string>

#include "core/Clock.h"
#include "gameplay/GameServer.h"
#include "gameplay/PacketBuilders.h"
#include "networking/PacketDispatcher.h"
#include "networking/Protocol.h"
#include "networking/Session.h"

namespace quiz {

void RegisterGameHandlers(GameServer& server, PacketDispatcher& dispatcher) {
  // C_LOGIN: u16 protocolVersion, str nickname
  dispatcher.Register(C_LOGIN, [&server](Session& session, PacketReader& reader) -> bool {
    uint16_t version = 0;
    std::string nickname;
    if (!reader.Read(version) || !reader.ReadString(nickname, kNicknameMaxBytes) || !reader.AtEnd()) return false;

    auto shared = session.SharedThis();
    server.Loop().Post([&server, shared, version, nickname = std::move(nickname)]() mutable {
      server.OnLogin(shared, version, std::move(nickname));
    });
    return true;
  });

  // C_CHAT: str message
  dispatcher.Register(C_CHAT, [&server](Session& session, PacketReader& reader) -> bool {
    std::string message;
    if (!reader.ReadString(message, kChatMaxBytes) || !reader.AtEnd()) return false;
    if (message.empty()) return false;

    auto shared = session.SharedThis();
    server.Loop().Post([&server, shared, message = std::move(message)]() mutable {
      server.OnChat(shared, std::move(message));
    });
    return true;
  });

  // C_PING: u64 clientTimeMs — 상태가 없으므로 워커에서 바로 응답한다.
  dispatcher.Register(C_PING, [](Session& session, PacketReader& reader) -> bool {
    uint64_t clientTimeMs = 0;
    if (!reader.Read(clientTimeMs) || !reader.AtEnd()) return false;
    session.Send(BuildPong(clientTimeMs, static_cast<uint64_t>(UnixTimeMs())));
    return true;
  });

  // C_QUIZ_QUEUE_JOIN: (empty)
  dispatcher.Register(C_QUIZ_QUEUE_JOIN, [&server](Session& session, PacketReader& reader) -> bool {
    if (!reader.AtEnd()) return false;
    auto shared = session.SharedThis();
    server.Loop().Post([&server, shared] { server.OnQuizQueueJoin(shared); });
    return true;
  });

  // C_QUIZ_QUEUE_LEAVE: (empty)
  dispatcher.Register(C_QUIZ_QUEUE_LEAVE, [&server](Session& session, PacketReader& reader) -> bool {
    if (!reader.AtEnd()) return false;
    auto shared = session.SharedThis();
    server.Loop().Post([&server, shared] { server.OnQuizQueueLeave(shared); });
    return true;
  });
}

}  // namespace quiz
