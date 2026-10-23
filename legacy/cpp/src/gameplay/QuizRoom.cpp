#include "gameplay/QuizRoom.h"

namespace quiz {

QuizRoom::QuizRoom(RoomId id, std::string name, float durationSec)
    : Room(id, std::move(name)), remainingSec_(durationSec) {}

void QuizRoom::Update(float dt) {
  if (finished_) return;
  remainingSec_ -= dt;
  if (remainingSec_ <= 0.0f) {
    remainingSec_ = 0.0f;
    finished_ = true;
  }
}

}  // namespace quiz
