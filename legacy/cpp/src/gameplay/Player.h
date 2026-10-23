#pragma once

#include <memory>
#include <string>

#include "core/Types.h"
#include "networking/SendBuffer.h"
#include "networking/Session.h"

namespace quiz {

/// 로그인한 유저. 세션의 SessionContext로 붙는다 (Session::SetContext).
/// 필드는 로직 스레드에서만 읽고 쓴다. Send는 세션의 스레드 안전 Send를 그대로 쓰므로 어디서든 호출 가능.
/// 세션이 먼저 사라질 수 있으므로 weak_ptr로 가리킨다 (Session → Player는 shared, 순환 없음).
struct Player final : public SessionContext {
  PlayerId id = kInvalidPlayerId;
  std::string nickname;
  RoomId roomId = kInvalidRoomId;
  bool inQuizQueue = false;
  std::weak_ptr<Session> session;

  void Send(const SendBufferRef& buffer) const {
    if (auto s = session.lock()) s->Send(buffer);
  }

  bool IsConnected() const {
    const auto s = session.lock();
    return s && s->IsConnected();
  }
};

}  // namespace quiz
