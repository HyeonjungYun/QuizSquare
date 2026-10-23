using QuizMMO.Protocol;
using QuizMMO.Server.Networking;
using Xunit;

namespace QuizMMO.Server.Tests;

public class RecvBufferTests
{
    [Fact]
    public void WriteThenRead_TracksPositions()
    {
        var buffer = new RecvBuffer(64);

        Assert.True(buffer.OnWrite(10));
        Assert.True(buffer.OnRead(4));

        Assert.Equal(6, buffer.DataSize);
        Assert.Equal(54, buffer.FreeSize);
    }

    [Fact]
    public void WriteBeyondFreeSpace_Fails()
    {
        var buffer = new RecvBuffer(16);
        buffer.OnWrite(10);

        Assert.False(buffer.OnWrite(7));
        Assert.Equal(10, buffer.DataSize);
    }

    [Fact]
    public void ReadBeyondData_Fails()
    {
        var buffer = new RecvBuffer(16);
        buffer.OnWrite(5);

        Assert.False(buffer.OnRead(6));
        Assert.Equal(5, buffer.DataSize);
    }

    [Fact]
    public void Clean_ResetsWhenEmpty()
    {
        var buffer = new RecvBuffer(16);
        buffer.OnWrite(12);
        buffer.OnRead(12);

        buffer.Clean();

        Assert.Equal(0, buffer.DataSize);
        Assert.Equal(16, buffer.FreeSize);
    }

    [Fact]
    public void Clean_CompactsPartialDataToFront()
    {
        var buffer = new RecvBuffer(16);
        new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }.CopyTo(buffer.Buffer, buffer.WritePos);
        buffer.OnWrite(8);
        buffer.OnRead(5);   // 6,7,8 남음

        buffer.Clean();

        Assert.Equal(3, buffer.DataSize);
        Assert.Equal(13, buffer.FreeSize);
        Assert.Equal(new byte[] { 6, 7, 8 }, buffer.Data.ToArray());
    }

    [Fact]
    public void HeaderSplitAcrossTwoWrites_Reassembles()
    {
        // 패킷 두 개를 이어붙인 스트림을 3바이트 / 나머지로 쪼개 넣는다 (TCP 분할 수신 시뮬레이션).
        var wire = Concat(
            PacketSerializer.Serialize(new CPing { ClientTimeMs = 123456789 }),
            PacketSerializer.Serialize(new CQuizQueueJoin()));
        var buffer = new RecvBuffer(256);

        wire.AsSpan(0, 3).CopyTo(buffer.Buffer.AsSpan(buffer.WritePos));
        buffer.OnWrite(3);
        var first = ExtractPacketIds(buffer);

        wire.AsSpan(3).CopyTo(buffer.Buffer.AsSpan(buffer.WritePos));
        buffer.OnWrite(wire.Length - 3);
        var second = ExtractPacketIds(buffer);

        Assert.Empty(first);
        Assert.Equal(new[] { (ushort)MsgId.CPing, (ushort)MsgId.CQuizQueueJoin }, second);
        Assert.Equal(0, buffer.DataSize);
    }

    [Fact]
    public void TwoPacketsInOneWrite_BothExtracted()
    {
        var wire = Concat(
            PacketSerializer.Serialize(new CQuizQueueJoin()),
            PacketSerializer.Serialize(new CChat { Text = "hi" }));
        var buffer = new RecvBuffer(256);

        wire.CopyTo(buffer.Buffer, buffer.WritePos);
        buffer.OnWrite(wire.Length);
        var ids = ExtractPacketIds(buffer);

        Assert.Equal(new[] { (ushort)MsgId.CQuizQueueJoin, (ushort)MsgId.CChat }, ids);
        Assert.Equal(0, buffer.DataSize);
    }

    /// <summary>Session.ProcessPackets 와 같은 규칙으로 완전한 패킷만 꺼낸다.</summary>
    private static List<ushort> ExtractPacketIds(RecvBuffer buffer)
    {
        var ids = new List<ushort>();
        while (buffer.DataSize >= PacketHeader.Size)
        {
            var header = PacketHeader.Read(buffer.Data);
            Assert.True(header.IsValid);
            if (buffer.DataSize < header.TotalSize) break;
            ids.Add(header.Id);
            buffer.OnRead(header.TotalSize);
        }
        buffer.Clean();
        return ids;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }
}
