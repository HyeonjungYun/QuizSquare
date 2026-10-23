using Google.Protobuf;
using QuizMMO.Protocol;
using QuizMMO.Server.Networking;
using Xunit;

namespace QuizMMO.Server.Tests;

/// <summary>소켓 대신 받은 메시지를 기록하는 가짜 상대. 룸/대기열 로직을 네트워크 없이 검증한다.</summary>
public sealed class FakeSink : IMessageSink
{
    public List<IMessage> Received { get; } = new();
    public bool IsConnected { get; private set; } = true;
    public string? DisconnectReason { get; private set; }

    public void Send(byte[] packet)
    {
        var header = PacketHeader.Read(packet);
        Assert.True(header.IsValid, "sent packet has an invalid header");
        Assert.Equal(packet.Length, header.TotalSize);
        var body = packet.AsSpan(PacketHeader.Size, header.BodySize);
        Assert.True(PacketSerializer.TryDeserialize(header.Id, body, out var message), "sent packet did not deserialize");
        Received.Add(message!);
    }

    public void Send(IMessage message) => Send(PacketSerializer.Serialize(message));

    public void Disconnect(string reason)
    {
        IsConnected = false;
        DisconnectReason = reason;
    }

    public IEnumerable<T> Of<T>() where T : IMessage => Received.OfType<T>();
}
