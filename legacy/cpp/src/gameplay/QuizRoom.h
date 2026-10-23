#pragma once

#include "gameplay/Room.h"

namespace quiz {

/// 퀴즈 인스턴스. 대기열에서 뽑힌 플레이어들이 들어와 라운드를 치르고, 끝나면 GameServer가 월드로 되돌린다.
/// 뼈대 단계에서는 duration 동안 카운트다운만 한다 — 룸 이동과 복귀 흐름을 검증하기 위한 자리표시.
/// 확장 지점(스토리 004/005): Update(dt)에 Waiting → Question → Reveal → Result 상태 머신,
/// 문제 데이터 로드, 답안 패킷 처리, 점수/순위, 보상 지급을 붙인다.
class QuizRoom final : public Room {
 public:
  QuizRoom(RoomId id, std::string name, float durationSec);

  RoomType Type() const override { return RoomType::Quiz; }

  /// 남은 시간을 줄인다. 0이 되면 IsFinished()가 true.
  void Update(float dt) override;

  bool IsFinished() const { return finished_; }
  float RemainingSec() const { return remainingSec_; }

 private:
  float remainingSec_;
  bool finished_ = false;
};

}  // namespace quiz
