#pragma once

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <memory>
#include <string>
#include <string_view>
#include <type_traits>

#include "networking/SendBuffer.h"

namespace quiz {

/// 모든 패킷의 머리. size는 헤더를 포함한 전체 바이트 수. 리틀엔디언 그대로 보낸다 (x86 클라이언트 가정).
#pragma pack(push, 1)
struct PacketHeader {
  uint16_t size;
  uint16_t id;
};
#pragma pack(pop)
static_assert(sizeof(PacketHeader) == 4, "PacketHeader must be 4 bytes on the wire");

constexpr size_t kPacketHeaderSize = sizeof(PacketHeader);
/// 이보다 큰 size 헤더는 신뢰하지 않고 즉시 끊는다. 수신 버퍼는 이 값의 2배 이상이어야 한다.
constexpr size_t kMaxPacketSize = 4096;
/// 문자열 필드 하나의 최대 바이트 수 (u16 길이 접두 + UTF-8).
constexpr size_t kMaxStringBytes = 1024;

/// 수신 패킷 본문을 경계 검사하며 읽는다.
/// 어떤 Read라도 실패하면 Ok()가 false가 되고 이후 Read는 모두 실패한다 — 핸들러는 마지막에 Ok()/AtEnd()만 보면 된다.
class PacketReader {
 public:
  PacketReader(const uint8_t* data, size_t length) : data_(data), length_(length) {}

  template <typename T>
  bool Read(T& out) {
    static_assert(std::is_trivially_copyable_v<T>, "Read<T> needs a trivially copyable T");
    if (!ok_ || sizeof(T) > Remaining()) {
      ok_ = false;
      return false;
    }
    std::memcpy(&out, data_ + pos_, sizeof(T));
    pos_ += sizeof(T);
    return true;
  }

  /// u16 길이 접두 + UTF-8 바이트. maxBytes를 넘거나 본문이 모자라면 실패.
  bool ReadString(std::string& out, size_t maxBytes = kMaxStringBytes);

  bool Ok() const { return ok_; }
  size_t Remaining() const { return length_ - pos_; }
  /// 본문을 끝까지 정확히 읽었는지. 뒤에 바이트가 남으면 잘못된 패킷으로 본다.
  bool AtEnd() const { return ok_ && pos_ == length_; }

 private:
  const uint8_t* data_;
  size_t length_;
  size_t pos_ = 0;
  bool ok_ = true;
};

/// 송신 패킷을 SendBuffer에 조립한다. 생성 시 헤더 자리를 예약하고 Finish()에서 size를 기록한다.
///
/// 예:
///   auto buf = MakePacketBuffer(64);
///   PacketWriter w(*buf, S_PONG);
///   w.Write(clientTime); w.Write(serverTime);
///   if (w.Finish()) session.Send(buf);
class PacketWriter {
 public:
  PacketWriter(SendBuffer& buffer, uint16_t id);

  template <typename T>
  bool Write(const T& value) {
    static_assert(std::is_trivially_copyable_v<T>, "Write<T> needs a trivially copyable T");
    if (!ok_ || buffer_.Free() < sizeof(T)) {
      ok_ = false;
      return false;
    }
    std::memcpy(buffer_.WritePtr(), &value, sizeof(T));
    buffer_.Advance(sizeof(T));
    return true;
  }

  /// u16 길이 접두 + UTF-8 바이트. kMaxStringBytes 초과면 실패.
  bool WriteString(std::string_view text);

  /// 헤더의 size를 확정한다. 중간에 실패했거나 kMaxPacketSize를 넘으면 false — 그 버퍼는 보내지 말 것.
  bool Finish();

  bool Ok() const { return ok_; }

 private:
  SendBuffer& buffer_;
  uint16_t id_;
  size_t headerPos_;
  bool ok_ = true;
};

/// 패킷 하나를 담을 새 SendBuffer. capacity는 헤더 포함 최대 크기.
inline SendBufferRef MakePacketBuffer(size_t capacity) { return std::make_shared<SendBuffer>(capacity); }

/// 문자열 필드가 차지하는 바이트 수 (길이 접두 포함). 버퍼 크기 계산용.
constexpr size_t StringWireSize(size_t bytes) { return sizeof(uint16_t) + bytes; }

}  // namespace quiz
