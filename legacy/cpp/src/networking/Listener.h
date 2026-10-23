#pragma once

#include <atomic>
#include <memory>
#include <string>
#include <vector>

#include "networking/Iocp.h"
#include "networking/NetCommon.h"

namespace quiz {

class SessionManager;

/// AcceptEx 기반 리스너. 소켓 N개를 미리 수락 대기 상태로 걸어두고, 하나가 완료되면 세션을 만들고 다시 건다.
/// shared_ptr로 만들어야 한다 (진행 중인 AcceptEx가 OverlappedEx::owner로 이 객체를 붙잡는다).
/// Start/Stop은 메인 스레드에서, Dispatch는 워커에서 호출된다.
class Listener final : public IocpObject {
 public:
  Listener(IocpCore& iocp, SessionManager& sessions, size_t acceptCount);
  ~Listener() override;

  /// bind/listen 후 IOCP에 등록하고 acceptCount개의 AcceptEx를 건다.
  bool Start(const std::string& bindAddress, uint16_t port);
  /// 리슨 소켓을 닫는다. 걸려 있던 AcceptEx는 오류로 완료되고 조용히 정리된다.
  void Stop();

  // IocpObject
  HANDLE GetHandle() const override { return reinterpret_cast<HANDLE>(listenSocket_); }
  void Dispatch(OverlappedEx* overlapped, DWORD bytesTransferred, DWORD error) override;

 private:
  struct AcceptContext : OverlappedEx {
    SOCKET socket = INVALID_SOCKET;
    // AcceptEx가 로컬/원격 주소를 쓰는 버퍼. (sockaddr_in + 16) * 2 가 문서상 최소 크기.
    alignas(8) char addressBuffer[(sizeof(sockaddr_in) + 16) * 2] = {};
  };

  bool PostAccept(AcceptContext& context);
  void OnAccept(AcceptContext& context, DWORD error);

  IocpCore& iocp_;
  SessionManager& sessions_;
  const size_t acceptCount_;
  SOCKET listenSocket_ = INVALID_SOCKET;
  LPFN_ACCEPTEX acceptEx_ = nullptr;
  std::vector<std::unique_ptr<AcceptContext>> accepts_;
  std::atomic<bool> running_{false};
};

}  // namespace quiz
