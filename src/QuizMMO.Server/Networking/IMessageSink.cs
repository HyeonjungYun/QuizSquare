using Google.Protobuf;

namespace QuizMMO.Server.Networking;

/// <summary>
/// 게임 계층이 보는 "메시지를 보낼 수 있는 상대". 실제 구현은 <see cref="Session"/>.
/// 룸/플레이어 로직이 소켓을 직접 알지 않게 하고, 테스트에서는 가짜 구현으로 바꿔 끼운다.
/// </summary>
public interface IMessageSink
{
    bool IsConnected { get; }

    /// <summary>직렬화된 패킷(헤더 포함)을 보낸다. 끊긴 상대면 조용히 버린다. 어느 스레드에서든 안전.</summary>
    void Send(byte[] packet);

    /// <summary>메시지를 직렬화해서 보낸다. 여러 상대에게 같은 메시지를 보낼 때는 한 번 직렬화한 바이트를 공유하는 쪽이 낫다.</summary>
    void Send(IMessage message);

    void Disconnect(string reason);
}
