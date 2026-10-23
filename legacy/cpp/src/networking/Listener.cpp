#include "networking/Listener.h"

#include "core/Log.h"
#include "networking/SessionManager.h"

namespace quiz {

Listener::Listener(IocpCore& iocp, SessionManager& sessions, size_t acceptCount)
    : iocp_(iocp), sessions_(sessions), acceptCount_(acceptCount) {}

Listener::~Listener() {
  Stop();
  // 워커가 이미 멈춘 뒤라면 완료가 오지 않으므로 남은 수락 소켓을 여기서 닫는다.
  for (auto& context : accepts_) {
    if (context->socket != INVALID_SOCKET) closesocket(context->socket);
  }
}

bool Listener::Start(const std::string& bindAddress, uint16_t port) {
  listenSocket_ = WSASocketW(AF_INET, SOCK_STREAM, IPPROTO_TCP, nullptr, 0, WSA_FLAG_OVERLAPPED);
  if (listenSocket_ == INVALID_SOCKET) {
    LOG_ERROR("listen socket creation failed: %s", WsaErrorName(WSAGetLastError()));
    return false;
  }

  // SO_REUSEADDR는 Windows에서 포트 가로채기를 허용한다. 배타 점유로 중복 실행을 막는다.
  BOOL exclusive = TRUE;
  setsockopt(listenSocket_, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, reinterpret_cast<const char*>(&exclusive), sizeof(exclusive));

  sockaddr_in address{};
  address.sin_family = AF_INET;
  address.sin_port = htons(port);
  if (inet_pton(AF_INET, bindAddress.c_str(), &address.sin_addr) != 1) {
    LOG_WARN("bind address '%s' is not a valid IPv4 address - using 0.0.0.0", bindAddress.c_str());
    address.sin_addr.s_addr = htonl(INADDR_ANY);
  }

  if (bind(listenSocket_, reinterpret_cast<const sockaddr*>(&address), sizeof(address)) == SOCKET_ERROR) {
    LOG_ERROR("bind %s:%u failed: %s", bindAddress.c_str(), static_cast<unsigned>(port), WsaErrorName(WSAGetLastError()));
    return false;
  }
  if (listen(listenSocket_, SOMAXCONN) == SOCKET_ERROR) {
    LOG_ERROR("listen failed: %s", WsaErrorName(WSAGetLastError()));
    return false;
  }
  if (!iocp_.Register(*this)) return false;

  GUID acceptExGuid = WSAID_ACCEPTEX;
  DWORD bytes = 0;
  if (WSAIoctl(listenSocket_, SIO_GET_EXTENSION_FUNCTION_POINTER, &acceptExGuid, sizeof(acceptExGuid), &acceptEx_,
               sizeof(acceptEx_), &bytes, nullptr, nullptr) == SOCKET_ERROR) {
    LOG_ERROR("loading AcceptEx failed: %s", WsaErrorName(WSAGetLastError()));
    return false;
  }

  running_.store(true, std::memory_order_release);
  accepts_.reserve(acceptCount_);
  size_t posted = 0;
  for (size_t i = 0; i < acceptCount_; ++i) {
    auto context = std::make_unique<AcceptContext>();
    context->type = IoType::Accept;
    if (PostAccept(*context)) ++posted;
    accepts_.push_back(std::move(context));
  }
  if (posted == 0) {
    LOG_ERROR("no AcceptEx could be posted");
    return false;
  }
  LOG_INFO("listening on %s:%u (%zu accepts posted)", bindAddress.c_str(), static_cast<unsigned>(port), posted);
  return true;
}

void Listener::Stop() {
  running_.store(false, std::memory_order_release);
  if (listenSocket_ != INVALID_SOCKET) {
    closesocket(listenSocket_);
    listenSocket_ = INVALID_SOCKET;
  }
}

void Listener::Dispatch(OverlappedEx* overlapped, DWORD /*bytesTransferred*/, DWORD error) {
  if (overlapped->type != IoType::Accept) {
    LOG_ERROR("listener got a non-accept completion (type=%u)", static_cast<unsigned>(overlapped->type));
    return;
  }
  OnAccept(*static_cast<AcceptContext*>(overlapped), error);
}

bool Listener::PostAccept(AcceptContext& context) {
  if (!running_.load(std::memory_order_acquire)) return false;

  context.socket = WSASocketW(AF_INET, SOCK_STREAM, IPPROTO_TCP, nullptr, 0, WSA_FLAG_OVERLAPPED);
  if (context.socket == INVALID_SOCKET) {
    LOG_RATE_LIMITED(LogLevel::Error, 1000, "accept socket creation failed: %s", WsaErrorName(WSAGetLastError()));
    return false;
  }

  context.Reset();
  context.owner = shared_from_this();

  // dwReceiveDataLength = 0: 첫 데이터를 기다리지 않고 연결 즉시 완료한다.
  DWORD bytes = 0;
  const DWORD addressLength = sizeof(sockaddr_in) + 16;
  if (!acceptEx_(listenSocket_, context.socket, context.addressBuffer, 0, addressLength, addressLength, &bytes,
                 &context.overlapped)) {
    const int err = WSAGetLastError();
    if (err != WSA_IO_PENDING) {
      context.owner.reset();
      closesocket(context.socket);
      context.socket = INVALID_SOCKET;
      LOG_RATE_LIMITED(LogLevel::Error, 1000, "AcceptEx failed: %s", WsaErrorName(err));
      return false;
    }
  }
  return true;
}

void Listener::OnAccept(AcceptContext& context, DWORD error) {
  const SOCKET accepted = context.socket;
  context.socket = INVALID_SOCKET;

  if (!running_.load(std::memory_order_acquire)) {
    if (accepted != INVALID_SOCKET) closesocket(accepted);
    return;
  }
  if (error != 0) {
    LOG_RATE_LIMITED(LogLevel::Warn, 1000, "accept completed with %s", WsaErrorName(static_cast<int>(error)));
    if (accepted != INVALID_SOCKET) closesocket(accepted);
    PostAccept(context);
    return;
  }

  // 수락 소켓에 리슨 소켓의 컨텍스트를 반영한다. 없으면 getpeername/shutdown 등이 실패한다.
  if (setsockopt(accepted, SOL_SOCKET, SO_UPDATE_ACCEPT_CONTEXT, reinterpret_cast<const char*>(&listenSocket_),
                 sizeof(listenSocket_)) == SOCKET_ERROR) {
    LOG_RATE_LIMITED(LogLevel::Warn, 1000, "SO_UPDATE_ACCEPT_CONTEXT failed: %s", WsaErrorName(WSAGetLastError()));
    closesocket(accepted);
    PostAccept(context);
    return;
  }
  BOOL noDelay = TRUE;  // 게임 패킷은 작고 잦다. Nagle 지연을 끈다.
  setsockopt(accepted, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&noDelay), sizeof(noDelay));

  sessions_.Accept(accepted);  // 소켓 소유권 이전 (실패 시 안에서 닫는다)
  PostAccept(context);
}

}  // namespace quiz
