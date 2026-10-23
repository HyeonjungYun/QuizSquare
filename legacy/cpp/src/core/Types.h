#pragma once

#include <cstdint>

namespace quiz {

/// 프로젝트 전역 식별자. 0은 항상 "없음"을 뜻한다.
using SessionId = uint64_t;
using PlayerId = uint64_t;
using RoomId = uint32_t;

constexpr SessionId kInvalidSessionId = 0;
constexpr PlayerId kInvalidPlayerId = 0;
constexpr RoomId kInvalidRoomId = 0;

}  // namespace quiz
