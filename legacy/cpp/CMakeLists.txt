cmake_minimum_required(VERSION 3.20)
project(QuizMMOServer VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# 커맨드라인/CI용 빌드. IDE에서는 루트의 QuizMMOServer.sln(직접 관리하는 VS 솔루션)을 연다.
# 두 빌드의 컴파일 옵션은 같아야 한다 — VS 쪽은 vs/QuizCommon.props가 든다.
# 실행 파일은 <빌드 폴더>/bin/<Config>/ 아래에 모인다 (project.yaml commands: build/cmake/bin/Debug/...).
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin)

if(MSVC)
  # /utf-8: 한글 주석·문자열을 UTF-8로 해석. /permissive-: 표준 준수 모드.
  add_compile_options(/W4 /permissive- /utf-8 /Zc:__cplusplus /MP)
  add_compile_definitions(WIN32_LEAN_AND_MEAN NOMINMAX _WIN32_WINNT=0x0A00)
else()
  message(FATAL_ERROR "QuizMMOServer targets Windows/MSVC (Winsock2 IOCP).")
endif()

enable_testing()

add_subdirectory(src)
add_subdirectory(tools/testclient)
add_subdirectory(tests)
