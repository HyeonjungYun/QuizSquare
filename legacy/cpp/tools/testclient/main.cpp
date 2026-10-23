// QuizTestClient — 서버 뼈대 검증용 콘솔 클라이언트 (블로킹 소켓, 클라이언트당 스레드 1개).
//
// 사용법: QuizTestClient [--host 127.0.0.1] [--port 7777] [--clients 1] [--duration 3]
//                        [--version 1] [--no-queue] [--quiet]
// 각 클라이언트: 접속 → C_LOGIN → (S_LOGIN ok) → C_CHAT, C_PING, C_QUIZ_QUEUE_JOIN → duration 동안 수신 출력.
// 종료 코드 0 = 모든 클라이언트가 로그인 성공 + 자기 채팅 echo + PONG + 대기열 상태를 받음.
//   --version이 서버와 다르면: S_LOGIN(result=VersionMismatch)를 받고 서버가 끊어야 0.
//   --no-queue: 대기열 참가와 그 상태 수신 검사를 생략한다.

#include <atomic>
#include <chrono>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "core/Clock.h"
#include "networking/NetCommon.h"
#include "networking/Packet.h"
#include "networking/Protocol.h"

using namespace quiz;

namespace {

struct Options {
  std::string host = "127.0.0.1";
  uint16_t port = 7777;
  int clients = 1;
  int durationSec = 3;
  uint16_t version = kProtocolVersion;
  bool joinQueue = true;
  bool quiet = false;
};

struct ClientResult {
  bool connected = false;
  bool loginOk = false;
  uint8_t loginResult = 255;
  bool gotOwnChat = false;
  bool gotPong = false;
  bool gotQueueStatus = false;
  bool serverClosed = false;
  int packets = 0;
};

std::mutex g_printMutex;

void Print(const Options& options, int index, const char* fmt, ...) {
  if (options.quiet) return;
  char message[512];
  va_list args;
  va_start(args, fmt);
  std::vsnprintf(message, sizeof(message), fmt, args);
  va_end(args);
  std::lock_guard<std::mutex> lock(g_printMutex);
  std::printf("[client %d] %s\n", index, message);
  std::fflush(stdout);
}

bool SendAll(SOCKET socket, const uint8_t* data, size_t length) {
  while (length > 0) {
    const int sent = send(socket, reinterpret_cast<const char*>(data), static_cast<int>(length), 0);
    if (sent <= 0) return false;
    data += sent;
    length -= static_cast<size_t>(sent);
  }
  return true;
}

bool SendPacket(SOCKET socket, SendBuffer& buffer) { return SendAll(socket, buffer.Data(), buffer.Size()); }

void RunClient(const Options& options, int index, ClientResult& result) {
  const SOCKET socket = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
  if (socket == INVALID_SOCKET) {
    Print(options, index, "socket() failed: %s", WsaErrorName(WSAGetLastError()));
    return;
  }

  sockaddr_in address{};
  address.sin_family = AF_INET;
  address.sin_port = htons(options.port);
  inet_pton(AF_INET, options.host.c_str(), &address.sin_addr);
  if (connect(socket, reinterpret_cast<const sockaddr*>(&address), sizeof(address)) == SOCKET_ERROR) {
    Print(options, index, "connect to %s:%u failed: %s", options.host.c_str(), static_cast<unsigned>(options.port),
          WsaErrorName(WSAGetLastError()));
    closesocket(socket);
    return;
  }
  result.connected = true;

  DWORD recvTimeoutMs = 100;
  setsockopt(socket, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&recvTimeoutMs), sizeof(recvTimeoutMs));

  const std::string nickname = "tester" + std::to_string(index);
  const std::string chatText = "hello from " + nickname;

  {
    SendBuffer login(64);
    PacketWriter writer(login, C_LOGIN);
    writer.Write(options.version);
    writer.WriteString(nickname);
    if (!writer.Finish() || !SendPacket(socket, login)) {
      Print(options, index, "sending C_LOGIN failed");
      closesocket(socket);
      return;
    }
  }

