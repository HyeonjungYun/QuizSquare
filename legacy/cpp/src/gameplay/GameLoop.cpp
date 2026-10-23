#include "gameplay/GameLoop.h"

#include <algorithm>
#include <exception>

#include "core/Log.h"

namespace quiz {

GameLoop::GameLoop(int tickHz) : tickInterval_(std::chrono::milliseconds(1000 / std::max(1, tickHz))) {}

GameLoop::~GameLoop() { Stop(); }

void GameLoop::Start() {
  if (running_.exchange(true, std::memory_order_acq_rel)) return;
  thread_ = std::thread([this] { Run(); });
  LOG_INFO("game loop started (tick %lld ms)", static_cast<long long>(tickInterval_.count()));
}

void GameLoop::Stop() {
  if (!running_.exchange(false, std::memory_order_acq_rel)) return;
  queue_.Notify();
  if (thread_.joinable()) thread_.join();
  LOG_INFO("game loop stopped");
}

void GameLoop::Run() {
  threadId_.store(std::this_thread::get_id(), std::memory_order_release);
  using Clock = std::chrono::steady_clock;

  auto lastTick = Clock::now();
  auto nextTick = lastTick + tickInterval_;

  while (running_.load(std::memory_order_acquire)) {
    const auto now = Clock::now();
    const auto wait = (nextTick > now) ? std::chrono::duration_cast<std::chrono::milliseconds>(nextTick - now)
                                       : std::chrono::milliseconds(0);
    queue_.Drain(pending_, wait);
    RunPending();

    const auto afterJobs = Clock::now();
    if (afterJobs >= nextTick) {
      const float dt = std::chrono::duration<float>(afterJobs - lastTick).count();
      lastTick = afterJobs;
      if (tick_) tick_(dt);
      nextTick += tickInterval_;
      // 틱이 심하게 밀렸으면 따라잡기를 포기하고 지금부터 다시 센다 (연쇄 지연 방지).
      if (nextTick < afterJobs) nextTick = afterJobs + tickInterval_;
    }
  }

  // 종료 직전: 쌓인 작업(퇴장 처리 등)을 한 번 더 비운다.
  queue_.Drain(pending_);
  RunPending();
}

void GameLoop::RunPending() {
  for (auto& job : pending_) {
    try {
      job();
    } catch (const std::exception& e) {
      LOG_ERROR("game loop job threw: %s", e.what());
    }
  }
  pending_.clear();
}

}  // namespace quiz
