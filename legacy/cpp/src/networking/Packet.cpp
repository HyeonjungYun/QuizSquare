#include "networking/Packet.h"

namespace quiz {

bool PacketReader::ReadString(std::string& out, size_t maxBytes) {
  uint16_t length = 0;
  if (!Read(length)) return false;
  if (length > maxBytes || length > Remaining()) {
    ok_ = false;
    return false;
  }
  out.assign(reinterpret_cast<const char*>(data_ + pos_), length);
  pos_ += length;
  return true;
}

PacketWriter::PacketWriter(SendBuffer& buffer, uint16_t id) : buffer_(buffer), id_(id), headerPos_(buffer.Size()) {
  const PacketHeader placeholder{0, id};
  if (buffer_.Free() < sizeof(placeholder)) {
    ok_ = false;
    return;
  }
  std::memcpy(buffer_.WritePtr(), &placeholder, sizeof(placeholder));
  buffer_.Advance(sizeof(placeholder));
}

bool PacketWriter::WriteString(std::string_view text) {
  if (!ok_ || text.size() > kMaxStringBytes) {
    ok_ = false;
    return false;
  }
  if (!Write(static_cast<uint16_t>(text.size()))) return false;
  if (buffer_.Free() < text.size()) {
    ok_ = false;
    return false;
  }
  std::memcpy(buffer_.WritePtr(), text.data(), text.size());
  buffer_.Advance(text.size());
  return true;
}

bool PacketWriter::Finish() {
  if (!ok_) return false;
  const size_t total = buffer_.Size() - headerPos_;
  if (total > kMaxPacketSize) {
    ok_ = false;
    return false;
  }
  const PacketHeader header{static_cast<uint16_t>(total), id_};
  std::memcpy(buffer_.Data() + headerPos_, &header, sizeof(header));
  return true;
}

}  // namespace quiz
