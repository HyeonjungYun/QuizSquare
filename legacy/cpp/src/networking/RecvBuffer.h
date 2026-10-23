#pragma once

#include <cstddef>
#include <cstdint>
#include <vector>

namespace quiz {

/// 세션당 하나인 수신 버퍼. 생성 시 한 번만 할당하고 WSARecv가 WritePos()에 바로 쓴다.
/// 완전한 패킷을 소비한 뒤 Clean()으로 남은 바이트를 앞으로 당겨 다음 수신 공간을 확보한다.
/// 단일 스레드 전용: 한 세션의 수신 완료는 한 번에 하나만 처리된다 (WSARecv 1개 규칙).
///
/// 예:
///   recv.OnWrite(bytesFromWsaRecv);
///   while (recv.DataSize() >= kPacketHeaderSize) { ... recv.OnRead(packetSize); }
///   recv.Clean();
class RecvBuffer {
 public:
  explicit RecvBuffer(size_t capacity);

  uint8_t* WritePos() { return buffer_.data() + writePos_; }
  const uint8_t* ReadPos() const { return buffer_.data() + readPos_; }

  size_t FreeSize() const { return buffer_.size() - writePos_; }
  size_t DataSize() const { return writePos_ - readPos_; }
  size_t Capacity() const { return buffer_.size(); }

  /// WSARecv가 n바이트를 썼다. 남은 공간보다 크면 false (버퍼 오염 방지).
  bool OnWrite(size_t n);
  /// n바이트를 소비했다. 쌓인 데이터보다 크면 false.
  bool OnRead(size_t n);

  /// 비어 있으면 포지션을 0으로, 아니면 남은 데이터를 앞으로 당긴다 (memmove).
  void Clean();

 private:
  std::vector<uint8_t> buffer_;
  size_t readPos_ = 0;
  size_t writePos_ = 0;
};

}  // namespace quiz
