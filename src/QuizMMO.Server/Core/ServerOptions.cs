using Microsoft.Extensions.Logging;
using QuizMMO.Protocol;

namespace QuizMMO.Server.Core;

/// <summary>
/// appsettings.json 의 "Server" 섹션. 네트워크·게임·월드·퀴즈·보상·거래 수치는 모두 여기서 읽고 코드에 박지 않는다.
/// <see cref="Normalize"/> 가 범위를 벗어난 값을 보정하고 경고를 남긴다.
/// </summary>
public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public NetOptions Net { get; set; } = new();
    public GameOptions Game { get; set; } = new();
    public WorldOptions World { get; set; } = new();
    public QuizOptions Quiz { get; set; } = new();
    public RewardOptions Rewards { get; set; } = new();
    public TradeOptions Trade { get; set; } = new();

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
        /// <summary>채팅 최소 간격(ms). 더 빠르면 SError(ChatTooFast) 로 버린다.</summary>
        public int ChatCooldownMs { get; set; } = 500;
        /// <summary>로그인 시 지급하는 골드 (거래를 바로 해볼 수 있게).</summary>
        public long StartingGold { get; set; } = 100;
    }

    public sealed class WorldOptions
    {
        public float Width { get; set; } = 2000f;
        public float Height { get; set; } = 2000f;
        public float SpawnX { get; set; } = 1000f;
        public float SpawnY { get; set; } = 1000f;
        /// <summary>스폰 지점 주변 이 반지름 안에 무작위로 놓는다.</summary>
        public float SpawnRadius { get; set; } = 50f;
        /// <summary>초당 이동 가능 거리(월드 단위).</summary>
        public float MaxSpeed { get; set; } = 300f;
        /// <summary>네트워크 지터를 감안한 여유 배수.</summary>
        public float SpeedTolerance { get; set; } = 1.5f;
        /// <summary>이동 간격이 이보다 짧아도 최소 이 간격만큼의 이동은 허용한다.</summary>
        public int MoveMinIntervalMs { get; set; } = 30;
        /// <summary>오래 멈춰 있다가 움직여도 한 번에 이 시간 이상의 거리는 허용하지 않는다 (순간이동 방지).</summary>
        public double MaxMoveDeltaSec { get; set; } = 1.0;
    }

    public sealed class QuizOptions
    {
        /// <summary>대기열에서 이 인원이 모이면 퀴즈방을 만든다.</summary>
        public int RoomSize { get; set; } = 20;
        public double MatchCheckIntervalSec { get; set; } = 1.0;
        /// <summary>한 판에 출제하는 문제 수. 문제 파일보다 많으면 파일 전체.</summary>
        public int QuestionsPerRound { get; set; } = 5;
        /// <summary>SQuizStart 뒤 첫 문제까지.</summary>
        public double StartDelaySec { get; set; } = 3.0;
        public double QuestionTimeLimitSec { get; set; } = 10.0;
        /// <summary>정답 공개 화면 길이.</summary>
        public double RevealSec { get; set; } = 3.0;
        /// <summary>결과 화면 길이. 끝나면 월드로 복귀.</summary>
        public double ResultSec { get; set; } = 5.0;
        /// <summary>정답 기본 점수.</summary>
        public int BasePoints { get; set; } = 100;
        /// <summary>즉시 답하면 이만큼, 제한시간 끝에 답하면 0 이 더해진다.</summary>
        public int SpeedBonusMaxPoints { get; set; } = 50;
        /// <summary>문제 데이터 파일. 실행 파일 옆 경로가 우선.</summary>
        public string QuestionsFile { get; set; } = "Data/questions.json";
        public string ItemsFile { get; set; } = "Data/items.json";
    }

    public sealed class RewardOptions
    {
        public long ParticipationGold { get; set; } = 10;
        public long GoldPerCorrect { get; set; } = 5;
        /// <summary>등수별 보너스 골드. 1등부터.</summary>
        public List<long> RankBonusGold { get; set; } = new() { 100, 50, 25 };
        /// <summary>등수별 보상 아이템 id (0 이면 없음). 1등부터.</summary>
        public List<uint> RankItemIds { get; set; } = new() { 1001, 1002, 1003 };
        /// <summary>참가만 해도 받는 아이템 id (0 이면 없음).</summary>
        public uint ParticipationItemId { get; set; } = 2001;
    }

    public sealed class TradeOptions
    {
        /// <summary>요청 후 상대가 응답하지 않으면 취소.</summary>
        public double RequestTimeoutSec { get; set; } = 20.0;
        /// <summary>진행 중 아무 변화가 없으면 취소.</summary>
        public double IdleTimeoutSec { get; set; } = 120.0;
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
        Game.ChatCooldownMs = Math.Max(0, Game.ChatCooldownMs);
        Game.StartingGold = Math.Max(0, Game.StartingGold);

        World.Width = Math.Max(1f, World.Width);
        World.Height = Math.Max(1f, World.Height);
        World.SpawnX = Math.Clamp(World.SpawnX, 0f, World.Width);
        World.SpawnY = Math.Clamp(World.SpawnY, 0f, World.Height);
        World.SpawnRadius = Math.Max(0f, World.SpawnRadius);
        World.MaxSpeed = Math.Max(1f, World.MaxSpeed);
        World.SpeedTolerance = Math.Max(1f, World.SpeedTolerance);
        World.MoveMinIntervalMs = Math.Max(1, World.MoveMinIntervalMs);
        World.MaxMoveDeltaSec = Math.Max(0.05, World.MaxMoveDeltaSec);

        Quiz.RoomSize = Math.Max(2, Quiz.RoomSize);
        Quiz.MatchCheckIntervalSec = Math.Max(0.1, Quiz.MatchCheckIntervalSec);
        Quiz.QuestionsPerRound = Math.Max(1, Quiz.QuestionsPerRound);
        Quiz.StartDelaySec = Math.Max(0.0, Quiz.StartDelaySec);
        Quiz.QuestionTimeLimitSec = Math.Max(1.0, Quiz.QuestionTimeLimitSec);
        Quiz.RevealSec = Math.Max(0.0, Quiz.RevealSec);
        Quiz.ResultSec = Math.Max(0.0, Quiz.ResultSec);
        Quiz.BasePoints = Math.Max(0, Quiz.BasePoints);
        Quiz.SpeedBonusMaxPoints = Math.Max(0, Quiz.SpeedBonusMaxPoints);
        if (string.IsNullOrWhiteSpace(Quiz.QuestionsFile)) Quiz.QuestionsFile = "Data/questions.json";
        if (string.IsNullOrWhiteSpace(Quiz.ItemsFile)) Quiz.ItemsFile = "Data/items.json";

        Rewards.ParticipationGold = Math.Max(0, Rewards.ParticipationGold);
        Rewards.GoldPerCorrect = Math.Max(0, Rewards.GoldPerCorrect);
        Rewards.RankBonusGold ??= new List<long>();
        Rewards.RankItemIds ??= new List<uint>();
        for (var i = 0; i < Rewards.RankBonusGold.Count; i++) Rewards.RankBonusGold[i] = Math.Max(0, Rewards.RankBonusGold[i]);

        Trade.RequestTimeoutSec = Math.Max(1.0, Trade.RequestTimeoutSec);
        Trade.IdleTimeoutSec = Math.Max(1.0, Trade.IdleTimeoutSec);
    }
}

/// <summary>"Smoke" 섹션. Seconds > 0 이면 그 시간 뒤 서버가 스스로 종료한다 (기동 확인용).</summary>
public sealed class SmokeOptions
{
    public const string SectionName = "Smoke";
    public int Seconds { get; set; }
}
