#include "core/Log.h"

#include <chrono>
#include <cstdarg>
#include <cstdio>
#include <ctime>
#include <functional>
#include <mutex>
#include <thread>

namespace quiz::Log {

namespace {

std::atomic<LogLevel> g_level{LogLevel::Info};
std::mutex g_writeMutex;

const char* LevelName(LogLevel level) {
  switch (level) {
    case LogLevel::Debug:
      return "DBG";
    case LogLevel::Info:
      return "INF";
    case LogLevel::Warn:
      return "WRN";
    case LogLevel::Error:
      return "ERR";
  }
  return "???";
}

/// 스레드를 구분하는 짧은 태그. 스레드 ID 해시의 뒤 4자리.
unsigned ThreadTag() {
  static thread_local const unsigned tag =
      static_cast<unsigned>(std::hash<std::thread::id>{}(std::this_thread::get_id()) % 10000u);
  return tag;
}

}  // namespace

void SetLevel(LogLevel level) { g_level.store(level, std::memory_order_relaxed); }

LogLevel GetLevel() { return g_level.load(std::memory_order_relaxed); }

bool Enabled(LogLevel level) { return level >= GetLevel(); }

void Write(LogLevel level, const char* fmt, ...) {
  if (!Enabled(level)) return;

  char message[1024];
  va_list args;
  va_start(args, fmt);
  std::vsnprintf(message, sizeof(message), fmt, args);
  va_end(args);

  using namespace std::chrono;
  const auto now = system_clock::now();
  const std::time_t seconds = system_clock::to_time_t(now);
  const int millis = static_cast<int>(duration_cast<milliseconds>(now.time_since_epoch()).count() % 1000);
  std::tm local{};
  localtime_s(&local, &seconds);

  std::lock_guard<std::mutex> lock(g_writeMutex);
  std::printf("[%02d:%02d:%02d.%03d][%s][t%04u] %s\n", local.tm_hour, local.tm_min, local.tm_sec, millis,
              LevelName(level), ThreadTag(), message);
  std::fflush(stdout);
}

}  // namespace quiz::Log
