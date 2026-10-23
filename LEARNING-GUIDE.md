# QuizSquare 서버 학습 가이드

이 문서는 세 부분입니다.

1. **학습 가이드** — 무엇을 알아야 하고, 어떤 순서로 읽고, 무엇을 직접 해 보면 되는지
2. **서버 구조와 클래스 역할** — 프로젝트·클래스별 책임, 패킷 하나의 여정, 스레드 규칙
3. **C++ IOCP 서버와의 차이** — `legacy/cpp/`에 있는 이전 버전과 무엇이 같고 무엇이 달라졌는지

코드를 옆에 띄워 두고 읽는 것을 전제로 썼습니다. 파일 경로는 모두 저장소 루트 기준입니다.

---

## 1. 학습 가이드

### 1.1 먼저 알아야 할 것 (선행 지식 체크리스트)

각 주제의 설명은 이 문서 끝의 **부록 A**에 있습니다. 체크리스트에서 모르는 항목만 골라 읽으면 됩니다.

| 주제 | 이 서버에서 어디에 쓰이나 | 설명 |
|---|---|---|
| C# 기초 문법 (C++과 다른 점) | 전체 | 부록 A.1 |
| 스레드, `lock`, `Monitor.Wait/Pulse`, `Interlocked`, `Volatile` | `JobQueue`, `Session`의 송신 큐와 연결 플래그 | 부록 A.2 |
| TCP는 **바이트 스트림**이다 (메시지 경계가 없다) | `RecvBuffer`, `Session.ProcessPackets`의 프레이밍 | 부록 A.3, `tests/.../RecvBufferTests.cs` |
| C# `async/await`, `Task`, `CancellationToken` | 테스트 클라이언트, Generic Host 종료 | 부록 A.4 |
| 델리게이트, 람다, 이벤트, 제네릭 제약(`where T : IMessage<T>`) | `Session.Disconnected`, `PacketDispatcher.Register<T>` | 부록 A.5 |
| `Span<byte>`, `ArraySegment<byte>`, `BinaryPrimitives` | `PacketHeader`, `RecvBuffer.Data`, 송신 gather | 부록 A.6 |
| protobuf 개념 (스키마, 필드 번호, proto3 기본값) | `src/QuizMMO.Protocol/Protos/protocol.proto` | 부록 A.7, https://protobuf.dev/programming-guides/proto3/ |
| `SocketAsyncEventArgs` 비동기 소켓 모델 | `Listener`, `Session` | 부록 A.8, https://learn.microsoft.com/dotnet/api/system.net.sockets.socketasynceventargs |
| .NET Generic Host (설정·로깅·DI·종료) | `Program.cs`, `GameServer : IHostedService` | 부록 A.9, https://learn.microsoft.com/dotnet/core/extensions/generic-host |

### 1.2 읽는 순서 (단계별 목표와 확인 질문)

의존 방향이 `Protocol ← Server(Core ← Networking ← Gameplay)` 한쪽으로만 흐르므로, 아래에서 위로 올라가면 모르는 타입이 나오지 않습니다.

**0단계 — 일단 돌려 본다 (20분)**

```
dotnet build QuizMMO.sln
dotnet test QuizMMO.sln
dotnet run --project src/QuizMMO.Server -- --Logging:LogLevel:Default=Debug --Server:StatusIntervalSec=2
dotnet run --project tools/QuizMMO.TestClient -- --clients 3 --duration 3     # 다른 터미널에서
```

로그의 `[tN]`이 스레드 번호입니다. 세션 연결/끊김은 매번 다른 번호(I/O 스레드), 로그인 처리는 항상 같은 번호(로직 스레드)로 찍히는 것을 확인하세요. 이 서버의 핵심 규칙이 눈에 보이는 순간입니다.

**1단계 — 전체 그림 (30분)**: `src/QuizMMO.Server/Program.cs` → `Gameplay/GameServer.cs`의 머리 주석, `StartAsync`, `Stop`.
확인 질문: 서버에 어떤 객체가 있고 어떤 순서로 켜지고 꺼지는가? 왜 리스너 → 세션 → 로직 스레드 순으로 멈추는가?

**2단계 — 프로토콜 (40분)**: `Protos/protocol.proto` → `PacketHeader.cs` → `ProtocolInfo.cs` → `PacketRegistry.cs` → `PacketSerializer.cs`.
생성된 코드는 `src/QuizMMO.Protocol/obj/` 아래에 있지만, VS에서 `CLogin`에 F12를 누르는 쪽이 빠릅니다.
확인 질문: 와이어에서 `SChat` 한 개는 어떤 바이트열인가? 헤더의 `size`에 헤더 4바이트가 포함되는가? `MsgId.CLogin`과 클래스 `CLogin`은 어떻게 연결되는가?

**3단계 — Core (30분)**: `Core/JobQueue.cs`(가장 중요) → `Clock.cs` → `RateLimitedLog.cs` → `ServerOptions.cs`.
확인 질문: `JobQueue.Drain`이 리스트를 **swap**하는 이유는? `Monitor.Wait`에 timeout을 주는 이유는? 왜 로그를 레이트리밋하는가?

**4단계 — Networking (2시간, 핵심)**: `Networking/Listener.cs` → `SessionManager.cs` → `Session.cs` → `PacketDispatcher.cs`.
`Session.cs`는 함수 순서가 아니라 흐름 순서로 읽습니다.
- 수신: `PostRecv → HandleRecv → ProcessPackets → PacketDispatcher.Dispatch → Clean → PostRecv`
- 송신: `Send → PostSend → HandleSend` ("진행 중 1개 + 큐 + gather"가 어떻게 지켜지는가)
- 종료: `Disconnect`의 `Interlocked.Exchange`, `DisconnectAfterFlush`
확인 질문: `ReceiveAsync`가 `false`를 돌려주면 무슨 뜻이고 왜 `while` 루프로 처리하는가? `BufferList`를 매번 다시 지정하는 이유는? 세션 객체는 언제 메모리에서 사라지는가?

**5단계 — Gameplay (1시간)**: `Gameplay/GameLoop.cs` → `GameHandlers.cs` → `GameServer.cs`의 `On*` → `Player.cs`, `Room.cs`, `WorldRoom.cs`, `QuizRoom.cs`, `QuizQueue.cs` → `GameServer.Tick / TryMatchQuiz / FinishQuizRoom`.
확인 질문: `Room`에 `lock`이 하나도 없는데 어떻게 안전한가? 핸들러가 `GameLoop.Post` 없이 룸을 직접 만지면 무슨 일이 생기는가? 퀴즈방이 끝나면 플레이어는 어떤 패킷을 어떤 순서로 받는가?

**6단계 — 테스트와 클라이언트 (40분)**: `tests/QuizMMO.Server.Tests/FakeSink.cs`와 테스트 5개 → `tools/QuizMMO.TestClient/Program.cs`.
확인 질문: 게임 로직이 `Session`이 아니라 `IMessageSink`에 의존하는 덕분에 무엇이 가능해졌는가? 클라이언트의 수신 루프와 `Session.ProcessPackets`는 어디가 같고 어디가 다른가?

**7단계 — C++ 대조 읽기 (선택, 1시간)**: 3부의 대응표를 보며 `legacy/cpp/src/networking/Session.cpp`와 `Session.cs`를 나란히 읽습니다.

### 1.3 관통하는 한 줄: 채팅 한 번의 여정

```
클라이언트 send(CChat)
  → [I/O 스레드] Session.HandleRecv → ProcessPackets → PacketDispatcher.Dispatch
  → GameHandlers의 CChat 핸들러: 길이 검증만 하고 GameLoop.Post(...)
  → [로직 스레드] GameServer.OnChat → Room.Chat → Room.Broadcast (한 번 직렬화)
  → Player.Send → Session.Send → PostSend → SendAsync
  → [I/O 스레드] HandleSend (큐가 남았으면 이어서 전송)
  → 같은 룸의 모든 클라이언트가 SChat 수신 (보낸 사람 포함)
```

### 1.4 직접 해 보기 (난이도순)

