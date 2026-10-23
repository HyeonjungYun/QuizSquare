# QuizSquare — Quiz MMO Server

거대한 공유 맵(광장)에서 채팅하고 거래하다가, 대기열에 줄을 서면 20명이 퀴즈방으로 소환되어 실시간 퀴즈 대결을 벌이고
보상을 들고 광장으로 돌아오는 MMORPG 퀴즈 파티 게임의 **전용 서버**입니다.

- 언어/런타임: C# / .NET 10 (LTS)
- 네트워크: TCP, `SocketAsyncEventArgs` 비동기 소켓 (Windows에서는 IOCP, Linux에서는 epoll 위에서 동작)
- 프로토콜: 4바이트 헤더 `{u16 size, u16 id}` + protobuf 본문. 서버와 Unity 클라이언트가 `QuizMMO.Protocol`을 공유
- 스레드 모델: I/O 스레드는 수신·파싱·검증만, 게임 상태는 로직 스레드 하나가 작업 큐로 처리 (게임 코드에 락 없음)

> 처음 보신다면 **[LEARNING-GUIDE.md](LEARNING-GUIDE.md)** 부터 읽으세요. 학습 순서, 구조와 클래스 역할, C++ IOCP 버전과의 차이를 정리해 두었습니다.

## 폴더 구조

```
QuizMMO.sln
src/QuizMMO.Protocol/        netstandard2.1 — protocol.proto, 프레이밍, 직렬화 (Unity 공유)
src/QuizMMO.Server/          .NET 10 콘솔 서버 — Core / Networking / Gameplay
tools/QuizMMO.TestClient/    검증용 콘솔 클라이언트
tests/QuizMMO.Server.Tests/  xUnit 단위 테스트
legacy/cpp/                  C++ IOCP 버전 (참고용, 빌드 대상 아님)
LEARNING-GUIDE.md            학습 가이드 — 읽는 순서, 구조와 클래스 역할, C++ 버전과의 차이
```

## 빌드 · 테스트 · 실행

```
dotnet build QuizMMO.sln
dotnet test QuizMMO.sln
dotnet run --project src/QuizMMO.Server                                   # Ctrl+C 로 종료
dotnet run --project src/QuizMMO.Server -- --Smoke:Seconds=5              # 5초 뒤 자동 종료 (기동 확인)
dotnet run --project tools/QuizMMO.TestClient -- --clients 3 --duration 3 # 서버에 3명 접속해 채팅/핑/대기열 확인
```

Visual Studio 2022 17.14 이상에서 `QuizMMO.sln`을 열고 `QuizMMO.Server`를 시작 프로젝트로 F5.
설정은 `src/QuizMMO.Server/appsettings.json`이며, `--Server:Quiz:RoomSize=2` 처럼 커맨드라인으로 덮어쓸 수 있습니다.

## 현재 상태 (2026-10-23)

- 완료: 접속 수락, 패킷 프레이밍/검증, 로그인, 월드 룸 입퇴장 알림, 채팅 브로드캐스트, 핑, 퀴즈 대기열, 20명 매칭 → 퀴즈방 생성 → 월드 복귀 골격
- 다음 (이 순서로): 퀴즈 진행(문제/제한시간/채점) → 결과·보상·월드 복귀 → 월드 이동 동기화 → 1:1 거래
