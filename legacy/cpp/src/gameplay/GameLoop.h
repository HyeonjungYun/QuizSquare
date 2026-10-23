#pragma once

#include <atomic>
#include <chrono>
#include <functional>
#include <thread>
#include <vector>

#include "core/JobQueue.h"

namespace quiz {

/// 게임 로직 스레드. 모든 룸/플레이어/대기열 상태는 이 스레드만 만진다.
/// 워커 스레드는 Post로 작업을 넘기고, 이 스레드가 순서대로 실행한 뒤 틱 간격마다 tick(dt)를 부른다.
/// 그래서 게임 코드에는 락이 없다 — 룸 하나를 select 모델 단일 스레드로 돌리는 것과 같은 단순함을
/// IOCP 위에서 얻는다. 룸이 많아져 한 스레드가 모자라면 룸 묶음별로 GameLoop를 늘린다.
///
/// Start/Stop은 메인 스레드에서, Post는 어느 스레드에서든. Stop을 로직 스레드 안에서 부르면 교착한다.
class GameLoop {
 public:
  explicit GameLoop(int tickHz);
  ~GameLoop();
  GameLoop(const GameLoop&) = delete;
  GameLoop& operator=(const GameLoop&) = delete;

  /// 틱마다 호출될 함수. Start 전에 설정한다.
  void SetTickHandler(std::function<void(float dt)> handler) { tick_ = std::move(handler); }

  void Start();
  /// 남은 작업을 한 번 더 비운 뒤 스레드를 끝낸다. 두 번 불러도 안전.
  void Stop();

  void Post(Job job) { queue_.Push(std::move(job)); }

  bool IsRunning() const { return running_.load(std::memory_order_acquire); }
  bool IsOnLogicThread() const { return std::this_thread::get_id() == threadId_.load(std::memory_order_acquire); }

 private:
  void Run();
  void RunPending();

  const std::chrono::milliseconds tickInterval_;
  std::function<void(float)> tick_;
  JobQueue queue_;
  std::vector<Job> pending_;  // Drain이 재사용하는 버퍼 (정상 상태에서 할당 없음)
  std::thread thread_;
  std::atomic<bool> running_{false};
  std::atomic<std::thread::id> threadId_{};
};

}  // namespace quiz
