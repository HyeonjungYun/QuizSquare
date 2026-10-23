# 의존 방향은 한쪽으로만 흐른다: core <- networking <- gameplay.
# core는 networking/gameplay를 모르고, networking은 gameplay(Player, Room)를 모른다.
#
# 소스 목록은 폴더에서 자동 수집한다 (CONFIGURE_DEPENDS: 빌드 때 다시 훑는다).
# 그래서 Visual Studio 솔루션(QuizMMOServer.sln)에서 파일을 추가하면 CMake 쪽은 손댈 것이 없다.

# core — 로그, 설정, 작업 큐. Winsock에 의존하지 않는다.
file(GLOB QUIZ_CORE_SOURCES CONFIGURE_DEPENDS core/*.cpp core/*.h)
add_library(quiz_core STATIC ${QUIZ_CORE_SOURCES})
target_include_directories(quiz_core PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})

# networking — IOCP 코어, 리스너, 세션, 패킷 프레이밍/디스패치.
file(GLOB QUIZ_NET_SOURCES CONFIGURE_DEPENDS networking/*.cpp networking/*.h)
add_library(quiz_net STATIC ${QUIZ_NET_SOURCES})
target_link_libraries(quiz_net PUBLIC quiz_core ws2_32 mswsock)

# gameplay — 플레이어, 룸(월드/퀴즈), 퀴즈 대기열, 게임 루프, 패킷 핸들러.
file(GLOB QUIZ_GAME_SOURCES CONFIGURE_DEPENDS gameplay/*.cpp gameplay/*.h)
add_library(quiz_game STATIC ${QUIZ_GAME_SOURCES})
target_link_libraries(quiz_game PUBLIC quiz_net)

add_executable(QuizServer main.cpp)
target_link_libraries(QuizServer PRIVATE quiz_game)
