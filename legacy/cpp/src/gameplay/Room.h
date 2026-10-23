#pragma once

#include <memory>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

#include "core/Types.h"
#include "gameplay/Player.h"
#include "networking/Protocol.h"

namespace quiz {

/// 플레이어 집합의 기본 단위. 월드(광장)도 퀴즈방도 Room이다.
/// **로직 스레드 전용.** GameLoop가 소유하고 그 스레드에서만 호출하므로 락이 없다.
/// 워커 스레드는 룸을 직접 만지지 말고 GameLoop::Post로 작업을 넘긴다.
class Room {
 public:
  Room(RoomId id, std::string name);
  virtual ~Room() = default;
  Room(const Room&) = delete;
  Room& operator=(const Room&) = delete;

  RoomId Id() const { return id_; }
  const std::string& Name() const { return name_; }
  virtual RoomType Type() const = 0;

  /// 플레이어를 넣는다. 본인에게 S_ROOM_CHANGED와 기존 인원의 S_PLAYER_ENTER를, 기존 인원에게 S_PLAYER_ENTER를 보낸다.
  /// 이미 들어와 있으면 무시. (인원 전체를 보내는 것은 AOI가 없는 뼈대의 선택이다 — 브리프 Out of scope 참고.)
  virtual void Enter(const std::shared_ptr<Player>& player);

  /// 플레이어를 뺀다. 남은 인원에게 S_PLAYER_LEAVE. 없으면 무시.
  virtual void Leave(PlayerId playerId);

  /// 매 로직 틱 호출. dt는 초. 기본 구현은 없음.
  virtual void Update(float /*dt*/) {}

  /// 룸 안 채팅. 보낸 사람을 포함한 전원에게 S_CHAT.
  void Chat(const Player& from, std::string_view message);

  /// 같은 버퍼를 전원에게 보낸다 (복사 없음). except는 제외할 플레이어.
  void Broadcast(const SendBufferRef& buffer, PlayerId except = kInvalidPlayerId) const;

  size_t PlayerCount() const { return players_.size(); }
  std::shared_ptr<Player> FindPlayer(PlayerId playerId) const;
  /// 현재 인원의 복사본. 순회하면서 Enter/Leave를 부를 때 쓴다.
  std::vector<std::shared_ptr<Player>> Players() const;

 protected:
  const RoomId id_;
  const std::string name_;
  std::unordered_map<PlayerId, std::shared_ptr<Player>> players_;
};

}  // namespace quiz
