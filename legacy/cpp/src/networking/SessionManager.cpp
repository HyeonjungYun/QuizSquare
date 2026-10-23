#include "networking/SessionManager.h"

#include <vector>

#include "core/Log.h"

namespace quiz {

SessionManager::SessionManager(IocpCore& iocp, const PacketDispatcher& dispatcher, size_t maxSessions,
                               size_t recvBufferBytes)
    : iocp_(iocp), dispatcher_(dispatcher), maxSessions_(maxSessions), recvBufferBytes_(recvBufferBytes) {}

std::shared_ptr<Session> SessionManager::Accept(SOCKET socket) {
  std::shared_ptr<Session> session;
  size_t online = 0;
  {
    std::lock_guard<std::mutex> lock(mutex_);
    if (sessions_.size() >= maxSessions_) {
      online = sessions_.size();
    } else {
      const SessionId id = nextId_++;
      session = std::make_shared<Session>(socket, id, dispatcher_, recvBufferBytes_);
      sessions_.emplace(id, session);
      online = sessions_.size();
    }
  }

  if (!session) {
    LOG_RATE_LIMITED(LogLevel::Warn, 1000, "session limit %zu reached - rejecting a connection", maxSessions_);
    closesocket(socket);
    return nullptr;
  }

  session->onDisconnected = [this](const std::shared_ptr<Session>& s, const char* reason) { OnDisconnected(s, reason); };

  if (!iocp_.Register(*session)) {
    session->Disconnect("iocp register failed");
    return nullptr;
  }
  session->Start();
  LOG_DEBUG("[session %llu] connected from %s (%zu online)", static_cast<unsigned long long>(session->Id()),
            session->RemoteAddress().c_str(), online);
  return session;
}

size_t SessionManager::Count() const {
  std::lock_guard<std::mutex> lock(mutex_);
  return sessions_.size();
}

void SessionManager::DisconnectAll(const char* reason) {
  std::vector<std::shared_ptr<Session>> snapshot;
  {
    std::lock_guard<std::mutex> lock(mutex_);
    snapshot.reserve(sessions_.size());
    for (auto& [id, session] : sessions_) snapshot.push_back(session);
  }
  for (auto& session : snapshot) session->Disconnect(reason);
}

void SessionManager::OnDisconnected(const std::shared_ptr<Session>& session, const char* reason) {
  {
    std::lock_guard<std::mutex> lock(mutex_);
    sessions_.erase(session->Id());
  }
  if (onSessionClosed) onSessionClosed(session, reason);
}

}  // namespace quiz
