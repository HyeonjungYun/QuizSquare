#pragma once

#include <cstdint>
#include <string_view>

#include "core/Types.h"
#include "networking/Protocol.h"
#include "networking/SendBuffer.h"

namespace quiz {

/// 서버→클라이언트(S_) 패킷 조립 함수들. 레이아웃은 networking/Protocol.h의 주석과 1:1로 맞는다.
/// 조립에 실패하면(용량 초과 등) nullptr을 돌려주고 오류 로그를 남긴다. Session::Send는 nullptr을 무시한다.
/// 어느 스레드에서든 호출 가능 (공유 상태 없음).

SendBufferRef BuildLoginResult(LoginResult result, PlayerId playerId, RoomId roomId);
SendBufferRef BuildChat(PlayerId fromPlayerId, std::string_view nickname, std::string_view message);
SendBufferRef BuildPong(uint64_t clientTimeMs, uint64_t serverUnixTimeMs);
SendBufferRef BuildQueueStatus(QueueState state, uint16_t waiting, uint16_t required);
SendBufferRef BuildRoomChanged(RoomId roomId, RoomType type, std::string_view roomName);
SendBufferRef BuildPlayerEnter(PlayerId playerId, std::string_view nickname);
SendBufferRef BuildPlayerLeave(PlayerId playerId);

}  // namespace quiz
