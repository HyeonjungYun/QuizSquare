using Microsoft.Extensions.Logging;
using QuizMMO.Protocol;

namespace QuizMMO.Server.Core;

/// <summary>
/// appsettings.json 의 "Server" 섹션. 네트워크·게임·퀴즈 수치는 모두 여기서 읽고 코드에 박지 않는다.
/// <see cref="Normalize"/> 가 범위를 벗어난 값을 보정하고 경고를 남긴다.
/// </summary>
public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public NetOptions Net { get; set; } = new();
    public GameOptions Game { get; set; } = new();
    public QuizOptions Quiz { get; set; } = new();

    /// <summary>세션/룸/대기열 현황 로그 주기(초). 0 이하면 끈다.</summary>
    public double StatusIntervalSec { get; set; } = 10;

    public sealed class NetOptions
    {
        public int Port { get; set; } = 7777;
        public string BindAddress { get; set; } = "0.0.0.0";
        /// <summary>초과 접속은 즉시 닫는다.</summary>
        public int MaxSessions { get; set; } = 2000;
        /// <summary>세션당 수신 버퍼. 최대 패킷(4096)의 2배 이상.</summary>
        public int RecvBufferBytes { get; set; } = 65536;
        /// <summary>미리 걸어두는 AcceptAsync 수.</summary>
        public int AcceptBacklog { get; set; } = 64;
    }

    public sealed class GameOptions
    {
        /// <summary>로직 스레드 틱. 룸 Update(dt) 호출 주기.</summary>
        public int TickHz { get; set; } = 20;
        public string WorldName { get; set; } = "Plaza";
    }

    public sealed class QuizOptions
    {
        /// <summary>대기열에서 이 인원이 모이면 퀴즈방을 만든다.</summary>
        public int RoomSize { get; set; } = 20;
        public double MatchCheckIntervalSec { get; set; } = 1.0;
        /// <summary>퀴즈 로직(스토리 004) 전까지의 자리표시 길이. 끝나면 월드로 복귀.</summary>
        public double RoomDurationSec { get; set; } = 30.0;
    }

    /// <summary>범위를 벗어난 값을 안전한 값으로 보정한다. 서버 시작 시 한 번 호출.</summary>
    public void Normalize(ILogger logger)
    {
        Net.Port = Math.Clamp(Net.Port, 1, 65535);
        Net.MaxSessions = Math.Max(1, Net.MaxSessions);
        Net.AcceptBacklog = Math.Clamp(Net.AcceptBacklog, 1, 1024);

        var minRecv = PacketHeader.MaxPacketSize * 2;
        if (Net.RecvBufferBytes < minRecv)
        {
            logger.LogWarning("Server:Net:RecvBufferBytes={Value} is below {Min} (2 x max packet) - clamped", Net.RecvBufferBytes, minRecv);
            Net.RecvBufferBytes = minRecv;
        }

        Game.TickHz = Math.Clamp(Game.TickHz, 1, 240);
        if (string.IsNullOrWhiteSpace(Game.WorldName)) Game.WorldName = "Plaza";

        Quiz.RoomSize = Math.Max(2, Quiz.RoomSize);
        Quiz.MatchCheckIntervalSec = Math.Max(0.1, Quiz.MatchCheckIntervalSec);
        Quiz.RoomDurationSec = Math.Max(1.0, Quiz.RoomDurationSec);
    }
}

/// <summary>"Smoke" 섹션. Seconds > 0 이면 그 시간 뒤 서버가 스스로 종료한다 (기동 확인용).</summary>
public sealed class SmokeOptions
{
    public const string SectionName = "Smoke";
    public int Seconds { get; set; }
}
