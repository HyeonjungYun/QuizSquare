#pragma once

#include <cstdint>
#include <functional>
#include <unordered_map>

#include "networking/Packet.h"

namespace quiz {

class Session;

/// 패킷 핸들러. IOCP 워커 스레드에서 호출된다.
/// 본문을 파싱해 검증하고, 게임 상태가 필요하면 GameLoop::Post로 작업을 넘긴다 — 여기서 룸을 직접 만지지 않는다.
/// 본문이 잘못되었으면 false를 반환한다 → 세션이 끊긴다.
using PacketHandler = std::function<bool(Session& session, PacketReader& reader)>;

/// 패킷 ID → 핸들러 표. Register는 서버 시작 전에만, Dispatch는 어느 워커에서든.
class PacketDispatcher {
 public:
  /// 시작 전에만 호출 (스레드 안전하지 않다). 같은 id를 다시 등록하면 덮어쓴다.
  void Register(uint16_t id, PacketHandler handler);

  /// 미등록 id, 핸들러가 false, 핸들러가 본문을 다 읽지 못함 → false (레이트리밋 로그 포함).
  bool Dispatch(Session& session, uint16_t id, const uint8_t* body, size_t length) const;

  size_t Count() const { return handlers_.size(); }

 private:
  std::unordered_map<uint16_t, PacketHandler> handlers_;
};

}  // namespace quiz
