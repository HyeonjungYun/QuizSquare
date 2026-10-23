#pragma once

#include <atomic>
#include <functional>
#include <memory>
#include <mutex>
#include <unordered_map>

#include "networking/Iocp.h"
#include "networking/Session.h"

namespace quiz {

class PacketDispatcher;

/// 살아 있는 세션의 소유자. 수락된 소켓으로 세션을 만들고, 끊기면 표에서 지운다.
/// 모든 메서드는 스레드 안전 (워커 여러 개가 동시에 Accept/끊김을 처리한다).
class SessionManager {
 public:
  SessionManager(IocpCore& iocp, const PacketDispatcher& dispatcher, size_t maxSessions, size_t recvBufferBytes);

  /// 수락된 소켓을 세션으로 감싸 IOCP에 등록하고 수신을 시작한다.
  /// 정원 초과나 등록 실패면 소켓을 닫고 nullptr. 소켓 소유권은 항상 이쪽으로 넘어온다.
  std::shared_ptr<Session> Accept(SOCKET socket);

  size_t Count() const;
  void DisconnectAll(const char* reason);

  /// 세션이 표에서 제거된 직후 호출된다 (워커 스레드). 게임 계층이 퇴장 처리를 로직 스레드로 넘긴다.
  std::function<void(const std::shared_ptr<Session>&, const char* reason)> onSessionClosed;

 private:
  void OnDisconnected(const std::shared_ptr<Session>& session, const char* reason);

  IocpCore& iocp_;
  const PacketDispatcher& dispatcher_;
  const size_t maxSessions_;
  const size_t recvBufferBytes_;

  mutable std::mutex mutex_;
  std::unordered_map<SessionId, std::shared_ptr<Session>> sessions_;
  SessionId nextId_ = 1;
};

}  // namespace quiz
