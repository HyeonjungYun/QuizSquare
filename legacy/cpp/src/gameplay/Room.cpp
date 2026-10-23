#include "gameplay/Room.h"

#include "gameplay/PacketBuilders.h"

namespace quiz {

Room::Room(RoomId id, std::string name) : id_(id), name_(std::move(name)) {}

void Room::Enter(const std::shared_ptr<Player>& player) {
  if (!player || players_.count(player->id) != 0) return;

  player->Send(BuildRoomChanged(id_, Type(), name_));
  for (const auto& [id, other] : players_) player->Send(BuildPlayerEnter(other->id, other->nickname));
  Broadcast(BuildPlayerEnter(player->id, player->nickname));

  players_.emplace(player->id, player);
  player->roomId = id_;
}

void Room::Leave(PlayerId playerId) {
  const auto it = players_.find(playerId);
  if (it == players_.end()) return;

  const std::shared_ptr<Player> player = it->second;
  players_.erase(it);
  if (player->roomId == id_) player->roomId = kInvalidRoomId;

  Broadcast(BuildPlayerLeave(playerId));
}

void Room::Chat(const Player& from, std::string_view message) {
  Broadcast(BuildChat(from.id, from.nickname, message));
}

void Room::Broadcast(const SendBufferRef& buffer, PlayerId except) const {
  if (!buffer) return;
  for (const auto& [id, player] : players_) {
    if (id != except) player->Send(buffer);
  }
}

std::shared_ptr<Player> Room::FindPlayer(PlayerId playerId) const {
  const auto it = players_.find(playerId);
  return it == players_.end() ? nullptr : it->second;
}

std::vector<std::shared_ptr<Player>> Room::Players() const {
  std::vector<std::shared_ptr<Player>> result;
  result.reserve(players_.size());
  for (const auto& [id, player] : players_) result.push_back(player);
  return result;
}

}  // namespace quiz
