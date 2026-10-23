#pragma once

#include <thread>
#include <vector>

#include "networking/NetCommon.h"

namespace quiz {

/// I/O 완료 포트 래퍼. 소켓을 등록하고 완료 패킷을 꺼내 소유 객체의 Dispatch로 넘긴다.
/// Register/PostQuit/DispatchOne 모두 스레드 안전 (커널 객체가 동기화한다).
class IocpCore {
 public:
  IocpCore();
  ~IocpCore();
  IocpCore(const IocpCore&) = delete;
  IocpCore& operator=(const IocpCore&) = delete;

  bool Valid() const { return port_ != nullptr; }
  HANDLE Handle() const { return port_; }

  /// 객체의 핸들을 이 포트에 연결한다. 한 번 연결된 핸들은 닫기 전까지 해제할 수 없다.
  bool Register(IocpObject& object);

  /// 완료 패킷 하나를 꺼내 처리한다. 종료 신호(PostQuit)를 받으면 false.
  /// timeoutMs 안에 패킷이 없으면 아무 일 없이 true.
  bool DispatchOne(DWORD timeoutMs);

  /// 워커 하나를 깨워 종료시키는 패킷을 넣는다. 워커 수만큼 호출한다.
  void PostQuit();

 private:
  HANDLE port_ = nullptr;
};

/// IOCP 워커 스레드 묶음. 각 스레드는 DispatchOne(INFINITE)를 종료 신호까지 반복한다.
/// 워커는 I/O와 패킷 파싱만 한다. 게임 상태는 GameLoop에 작업으로 넘긴다.
class IocpWorkerPool {
 public:
  ~IocpWorkerPool();

  void Start(IocpCore& core, unsigned threadCount);
  /// 스레드 수만큼 PostQuit 후 join. 두 번 불러도 안전.
  void Stop();

  size_t ThreadCount() const { return threads_.size(); }

 private:
  IocpCore* core_ = nullptr;
  std::vector<std::thread> threads_;
};

}  // namespace quiz