1. **스레드 관찰**: 0단계처럼 Debug 로그로 띄우고 접속 20명(`--clients 20`)을 붙여 `[tN]` 분포를 본다. `GameServer.OnLogin`, `Session.HandleRecv`, `Listener.HandleAccept`에 중단점을 걸고 VS의 "스레드" 창에서 어느 스레드인지 확인한다.
2. **설정만 바꿔 퀴즈방 흐름 보기**: `--Server:Quiz:RoomSize=2 --Server:Quiz:RoomDurationSec=5`로 띄우고 클라이언트 2개(`--clients 2 --duration 8`)를 붙인다. `SQuizQueueStatus(Matched)` → `SRoomChanged(Quiz)` → 5초 뒤 `SRoomChanged(World)` 순서를 확인한다.
3. **패킷 추가 (귓속말)**: `protocol.proto`에 `MSG_ID_C_WHISPER`, `MSG_ID_S_WHISPER`와 `CWhisper { uint64 target_player_id; string text; }`, `SWhisper { uint64 from_player_id; string nickname; string text; }`를 추가 → `PacketRegistry`에 두 줄 → `GameHandlers`에 핸들러(길이 검증 후 `Post`) → `GameServer.OnWhisper`(대상이 같은 룸에 있을 때만 전달) → 테스트 클라이언트에서 보내고 받기. 세 곳을 모두 고쳐야 동작한다는 것을 몸으로 익히는 과제입니다.
4. **깨진 패킷 보내기**: 테스트 클라이언트에 `--bad-header` 옵션을 만들어 `size=9999`인 헤더를 보내고, 서버가 `bad packet size` 로그 후 끊는지 확인한다. 이어서 `size=4, id=60000`(미등록 id)도 보내 본다.
5. **테스트 추가**: `QuizQueueTests`에 "Leave 뒤 TryTake가 남은 사람을 선착순으로 꺼낸다"를, `RoomTests`에 "Leave한 플레이어는 Chat을 받지 않는다"를 추가한다. 실패하는 테스트를 먼저 보고 나서 통과시키는 순서로.
6. **부하와 정원**: `--clients 300 --duration 10 --quiet`로 붙여 status 로그와 CPU를 본다. 그다음 `--Server:Net:MaxSessions=100`으로 띄워 101번째부터 거절되는지(`session limit ... reached` 로그) 확인한다.
7. **심화 — 할당 줄이기**: `PacketSerializer.Serialize`가 패킷마다 `byte[]`를 만든다. `ArrayPool<byte>`를 쓰면 어디서 빌리고 어디서 돌려줘야 하는지(브로드캐스트는 여러 세션이 같은 배열을 참조한다는 점) 설계해 본다. 바로 구현하기보다 "왜 지금은 안 해도 되는가"를 먼저 답해 본다.
8. **심화 — 로직 스레드 분할**: 룸이 수백 개가 되면 `GameLoop` 하나로 부족해진다. 룸 ID로 `GameLoop`를 N개 중 하나에 고정 배정할 때 `MovePlayer`(룸 간 이동)는 어떻게 안전하게 만들 수 있는지 생각해 본다.
9. **Unity 연결**: Unity 프로젝트에 `src/QuizMMO.Protocol/bin/Debug/netstandard2.1/QuizMMO.Protocol.dll`과 `Google.Protobuf.dll`을 `Assets/Plugins`에 넣고, `tools/QuizMMO.TestClient/Program.cs`의 접속·프레이밍 코드를 옮겨 로그인까지 해 본다.

### 1.5 흔한 실수와 디버깅 팁

- **핸들러에서 룸을 직접 만지면 안 된다.** 핸들러는 I/O 스레드에서 돈다. 반드시 `server.Loop.Post(...)`로 넘긴다. 어기면 간헐적으로 `Dictionary` 손상 같은 재현 안 되는 버그가 생긴다.
- **`xxxAsync(args)`가 `false`면 `Completed`는 오지 않는다.** 결과를 바로 처리해야 한다. 재귀로 처리하면 데이터가 몰릴 때 스택이 깊어지므로 `Session`/`Listener`는 `while` 루프를 쓴다.
- **`SocketAsyncEventArgs` 재사용 규칙**: accept용은 `AcceptSocket = null`로 비우고 다시 건다. 송신용은 `BufferList`를 매번 다시 지정한다 (내부에 복사해 둔다).
- **`GameLoop.Stop()`을 로직 스레드 안에서 부르면 교착한다** (자기 자신을 `Join`). 종료는 호스트 스레드에서.
- **protobuf는 잘린 본문을 관대하게 읽는다.** 빠진 필드는 기본값(0, 빈 문자열)이 된다. 그래서 길이·범위 검증은 핸들러에서 직접 한다 (`ProtocolInfo` 상수).
- **설정이 안 먹을 때**: Generic Host는 현재 디렉터리의 `appsettings.json`을 읽는다. `Program.cs`가 실행 파일 옆의 파일도 보조로 읽게 해 두었고, 커맨드라인 `--Server:...=값`이 항상 우선한다.
- **디버깅 시작점**: `Session.HandleRecv`(모든 수신), `PacketDispatcher.Dispatch`(어떤 패킷이 왔나), `GameLoop.RunPending`(로직 스레드 입구), `GameServer.Tick`(매 50ms).

### 1.6 더 읽을 자료

- SocketAsyncEventArgs: https://learn.microsoft.com/dotnet/api/system.net.sockets.socketasynceventargs
- .NET 소켓 서비스 가이드: https://learn.microsoft.com/dotnet/fundamentals/networking/sockets/socket-services
- Generic Host와 설정 우선순위: https://learn.microsoft.com/dotnet/core/extensions/generic-host , https://learn.microsoft.com/dotnet/core/extensions/configuration
- protobuf proto3 가이드와 C# 생성 코드: https://protobuf.dev/programming-guides/proto3/ , https://protobuf.dev/reference/csharp/csharp-generated/
- 소켓 모델별 함정 정리: 이 문서 1.5절과 3.2절의 "동기 완료", "SAEA 재사용", "protobuf 관대한 파싱" 항목

---

## 2. 서버 구조와 클래스 역할

### 2.1 한 장 그림

