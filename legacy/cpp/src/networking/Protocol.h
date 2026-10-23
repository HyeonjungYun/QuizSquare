#pragma once

#include <cstddef>
#include <cstdint>

namespace quiz {

/// 프로토콜 버전. 패킷 레이아웃이 바뀌면 올린다. C_LOGIN이 들고 오고 불일치는 거절된다.
constexpr uint16_t kProtocolVersion = 1;

/// 패킷 ID. C_ = 클라이언트→서버, S_ = 서버→클라이언트.
/// 본문 레이아웃은 주석 순서대로 바이트가 이어진다. str = u16 길이(바이트) + UTF-8.
enum PacketId : uint16_t {
  C_LOGIN = 1,              // u16 protocolVersion, str nickname
  S_LOGIN = 2,              // u8 result(LoginResult), u64 playerId, u32 roomId

  C_CHAT = 10,              // str message
  S_CHAT = 11,              // u64 fromPlayerId, str nickname, str message

  C_PING = 20,              // u64 clientTimeMs
  S_PONG = 21,              // u64 clientTimeMs, u64 serverUnixTimeMs

  C_QUIZ_QUEUE_JOIN = 30,   // (empty)
  C_QUIZ_QUEUE_LEAVE = 31,  // (empty)
  S_QUIZ_QUEUE_STATUS = 32, // u8 state(QueueState), u16 waiting, u16 required

  S_ROOM_CHANGED = 40,      // u32 roomId, u8 roomType(RoomType), str roomName  — 내가 들어간 룸
  S_PLAYER_ENTER = 41,      // u64 playerId, str nickname                       — 같은 룸에 누가 들어옴
  S_PLAYER_LEAVE = 42,      // u64 playerId                                     — 같은 룸에서 누가 나감
};

enum class LoginResult : uint8_t {
  Ok = 0,
  VersionMismatch = 1,
  BadNickname = 2,
  AlreadyLoggedIn = 3,
};

enum class QueueState : uint8_t { None = 0, Waiting = 1, Matched = 2 };

enum class RoomType : uint8_t { World = 0, Quiz = 1 };

// 필드 범위. 수신 시 검증한다 (network-code 규칙: 모든 필드 범위 검사).
constexpr size_t kNicknameMinBytes = 1;
constexpr size_t kNicknameMaxBytes = 32;
constexpr size_t kChatMaxBytes = 400;
constexpr size_t kRoomNameMaxBytes = 64;

}  // namespace quiz
