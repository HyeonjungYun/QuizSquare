#pragma once

#include <cstddef>
#include <cstdint>
#include <memory>
#include <vector>

namespace quiz {

/// 송신 바이트 덩어리. PacketWriter가 채우고, Finish 이후에는 바뀌지 않는다.
/// 불변이므로 하나를 여러 세션에 공유해 브로드캐스트할 수 있다 (수신자마다 복사하지 않는다).
/// 확장 지점: 트래픽이 늘면 세션/스레드별 청크 풀로 교체한다. 인터페이스는 그대로 둔다.
class SendBuffer {
 public:
  explicit SendBuffer(size_t capacity) : buffer_(capacity) {}

  uint8_t* Data() { return buffer_.data(); }
  const uint8_t* Data() const { return buffer_.data(); }
  size_t Size() const { return size_; }
  size_t Capacity() const { return buffer_.size(); }

  uint8_t* WritePtr() { return buffer_.data() + size_; }
  size_t Free() const { return buffer_.size() - size_; }

  /// n바이트를 썼다고 표시한다. 남은 공간보다 크면 false.
  bool Advance(size_t n) {
    if (n > Free()) return false;
    size_ += n;
    return true;
  }

 private:
  std::vector<uint8_t> buffer_;
  size_t size_ = 0;
};

using SendBufferRef = std::shared_ptr<SendBuffer>;

}  // namespace quiz
