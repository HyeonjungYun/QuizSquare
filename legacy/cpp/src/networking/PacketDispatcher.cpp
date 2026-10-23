#include "networking/PacketDispatcher.h"

#include "core/Log.h"
#include "networking/Session.h"

namespace quiz {

void PacketDispatcher::Register(uint16_t id, PacketHandler handler) { handlers_[id] = std::move(handler); }

bool PacketDispatcher::Dispatch(Session& session, uint16_t id, const uint8_t* body, size_t length) const {
  const auto it = handlers_.find(id);
  if (it == handlers_.end()) {
    LOG_RATE_LIMITED(LogLevel::Warn, 1000, "[session %llu] unknown packet id %u (%zu bytes) from %s",
                     static_cast<unsigned long long>(session.Id()), static_cast<unsigned>(id), length,
                     session.RemoteAddress().c_str());
    return false;
  }

  PacketReader reader(body, length);
  if (!it->second(session, reader) || !reader.Ok()) {
    LOG_RATE_LIMITED(LogLevel::Warn, 1000, "[session %llu] malformed packet id %u (%zu bytes) from %s",
                     static_cast<unsigned long long>(session.Id()), static_cast<unsigned>(id), length,
                     session.RemoteAddress().c_str());
    return false;
  }
  return true;
}

}  // namespace quiz
