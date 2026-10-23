#pragma once

#include <atomic>
#include <cstdint>

#include "core/Clock.h"

namespace quiz {

enum class LogLevel : uint8_t { Debug = 0, Info, Warn, Error };

/// 스레드 안전한 최소 로거. 표준 출력에 `[시각][레벨][tNNNN] 메시지` 형식으로 쓴다.
/// 메시지는 고정 크기 스택 버퍼에 포맷하므로 호출 경로에서 힙 할당이 없다.
/// 어느 스레드에서든 호출 가능. 기본 레벨은 Info.
namespace Log {

void SetLevel(LogLevel level);
LogLevel GetLevel();
bool Enabled(LogLevel level);

/// printf 형식. fmt와 문자열 인자는 UTF-8을 가정한다 (/utf-8 컴파일 옵션).
void Write(LogLevel level, const char* fmt, ...);

}  // namespace Log
}  // namespace quiz

#define LOG_DEBUG(...) ::quiz::Log::Write(::quiz::LogLevel::Debug, __VA_ARGS__)
#define LOG_INFO(...) ::quiz::Log::Write(::quiz::LogLevel::Info, __VA_ARGS__)
#define LOG_WARN(...) ::quiz::Log::Write(::quiz::LogLevel::Warn, __VA_ARGS__)
#define LOG_ERROR(...) ::quiz::Log::Write(::quiz::LogLevel::Error, __VA_ARGS__)

/// 같은 호출 지점에서 intervalMs 당 최대 1회만 기록한다.
/// 네트워크 오류처럼 폭주할 수 있는 로그는 반드시 이 매크로를 쓴다 (network-code 규칙).
#define LOG_RATE_LIMITED(level, intervalMs, ...)                                  \
  do {                                                                           \
    static std::atomic<int64_t> quizLogLastMs_{0};                               \
    const int64_t quizLogNowMs_ = ::quiz::NowMs();                               \
    int64_t quizLogPrevMs_ = quizLogLastMs_.load(std::memory_order_relaxed);     \
    if (quizLogNowMs_ - quizLogPrevMs_ >= static_cast<int64_t>(intervalMs) &&    \
        quizLogLastMs_.compare_exchange_strong(quizLogPrevMs_, quizLogNowMs_)) { \
      ::quiz::Log::Write(level, __VA_ARGS__);                                    \
    }                                                                            \
  } while (0)
