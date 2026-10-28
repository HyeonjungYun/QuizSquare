using System;
using System.Collections.Generic;
using Google.Protobuf;

namespace QuizMMO.Protocol;

/// <summary>
/// 패킷 ID ↔ protobuf 메시지 타입 매핑. 새 메시지를 .proto 에 추가하면 여기 한 줄을 같이 추가한다.
/// 정적 생성자에서 한 번 채워지고 이후 읽기만 하므로 어느 스레드에서든 안전하다.
/// </summary>
public static class PacketRegistry
{
    private static readonly Dictionary<ushort, MessageParser> ParsersById = new Dictionary<ushort, MessageParser>();
    private static readonly Dictionary<Type, ushort> IdsByType = new Dictionary<Type, ushort>();

    static PacketRegistry()
    {
        // 로그인 / 채팅 / 핑
        Register(MsgId.CLogin, CLogin.Parser);
        Register(MsgId.SLogin, SLogin.Parser);
        Register(MsgId.CChat, CChat.Parser);
        Register(MsgId.SChat, SChat.Parser);
        Register(MsgId.CPing, CPing.Parser);
        Register(MsgId.SPong, SPong.Parser);

        // 대기열 / 룸
        Register(MsgId.CQuizQueueJoin, CQuizQueueJoin.Parser);
        Register(MsgId.CQuizQueueLeave, CQuizQueueLeave.Parser);
        Register(MsgId.SQuizQueueStatus, SQuizQueueStatus.Parser);
        Register(MsgId.SRoomChanged, SRoomChanged.Parser);
        Register(MsgId.SPlayerEnter, SPlayerEnter.Parser);
        Register(MsgId.SPlayerLeave, SPlayerLeave.Parser);

        // 이동
        Register(MsgId.CMove, CMove.Parser);
        Register(MsgId.SMove, SMove.Parser);

        // 퀴즈 진행
        Register(MsgId.SQuizStart, SQuizStart.Parser);
        Register(MsgId.SQuizQuestion, SQuizQuestion.Parser);
        Register(MsgId.CQuizAnswer, CQuizAnswer.Parser);
        Register(MsgId.SQuizAnswerAck, SQuizAnswerAck.Parser);
        Register(MsgId.SQuizReveal, SQuizReveal.Parser);
        Register(MsgId.SQuizResult, SQuizResult.Parser);

        // 인벤토리
        Register(MsgId.SInventory, SInventory.Parser);

        // 거래
        Register(MsgId.CTradeRequest, CTradeRequest.Parser);
        Register(MsgId.STradeRequested, STradeRequested.Parser);
        Register(MsgId.CTradeRespond, CTradeRespond.Parser);
        Register(MsgId.STradeStarted, STradeStarted.Parser);
        Register(MsgId.CTradeOffer, CTradeOffer.Parser);
        Register(MsgId.STradeOffer, STradeOffer.Parser);
        Register(MsgId.CTradeConfirm, CTradeConfirm.Parser);
        Register(MsgId.STradeConfirmed, STradeConfirmed.Parser);
        Register(MsgId.CTradeCancel, CTradeCancel.Parser);
        Register(MsgId.STradeCompleted, STradeCompleted.Parser);
        Register(MsgId.STradeCancelled, STradeCancelled.Parser);

        // 공통
        Register(MsgId.SError, SError.Parser);
    }

    private static void Register<T>(MsgId id, MessageParser<T> parser) where T : IMessage<T>
    {
        ParsersById[(ushort)id] = parser;
        IdsByType[typeof(T)] = (ushort)id;
    }

    /// <summary>등록된 메시지 수 (테스트용).</summary>
    public static int Count => ParsersById.Count;

    public static bool TryGetParser(ushort id, out MessageParser parser)
    {
        return ParsersById.TryGetValue(id, out parser!);
    }

    public static bool TryGetId(Type messageType, out ushort id)
    {
        return IdsByType.TryGetValue(messageType, out id);
    }

    /// <summary>등록되지 않은 타입이면 예외. 송신 쪽 프로그래밍 오류를 바로 드러내기 위해서다.</summary>
    public static ushort GetId(Type messageType)
    {
        if (!IdsByType.TryGetValue(messageType, out var id))
        {
            throw new InvalidOperationException($"{messageType.Name} is not registered in PacketRegistry. Add it to the .proto MsgId enum and to PacketRegistry.");
        }
        return id;
    }

    public static ushort GetId<T>() where T : IMessage => GetId(typeof(T));
}
