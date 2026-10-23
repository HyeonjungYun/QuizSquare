#include "gameplay/GameServer.h"

#include <algorithm>
#include <thread>

#include "core/Log.h"
#include "gameplay/GameHandlers.h"
#include "gameplay/PacketBuilders.h"
#include "gameplay/Player.h"
#include "networking/Packet.h"
#include "networking/Protocol.h"
#include "networking/Session.h"

namespace quiz {

GameServer::Settings GameServer::LoadSettings(const Config& config) {
  Settings s;
  s.port = static_cast<uint16_t>(std::clamp(config.GetInt("net.port", 7777), 1, 65535));
  s.bindAddress = config.GetString("net.bind", "0.0.0.0");
  s.workerThreads = static_cast<unsigned>(std::max(0, config.GetInt("net.worker_threads", 0)));
  s.maxSessions = static_cast<size_t>(std::max(1, config.GetInt("net.max_sessions", 2000)));
  s.acceptCount = static_cast<size_t>(std::clamp(config.GetInt("net.accept_backlog", 64), 1, 1024));
  s.tickHz = std::clamp(config.GetInt("game.tick_hz", 20), 1, 240);
  s.worldName = config.GetString("game.world_name", "Plaza");
  s.quizRoomSize = static_cast<size_t>(std::max(2, config.GetInt("quiz.room_size", 20)));
  s.matchCheckIntervalSec = static_cast<float>(std::max(0.1, config.GetDouble("quiz.match_check_interval_sec", 1.0)));
  s.quizRoomDurationSec = static_cast<float>(std::max(1.0, config.GetDouble("quiz.room_duration_sec", 30.0)));
  s.statusIntervalSec = static_cast<float>(config.GetDouble("log.status_interval_sec", 10.0));

  const int minRecv = static_cast<int>(kMaxPacketSize * 2);
  const int recv = config.GetInt("net.recv_buffer_bytes", 65536);
  if (recv < minRecv) {
    LOG_WARN("net.recv_buffer_bytes=%d is below %d (2 x max packet) - clamped", recv, minRecv);
  }
  s.recvBufferBytes = static_cast<size_t>(std::max(minRecv, recv));
  return s;
}

GameServer::GameServer(const Config& config) : settings_(LoadSettings(config)), loop_(settings_.tickHz) {}

GameServer::~GameServer() { Stop(); }

bool GameServer::Start() {
  if (!wsa_.Ok() || !iocp_.Valid()) return false;

  RegisterGameHandlers(*this, dispatcher_);

  sessions_ = std::make_unique<SessionManager>(iocp_, dispatcher_, settings_.maxSessions, settings_.recvBufferBytes);
  sessions_->onSessionClosed = [this](const std::shared_ptr<Session>& session, const char* /*reason*/) {
    loop_.Post([this, session] { OnSessionClosed(session); });
  };

  world_ = std::make_unique<WorldRoom>(nextRoomId_++, settings_.worldName);

  loop_.SetTickHandler([this](float dt) { Tick(dt); });
  loop_.Start();

  const unsigned hardware = std::thread::hardware_concurrency();
  const unsigned threads = settings_.workerThreads > 0 ? settings_.workerThreads : std::max(2u, hardware);
  workers_.Start(iocp_, threads);

  listener_ = std::make_shared<Listener>(iocp_, *sessions_, settings_.acceptCount);
  if (!listener_->Start(settings_.bindAddress, settings_.port)) {
    Stop();
    return false;
  }

  LOG_INFO("QuizServer started - port %u, %u iocp workers, tick %d Hz, world '%s', quiz room size %zu, %zu handlers",
           static_cast<unsigned>(settings_.port), threads, settings_.tickHz, settings_.worldName.c_str(),
           settings_.quizRoomSize, dispatcher_.Count());
  return true;
}

void GameServer::Stop() {
  if (stopped_.exchange(true, std::memory_order_acq_rel)) return;
  LOG_INFO("QuizServer stopping");
  if (listener_) listener_->Stop();
  if (sessions_) sessions_->DisconnectAll("server shutdown");
  loop_.Stop();     // 남은 퇴장 작업을 실행하고 멈춘다
  workers_.Stop();  // 취소된 I/O 완료를 모두 소화한 뒤 멈춘다
  LOG_INFO("QuizServer stopped");
}

// ---------------------------------------------------------------- logic thread

std::shared_ptr<Player> GameServer::PlayerOf(const Session& session) const {
  return std::static_pointer_cast<Player>(session.GetContext());
}

Room* GameServer::FindRoom(RoomId roomId) {
  if (roomId == kInvalidRoomId) return nullptr;
  if (world_ && world_->Id() == roomId) return world_.get();
  const auto it = quizRooms_.find(roomId);
  return it == quizRooms_.end() ? nullptr : it->second.get();
}

void GameServer::MovePlayer(const std::shared_ptr<Player>& player, Room& to) {
  if (Room* from = FindRoom(player->roomId)) from->Leave(player->id);
  to.Enter(player);
}

void GameServer::OnLogin(const std::shared_ptr<Session>& session, uint16_t protocolVersion, std::string nickname) {
  if (!session->IsConnected()) return;

  if (PlayerOf(*session)) {
    session->Send(BuildLoginResult(LoginResult::AlreadyLoggedIn, kInvalidPlayerId, kInvalidRoomId));
    return;
  }
  if (protocolVersion != kProtocolVersion) {
    LOG_WARN("[session %llu] protocol version %u rejected (server %u)", static_cast<unsigned long long>(session->Id()),
             static_cast<unsigned>(protocolVersion), static_cast<unsigned>(kProtocolVersion));
    session->Send(BuildLoginResult(LoginResult::VersionMismatch, kInvalidPlayerId, kInvalidRoomId));
    session->DisconnectAfterFlush("protocol version mismatch");
    return;
  }
  if (nickname.size() < kNicknameMinBytes || nickname.size() > kNicknameMaxBytes) {
    session->Send(BuildLoginResult(LoginResult::BadNickname, kInvalidPlayerId, kInvalidRoomId));
    session->DisconnectAfterFlush("bad nickname");
    return;
  }

  auto player = std::make_shared<Player>();
  player->id = nextPlayerId_++;
  player->nickname = std::move(nickname);
  player->session = session;
  session->SetContext(player);

  session->Send(BuildLoginResult(LoginResult::Ok, player->id, world_->Id()));
  world_->Enter(player);
  LOG_INFO("[player %llu] '%s' logged in from %s (world %zu)", static_cast<unsigned long long>(player->id),
           player->nickname.c_str(), session->RemoteAddress().c_str(), world_->PlayerCount());
}

void GameServer::OnChat(const std::shared_ptr<Session>& session, std::string message) {
  const auto player = PlayerOf(*session);
  if (!player) {
    session->Disconnect("chat before login");
    return;
  }
  if (Room* room = FindRoom(player->roomId)) room->Chat(*player, message);
}

void GameServer::OnQuizQueueJoin(const std::shared_ptr<Session>& session) {
  const auto player = PlayerOf(*session);
  if (!player) {
    session->Disconnect("queue join before login");
    return;
  }
  if (player->roomId != world_->Id()) {
    // 퀴즈방 안에서는 다시 줄을 설 수 없다.
    player->Send(BuildQueueStatus(QueueState::None, 0, static_cast<uint16_t>(settings_.quizRoomSize)));
    return;
  }
  quizQueue_.Join(player);  // 이미 대기 중이어도 현재 상태를 다시 알려준다
  BroadcastQueueStatus();
}

void GameServer::OnQuizQueueLeave(const std::shared_ptr<Session>& session) {
  const auto player = PlayerOf(*session);
  if (!player) {
    session->Disconnect("queue leave before login");
    return;
  }
  if (quizQueue_.Leave(player->id)) {
    player->Send(BuildQueueStatus(QueueState::None, 0, static_cast<uint16_t>(settings_.quizRoomSize)));
    BroadcastQueueStatus();
  }
}

void GameServer::OnSessionClosed(const std::shared_ptr<Session>& session) {
  const auto player = PlayerOf(*session);
  if (!player) {
    LOG_DEBUG("[session %llu] closed before login", static_cast<unsigned long long>(session->Id()));
    return;
  }
  if (player->inQuizQueue && quizQueue_.Leave(player->id)) BroadcastQueueStatus();
  if (Room* room = FindRoom(player->roomId)) room->Leave(player->id);
  LOG_INFO("[player %llu] '%s' left (world %zu)", static_cast<unsigned long long>(player->id), player->nickname.c_str(),
           world_->PlayerCount());
}

void GameServer::BroadcastQueueStatus() const {
  const auto waiting = static_cast<uint16_t>(std::min<size_t>(quizQueue_.Size(), 0xFFFF));
  const auto required = static_cast<uint16_t>(std::min<size_t>(settings_.quizRoomSize, 0xFFFF));
  const SendBufferRef status = BuildQueueStatus(QueueState::Waiting, waiting, required);
  for (const auto& player : quizQueue_.Waiting()) player->Send(status);
}

void GameServer::Tick(float dt) {
  matchTimer_ += dt;
  if (matchTimer_ >= settings_.matchCheckIntervalSec) {
    matchTimer_ = 0.0f;
    TryMatchQuiz();
  }

  finishedRooms_.clear();
  for (auto& [id, room] : quizRooms_) {
    room->Update(dt);
    if (room->IsFinished() || room->PlayerCount() == 0) finishedRooms_.push_back(id);
  }
  for (const RoomId id : finishedRooms_) FinishQuizRoom(id);

  if (settings_.statusIntervalSec > 0.0f) {
    statusTimer_ += dt;
    if (statusTimer_ >= settings_.statusIntervalSec) {
      statusTimer_ = 0.0f;
      LOG_INFO("status: sessions=%zu world=%zu queue=%zu quizRooms=%zu", sessions_ ? sessions_->Count() : 0,
               world_->PlayerCount(), quizQueue_.Size(), quizRooms_.size());
    }
  }
}

void GameServer::TryMatchQuiz() {
  std::vector<std::shared_ptr<Player>> matched;
  bool anyMatched = false;
  while (quizQueue_.TryTake(settings_.quizRoomSize, matched)) {
    anyMatched = true;
    const RoomId roomId = nextRoomId_++;
    auto room = std::make_unique<QuizRoom>(roomId, "Quiz-" + std::to_string(roomId), settings_.quizRoomDurationSec);
    const SendBufferRef status =
        BuildQueueStatus(QueueState::Matched, 0, static_cast<uint16_t>(settings_.quizRoomSize));
    for (const auto& player : matched) {
      player->Send(status);
      MovePlayer(player, *room);
    }
    LOG_INFO("quiz room %u created with %zu players (%.0f s)", roomId, matched.size(), settings_.quizRoomDurationSec);
    quizRooms_.emplace(roomId, std::move(room));
  }
  if (anyMatched) BroadcastQueueStatus();  // 남은 대기자에게 갱신된 인원을 알린다
}

void GameServer::FinishQuizRoom(RoomId roomId) {
  const auto it = quizRooms_.find(roomId);
  if (it == quizRooms_.end()) return;

  const auto players = it->second->Players();
  for (const auto& player : players) MovePlayer(player, *world_);
  LOG_INFO("quiz room %u finished - %zu players returned to '%s'", roomId, players.size(), world_->Name().c_str());
  quizRooms_.erase(it);
}

}  // namespace quiz
