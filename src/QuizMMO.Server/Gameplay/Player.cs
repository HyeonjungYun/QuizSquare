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

    public bool IsConnected => Link.IsConnected;

    public void Send(byte[] packet) => Link.Send(packet);
    public void Send(IMessage message) => Link.Send(message);
}
