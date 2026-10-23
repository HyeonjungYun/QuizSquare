#include "networking/Iocp.h"

#include "core/Log.h"

namespace quiz {

namespace {
constexpr ULONG_PTR kIoCompletionKey = 0;
constexpr ULONG_PTR kQuitCompletionKey = 1;
}  // namespace

IocpCore::IocpCore() {
  port_ = CreateIoCompletionPort(INVALID_HANDLE_VALUE, nullptr, 0, 0);
  if (port_ == nullptr) LOG_ERROR("CreateIoCompletionPort failed: %s", WsaErrorName(static_cast<int>(GetLastError())));
}

IocpCore::~IocpCore() {
  if (port_ != nullptr) CloseHandle(port_);
}

bool IocpCore::Register(IocpObject& object) {
  const HANDLE result = CreateIoCompletionPort(object.GetHandle(), port_, kIoCompletionKey, 0);
  if (result == nullptr) {
    LOG_ERROR("IOCP register failed: %s", WsaErrorName(static_cast<int>(GetLastError())));
    return false;
  }
  return true;
}

bool IocpCore::DispatchOne(DWORD timeoutMs) {
  DWORD bytes = 0;
  ULONG_PTR key = 0;
  OVERLAPPED* overlapped = nullptr;
  const BOOL ok = GetQueuedCompletionStatus(port_, &bytes, &key, &overlapped, timeoutMs);
  // GetLastError는 다른 호출 전에 바로 읽어야 한다.
  const DWORD error = ok ? 0 : GetLastError();

  if (overlapped == nullptr) {
    if (ok && key == kQuitCompletionKey) return false;  // 종료 신호
    if (!ok && error != WAIT_TIMEOUT) {
      LOG_RATE_LIMITED(LogLevel::Error, 1000, "GetQueuedCompletionStatus failed: %s", WsaErrorName(static_cast<int>(error)));
    }
    return true;
  }

  OverlappedEx* ex = CONTAINING_RECORD(overlapped, OverlappedEx, overlapped);
  // 소유 참조를 꺼내 Dispatch 동안 들고 있는다. 안에서 owner가 리셋되어도 객체는 살아 있다.
  std::shared_ptr<IocpObject> owner = std::move(ex->owner);
  if (!owner) {
    LOG_ERROR("completion without owner (type=%u) - dropped", static_cast<unsigned>(ex->type));
    return true;
  }
  owner->Dispatch(ex, bytes, error);
  return true;
}

void IocpCore::PostQuit() { PostQueuedCompletionStatus(port_, 0, kQuitCompletionKey, nullptr); }

IocpWorkerPool::~IocpWorkerPool() { Stop(); }

void IocpWorkerPool::Start(IocpCore& core, unsigned threadCount) {
  if (!threads_.empty()) return;
  core_ = &core;
  threads_.reserve(threadCount);
  for (unsigned i = 0; i < threadCount; ++i) {
    threads_.emplace_back([&core] {
      while (core.DispatchOne(INFINITE)) {
      }
    });
  }
  LOG_INFO("iocp: %u worker threads started", threadCount);
}

void IocpWorkerPool::Stop() {
  if (threads_.empty()) return;
  for (size_t i = 0; i < threads_.size(); ++i) core_->PostQuit();
  for (auto& thread : threads_) {
    if (thread.joinable()) thread.join();
  }
  threads_.clear();
  LOG_INFO("iocp: worker threads stopped");
}

}  // namespace quiz
