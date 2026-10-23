// QuizServer — Quiz MMO 전용 서버 진입점.
//
// 사용법: QuizServer [--config config/server.cfg] [--smoke [초]] [--log debug|info|warn|error]
//   --config  설정 파일 경로 (기본 config/server.cfg; 프로젝트 루트에서 실행한다고 가정)
//   --smoke   N초 동안 띄웠다가 정상 종료 (기동 확인용, 기본 3초)
//   --log     설정 파일의 log.level을 덮어쓴다

#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <thread>

#include "core/Config.h"
#include "core/Log.h"
#include "gameplay/GameServer.h"
#include "networking/NetCommon.h"

namespace {

std::atomic<bool> g_stopRequested{false};

BOOL WINAPI OnConsoleCtrl(DWORD type) {
  switch (type) {
    case CTRL_C_EVENT:
    case CTRL_BREAK_EVENT:
    case CTRL_CLOSE_EVENT:
    case CTRL_SHUTDOWN_EVENT:
      g_stopRequested.store(true);
      return TRUE;
    default:
      return FALSE;
  }
}

quiz::LogLevel ParseLogLevel(const std::string& text, quiz::LogLevel fallback) {
  if (text == "debug") return quiz::LogLevel::Debug;
  if (text == "info") return quiz::LogLevel::Info;
  if (text == "warn" || text == "warning") return quiz::LogLevel::Warn;
  if (text == "error") return quiz::LogLevel::Error;
  return fallback;
}

void PrintUsage() {
  std::printf("usage: QuizServer [--config <path>] [--smoke [seconds]] [--log debug|info|warn|error]\n");
}

}  // namespace

int main(int argc, char** argv) {
  std::string configPath = "config/server.cfg";
  std::string logOverride;
  int smokeSeconds = 0;

  for (int i = 1; i < argc; ++i) {
    const std::string arg = argv[i];
    if (arg == "--config" && i + 1 < argc) {
      configPath = argv[++i];
    } else if (arg == "--smoke") {
      smokeSeconds = 3;
      if (i + 1 < argc && argv[i + 1][0] != '-') smokeSeconds = std::atoi(argv[++i]);
    } else if (arg == "--log" && i + 1 < argc) {
      logOverride = argv[++i];
    } else if (arg == "--help" || arg == "-h") {
      PrintUsage();
      return 0;
    } else {
      std::fprintf(stderr, "unknown argument: %s\n", arg.c_str());
      PrintUsage();
      return 2;
    }
  }

  quiz::Config config;
  if (!config.LoadFile(configPath)) {
    LOG_ERROR("cannot open config '%s' - run from the project root or pass --config", configPath.c_str());
    return 1;
  }
  const std::string levelText = logOverride.empty() ? config.GetString("log.level", "info") : logOverride;
  quiz::Log::SetLevel(ParseLogLevel(levelText, quiz::LogLevel::Info));
  LOG_INFO("config loaded from %s (%zu keys)", configPath.c_str(), config.Size());

  quiz::GameServer server(config);
  if (!server.Start()) {
    LOG_ERROR("server failed to start");
    return 2;
  }

  SetConsoleCtrlHandler(OnConsoleCtrl, TRUE);

  using namespace std::chrono;
  if (smokeSeconds > 0) {
    LOG_INFO("smoke mode: running for %d seconds", smokeSeconds);
    const auto until = steady_clock::now() + seconds(smokeSeconds);
    while (steady_clock::now() < until && !g_stopRequested.load()) std::this_thread::sleep_for(milliseconds(100));
  } else {
    LOG_INFO("press Ctrl+C to stop");
    while (!g_stopRequested.load()) std::this_thread::sleep_for(milliseconds(100));
  }

  server.Stop();
  return 0;
}