  std::vector<uint8_t> inbox;
  inbox.reserve(8192);
  uint8_t chunk[4096];
  bool sentAfterLogin = false;
  bool stop = false;

  const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(options.durationSec);
  while (!stop && std::chrono::steady_clock::now() < deadline) {
    const int received = recv(socket, reinterpret_cast<char*>(chunk), sizeof(chunk), 0);
    if (received == 0) {
      result.serverClosed = true;
      Print(options, index, "server closed the connection");
      break;
    }
    if (received < 0) {
      const int err = WSAGetLastError();
      if (err != WSAETIMEDOUT) {
        result.serverClosed = true;
        Print(options, index, "recv failed: %s", WsaErrorName(err));
        break;
      }
    } else {
      inbox.insert(inbox.end(), chunk, chunk + received);
    }

    // 프레이밍: 서버와 같은 {u16 size, u16 id} 헤더.
    size_t offset = 0;
    while (inbox.size() - offset >= kPacketHeaderSize) {
      PacketHeader header;
      std::memcpy(&header, inbox.data() + offset, sizeof(header));
      if (header.size < kPacketHeaderSize || header.size > kMaxPacketSize) {
        Print(options, index, "bad header size %u - giving up", static_cast<unsigned>(header.size));
        stop = true;
        break;
      }
      if (inbox.size() - offset < header.size) break;

      PacketReader reader(inbox.data() + offset + kPacketHeaderSize, header.size - kPacketHeaderSize);
      ++result.packets;
      switch (header.id) {
        case S_LOGIN: {
          uint8_t loginResult = 0;
          uint64_t playerId = 0;
          uint32_t roomId = 0;
          reader.Read(loginResult);
          reader.Read(playerId);
          reader.Read(roomId);
          result.loginResult = loginResult;
          result.loginOk = (loginResult == static_cast<uint8_t>(LoginResult::Ok));
          Print(options, index, "S_LOGIN result=%u playerId=%llu roomId=%u", static_cast<unsigned>(loginResult),
                static_cast<unsigned long long>(playerId), roomId);
          break;
        }
        case S_CHAT: {
          uint64_t from = 0;
          std::string nick, message;
          reader.Read(from);
          reader.ReadString(nick);
          reader.ReadString(message);
          if (message == chatText) result.gotOwnChat = true;
          Print(options, index, "S_CHAT [%llu %s] %s", static_cast<unsigned long long>(from), nick.c_str(),
                message.c_str());
          break;
        }
        case S_PONG: {
          uint64_t clientTime = 0, serverTime = 0;
          reader.Read(clientTime);
          reader.Read(serverTime);
          result.gotPong = true;
          Print(options, index, "S_PONG rtt=%lld ms", static_cast<long long>(UnixTimeMs() - static_cast<int64_t>(clientTime)));
          break;
        }
        case S_QUIZ_QUEUE_STATUS: {
          uint8_t state = 0;
          uint16_t waiting = 0, required = 0;
          reader.Read(state);
          reader.Read(waiting);
          reader.Read(required);
          result.gotQueueStatus = true;
          Print(options, index, "S_QUIZ_QUEUE_STATUS state=%u waiting=%u required=%u", static_cast<unsigned>(state),
                static_cast<unsigned>(waiting), static_cast<unsigned>(required));
          break;
        }
        case S_ROOM_CHANGED: {
          uint32_t roomId = 0;
          uint8_t type = 0;
          std::string name;
          reader.Read(roomId);
          reader.Read(type);
          reader.ReadString(name);
          Print(options, index, "S_ROOM_CHANGED room=%u type=%u name=%s", roomId, static_cast<unsigned>(type),
                name.c_str());
          break;
        }
        case S_PLAYER_ENTER: {
          uint64_t playerId = 0;
          std::string nick;
          reader.Read(playerId);
          reader.ReadString(nick);
          Print(options, index, "S_PLAYER_ENTER %llu %s", static_cast<unsigned long long>(playerId), nick.c_str());
          break;
        }
        case S_PLAYER_LEAVE: {
          uint64_t playerId = 0;
          reader.Read(playerId);
          Print(options, index, "S_PLAYER_LEAVE %llu", static_cast<unsigned long long>(playerId));
          break;
        }
        default:
          Print(options, index, "unknown packet id %u (%u bytes)", static_cast<unsigned>(header.id),
                static_cast<unsigned>(header.size));
          break;
      }
      if (!reader.Ok()) Print(options, index, "packet id %u was shorter than expected", static_cast<unsigned>(header.id));
      offset += header.size;
    }
    inbox.erase(inbox.begin(), inbox.begin() + static_cast<std::ptrdiff_t>(offset));

    if (result.loginOk && !sentAfterLogin) {
      sentAfterLogin = true;
      SendBuffer chat(kPacketHeaderSize + StringWireSize(chatText.size()));
      PacketWriter chatWriter(chat, C_CHAT);
      chatWriter.WriteString(chatText);
      chatWriter.Finish();
      SendPacket(socket, chat);

      SendBuffer ping(kPacketHeaderSize + sizeof(uint64_t));
      PacketWriter pingWriter(ping, C_PING);
      pingWriter.Write(static_cast<uint64_t>(UnixTimeMs()));
      pingWriter.Finish();
      SendPacket(socket, ping);

      if (options.joinQueue) {
        SendBuffer join(kPacketHeaderSize);
        PacketWriter joinWriter(join, C_QUIZ_QUEUE_JOIN);
        joinWriter.Finish();
        SendPacket(socket, join);
      }
    }
  }

