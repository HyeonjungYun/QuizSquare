#pragma once

namespace quiz {

class GameServer;
class PacketDispatcher;

/// C_ 패킷 핸들러를 디스패처에 등록한다. 서버 시작 전에 한 번 호출.
/// 핸들러는 IOCP 워커에서 본문을 검증만 하고, 상태가 필요한 일은 GameServer::Loop()에 작업으로 넘긴다.
void RegisterGameHandlers(GameServer& server, PacketDispatcher& dispatcher);

}  // namespace quiz
