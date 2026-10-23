#pragma once

#include "gameplay/Room.h"

namespace quiz {

/// 거대한 공유 맵(광장). 로그인한 플레이어는 여기서 시작하고, 퀴즈가 끝나면 여기로 돌아온다.
/// 뼈대 단계에서는 Room의 입퇴장·채팅만 쓴다.
/// 확장 지점: 이동 동기화(스토리 006)와 1:1 거래(스토리 007)는 이 클래스에 Update(dt)와 핸들러로 붙인다.
class WorldRoom final : public Room {
 public:
  using Room::Room;

  RoomType Type() const override { return RoomType::World; }
};

}  // namespace quiz
