#include "gameplay/QuizQueue.h"

#include <algorithm>

namespace quiz {

bool QuizQueue::Join(const std::shared_ptr<Player>& player) {
  if (!player || player->inQuizQueue) return false;
  player->inQuizQueue = true;
  waiting_.push_back(player);
  return true;
}

bool QuizQueue::Leave(PlayerId playerId) {
  const auto it = std::find_if(waiting_.begin(), waiting_.end(),
                               [playerId](const std::shared_ptr<Player>& p) { return p->id == playerId; });
  if (it == waiting_.end()) return false;
  (*it)->inQuizQueue = false;
  waiting_.erase(it);
  return true;
}

bool QuizQueue::TryTake(size_t count, std::vector<std::shared_ptr<Player>>& out) {
  out.clear();
  if (count == 0 || waiting_.size() < count) return false;
  out.assign(waiting_.begin(), waiting_.begin() + static_cast<std::ptrdiff_t>(count));
  waiting_.erase(waiting_.begin(), waiting_.begin() + static_cast<std::ptrdiff_t>(count));
  for (auto& player : out) player->inQuizQueue = false;
  return true;
}

}  // namespace quiz
