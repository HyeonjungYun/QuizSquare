#pragma once

#include <atomic>
#include <deque>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include "networking/NetCommon.h"
#include "networking/RecvBuffer.h"
#include "networking/SendBuffer.h"

namespace quiz {

class PacketDispatcher;

/// 세션에 붙는 상위 계층 데이터(gameplay의 Player 등). networking은 구체 타입을 모른다.
struct SessionContext {
  virtual ~SessionContext() = default;
};

/// TCP 연결 하나. 소켓, 수신 버퍼, 송신 큐를 소유한다.
///
/// 스레드 규칙:
/// - OnRecv/OnSend는 IOCP 워커에서 돈다. 한 세션의 수신 완료는 동시에 하나만 처리된다 (WSARecv 1개 규칙).
/// - Send/Disconnect는 어느 스레드에서든 호출 가능하다 (내부 락·원자값으로 보호).
/// - SetContext는 로직 스레드에서만, GetContext는 어디서든.
///
/// 수명: SessionManager가 shared_ptr로 소유하고, 진행 중인 I/O의 OverlappedEx::owner가 추가로 붙잡는다.
/// 따라서 모든 완료가 돌아온 뒤에야 파괴된다. 끊김 처리는 onDisconnected 콜백(워커 스레드)에서 시작된다.
class Session final : public IocpObject {
 public:
  Session(SOCKET socket, SessionId id, const PacketDispatcher& dispatcher, size_t recvBufferBytes);
  ~Session() override;

  SessionId Id() const { return id_; }
  const std::string& RemoteAddress() const { return remote_; }
  bool IsConnected() const { return connected_.load(std::memory_order_acquire); }

  /// IOCP 등록 뒤 한 번 호출한다. 첫 WSARecv를 건다.
  void Start();

  /// 송신 큐에 넣는다. 진행 중인 송신이 없으면 즉시 WSASend. 끊긴 세션이면 조용히 버린다.
  void Send(SendBufferRef buffer);

  /// 소켓을 닫고 onDisconnected를 정확히 한 번 호출한다. 여러 번, 어느 스레드에서 불러도 안전.
  void Disconnect(const char* reason);

  /// 큐에 있는 송신을 모두 보낸 뒤 끊는다 (거절 응답을 보내고 끊을 때).
  void DisconnectAfterFlush(const char* reason);

  void SetContext(std::shared_ptr<SessionContext> context);
  std::shared_ptr<SessionContext> GetContext() const;

  /// 끊길 때 한 번 호출된다 (워커 스레드). SessionManager가 설정한다.
  std::function<void(const std::shared_ptr<Session>&, const char* reason)> onDisconnected;

  std::shared_ptr<Session> SharedThis() { return std::static_pointer_cast<Session>(shared_from_this()); }

  // IocpObject
  HANDLE GetHandle() const override { return reinterpret_cast<HANDLE>(socket_); }
  void Dispatch(OverlappedEx* overlapped, DWORD bytesTransferred, DWORD error) override;

 private:
  void PostRecv();
  void OnRecv(DWORD bytes, DWORD error);
  /// 수신 버퍼에서 완전한 패킷을 모두 꺼내 디스패치한다. 끊겼으면 false.
  bool ProcessPackets();

  void PostSend();
  void OnSend(DWORD bytes, DWORD error);

  SOCKET socket_;
  const SessionId id_;
  const PacketDispatcher& dispatcher_;
  std::string remote_;
  std::atomic<bool> connected_{true};

  RecvBuffer recvBuffer_;
  OverlappedEx recvOverlapped_;

  // 송신: 한 번에 WSASend 하나. 나머지는 큐에 모아 다음 완료 때 한꺼번에(gather) 보낸다.
  std::mutex sendMutex_;
  std::deque<SendBufferRef> sendQueue_;
  std::vector<SendBufferRef> sending_;   // 진행 중인 WSASend가 참조하는 버퍼들
  std::vector<WSABUF> wsaBuffers_;
  bool sendInFlight_ = false;
  bool closeAfterFlush_ = false;
  std::string closeReason_;
  OverlappedEx sendOverlapped_;

  mutable std::mutex contextMutex_;
  std::shared_ptr<SessionContext> context_;
};

}  // namespace quiz
