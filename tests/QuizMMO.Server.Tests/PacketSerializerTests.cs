using QuizMMO.Protocol;
using Xunit;
using Empty = Google.Protobuf.WellKnownTypes.Empty;

namespace QuizMMO.Server.Tests;

public class PacketSerializerTests
{
    [Fact]
    public void Serialize_WritesHeaderWithTotalSizeAndId()
    {
        var packet = PacketSerializer.Serialize(new CLogin { ProtocolVersion = 1, Nickname = "tester" });

        var header = PacketHeader.Read(packet);
        Assert.True(header.IsValid);
        Assert.Equal(packet.Length, header.TotalSize);
        Assert.Equal((ushort)MsgId.CLogin, header.Id);
    }

    [Fact]
    public void RoundTrip_PreservesFields()
    {
        var packet = PacketSerializer.Serialize(new SChat { PlayerId = 42, Nickname = "홍길동", Text = "안녕" });
        var header = PacketHeader.Read(packet);

        var ok = PacketSerializer.TryDeserialize(header.Id, packet.AsSpan(PacketHeader.Size, header.BodySize), out var message);

        Assert.True(ok);
        var chat = Assert.IsType<SChat>(message);
        Assert.Equal(42UL, chat.PlayerId);
        Assert.Equal("홍길동", chat.Nickname);
        Assert.Equal("안녕", chat.Text);
    }

    [Fact]
    public void EmptyMessage_SerializesToHeaderOnly()
    {
        var packet = PacketSerializer.Serialize(new CQuizQueueJoin());

        Assert.Equal(PacketHeader.Size, packet.Length);
        Assert.Equal((ushort)MsgId.CQuizQueueJoin, PacketHeader.Read(packet).Id);
    }

    [Fact]
    public void TryDeserialize_UnknownId_ReturnsFalse()
    {
        var ok = PacketSerializer.TryDeserialize(60000, ReadOnlySpan<byte>.Empty, out var message);

        Assert.False(ok);
        Assert.Null(message);
    }

    [Fact]
    public void TryDeserialize_GarbageBody_ReturnsFalse()
    {
        var garbage = Enumerable.Repeat((byte)0xFF, 12).ToArray();   // 끝나지 않는 varint → 깨진 본문

        var ok = PacketSerializer.TryDeserialize((ushort)MsgId.CLogin, garbage, out _);

        Assert.False(ok);
    }

    [Fact]
    public void Serialize_UnregisteredMessage_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => PacketSerializer.Serialize(new Empty()));
    }

    [Fact]
    public void Registry_CoversEveryMsgIdExceptNone()
    {
        var ids = Enum.GetValues<MsgId>().Where(id => id != MsgId.None).ToArray();

        Assert.Equal(ids.Length, PacketRegistry.Count);
        foreach (var id in ids)
        {
            Assert.True(PacketRegistry.TryGetParser((ushort)id, out _), $"{id} has no parser registered");
        }
    }

    [Fact]
    public void Header_RoundTripsThroughBytes()
    {
        var bytes = new byte[PacketHeader.Size];
        new PacketHeader(1234, 56).Write(bytes);

        var header = PacketHeader.Read(bytes);

        Assert.Equal(1234, header.TotalSize);
        Assert.Equal(56, header.Id);
        Assert.Equal(new byte[] { 0xD2, 0x04, 0x38, 0x00 }, bytes);   // 리틀엔디언
    }

    [Fact]
    public void Header_OutOfRangeSize_IsInvalid()
    {
        Assert.False(new PacketHeader(3, 1).IsValid);
        Assert.False(new PacketHeader(PacketHeader.MaxPacketSize + 1, 1).IsValid);
        Assert.True(new PacketHeader(PacketHeader.Size, 1).IsValid);
        Assert.True(new PacketHeader(PacketHeader.MaxPacketSize, 1).IsValid);
    }
}