  closesocket(socket);
}

void PrintUsage() {
  std::printf("usage: QuizTestClient [--host H] [--port P] [--clients N] [--duration S] [--version V] [--no-queue] [--quiet]\n");
}

}  // namespace

int main(int argc, char** argv) {
  Options options;
  for (int i = 1; i < argc; ++i) {
    const std::string arg = argv[i];
    const bool hasValue = (i + 1 < argc);
    if (arg == "--host" && hasValue) options.host = argv[++i];
    else if (arg == "--port" && hasValue) options.port = static_cast<uint16_t>(std::atoi(argv[++i]));
    else if (arg == "--clients" && hasValue) options.clients = std::atoi(argv[++i]);
    else if (arg == "--duration" && hasValue) options.durationSec = std::atoi(argv[++i]);
    else if (arg == "--version" && hasValue) options.version = static_cast<uint16_t>(std::atoi(argv[++i]));
    else if (arg == "--no-queue") options.joinQueue = false;
    else if (arg == "--quiet") options.quiet = true;
    else if (arg == "--help" || arg == "-h") { PrintUsage(); return 0; }
    else { std::fprintf(stderr, "unknown argument: %s\n", arg.c_str()); PrintUsage(); return 2; }
  }
  if (options.clients < 1) options.clients = 1;

  WsaInitializer wsa;
  if (!wsa.Ok()) return 1;

  std::vector<ClientResult> results(static_cast<size_t>(options.clients));
  std::vector<std::thread> threads;
  threads.reserve(results.size());
  for (int i = 0; i < options.clients; ++i) {
    threads.emplace_back([&options, i, &results] { RunClient(options, i, results[static_cast<size_t>(i)]); });
  }
  for (auto& thread : threads) thread.join();

  const bool expectRejection = (options.version != kProtocolVersion);
  int ok = 0;
  int totalPackets = 0;
  for (const auto& r : results) {
    totalPackets += r.packets;
    bool pass = false;
    if (expectRejection) {
      pass = r.connected && r.loginResult == static_cast<uint8_t>(LoginResult::VersionMismatch) && r.serverClosed;
    } else {
      pass = r.connected && r.loginOk && r.gotOwnChat && r.gotPong && (!options.joinQueue || r.gotQueueStatus);
    }
    if (pass) ++ok;
  }

  std::printf("summary: %d/%d clients passed (%s), %d packets received\n", ok, options.clients,
              expectRejection ? "expected version rejection" : "login+chat+ping+queue", totalPackets);
  return ok == options.clients ? 0 : 1;
}
