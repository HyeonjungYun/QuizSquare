#include "networking/RecvBuffer.h"

#include <cstring>

namespace quiz {

RecvBuffer::RecvBuffer(size_t capacity) : buffer_(capacity) {}

bool RecvBuffer::OnWrite(size_t n) {
  if (n > FreeSize()) return false;
  writePos_ += n;
  return true;
}

bool RecvBuffer::OnRead(size_t n) {
  if (n > DataSize()) return false;
  readPos_ += n;
  return true;
}

void RecvBuffer::Clean() {
  const size_t remaining = DataSize();
  if (remaining == 0) {
    readPos_ = 0;
    writePos_ = 0;
    return;
  }
  if (readPos_ > 0) {
    std::memmove(buffer_.data(), buffer_.data() + readPos_, remaining);
    readPos_ = 0;
    writePos_ = remaining;
  }
}

}  // namespace quiz
