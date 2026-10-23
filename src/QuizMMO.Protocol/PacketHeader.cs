using System;
using System.Buffers.Binary;

namespace QuizMMO.Protocol;

/// <summary>
/// 모든 패킷의 머리 4바이트: u16 size(헤더 포함 전체 길이), u16 id(<see cref="MsgId"/>). 리틀엔디언.
/// 본문은 protobuf 직렬화 바이트다. 서버와 Unity 클라이언트가 이 구조체 하나로 프레이밍을 맞춘다.
/// </summary>
public readonly struct PacketHeader
{
    /// <summary>헤더 바이트 수.</summary>
    public const int Size = 4;

    /// <summary>이보다 큰 size 헤더는 신뢰하지 않고 즉시 끊는다. 수신 버퍼는 이 값의 2배 이상이어야 한다.</summary>
    public const int MaxPacketSize = 4096;

    /// <summary>헤더를 포함한 패킷 전체 길이.</summary>
    public ushort TotalSize { get; }

    /// <summary><see cref="MsgId"/> 값.</summary>
    public ushort Id { get; }

    public PacketHeader(ushort totalSize, ushort id)
    {
        TotalSize = totalSize;
        Id = id;
    }

    public int BodySize => TotalSize - Size;

    /// <summary>size 가 [Size, MaxPacketSize] 범위 안인지. 아니면 깨진/악의적 패킷으로 본다.</summary>
    public bool IsValid => TotalSize >= Size && TotalSize <= MaxPacketSize;

    /// <summary>앞 4바이트에서 헤더를 읽는다. source 는 Size 이상이어야 한다.</summary>
    public static PacketHeader Read(ReadOnlySpan<byte> source)
    {
        return new PacketHeader(
            BinaryPrimitives.ReadUInt16LittleEndian(source),
            BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(2)));
    }

    /// <summary>앞 4바이트에 헤더를 쓴다. destination 은 Size 이상이어야 한다.</summary>
    public void Write(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination, TotalSize);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(2), Id);
    }
}
