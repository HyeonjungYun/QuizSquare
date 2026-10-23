using System;
using Google.Protobuf;

namespace QuizMMO.Protocol;

/// <summary>헤더 + protobuf 본문을 한 바이트 배열로 묶고, 반대로 푼다. 상태가 없어 어느 스레드에서든 안전하다.</summary>
public static class PacketSerializer
{
    /// <summary>
    /// 메시지를 [헤더][본문] 바이트 배열로 직렬화한다. 결과는 불변으로 다루어 브로드캐스트 때 수신자마다 복사하지 않고 공유한다.
    /// 등록되지 않은 메시지나 MaxPacketSize 초과는 예외 (송신 쪽 프로그래밍 오류).
    /// </summary>
    public static byte[] Serialize(IMessage message)
    {
        var id = PacketRegistry.GetId(message.GetType());
        var bodySize = message.CalculateSize();
        var totalSize = PacketHeader.Size + bodySize;
        if (totalSize > PacketHeader.MaxPacketSize)
        {
            throw new InvalidOperationException($"{message.GetType().Name} serializes to {totalSize} bytes, over the {PacketHeader.MaxPacketSize}-byte packet limit.");
        }

        var buffer = new byte[totalSize];
        new PacketHeader((ushort)totalSize, id).Write(buffer);
        message.WriteTo(buffer.AsSpan(PacketHeader.Size));
        return buffer;
    }

    /// <summary>수신 본문을 id 에 맞는 메시지로 푼다. 미등록 id 거나 깨진 본문이면 false (호출자는 세션을 끊는다).</summary>
    public static bool TryDeserialize(ushort id, ReadOnlySpan<byte> body, out IMessage? message)
    {
        message = null;
        if (!PacketRegistry.TryGetParser(id, out var parser)) return false;
        try
        {
            message = parser.ParseFrom(body);
            return true;
        }
        catch (InvalidProtocolBufferException)
        {
            return false;
        }
    }
}
