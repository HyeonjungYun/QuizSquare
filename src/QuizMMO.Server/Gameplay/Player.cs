using Google.Protobuf;
using QuizMMO.Server.Networking;

namespace QuizMMO.Server.Gameplay;

/// <summary>
/// 로그인한 유저. 세션의 <see cref="Session.Context"/> 로 붙는다.
/// 필드는 로직 스레드에서만 읽고 쓴다. Send 는 세션의 스레드 안전 Send 를 그대로 쓰므로 어디서든 가능.
/// </summary>
public sealed class Player
{
    public Player(ulong id, string nickname, IMessageSink link)
    {
        Id = id;
        Nickname = nickname;
        Link = link;
    }

    public ulong Id { get; }
    public string Nickname { get; }
    public IMessageSink Link { get; }

    /// <summary>현재 들어가 있는 룸. 0 이면 없음. Room.Enter/Leave 가 관리한다.</summary>
    public uint RoomId { get; set; }

    /// <summary>QuizQueue 가 관리한다. 밖에서 직접 바꾸지 않는다.</summary>
    public bool InQuizQueue { get; set; }

    /// <summary>월드 좌표. WorldRoom 이 스폰과 이동 검증에서 관리한다.</summary>
    public float X { get; set; }
    public float Y { get; set; }
    /// <summary>마지막으로 수락된 이동 시각 (Clock.NowMs). 속도 검증용.</summary>
    public long LastMoveAtMs { get; set; }

    /// <summary>마지막 채팅 시각 (Clock.NowMs). "아직 없음" 을 0 으로 두면 서버 기동 직후(시계가 0 근처)의 첫 채팅이 쿨다운에 걸리므로 먼 과거로 둔다.</summary>
    private long _lastChatAtMs = long.MinValue / 2;

    /// <summary>채팅 쿨다운 판정. 지났으면 지금을 기록하고 true, 아니면 false. 첫 채팅은 항상 허용.</summary>
    public bool TryChat(long nowMs, int cooldownMs)
    {
        if (nowMs - _lastChatAtMs < cooldownMs) return false;
        _lastChatAtMs = nowMs;
        return true;
    }

    public Inventory Inventory { get; } = new();

    /// <summary>진행 중인 거래 id. 0 이면 없음. TradeManager 가 관리한다.</summary>
    public uint TradeId { get; set; }

    public bool IsConnected => Link.IsConnected;

    public void Send(byte[] packet) => Link.Send(packet);
    public void Send(IMessage message) => Link.Send(message);
}
