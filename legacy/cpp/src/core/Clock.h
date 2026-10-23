#pragma once

#include <chrono>
#include <cstdint>

namespace quiz {

/// 단조 증가 시계(ms). 타임아웃·레이트리밋 계산용. 벽시계가 아니라 뒤로 가지 않는다.
inline int64_t NowMs() {
  using namespace std::chrono;
  return duration_cast<milliseconds>(steady_clock::now().time_since_epoch()).count();
}

/// 유닉스 시각(ms). 클라이언트와 주고받는 타임스탬프(핑 등)에 쓴다.
inline int64_t UnixTimeMs() {
  using namespace std::chrono;
  return duration_cast<milliseconds>(system_clock::now().time_since_epoch()).count();
}

}  // namespace quiz
