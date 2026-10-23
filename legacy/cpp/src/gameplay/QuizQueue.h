#pragma once

#include <memory>
#include <vector>

#include "core/Types.h"
#include "gameplay/Player.h"

namespace quiz {

/// 퀴즈 참가 대기열. 선착순(FIFO). **로직 스레드 전용.**
/// Player::inQuizQueue 플래그를 이 클래스가 관리한다 — 밖에서 직접 바꾸지 않는다.
/// 확장 지점: MMR 매칭은 브리프 Out of scope. 필요해지면 TryTake의 선택 규칙만 바꾼다.
class QuizQueue {
 public:
  /// 이미 대기 중이면 false.
  bool Join(const std::shared_ptr<Player>& player);
  /// 대기 중이 아니면 false.
  bool Leave(PlayerId playerId);

  /// count명이 모였으면 앞에서부터 꺼내 out에 담고 true. 모자라면 out을 비우고 false.
  bool TryTake(size_t count, std::vector<std::shared_ptr<Player>>& out);

  size_t Size() const { return waiting_.size(); }
  const std::vector<std::shared_ptr<Player>>& Waiting() const { return waiting_; }

 private:
  std::vector<std::shared_ptr<Player>> waiting_;
};

}  // namespace quiz