```
                 Unity / TestClient                       (TCP, {u16 size, u16 id} + protobuf)
                         │
   ┌─────────────────────┼─────────────────── I/O 스레드 (.NET ThreadPool, 여러 개) ─────────────────┐
   │   Listener ──AcceptAsync──▶ SessionManager.Accept ──▶ Session (수신 1개·송신 1개 진행)           │
   │                                                       │ ProcessPackets → PacketDispatcher      │
   │                                                       │ GameHandlers: 필드 검증만              │
   └───────────────────────────────────────────────────────┼────────── GameLoop.Post(작업) ──────────┘
                                                           ▼
   ┌─────────────────────── 로직 스레드 (GameLoop, 1개, 락 없음) ──────────────────────────────────┐
   │   GameServer.On*  →  WorldRoom / QuizRoom(들) / QuizQueue  →  Player.Send → Session.Send     │
   │   GameServer.Tick(dt) 매 50ms: 매칭 검사, QuizRoom.Update, 상태 로그                           │
   └────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### 2.2 프로젝트와 의존 방향

| 프로젝트 | 타깃 | 참조 | 역할 |
|---|---|---|---|
| `QuizMMO.Protocol` | netstandard2.1 | Google.Protobuf | 와이어 포맷. 서버와 Unity가 공유 |
| `QuizMMO.Server` | net10.0 | Protocol, Microsoft.Extensions.Hosting | 서버 본체 (Core ← Networking ← Gameplay) |
| `QuizMMO.TestClient` | net10.0 | Protocol | 검증용 클라이언트 |
| `QuizMMO.Server.Tests` | net10.0 | Server, xunit | 단위 테스트 |

서버 안에서도 `Core`는 아무것도 모르고, `Networking`은 `Core`만 알고, `Gameplay`가 둘을 씁니다. `Networking`이 `Player`를 모르게 하려고 `Session.Context`(object)와 `IMessageSink`를 둡니다.

### 2.3 클래스별 역할

**QuizMMO.Protocol**

| 파일 | 역할 | 알아둘 점 |
|---|---|---|
| `Protos/protocol.proto` | 메시지 정의와 `MsgId`, `LoginResult`, `QueueState`, `RoomType` 열거형 | 빌드 시 Grpc.Tools가 C# 클래스(`CLogin`, `SChat` …)를 생성. 열거형 값의 접두(`MSG_ID_`)는 C#에서 잘림 |
| `PacketHeader` | 4바이트 헤더 읽기/쓰기, `IsValid`(4..4096) | `size`는 헤더 포함 전체 길이. 리틀엔디언 |
| `ProtocolInfo` | 프로토콜 버전(1)과 필드 범위(닉네임 1..32바이트, 채팅 ≤400) | 레이아웃을 바꾸면 버전을 올린다 |
| `PacketRegistry` | `MsgId` ↔ 메시지 타입/파서 매핑 (정적 생성자에서 12개 등록) | 새 메시지는 여기 한 줄 추가. `GetId<T>()`가 송신 쪽 오류를 즉시 드러낸다 |
| `PacketSerializer` | `Serialize(IMessage) → byte[]`, `TryDeserialize(id, body) → IMessage` | 브로드캐스트는 한 번 직렬화한 배열을 공유. 깨진 본문은 `false` |

**QuizMMO.Server / Core**

| 클래스 | 역할 | 알아둘 점 |
|---|---|---|
| `ServerOptions`, `SmokeOptions` | `appsettings.json`의 `Server`, `Smoke` 섹션 바인딩. `Normalize`가 범위 보정 | 수치를 코드에 박지 않기 위한 유일한 입구 |
| `Clock` | 단조 시계(`NowMs`)와 유닉스 시각(`UnixTimeMs`) | 타임아웃은 단조 시계, 핑/퐁은 유닉스 시각 |
| `RateLimitedLog` / `LogRateLimited` | 같은 key로 일정 간격에 1회만 로그 | 네트워크 오류 로그 폭주 방지 |
| `JobQueue` | 여러 스레드가 `Post`, 로직 스레드가 `Drain`(swap) | `Monitor.Wait/Pulse`로 틱 간격까지만 잔다. 스레드 모델의 심장 |

**QuizMMO.Server / Networking**

| 클래스 | 역할 | 알아둘 점 |
|---|---|---|
| `IMessageSink` | 게임 계층이 보는 "보낼 수 있는 상대" | `Session`이 구현, 테스트는 `FakeSink` |
| `RecvBuffer` | 세션당 수신 버퍼. `OnWrite/OnRead/Clean` | 최대 패킷(4096)보다 2배 이상 커야 항상 수신 공간이 남는다 |
| `Listener` | 리슨 소켓, `AcceptAsync` N개 선행 게시, 수락 시 `SessionManager.Accept` | `ExclusiveAddressUse`, `NoDelay`. 동기 완료는 루프로 |
| `SessionManager` | 세션 표(`ConcurrentDictionary`), 정원, `DisconnectAll`, `SessionClosed` 이벤트 | 끊김은 I/O 스레드에서 알려오고 게임 계층이 `Post`로 넘긴다 |
| `Session` | 소켓 하나. SAEA 2개(수신/송신), 프레이밍, 송신 큐, `Disconnect` 1회 보장 | 수신 1개·송신 1개 진행. `DisconnectAfterFlush`는 거절 응답을 보낸 뒤 끊을 때 |
| `PacketDispatcher` | `MsgId` → 핸들러. `Register<T>`가 타입으로 등록, `Dispatch`가 파싱 후 호출 | 미등록 id·깨진 본문·핸들러 거절 모두 `false` → 세션 끊김 |

**QuizMMO.Server / Gameplay** (모두 로직 스레드 전용)

| 클래스 | 역할 | 알아둘 점 |
|---|---|---|
| `Player` | 로그인한 유저. `Id`, `Nickname`, `RoomId`, `InQuizQueue`, `Link`(IMessageSink) | `Session.Context`로 붙는다 |
| `Room` (추상) | 플레이어 집합. `Enter/Leave`가 입퇴장 알림을 보냄, `Chat`, `Broadcast`, `Update(dt)` | 입장자에게 기존 인원 전체를 보낸다 (AOI 없음, 의도된 단순화) |
| `WorldRoom` | 광장. 지금은 `Room` 그대로 | 이동 동기화·거래의 확장 지점 |
| `QuizRoom` | 퀴즈 인스턴스. 지금은 `RoomDurationSec` 카운트다운만 | 퀴즈 상태 머신(출제/채점/결과)의 확장 지점 |
| `QuizQueue` | 선착순 대기열. `Join/Leave/TryTake` | `Player.InQuizQueue`는 이 클래스만 바꾼다 |
| `GameLoop` | 전용 로직 스레드. `Post`, 틱(`TickHandler(dt)`), 종료 시 남은 작업 처리 | 틱이 밀리면 따라잡기를 포기하고 다시 센다 |
| `GameHandlers` | C_ 패킷 5개의 핸들러 등록. 검증 후 `Post` | `CPing`만 I/O 스레드에서 바로 응답 (상태가 없으므로) |
| `GameServer` | 조립과 수명(`IHostedService`), `On*` 게임 명령, `Tick`(매칭·퀴즈방 종료·상태 로그) | 룸 간 이동은 `MovePlayer` 하나로만 |
| `SmokeStopper` | `Smoke:Seconds` 뒤 호스트 종료 | 자동 테스트·기동 확인용 |
| `Program.cs` | Generic Host 구성: 설정 소스, 콘솔 로거, DI 등록 | `--Key=Value`가 파일 설정보다 우선 |

**tools / tests**

| 항목 | 역할 |
|---|---|
| `QuizMMO.TestClient/Program.cs` | `TcpClient`로 접속 → `CLogin` → 채팅/핑/대기열 → 수신 출력. 종료 코드로 합격 판정. 옵션 `--clients --duration --version --no-queue --quiet` |
| `FakeSink` | 받은 패킷을 헤더 검증 후 메시지로 풀어 기록. 룸/대기열 테스트의 상대 |
| `RecvBufferTests`, `PacketSerializerTests` | 프레이밍, 분할 수신 복원, 헤더 범위, 깨진 본문, 레지스트리 완전성 |
| `QuizQueueTests`, `RoomTests`, `GameLoopTests` | 대기열 FIFO, 입퇴장 알림과 채팅 브로드캐스트, 로직 스레드 실행 보장 |

### 2.4 스레드 규칙 요약

- I/O 스레드(.NET ThreadPool): 소켓 완료, 프레이밍, protobuf 파싱, 필드 범위 검증, `CPing` 응답. **여기서 룸·플레이어·대기열을 만지지 않는다.**
- 로직 스레드(`GameLoop`, 1개): `GameServer.On*`, `Room`, `QuizQueue`, `Tick`. 이 스레드만 게임 상태를 만지므로 게임 코드에 락이 없다.
- 두 세계의 다리: `GameLoop.Post`(I/O → 로직)와 `Session.Send`(로직 → I/O, 내부 락으로 보호).
- 세션이 끊기면 `SessionManager.SessionClosed`(I/O 스레드) → `Post` → `GameServer.OnSessionClosed`(로직 스레드)에서 대기열·룸 정리.

### 2.5 시작과 종료 순서

시작: 핸들러 등록 → `SessionManager` → `WorldRoom` → `GameLoop.Start` → `Listener.Start`.
종료(`Ctrl+C` 또는 `Smoke`): `Listener.Stop`(새 접속 거부) → `SessionManager.DisconnectAll`(모든 세션 끊김 → 퇴장 작업이 `Post`됨) → `GameLoop.Stop`(남은 작업 실행 후 스레드 종료).

### 2.6 설정 키 (`appsettings.json` → `ServerOptions`)

| 키 | 기본값 | 의미 |
|---|---|---|
| `Server:Net:Port`, `BindAddress` | 7777, 0.0.0.0 | 리슨 주소 |
| `Server:Net:MaxSessions` | 2000 | 초과 접속은 즉시 닫힘 |
| `Server:Net:RecvBufferBytes` | 65536 | 세션당 수신 버퍼 (최소 8192로 보정) |
| `Server:Net:AcceptBacklog` | 64 | 미리 걸어두는 `AcceptAsync` 수 |
| `Server:Game:TickHz`, `WorldName` | 20, Plaza | 로직 틱, 광장 이름 |
| `Server:Quiz:RoomSize` | 20 | 퀴즈방 정원 |
| `Server:Quiz:MatchCheckIntervalSec` | 1.0 | 매칭 검사 주기 |
| `Server:Quiz:RoomDurationSec` | 30.0 | 퀴즈방 자리표시 길이 (퀴즈 로직 전까지) |
| `Server:StatusIntervalSec` | 10 | 상태 로그 주기 (0 이하면 끔) |
| `Smoke:Seconds` | 0 | 0보다 크면 그 시간 뒤 자동 종료 |

### 2.7 프로토콜 요약

| MsgId | 방향 | 본문 | 설명 |
|---|---|---|---|
| `CLogin` (1) | C→S | `protocol_version`, `nickname` | 버전 불일치·닉네임 길이 위반은 `SLogin` 거절 후 끊김 |
| `SLogin` (2) | S→C | `result`, `player_id`, `room_id` | 성공 시 월드 룸 입장 |
| `CChat` (10) / `SChat` (11) | C→S / S→C | `text` / `player_id`, `nickname`, `text` | 같은 룸 전원에게 (본인 포함) |
| `CPing` (20) / `SPong` (21) | C→S / S→C | `client_time_ms` / `+ server_time_ms` | I/O 스레드에서 즉시 응답 |
| `CQuizQueueJoin` (30), `CQuizQueueLeave` (31) | C→S | 없음 | 월드에서만 참가 가능 |
| `SQuizQueueStatus` (32) | S→C | `state`, `waiting`, `required` | 대기 인원 변동 시 대기자 전원에게 |
| `SRoomChanged` (40) | S→C | `room_id`, `room_type`, `room_name` | 내가 들어간 룸 |
| `SPlayerEnter` (41) / `SPlayerLeave` (42) | S→C | `player_id`(, `nickname`) | 같은 룸의 입퇴장 알림 |

---

## 3. C++ IOCP 서버와의 차이

이전 버전은 `legacy/cpp/`에 그대로 있습니다 (Windows 전용, CMake 또는 `QuizMMOServer.sln`). 설계는 같은 사람이 같은 목표로 만든 것이라 **무엇이 같은지**를 먼저 보고, 그다음 **무엇이 달라졌는지**를 보면 .NET이 어디까지 대신해 주는지 분명해집니다.

### 3.1 같은 것 (설계)

- 스레드 모델: I/O 스레드는 파싱·검증만, 로직 스레드 하나가 룸을 소유, 둘 사이는 작업 큐. C++의 `JobQueue`/`GameLoop`와 C#의 `JobQueue`/`GameLoop`는 거의 같은 코드다.
- 프레이밍: `{u16 size, u16 id}` 헤더, 수신 버퍼 압축(`Clean`), 완전한 패킷만 꺼내는 루프.
- 세션 규칙: 수신 1개·송신 1개 진행, 송신 큐 + gather, 잘못된 패킷은 끊기, 거절 응답 후 끊기(`DisconnectAfterFlush`).
- 룸/대기열/매칭/퀴즈방 복귀 로직, 설정 주도 수치, 레이트리밋 로그, 서버 권위 검증.

### 3.2 달라진 것 (대응표)

| 항목 | C++ (legacy/cpp) | C# (.NET 10) | 왜 달라졌나 |
|---|---|---|---|
| 완료 포트 | `CreateIoCompletionPort`, 워커 스레드 N개가 `GetQueuedCompletionStatus` 루프, `CONTAINING_RECORD`로 `OverlappedEx` 복원, `PostQueuedCompletionStatus`로 종료 (`Iocp.h/.cpp`) | **없음.** `Socket.ReceiveAsync/SendAsync(SocketAsyncEventArgs)`가 내부적으로 IOCP(Windows)/epoll(Linux)를 쓰고 `Completed` 이벤트가 ThreadPool에서 호출됨 | 런타임이 완료 포트와 워커 풀을 소유한다 |
| 수락 | `WSAIoctl`로 `AcceptEx` 로딩, 주소 버퍼, `SO_UPDATE_ACCEPT_CONTEXT` | `AcceptAsync(args)`, `args.AcceptSocket` | 확장 함수 로딩이 API에 내장됨 |
| 동기 완료 | IOCP는 즉시 완료되어도 완료 패킷을 큐에 넣으므로 항상 같은 경로 | `xxxAsync`가 `false`면 동기 완료이고 `Completed`가 **안 온다** → `Session`/`Listener`가 `while` 루프로 직접 처리 | .NET의 최적화. 가장 자주 틀리는 부분 |
| 객체 수명 | `std::shared_ptr` + `OverlappedEx::owner`로 I/O 진행 중 세션 생존 보장, `Player → Session`은 `weak_ptr`, 소멸자에서 소켓 닫기 | GC. SAEA가 소켓·버퍼를 참조하므로 완료 전에는 수거되지 않음. 순환 참조도 무해 | 수명 관리 코드가 통째로 사라짐 |
| 오류 코드 | `WSAGetLastError()`/`GetLastError()` 숫자 (64, 995, 1236, 10054…)와 `WsaErrorName` | `SocketAsyncEventArgs.SocketError` 열거형(`ConnectionReset`, `OperationAborted` …) | 플랫폼 중립 열거형 |
| 송신 버퍼 | `SendBuffer` 클래스, `WSABUF[]` gather | `byte[]` + `List<ArraySegment<byte>>`를 `BufferList`에 지정 | 표준 타입으로 충분 |
| 직렬화 | 수기 `PacketReader/Writer`(길이 접두 문자열, 실패 시 "오염" 플래그), `Protocol.h` 주석이 레이아웃 문서 | protobuf: `.proto`가 스키마이자 문서, 생성 코드, 필드 추가 시 하위 호환, Unity와 공유 | Unity 클라이언트와 코드 공유. 단, 잘린 본문을 관대하게 읽으므로 의미 검증은 핸들러가 담당 |
| 디스패처 | `id → handler(Session&, PacketReader&)` | `Register<T>(Func<Session,T,bool>)`: 타입으로 등록, `PacketRegistry`가 id를 찾음 | 핸들러가 바이트가 아니라 메시지 객체를 받는다 |
| 응답 조립 | `PacketBuilders`(필드별 `Write`) | `new SChat { ... }` 객체 초기화 + `PacketSerializer.Serialize` | 빌더 코드가 사라짐 |
| 설정 | `config/server.cfg` key=value 파서 직접 작성 | `appsettings.json` + 옵션 바인딩 + `--Key=Value` 덮어쓰기 | 프레임워크 제공 |
| 로깅 | `printf` 로거, `LOG_RATE_LIMITED` 매크로, 스레드 태그 | `ILogger` 구조적 로깅, `LogRateLimited` 확장 메서드 | 매크로 대신 확장 메서드 |
| 스레드 생성 | `std::thread` 워커 N개를 직접 만들고 `PostQuit`으로 종료 | ThreadPool이 I/O 스레드를 관리. 로직 스레드만 직접 생성 | 워커 수 튜닝이 런타임으로 이동 |
| 종료 | `SetConsoleCtrlHandler` + 100ms 폴링, `--smoke` 인자 파싱 | Generic Host가 Ctrl+C → `StopAsync`, `SmokeStopper` 서비스 | 호스트 수명 관리 |
| 테스트 | 자체 하네스(`CHECK` 매크로) + CTest, 소켓 없이 테스트하려면 코드 구조상 어려움 | xUnit + `IMessageSink`/`FakeSink`로 룸·대기열을 네트워크 없이 테스트 | 인터페이스 주입이 쉬워짐 |
| 테스트 클라이언트 | 블로킹 Winsock + 스레드/클라이언트 | `TcpClient` + `async/await` + `Task.WhenAll` | 같은 역할, 더 짧음 |
| 빌드/플랫폼 | CMake 또는 vcxproj, MSVC, Windows 전용 | `dotnet build`, 크로스 플랫폼 (Linux 배포 가능) | |
| 문자열/인코딩 | UTF-8 리터럴과 CP949 콘솔 충돌 → 로그 ASCII 강제 | `string`은 UTF-16, protobuf가 UTF-8 변환. 길이 검증은 `Encoding.UTF8.GetByteCount` | 콘솔 인코딩 문제는 그대로 (로그는 영어 유지) |

### 3.3 파일 대응표

| C++ `legacy/cpp/src/...` | C# |
|---|---|
| `core/Log.h/.cpp` | `ILogger` + `Core/RateLimitedLog.cs` |
| `core/Config.h/.cpp`, `config/server.cfg` | `Core/ServerOptions.cs`, `appsettings.json` |
| `core/JobQueue.h` | `Core/JobQueue.cs` |
| `core/Clock.h` | `Core/Clock.cs` |
| `networking/NetCommon.h/.cpp` (`OverlappedEx`, `IocpObject`, `WsaInitializer`) | 없음 (`SocketAsyncEventArgs`) |
| `networking/Iocp.h/.cpp` (`IocpCore`, `IocpWorkerPool`) | 없음 (런타임 ThreadPool) |
| `networking/Listener.h/.cpp` | `Networking/Listener.cs` |
| `networking/Session.h/.cpp`, `RecvBuffer`, `SendBuffer.h` | `Networking/Session.cs`, `RecvBuffer.cs` (`SendBuffer` → `byte[]`) |
| `networking/Packet.h/.cpp` (`PacketReader/Writer`) | `Protocol/PacketHeader.cs`, `PacketSerializer.cs` + protobuf |
| `networking/Protocol.h` | `Protocol/Protos/protocol.proto`, `ProtocolInfo.cs`, `PacketRegistry.cs` |
| `networking/PacketDispatcher.h/.cpp` | `Networking/PacketDispatcher.cs` |
| `networking/SessionManager.h/.cpp` | `Networking/SessionManager.cs` |
| `gameplay/PacketBuilders.h/.cpp` | 없음 (protobuf 객체 초기화) |
| `gameplay/Player, Room, WorldRoom, QuizRoom, QuizQueue, GameLoop, GameHandlers, GameServer` | 같은 이름의 `.cs` |
| `main.cpp` | `Program.cs` + `Gameplay/SmokeStopper.cs` |
| `tools/testclient/main.cpp` | `tools/QuizMMO.TestClient/Program.cs` |
| `tests/unit/networking/*_test.cpp` | `tests/QuizMMO.Server.Tests/*.cs` |

### 3.4 성능과 트레이드오프 (솔직한 메모)

- 둘 다 패킷마다 송신 버퍼를 한 번 할당합니다 (C++ `SendBuffer`, C# `byte[]`). C#은 GC가 회수하므로 트래픽이 커지면 Gen0 GC가 늘어납니다. 지금 규모(수천 세션, 작은 패킷)에서는 문제가 아니고, 필요해지면 `ArrayPool<byte>`나 `System.IO.Pipelines`로 옮깁니다. 측정 전에 최적화하지 않습니다.
- C++은 워커 스레드 수를 설정(`net.worker_threads`)으로 잡았지만 .NET은 ThreadPool이 알아서 늘립니다. 로직 스레드가 하나라는 점은 같으므로 병목은 양쪽 모두 로직 스레드에서 먼저 나타납니다.
- C++ IOCP 코드를 읽어 두면 .NET이 "무엇을 대신해 주는지"가 보입니다. 반대로 C# 버전을 먼저 이해하고 C++을 보면 `OverlappedEx::owner` 같은 수명 관리가 왜 필요했는지 이해됩니다. 둘을 번갈아 보는 것이 이 저장소를 두 버전으로 남겨 둔 이유입니다.

---

## 부록 A. 선행 지식 정리

1.1절 체크리스트의 각 항목을 이 서버에 필요한 만큼만 설명합니다. 예시는 모두 저장소 코드에 실제로 있는 형태입니다.

### A.1 C# 기초 문법 (C++ 개발자용)

**C++과 가장 다른 다섯 가지**

1. **메모리는 GC가 관리한다.** `new`만 있고 `delete`는 없습니다. 참조가 끊기면 언젠가 수거됩니다. 소멸자 대신 "자원 해제가 필요한 객체"는 `IDisposable`을 구현하고 `using`으로 씁니다.
   ```csharp
   using var client = new TcpClient();   // 블록을 벗어날 때 Dispose() 자동 호출
   ```
2. **값 타입과 참조 타입이 나뉜다.** `struct`, `int`, `enum`은 값 타입이라 대입하면 복사됩니다. `class`, `string`, 배열, 델리게이트는 참조 타입이라 대입하면 같은 객체를 가리킵니다. C++의 `shared_ptr<T>`가 기본이라고 생각하면 비슷합니다. 작고 불변인 `PacketHeader`는 `readonly struct`, 상태를 가진 `Session`은 `class`인 이유입니다.
3. **헤더 파일이 없고 선언 순서가 상관없다.** 한 파일에 클래스 여러 개를 둘 수 있고, 아래에 선언한 메서드를 위에서 불러도 됩니다. 대신 `namespace`와 `using`으로 묶습니다.
   ```csharp
   using QuizMMO.Protocol;                 // #include + using namespace 역할
   namespace QuizMMO.Server.Networking;    // 파일 전체에 적용되는 네임스페이스
   ```
4. **자유 함수가 없다.** 모든 함수는 어떤 타입의 멤버입니다. 유틸 함수는 `static class`에 둡니다 (`Clock`, `PacketSerializer`). 예외는 `Program.cs`의 최상위 문장인데, 컴파일러가 `Main`으로 감싸 줍니다.
5. **`null` 가능 여부를 타입이 표시한다.** `string`은 null이 아니어야 하고 `string?`은 null일 수 있습니다.
   ```csharp
   Player? player = session.Context as Player;   // 캐스팅 실패하면 null (예외 없음)
   if (player is null) return;
   var name = player?.Nickname ?? "unknown";      // null이면 기본값
   ```

**타입과 변수.** `int`(32비트), `long`(항상 64비트), `uint`, `ulong`, `ushort`, `byte`, `float`, `double`, `bool`, `string`, `char`(UTF-16 한 단위). 크기가 플랫폼에 따라 변하지 않습니다. `var`는 타입 추론(`auto`), `const`는 컴파일 타임 상수, `readonly`는 생성자 이후 불변인 필드입니다.

```csharp
public const int MaxPacketSize = 4096;
private readonly Socket _socket;
var header = PacketHeader.Read(span);
```

**문자열.** 불변 객체이고 `==`가 내용을 비교합니다. `$"..."`로 보간합니다. `Length`는 문자 수이고 바이트 수가 아니므로, 바이트 제한은 `Encoding.UTF8.GetByteCount(s)`로 검사합니다.

**제어문.** `if`, `for`, `while`은 같습니다. `foreach`가 기본 순회이고 순회 중 컬렉션을 바꾸면 예외가 납니다 (그래서 `Room.Players()`가 복사본을 돌려줍니다). `switch`는 각 `case`가 `break`/`return`으로 끝나야 하고(fallthrough 금지), 타입으로 분기할 수 있습니다.

```csharp
switch (message)
{
    case SLogin m: Console.WriteLine(m.Result); break;   // m은 SLogin으로 캐스팅됨
    case SChat m:  Console.WriteLine(m.Text);   break;
}
foreach (var (id, room) in _quizRooms) room.Update(dt);  // Dictionary 항목 분해
```

**클래스와 프로퍼티.** 필드는 `private`, 바깥에는 프로퍼티로 노출합니다. 프로퍼티는 겉보기엔 변수지만 안은 getter/setter 함수입니다.

```csharp
public sealed class Player                       // sealed: 상속 금지 (C++ final)
{
    public ulong Id { get; }                     // 읽기 전용 (생성자에서만 대입)
    public uint RoomId { get; set; }             // 읽기/쓰기
    public bool IsConnected => Link.IsConnected; // 계산 프로퍼티
    public Player(ulong id, string nickname, IMessageSink link) { Id = id; Nickname = nickname; Link = link; }
}
```

접근 제한자는 `public`, `private`, `protected`, `internal`(같은 어셈블리). 상속은 `: Base`, 가상 함수는 `virtual`/`override`, 순수 가상은 `abstract`, 인터페이스는 `interface`(이름은 `I`로 시작).

```csharp
public abstract class Room { public abstract RoomType Type { get; } public virtual void Update(float dt) { } }
public sealed class QuizRoom : Room { public override RoomType Type => RoomType.Quiz; }
public interface IMessageSink { bool IsConnected { get; } void Send(byte[] packet); }
```

**struct와 enum.** `struct`는 값 타입이라 복사가 싸고 GC를 거치지 않습니다. 작고 불변인 데이터에만 씁니다. `enum`은 정수 기반이고 정수로 쓰려면 명시적 캐스팅이 필요합니다 (`(ushort)MsgId.CLogin`).

**제네릭.** C++ 템플릿과 비슷하지만 런타임 타입이고, 제약을 `where`로 씁니다.

```csharp
public void Register<T>(Func<Session, T, bool> handler) where T : class, IMessage<T>
```

**컬렉션.** `List<T>`(vector), `Dictionary<K,V>`(unordered_map), `Queue<T>`, `HashSet<T>`, 배열 `byte[]`. 스레드 안전 버전은 `ConcurrentDictionary`, `ConcurrentQueue`.

**예외.** `try/catch/finally`, 타입별 `catch`. 반환 코드 대신 예외가 기본이라 소켓 함수는 `SocketException`을 던집니다. 실패가 정상 흐름인 곳은 `bool TryXxx(out result)` 패턴을 씁니다 (`TryDeserialize`, `TryGetValue`).

**C++ 출신이 자주 틀리는 것.** `if (count)`처럼 정수를 조건에 쓸 수 없습니다. 배열도 참조 타입이라 함수에 넘기면 원본이 바뀝니다. `ref`/`out`은 호출하는 쪽에도 써야 합니다 (`Drain(ref _pending, wait)`). 정수 나눗셈은 정수이므로 `1000 / tickHz`는 `1000.0 / tickHz`로 씁니다. `struct`를 `List`에서 꺼내 수정하면 복사본을 수정하는 것입니다.

### A.2 스레드와 동기화

**왜 필요한가.** 두 스레드가 같은 메모리를 동시에 읽고 쓰면(데이터 레이스) 결과가 실행마다 달라집니다. `Dictionary`에 두 스레드가 동시에 `Add`하면 내부 구조가 깨지고, `count++`는 "읽기, 더하기, 쓰기" 세 단계라 둘이 겹치면 한 번이 사라집니다. 이런 버그는 재현이 안 되고 로그에도 안 남습니다. 그래서 **규칙으로 예방**해야 하며, 이 서버는 모든 클래스 주석에 "어느 스레드에서 실행되는가"를 적어 둡니다.

**이 서버의 전략: 공유를 최소화한다.** 게임 상태(룸, 플레이어, 대기열)는 로직 스레드 하나만 만지므로 게임 코드에는 동기화가 없습니다. I/O 스레드는 상태를 직접 바꾸는 대신 "할 일"을 `JobQueue`에 넣습니다 (메시지 전달 방식). 그래서 스레드가 만나는 지점이 `JobQueue`, `Session`의 송신 큐, `SessionManager`의 세션 표, 로그 레이트리밋 네 곳으로 줄고, 그 네 곳만 아래 도구로 보호합니다.

**`lock`: 상호 배제.** `lock (obj) { ... }`는 한 번에 한 스레드만 블록에 들어가게 합니다. 내부적으로 `Monitor.Enter/Exit`를 `try/finally`로 감싼 것이라 예외가 나도 풀립니다. C++의 `std::lock_guard<std::mutex>`와 같습니다.

```csharp
private readonly object _sendLock = new();     // 잠그는 용도로만 쓰는 전용 객체
bool start;
lock (_sendLock)
{
    _sendQueue.Enqueue(packet);                // 락 안: 짧게, 자료구조만 건드린다
    start = !_sendInFlight;
    if (start) _sendInFlight = true;
}
if (start) PostSend();                         // 락 밖: I/O 호출은 락 밖에서
```

규칙 세 가지. (1) 락 객체는 `private readonly object` 전용 필드로 둡니다. `this`, `string`, 공개된 객체로 잠그면 밖에서 같은 객체를 잠가 교착을 만들 수 있습니다. (2) 락 안에서는 자료구조 조작만 하고, I/O·콜백·대기(`Join`, `Wait`)는 하지 않습니다. (3) C#의 `lock`은 재진입이 됩니다 (같은 스레드가 다시 잠가도 교착 없음). 그래도 설계상 재진입을 믿지 않는 편이 안전합니다.

**`Monitor.Wait/Pulse`: 조건 변수.** "조건이 될 때까지 잠들기"입니다. `Wait`는 락을 풀고 잠들다가 `Pulse`(하나) 또는 `PulseAll`(전부)로 깨어나며 락을 다시 잡습니다. 타임아웃을 주면 그 시간 뒤 스스로 깨어납니다. `JobQueue`가 이것으로 "작업이 오거나 다음 틱 시각이 되면" 깨어납니다.

```csharp
public void Post(Action job)
{
    lock (_lock) { _jobs.Add(job); Monitor.Pulse(_lock); }      // 넣고 깨운다
}
public int Drain(ref List<Action> into, TimeSpan timeout)
{
    into.Clear();
    lock (_lock)
    {
        if (_jobs.Count == 0 && timeout > TimeSpan.Zero) Monitor.Wait(_lock, timeout);   // 락을 풀고 잠든다
        (into, _jobs) = (_jobs, into);                                                    // swap: 복사 없이 통째로 넘긴다
    }
    return into.Count;
}
```

깨어났을 때 조건이 참이라는 보장이 없습니다 (타임아웃, 가짜 깨어남). 그래서 `Drain`은 "깨어나면 그냥 있는 것을 다 가져간다"로 설계해 조건 재검사가 필요 없게 했습니다. 일반적인 패턴은 `while (!조건) Monitor.Wait(lock);` 입니다.

**`Interlocked`: 원자 연산.** 한 번에 끝나는 읽기-수정-쓰기입니다. 락 없이 카운터를 올리거나(`Increment`), 값을 바꾸면서 이전 값을 받거나(`Exchange`), 기대값일 때만 바꿉니다(`CompareExchange`). `Session.Disconnect`가 "여러 스레드가 동시에 불러도 한 번만 실행"을 이것으로 보장합니다.

```csharp
private int _connected = 1;
public void Disconnect(string reason)
{
    if (Interlocked.Exchange(ref _connected, 0) == 0) return;   // 1→0 으로 바꾼 스레드 하나만 통과
    ...
}
var id = (ulong)Interlocked.Increment(ref _nextId);             // 락 없는 ID 발급
```

**`Volatile`과 `volatile`: 보이게 하기.** 컴파일러와 CPU는 성능을 위해 읽기/쓰기를 캐시하거나 순서를 바꿀 수 있어서, 한 스레드가 바꾼 일반 필드를 다른 스레드가 영원히 옛 값으로 볼 수도 있습니다. `volatile` 필드나 `Volatile.Read/Write`는 "항상 메모리에서 읽고 바로 쓰며 순서를 지켜라"는 표시입니다. 단순 플래그(`GameLoop._running`, `Listener._running`, `Session.Context`)에 적합하고, `count++` 같은 복합 연산에는 부적합합니다 (그건 `Interlocked`).

```csharp
private volatile bool _running;                                   // 루프 종료 플래그
public bool IsConnected => Volatile.Read(ref _connected) == 1;
public object? Context { get => Volatile.Read(ref _context); set => Volatile.Write(ref _context, value); }
```

`lock` 획득/해제, `Interlocked`, `Volatile`는 모두 메모리 배리어 역할도 하므로, 락 안에서 쓴 값은 다음에 같은 락을 잡는 스레드에게 보입니다. 그래서 락으로 보호되는 필드에는 `volatile`이 필요 없습니다.

**동시성 컬렉션.** `ConcurrentDictionary`, `ConcurrentQueue`는 개별 연산(`TryAdd`, `TryRemove`, `Enqueue`)이 스레드 안전합니다. 하지만 "확인한 뒤 행동"하는 두 단계는 안전하지 않습니다. `SessionManager.Accept`가 `Count`를 보고 `_sessions[id] = session`을 하는 사이 다른 스레드가 끼어들 수 있어 정원이 몇 명 넘을 수 있는데, 정확한 정원이 필요한 수치가 아니라서 허용했습니다. 정확해야 하면 `lock`을 쓰거나 `TryAdd`의 반환값으로 결정합니다.

**교착(deadlock) 피하기.** 락을 두 개 이상 잡을 때 스레드마다 순서가 다르면 교착입니다. 이 서버는 락 안에서 다른 락을 잡지 않도록 설계해 이 문제를 없앴습니다. 또 하나의 전형은 "스스로를 기다리기"인데, `GameLoop.Stop()`을 로직 스레드 안에서 부르면 자기 자신을 `Join`해서 영원히 멈춥니다. 종료는 반드시 호스트 스레드에서 합니다.

**ThreadPool과 전용 스레드.** 짧고 많은 작업(소켓 완료 처리)은 런타임의 ThreadPool이 맡습니다. 어느 스레드에서 실행될지 모르고 순서도 보장되지 않습니다. 순서와 상태가 중요한 긴 루프(게임 로직)는 `new Thread(Run)`으로 전용 스레드를 만듭니다. `IsBackground = true`로 두면 프로세스 종료를 막지 않습니다.

**디버깅.** 재현이 안 되면 로그에 스레드 번호를 남기고(`Environment.CurrentManagedThreadId`, 이 서버의 `[tN]`), VS의 디버그 > 창 > 스레드에서 중단점이 어느 스레드에서 걸렸는지 봅니다. "이 코드는 어느 스레드에서 도는가"에 바로 답할 수 없는 메서드가 있으면 그것이 버그 후보입니다.

### A.3 TCP는 바이트 스트림이다

TCP는 **순서가 보장되고 유실이 없는 바이트의 흐름**을 제공하지만 **메시지 경계는 없습니다.** 클라이언트가 `send`를 세 번 해도 서버의 `recv`는 한 번에 받을 수도, 다섯 번에 나눠 받을 수도 있습니다. 한 패킷이 두 번의 수신에 걸쳐 쪼개져 오고(분할), 두 패킷이 한 번에 붙어 오는(병합) 일이 정상입니다.

그래서 애플리케이션이 경계를 만들어야 하고, 가장 흔한 방법이 **길이 접두 헤더**입니다. 이 서버는 `{u16 size, u16 id}` 4바이트를 붙입니다. 수신 쪽 규칙은 하나입니다. "헤더가 다 왔으면 size를 읽고, size만큼 다 왔으면 패킷 하나를 꺼내고, 모자라면 다음 수신을 기다린다." `Session.ProcessPackets`와 `RecvBufferTests.HeaderSplitAcrossTwoWrites_Reassembles`가 정확히 이것입니다.

알아둘 사실 몇 가지. `recv`가 0바이트를 돌려주면 상대가 연결을 닫은 것입니다. `Shutdown(Both)` 후 `Close()`는 "보낼 것 없음"을 알리고 닫는 정상 종료 순서입니다. Nagle 알고리즘은 작은 패킷을 모아 보내 지연을 늘리므로 게임 서버는 `NoDelay = true`로 끕니다. 크기 필드는 신뢰하지 않습니다. 악의적 클라이언트가 `size=65535`를 보내면 그만큼 기다리며 버퍼를 점유하므로 `MaxPacketSize` 범위를 벗어나면 즉시 끊습니다.

### A.4 async/await와 Task

`Task`는 "아직 끝나지 않은 작업의 약속"이고, `await`는 "끝나면 여기서 이어서 실행해라"입니다. `await` 지점에서 메서드는 호출자에게 돌아가고 스레드는 다른 일을 하러 갑니다. 그래서 수천 개의 연결을 기다려도 스레드가 수천 개 필요하지 않습니다.

```csharp
static async Task<ClientResult> RunClientAsync(Options options, int index)
{
    await client.ConnectAsync(options.Host, options.Port);        // 연결될 때까지 스레드를 막지 않고 기다림
    var received = await stream.ReadAsync(chunk, cts.Token);      // 취소 토큰으로 중단 가능
    ...
}
var results = await Task.WhenAll(tasks);                           // 여러 작업을 동시에 돌리고 모두 기다림
```

규칙. 반환 타입은 `Task`(결과 없음) 또는 `Task<T>`이고, `async void`는 예외가 사라지므로 이벤트 핸들러 밖에서는 쓰지 않습니다. 예외는 `await` 지점에서 다시 던져지므로 그곳을 `try/catch`로 감쌉니다. 콘솔 앱에는 UI 스레드가 없어서 `await` 이후 코드는 ThreadPool의 아무 스레드에서 이어집니다 (그래서 A.2의 규칙이 그대로 적용됩니다). `CancellationToken`은 협력적 취소입니다. 토큰을 받는 함수는 취소되면 `OperationCanceledException`을 던지고, 호출자가 그것을 잡습니다.

서버의 `Session`은 `async/await` 대신 더 낮은 수준인 `SocketAsyncEventArgs`를 씁니다 (A.8). 할당이 없고 완료 처리를 완전히 통제할 수 있어서이고, 또 C++ IOCP 코드와 1:1로 대응되어 학습에 유리하기 때문입니다. 테스트 클라이언트처럼 가독성이 더 중요한 곳은 `async/await`가 맞습니다.

### A.5 델리게이트, 람다, 이벤트, 제네릭 제약

**델리게이트**는 타입이 있는 함수 참조입니다. 직접 선언할 수도 있지만 대부분 내장 제네릭을 씁니다. `Action<T1, T2>`는 반환이 없고, `Func<T1, TResult>`는 마지막 인자가 반환 타입입니다.

```csharp
public delegate bool PacketHandler(Session session, IMessage message);   // 직접 선언
Action<float>? TickHandler;                                               // float 하나 받고 반환 없음
Func<Session, T, bool> handler;                                           // 두 개 받고 bool 반환
```

**람다**는 이름 없는 함수이고 바깥 변수를 **캡처**합니다 (클로저). 캡처는 참조 캡처라서, 람다가 실행되는 시점의 값이 쓰입니다. 이 서버는 핸들러에서 캡처한 `session`과 `msg`를 로직 스레드로 넘길 때 이것을 씁니다.

```csharp
server.Loop.Post(() => server.OnLogin(session, msg.ProtocolVersion, msg.Nickname));
```

**이벤트**는 "여러 구독자를 받는 델리게이트 필드"에 `event`를 붙인 것입니다. 바깥에서는 `+=`/`-=`만 할 수 있고 호출은 선언한 클래스만 합니다. 구독자가 없으면 null이므로 `?.Invoke`로 부릅니다.

```csharp
public event Action<Session, string>? Disconnected;     // Session 안
session.Disconnected += OnDisconnected;                 // SessionManager 가 구독
Disconnected?.Invoke(this, reason);                     // Session 이 호출
```

**제네릭 제약** `where T : class, IMessage<T>`는 "T는 참조 타입이고 `IMessage<T>`를 구현한다"는 뜻입니다. 덕분에 `Register<CLogin>(...)`처럼 타입만 적으면 `PacketRegistry.GetId<T>()`로 id를 찾고, 핸들러는 `IMessage`가 아니라 `CLogin`을 받습니다. 캐스팅 코드가 호출자에서 사라집니다.

### A.6 Span, Memory, ArraySegment, BinaryPrimitives

`Span<T>`는 "어떤 연속 메모리의 일부"를 복사 없이 가리키는 창(view)입니다. 배열, 스택 메모리, 네이티브 메모리 어디든 같은 코드로 다룹니다. `ReadOnlySpan<T>`는 읽기 전용 창입니다. `Slice(offset, length)`로 더 작은 창을 만듭니다.

```csharp
public ReadOnlySpan<byte> Data => _buffer.AsSpan(_readPos, DataSize);           // 복사 없음
var body = _recvBuffer.Data.Slice(PacketHeader.Size, header.BodySize);           // 본문 부분만
message.WriteTo(buffer.AsSpan(PacketHeader.Size));                               // 헤더 뒤부터 쓰기
```

제약이 하나 있습니다. `Span`은 스택에만 살 수 있어서(`ref struct`) 클래스 필드에 저장하거나 `async` 메서드 안에서 `await`를 넘어 쓸 수 없습니다. 그런 경우는 `Memory<T>`/`ReadOnlyMemory<T>`를 씁니다. 서버 코드는 모두 동기 메서드 안에서 쓰므로 `Span`으로 충분합니다.

`BinaryPrimitives`는 정수를 **엔디언을 명시해** 읽고 씁니다. `ReadUInt16LittleEndian(span)`처럼 쓰면 서버와 클라이언트의 CPU가 달라도 같은 바이트 순서가 보장됩니다. C++에서 `memcpy`로 구조체를 그대로 보내던 것의 안전한 버전입니다.

`ArraySegment<byte>`는 "배열 + 오프셋 + 길이"입니다. `Span`과 비슷하지만 일반 struct라서 리스트에 담을 수 있고, `SocketAsyncEventArgs.BufferList`가 이 타입의 리스트를 받아 여러 버퍼를 한 번에(gather) 보냅니다. `ArrayPool<byte>.Shared`는 배열을 빌리고(`Rent`) 돌려주는(`Return`) 풀로, 할당을 줄일 때 씁니다. 지금은 쓰지 않고 1.4절 과제 7에 남겨 두었습니다.

### A.7 protobuf 핵심

**스키마가 먼저다.** `.proto` 파일에 메시지와 필드를 적으면 `protoc`가 각 언어의 클래스를 생성합니다. 이 저장소는 빌드 시 Grpc.Tools가 자동으로 생성하므로 `.proto`만 고치면 됩니다. Unity도 같은 `.proto`에서 같은 클래스를 얻습니다.

```proto
message CLogin {
  uint32 protocol_version = 1;   // "= 1" 은 기본값이 아니라 필드 번호
  string nickname = 2;
}
```

**필드 번호가 와이어의 식별자입니다.** 이름은 바뀌어도 되지만 번호는 바뀌면 안 됩니다. 필드를 지웠다면 그 번호를 재사용하지 말고 `reserved 3;`으로 막습니다. 새 필드를 추가하는 것은 하위 호환됩니다 (옛 클라이언트는 모르는 필드를 무시하고, 새 클라이언트는 빠진 필드를 기본값으로 읽습니다). 타입을 바꾸는 것은 호환되지 않습니다.

**proto3의 기본값.** 0, 빈 문자열, false, 열거형의 0번 값은 **와이어에 실리지 않습니다.** 그래서 "보내지 않았다"와 "0을 보냈다"를 구별할 수 없고, 열거형의 첫 값은 반드시 0이어야 합니다 (`MSG_ID_NONE = 0`). 구별이 필요하면 `optional`을 붙입니다.

**인코딩.** 정수는 varint라서 작은 값이 적은 바이트를 차지합니다 (`uint64 player_id = 5`는 2바이트). 문자열은 길이 접두 UTF-8입니다. 이 서버는 protobuf 본문 앞에 자체 4바이트 헤더를 붙이는데, protobuf 자체는 메시지 경계와 타입을 담지 않기 때문입니다 (A.3).

**C# 생성 코드 이름.** 메시지 `CLogin` → 클래스 `CLogin`, 필드 `protocol_version` → 프로퍼티 `ProtocolVersion`, 열거형 값 `MSG_ID_C_LOGIN` → `MsgId.CLogin` (열거형 이름 접두가 잘립니다). 자주 쓰는 API는 `CLogin.Parser.ParseFrom(span)`, `message.CalculateSize()`, `message.WriteTo(span)`입니다.

**주의: 관대한 파싱.** 본문이 중간에 잘려도 필드 경계에서 끊겼다면 예외 없이 파싱되고 빠진 필드는 기본값이 됩니다. 와이어 형식 자체가 깨진 경우에만 `InvalidProtocolBufferException`이 납니다. 그래서 "파싱이 됐다"는 "값이 정상이다"가 아니며, 길이·범위 검증은 `GameHandlers`에서 `ProtocolInfo` 상수로 직접 합니다.

### A.8 SocketAsyncEventArgs 모델

`Socket.ReceiveAsync(args)`는 "이 버퍼에 받아라"를 운영체제에 걸어 두고 즉시 돌아옵니다. 완료되면 `args.Completed` 이벤트가 ThreadPool 스레드에서 호출되고, `args.BytesTransferred`와 `args.SocketError`에 결과가 들어 있습니다. `SocketAsyncEventArgs`는 한 번 만들어 계속 재사용하므로 연결당 할당이 거의 없습니다. Windows에서는 이것이 IOCP 위에서, Linux에서는 epoll 위에서 돕니다.

**가장 중요한 규칙: 반환값 `false`는 동기 완료입니다.** 데이터가 이미 도착해 있으면 `ReceiveAsync`가 즉시 결과를 채우고 `false`를 돌려주며, 이때 `Completed`는 **호출되지 않습니다.** 결과를 그 자리에서 처리해야 하고, 또 다음 수신을 다시 걸어야 합니다. 이를 재귀로 처리하면 데이터가 몰릴 때 스택이 깊어지므로 루프로 씁니다.

```csharp
private void PostRecv()
{
    while (IsConnected)
    {
        _recvArgs.SetBuffer(_recvBuffer.Buffer, _recvBuffer.WritePos, _recvBuffer.FreeSize);
        bool pending = _socket.ReceiveAsync(_recvArgs);
        if (pending) return;                   // 비동기: 나중에 Completed → HandleRecv → PostRecv
        if (!HandleRecv(_recvArgs)) return;    // 동기: 바로 처리하고 루프로 다시 건다
    }
}
_recvArgs.Completed += (_, args) => { if (HandleRecv(args)) PostRecv(); };
```

**수신 1개, 송신 1개.** 한 소켓에 `ReceiveAsync`를 두 개 걸면 완료 순서가 보장되지 않아 바이트가 섞입니다. 송신도 같아서, 보낼 것이 여러 개면 큐에 모아 두고 진행 중인 송신이 끝날 때 `BufferList`로 한 번에 보냅니다 (gather). `BufferList`는 지정할 때 내부에 복사하므로 리스트 내용을 바꿨으면 다시 지정합니다.

**재사용 규칙.** 수락용 `args`는 다음 `AcceptAsync` 전에 `args.AcceptSocket = null`로 비웁니다. `Buffer`와 `BufferList`는 동시에 설정할 수 없습니다. 진행 중인 작업이 있을 수 있는 `args`는 `Dispose`하지 않습니다 (세션이 참조를 잃으면 GC가 처리합니다).

**종료.** `Socket.Close()`는 걸려 있는 작업을 취소하고, 그 작업들은 `SocketError.OperationAborted`로 완료됩니다. `Session.Disconnect`가 `_connected`를 0으로 바꾼 뒤이므로 `HandleRecv/HandleSend`는 `IsConnected`가 false인 것을 보고 조용히 끝납니다.

**C++ IOCP와의 대응.** `WSARecv/WSASend` ↔ `ReceiveAsync/SendAsync`, `OVERLAPPED`(+`OverlappedEx`) ↔ `SocketAsyncEventArgs`, `GetQueuedCompletionStatus`를 도는 워커 스레드 ↔ ThreadPool, `CONTAINING_RECORD`로 컨텍스트 복원 ↔ `Completed`의 `args` 자체가 컨텍스트, `AcceptEx` 선행 게시 ↔ `AcceptAsync` N개, `shared_ptr owner`로 수명 보장 ↔ GC. 한 가지 차이가 있는데, IOCP는 즉시 완료되어도 완료 패킷을 큐에 넣어 항상 같은 경로로 처리되지만 .NET은 `false`를 돌려주는 동기 경로가 따로 있습니다.

### A.9 .NET Generic Host

Generic Host는 "서버 프로그램의 뼈대"를 제공하는 라이브러리입니다. 설정 읽기, 로깅, 의존성 주입(DI), 시작/종료 순서, Ctrl+C 처리를 한 패키지(`Microsoft.Extensions.Hosting`)로 줍니다. 직접 만들면 수백 줄이 될 것을 `Program.cs` 스무 줄로 끝냅니다.

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("Server"));   // 설정 → 객체 바인딩
builder.Services.AddHostedService<GameServer>();                                         // 시작/종료를 호스트가 관리
await builder.Build().RunAsync();                                                        // Ctrl+C 까지 실행
```

**설정 우선순위.** `appsettings.json` → 환경 변수 → 커맨드라인 순으로 **나중에 추가된 소스가 우선**합니다. 그래서 `--Server:Quiz:RoomSize=2`가 파일 값을 덮어씁니다. 콜론은 중첩 섹션 구분자입니다. `IOptions<ServerOptions>`를 생성자에서 받으면 바인딩된 객체가 들어옵니다.

**IHostedService / BackgroundService.** `StartAsync`는 호스트가 뜰 때, `StopAsync`는 종료 신호(Ctrl+C, `StopApplication()`)가 오면 호출됩니다. 등록한 순서대로 시작하고 역순으로 멈춥니다. 오래 도는 작업은 `BackgroundService`를 상속해 `ExecuteAsync(stoppingToken)`에 쓰고, 토큰이 취소되면 정리합니다 (`SmokeStopper`).

**로깅.** `ILogger<T>`를 생성자에서 받아 `LogInformation("[player {Id}] ...", id)`처럼 **구조적 로깅**으로 씁니다. 문자열 보간이 아니라 자리표시자를 쓰는 이유는 로그 수집기가 필드로 인식할 수 있게 하기 위해서이고, 레벨이 꺼져 있으면 포맷 비용도 들지 않습니다.

**왜 쓰는가.** 설정 파일 파서, 종료 핸들러, 로거를 직접 만들었던 C++ 버전(`Config.cpp`, `main.cpp`의 `SetConsoleCtrlHandler`, `Log.cpp`)이 모두 이 라이브러리로 대체되었습니다. 3.2절의 대응표에서 "없음"으로 표시된 항목 상당수가 여기서 옵니다.
