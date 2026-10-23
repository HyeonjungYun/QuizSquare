# 서버 뼈대 검증용 콘솔 클라이언트. 패킷 레이아웃은 서버와 같은 헤더(Packet.h, Protocol.h)를 쓴다.
add_executable(QuizTestClient main.cpp)
target_link_libraries(QuizTestClient PRIVATE quiz_net)
