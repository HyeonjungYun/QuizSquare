#include "networking/NetCommon.h"

#include <cstdio>

#include "core/Log.h"

namespace quiz {

WsaInitializer::WsaInitializer() {
  WSADATA data{};
  const int rc = WSAStartup(MAKEWORD(2, 2), &data);
  ok_ = (rc == 0);
  if (!ok_) LOG_ERROR("WSAStartup failed: %s", WsaErrorName(rc));
}

WsaInitializer::~WsaInitializer() {
  if (ok_) WSACleanup();
}

const char* WsaErrorName(int error) {
  switch (error) {
    case 0:
      return "OK";
    case WSAECONNRESET:
      return "WSAECONNRESET(10054)";
    case WSAECONNABORTED:
      return "WSAECONNABORTED(10053)";
    case WSAENOTSOCK:
      return "WSAENOTSOCK(10038)";
    case WSAESHUTDOWN:
      return "WSAESHUTDOWN(10058)";
    case WSAEADDRINUSE:
      return "WSAEADDRINUSE(10048)";
    case WSAEACCES:
      return "WSAEACCES(10013)";
    case WSAETIMEDOUT:
      return "WSAETIMEDOUT(10060)";
    case WSAECONNREFUSED:
      return "WSAECONNREFUSED(10061)";
    case ERROR_NETNAME_DELETED:
      return "ERROR_NETNAME_DELETED(64)";
    case ERROR_CONNECTION_ABORTED:
      return "ERROR_CONNECTION_ABORTED(1236)";
    case ERROR_OPERATION_ABORTED:
      return "ERROR_OPERATION_ABORTED(995)";
    default: {
      static thread_local char buffer[32];
      std::snprintf(buffer, sizeof(buffer), "error(%d)", error);
      return buffer;
    }
  }
}

}  // namespace quiz
