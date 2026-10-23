#pragma once

#include <atomic>
#include <memory>
#include <string>
#include <unordered_map>
#include <vector>

#include "core/Config.h"
#include "core/Types.h"
#include "gameplay/GameLoop.h"
#include "gameplay/QuizQueue.h"
#include "gameplay/QuizRoom.h"
#include "gameplay/WorldRoom.h"
#include "networking/Iocp.h"
#include "networking/Listener.h"
#include "networking/NetCommon.h"
#include "networking/PacketDispatcher.h"
#include "networking/SessionManager.h"

namespace quiz {

struct Player;
class Room;
class Session;

/// 서버 전체를 조립한다.
///
///   [IOCP 워커 N개]  ── 소켓 I/O, 패킷 파싱, 핸들러(검증) ──▶ GameLoop::Post(작업)
///   [로직 스레드 1개] ── 작업 실행 + 틱: WorldRoom, QuizRoom들, QuizQueue (락 없음)
///
/// 스레드 모델 결정: IOCP에 묶인 소켓은 다른 모델로 옮길 수 없으므로 I/O는 전부 IOCP가 맡고,
/// 룸 격리는 작업 큐로 얻는다. 20명 퀴즈방도 2000명 월드도 Room 하나다.
///
/// On* 메서드는 로직 스레드 전용이다. GameHandlers가 Loop().Post로 넘겨 부른다.
class GameServer {
 public:
  explicit GameServer(const Config& config);
  ~GameServer();
  GameServer(const GameServer&) = delete;
  GameServer& operator=(const GameServer&) = delete;

  /// 서브시스템을 모두 시작한다. 실패하면 로그를 남기고 false (시작된 것은 되돌린다).
  bool Start();
  /// 리스너 → 세션 → 로직 스레드 → 워커 순으로 멈춘다. 두 번 불러도 안전.
  void Stop();

  GameLoop& Loop() { return loop_; }

  // ---- 로직 스레드 전용 게임 명령 ----
  void OnLogin(const std::shared_ptr<Session>& session, uint16_t protocolVersion, std::string nickname);
  void OnChat(const std::shared_ptr<Session>& session, std::string message);
  void OnQuizQueueJoin(const std::shared_ptr<Session>& session);
  void OnQuizQueueLeave(const std::shared_ptr<Session>& session);
  void OnSessionClosed(const std::shared_ptr<Session>& session);

 private:
  struct Settings {
    uint16_t port = 7777;
    std::string bindAddress = "0.0.0.0";
    unsigned workerThreads = 0;
    size_t maxSessions = 2000;
    size_t recvBufferBytes = 65536;
    size_t acceptCount = 64;
    int tickHz = 20;
    std::string worldName = "Plaza";
    size_t quizRoomSize = 20;
    float matchCheckIntervalSec = 1.0f;
    float quizRoomDurationSec = 30.0f;
    float statusIntervalSec = 10.0f;
  };
  static Settings LoadSettings(const Config& config);

  void Tick(float dt);
  void TryMatchQuiz();
  void FinishQuizRoom(RoomId roomId);
  void BroadcastQueueStatus() const;
  void MovePlayer(const std::shared_ptr<Player>& player, Room& to);
  Room* FindRoom(RoomId roomId);
  std::shared_ptr<Player> PlayerOf(const Session& session) const;

  const Settings settings_;
  std::atomic<bool> stopped_{false};

  // 선언 순서가 초기화 순서다: 네트워크 기반 → 세션 → 리스너 → 워커 → 로직.
  WsaInitializer wsa_;
  IocpCore iocp_;
  PacketDispatcher dispatcher_;
  std::unique_ptr<SessionManager> sessions_;
  std::shared_ptr<Listener> listener_;
  IocpWorkerPool workers_;
  GameLoop loop_;

  // ---- 로직 스레드 전용 상태 ----
  std::unique_ptr<WorldRoom> world_;
  std::unordered_map<RoomId, std::unique_ptr<QuizRoom>> quizRooms_;
  QuizQueue quizQueue_;
  std::vector<RoomId> finishedRooms_;  // Tick이 재사용하는 버퍼
  RoomId nextRoomId_ = 1;
  PlayerId nextPlayerId_ = 1;
  float matchTimer_ = 0.0f;
  float statusTimer_ = 0.0f;
};

}  // namespace quiz
