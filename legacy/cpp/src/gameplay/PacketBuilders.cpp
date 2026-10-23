#include "gameplay/PacketBuilders.h"

#include "core/Log.h"
#include "networking/Packet.h"

namespace quiz {

namespace {

SendBufferRef FinishOrNull(PacketWriter& writer, SendBufferRef buffer, const char* what) {
  if (!writer.Finish()) {
    LOG_ERROR("packet build failed: %s", what);
    return nullptr;
  }
  return buffer;
}

}  // namespace

SendBufferRef BuildLoginResult(LoginResult result, PlayerId playerId, RoomId roomId) {
  auto buffer = MakePacketBuffer(kPacketHeaderSize + sizeof(uint8_t) + sizeof(PlayerId) + sizeof(RoomId));
  PacketWriter writer(*buffer, S_LOGIN);
  writer.Write(static_cast<uint8_t>(result));
  writer.Write(playerId);
  writer.Write(roomId);
  return FinishOrNull(writer, std::move(buffer), "S_LOGIN");
}

SendBufferRef BuildChat(PlayerId fromPlayerId, std::string_view nickname, std::string_view message) {
  auto buffer = MakePacketBuffer(kPacketHeaderSize + sizeof(PlayerId) + StringWireSize(nickname.size()) +
                                 StringWireSize(message.size()));
  PacketWriter writer(*buffer, S_CHAT);
  writer.Write(fromPlayerId);
  writer.WriteString(nickname);
  writer.WriteString(message);
  return FinishOrNull(writer, std::move(buffer), "S_CHAT");
}

SendBufferRef BuildPong(uint64_t clientTimeMs, uint64_t serverUnixTimeMs) {
  auto buffer = MakePacketBuffer(kPacketHeaderSize + sizeof(uint64_t) * 2);
  PacketWriter writer(*buffer, S_PONG);
  writer.Write(clientTimeMs);
  writer.Write(serverUnixTimeMs);
  return FinishOrNull(writer, std::move(buffer), "S_PONG");
}

SendBufferRef BuildQueueStatus(QueueState state, uint16_t waiting, uint16_t required) {
  auto buffer = MakePacketBuffer(kPacketHeaderSize + sizeof(uint8_t) + sizeof(uint16_t) * 2);
  PacketWriter writer(*buffer, S_QUIZ_QUEUE_STATUS);
  writer.Write(static_cast<uint8_t>(state));
  writer.Write(waiting);
  writer.Write(required);
  return FinishOrNull(writer, std::move(buffer), "S_QUIZ_QUEUE_STATUS");
}

SendBufferRef BuildRoomChanged(RoomId roomId, RoomType type, std::string_view roomName) {
  auto buffer = MakePacketBuffer(kPacketHeaderSize + sizeof(RoomId) + sizeof(uint8_t) + StringWireSize(roomName.size()));
  PacketWriter writer(*buffer, S_ROOM_CHANGED);
  writer.Write(roomId);
  writer.Write(static_cast<uint8_t>(type));
  writer.WriteString(roomName);
  return FinishOrNull(writer, std::move(buffer), "S_ROOM_CHANGED");
}

SendBufferRef BuildPlayerEnter(PlayerId playerId, std::string_view nickname) {
  auto buffer = MakePacketBuffer(kPacketHeaderSize + sizeof(PlayerId) + StringWireSize(nickname.size()));
  PacketWriter writer(*buffer, S_PLAYER_ENTER);
  writer.Write(playerId);
  writer.WriteString(nickname);
  return FinishOrNull(writer, std::move(buffer), "S_PLAYER_ENTER");
}

SendBufferRef BuildPlayerLeave(PlayerId playerId) {
  auto buffer = MakePacketBuffer(kPacketHeaderSize + sizeof(PlayerId));
  PacketWriter writer(*buffer, S_PLAYER_LEAVE);
  writer.Write(playerId);
  return FinishOrNull(writer, std::move(buffer), "S_PLAYER_LEAVE");
}

}  // namespace quiz
