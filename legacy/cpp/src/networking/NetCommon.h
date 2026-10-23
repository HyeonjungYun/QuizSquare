#pragma once

// Winsock2 헤더는 Windows.h보다 먼저 와야 한다 (아니면 구형 winsock.h가 끼어든다).
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <WinSock2.h>
#include <WS2tcpip.h>
#include <MSWSock.h>
#include <Windows.h>

#include <cstdint>
#include <memory>

#include "core/Types.h"

namespace quiz {

/// WSAStartup/WSACleanup RAII. 프로세스에 하나만 만든다 (GameServer가 소유).
class WsaInitializer {
 public:
  WsaInitializer();
  ~WsaInitializer();
  WsaInitializer(const WsaInitializer&) = delete;
  WsaInitializer& operator=(const WsaInitializer&) = delete;

  bool Ok() const { return ok_; }

 private:
  bool ok_ = false;
};

/// Winsock/Win32 오류 코드를 로그용 짧은 이름으로. 모르는 코드는 thread_local 버퍼에 "error(N)".
const char* WsaErrorName(int error);

/// 어떤 비동기 작업의 완료인지 구분한다.
enum class IoType : uint8_t { Accept, Recv, Send };

class IocpObject;

/// OVERLAPPED 확장. 완료 패킷의 OVERLAPPED*에서 CONTAINING_RECORD로 복원한다.
/// `owner`는 I/O가 걸려 있는 동안 소유 객체(Session, Listener)를 살려두는 참조다.
/// 워커가 완료를 꺼낼 때 이 참조를 가져가서 Dispatch가 끝날 때까지 들고 있다.
struct OverlappedEx {
  OVERLAPPED overlapped{};
  IoType type{IoType::Recv};
  std::shared_ptr<IocpObject> owner;

  void Reset() { ZeroMemory(&overlapped, sizeof(overlapped)); }
};

/// IOCP에 등록되는 객체. 핸들(소켓)을 내주고 완료를 받는다.
class IocpObject : public std::enable_shared_from_this<IocpObject> {
 public:
  virtual ~IocpObject() = default;

  virtual HANDLE GetHandle() const = 0;

  /// IOCP 워커 스레드에서 호출된다. `error`가 0이면 성공, 아니면 Win32/WSA 오류 코드.
  virtual void Dispatch(OverlappedEx* overlapped, DWORD bytesTransferred, DWORD error) = 0;
};

}  // namespace quiz
