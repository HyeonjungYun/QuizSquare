#include "networking/Session.h"

#include "core/Log.h"
#include "networking/Packet.h"
#include "networking/PacketDispatcher.h"

namespace quiz {

Session::Session(SOCKET socket, SessionId id, const PacketDispatcher& dispatcher, size_t recvBufferBytes)
    : socket_(socket), id_(id), dispatcher_(dispatcher), recvBuffer_(recvBufferBytes) {
  recvOverlapped_.type = IoType::Recv;
  sendOverlapped_.type = IoType::Send;

  sockaddr_in addr{};
  int length = sizeof(addr);
  if (getpeername(socket_, reinterpret_cast<sockaddr*>(&addr), &length) == 0) {
    char ip[INET_ADDRSTRLEN] = {};
    inet_ntop(AF_INET, &addr.sin_addr, ip, sizeof(ip));
    remote_ = std::string(ip) + ":" + std::to_string(ntohs(addr.sin_port));
  } else {
    remote_ = "unknown";
  }
}

Session::~Session() {
  // 정상 경로에서는 Disconnect()가 이미 닫았다. 끊기지 않은 채 파괴되는 경우만 여기서 닫는다.
  if (connected_.load(std::memory_order_acquire)) closesocket(socket_);
}

void Session::Start() { PostRecv(); }

void Session::Dispatch(OverlappedEx* overlapped, DWORD bytesTransferred, DWORD error) {
  switch (overlapped->type) {
    case IoType::Recv:
      OnRecv(bytesTransferred, error);
      break;
    case IoType::Send:
      OnSend(bytesTransferred, error);
      break;
    default:
      LOG_ERROR("[session %llu] unexpected completion type %u", static_cast<unsigned long long>(id_),
                static_cast<unsigned>(overlapped->type));
      break;
  }
}

// ---------------------------------------------------------------- recv

void Session::PostRecv() {
  if (!IsConnected()) return;
  if (recvBuffer_.FreeSize() == 0) {
    // kMaxPacketSize < 버퍼 크기이고 Clean()이 항상 선행하므로 도달하지 않아야 한다.
    Disconnect("recv buffer full");
    return;
  }

  recvOverlapped_.Reset();
  recvOverlapped_.owner = shared_from_this();

  WSABUF buffer;
  buffer.buf = reinterpret_cast<CHAR*>(recvBuffer_.WritePos());
  buffer.len = static_cast<ULONG>(recvBuffer_.FreeSize());
  DWORD bytes = 0;
  DWORD flags = 0;
  if (WSARecv(socket_, &buffer, 1, &bytes, &flags, &recvOverlapped_.overlapped, nullptr) == SOCKET_ERROR) {
    const int err = WSAGetLastError();
    if (err != WSA_IO_PENDING) {
      recvOverlapped_.owner.reset();
      LOG_RATE_LIMITED(LogLevel::Debug, 1000, "[session %llu] WSARecv failed: %s", static_cast<unsigned long long>(id_),
                       WsaErrorName(err));
      Disconnect("WSARecv failed");
    }
  }
}

void Session::OnRecv(DWORD bytes, DWORD error) {
  if (error != 0) {
    LOG_RATE_LIMITED(LogLevel::Debug, 1000, "[session %llu] recv completed with %s", static_cast<unsigned long long>(id_),
                     WsaErrorName(static_cast<int>(error)));
    Disconnect("recv error");
    return;
  }
  if (bytes == 0) {
    Disconnect("peer closed");
    return;
  }
  if (!recvBuffer_.OnWrite(bytes)) {
    Disconnect("recv overflow");
    return;
  }
  if (!ProcessPackets()) return;
  recvBuffer_.Clean();
  PostRecv();
}

bool Session::ProcessPackets() {
  while (recvBuffer_.DataSize() >= kPacketHeaderSize) {
    PacketHeader header;
    std::memcpy(&header, recvBuffer_.ReadPos(), sizeof(header));

    if (header.size < kPacketHeaderSize || header.size > kMaxPacketSize) {
      LOG_RATE_LIMITED(LogLevel::Warn, 1000, "[session %llu] bad packet size %u (id %u) from %s",
                       static_cast<unsigned long long>(id_), static_cast<unsigned>(header.size),
                       static_cast<unsigned>(header.id), remote_.c_str());
      Disconnect("bad packet size");
      return false;
    }
    if (recvBuffer_.DataSize() < header.size) break;  // 나머지는 다음 수신에서

    const uint8_t* body = recvBuffer_.ReadPos() + kPacketHeaderSize;
    const size_t bodyLength = header.size - kPacketHeaderSize;
    if (!dispatcher_.Dispatch(*this, header.id, body, bodyLength)) {
      Disconnect("malformed packet");
      return false;
    }
    if (!IsConnected()) return false;  // 핸들러가 끊었다
    recvBuffer_.OnRead(header.size);
  }
  return true;
}

// ---------------------------------------------------------------- send

void Session::Send(SendBufferRef buffer) {
  if (!buffer || buffer->Size() == 0) return;
  if (!IsConnected()) return;

  bool startSend = false;
  {
    std::lock_guard<std::mutex> lock(sendMutex_);
    sendQueue_.push_back(std::move(buffer));
    if (!sendInFlight_) {
      sendInFlight_ = true;
      startSend = true;
    }
  }
  if (startSend) PostSend();
}

void Session::PostSend() {
  // 선행 조건: sendInFlight_ == true 이고, 이 스레드만 sending_/wsaBuffers_를 만진다.
  {
    std::lock_guard<std::mutex> lock(sendMutex_);
    sending_.clear();
    sending_.reserve(sendQueue_.size());
    while (!sendQueue_.empty()) {
      sending_.push_back(std::move(sendQueue_.front()));
      sendQueue_.pop_front();
    }
  }
  if (sending_.empty()) {
    std::lock_guard<std::mutex> lock(sendMutex_);
    sendInFlight_ = false;
    return;
  }

  wsaBuffers_.clear();
  wsaBuffers_.reserve(sending_.size());
  for (const auto& chunk : sending_) {
    WSABUF wsa;
    wsa.buf = reinterpret_cast<CHAR*>(chunk->Data());
    wsa.len = static_cast<ULONG>(chunk->Size());
    wsaBuffers_.push_back(wsa);
  }

  sendOverlapped_.Reset();
  sendOverlapped_.owner = shared_from_this();

  DWORD bytes = 0;
  if (WSASend(socket_, wsaBuffers_.data(), static_cast<DWORD>(wsaBuffers_.size()), &bytes, 0,
              &sendOverlapped_.overlapped, nullptr) == SOCKET_ERROR) {
    const int err = WSAGetLastError();
    if (err != WSA_IO_PENDING) {
      sendOverlapped_.owner.reset();
      {
        std::lock_guard<std::mutex> lock(sendMutex_);
        sendInFlight_ = false;
      }
      LOG_RATE_LIMITED(LogLevel::Debug, 1000, "[session %llu] WSASend failed: %s", static_cast<unsigned long long>(id_),
                       WsaErrorName(err));
      Disconnect("WSASend failed");
    }
  }
}

void Session::OnSend(DWORD bytes, DWORD error) {
  if (error != 0 || bytes == 0) {
    {
      std::lock_guard<std::mutex> lock(sendMutex_);
      sendInFlight_ = false;
    }
    LOG_RATE_LIMITED(LogLevel::Debug, 1000, "[session %llu] send completed with %s", static_cast<unsigned long long>(id_),
                     WsaErrorName(static_cast<int>(error)));
    Disconnect("send error");
    return;
  }

  bool more = false;
  bool closeNow = false;
  std::string reason;
  {
    std::lock_guard<std::mutex> lock(sendMutex_);
    sending_.clear();
    if (!sendQueue_.empty()) {
      more = true;  // sendInFlight_는 그대로 true
    } else {
      sendInFlight_ = false;
      closeNow = closeAfterFlush_;
      reason = closeReason_;
    }
  }
  if (more) {
    PostSend();
  } else if (closeNow) {
    Disconnect(reason.c_str());
  }
}

// ---------------------------------------------------------------- lifecycle

void Session::Disconnect(const char* reason) {
  if (!connected_.exchange(false, std::memory_order_acq_rel)) return;

  LOG_DEBUG("[session %llu] disconnect (%s): %s", static_cast<unsigned long long>(id_), remote_.c_str(), reason);
  // shutdown으로 FIN을 보내고, closesocket으로 걸려 있는 모든 overlapped I/O를 취소한다.
  // 취소된 완료는 ERROR_OPERATION_ABORTED 등으로 돌아오고 OnRecv/OnSend가 조용히 무시한다.
  shutdown(socket_, SD_BOTH);
  closesocket(socket_);

  if (onDisconnected) onDisconnected(SharedThis(), reason);
}

void Session::DisconnectAfterFlush(const char* reason) {
  bool now = false;
  {
    std::lock_guard<std::mutex> lock(sendMutex_);
    closeAfterFlush_ = true;
    closeReason_ = reason;
    now = !sendInFlight_;
  }
  if (now) Disconnect(reason);
}

void Session::SetContext(std::shared_ptr<SessionContext> context) {
  std::lock_guard<std::mutex> lock(contextMutex_);
  context_ = std::move(context);
}

std::shared_ptr<SessionContext> Session::GetContext() const {
  std::lock_guard<std::mutex> lock(contextMutex_);
  return context_;
}

}  // namespace quiz
