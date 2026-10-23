#pragma once

#include <chrono>
#include <condition_variable>
#include <functional>
#include <mutex>
#include <utility>
#include <vector>

namespace quiz {

using Job = std::function<void()>;

/// 여러 스레드가 Push하고 한 소유자 스레드가 Drain하는 작업 큐.
/// Push/Drain 모두 스레드 안전. Drain은 내부 벡터를 out과 swap하므로 소비자는 락을 잡지 않은 채
/// 작업을 실행하고, out을 재사용하면 정상 상태에서 추가 할당이 없다.
///
/// 예 (로직 스레드):
///   std::vector<Job> pending;
///   queue.Drain(pending, std::chrono::milliseconds(50));
///   for (auto& job : pending) job();
class JobQueue {
 public:
  void Push(Job job) {
    {
      std::lock_guard<std::mutex> lock(mutex_);
      jobs_.push_back(std::move(job));
    }
    cv_.notify_one();
  }

  /// 작업이 생기거나 timeout이 지날 때까지 기다린 뒤, 쌓인 작업을 모두 out으로 옮긴다.
  size_t Drain(std::vector<Job>& out, std::chrono::milliseconds timeout) {
    out.clear();
    std::unique_lock<std::mutex> lock(mutex_);
    if (jobs_.empty() && timeout.count() > 0) {
      cv_.wait_for(lock, timeout, [this] { return !jobs_.empty(); });
    }
    out.swap(jobs_);
    return out.size();
  }

  /// 기다리지 않고 쌓인 작업을 모두 out으로 옮긴다.
  size_t Drain(std::vector<Job>& out) {
    out.clear();
    std::lock_guard<std::mutex> lock(mutex_);
    out.swap(jobs_);
    return out.size();
  }

  /// 대기 중인 소비자를 깨운다 (종료 시).
  void Notify() { cv_.notify_all(); }

  size_t Size() {
    std::lock_guard<std::mutex> lock(mutex_);
    return jobs_.size();
  }

 private:
  std::mutex mutex_;
  std::condition_variable cv_;
  std::vector<Job> jobs_;
};

}  // namespace quiz
