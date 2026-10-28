namespace QuizMMO.Protocol;

/// <summary>프로토콜 버전과 필드 범위. 수신 시 범위를 검증한다 (서버 권위, 클라이언트 값은 믿지 않는다).</summary>
public static class ProtocolInfo
{
    /// <summary>
    /// 메시지 레이아웃이 바뀌면 올린다. CLogin.protocol_version 과 비교해 불일치는 거절한다.
    /// 2: 이동(CMove/SMove), 퀴즈 진행, 인벤토리, 거래 메시지 추가. 닉네임 유일성.
    /// </summary>
    public const uint Version = 2;

    public const int NicknameMinBytes = 1;
    public const int NicknameMaxBytes = 32;
    public const int ChatMaxBytes = 400;
    public const int RoomNameMaxBytes = 64;

    /// <summary>문제 하나의 보기 수 범위.</summary>
    public const int MinQuizChoices = 2;
    public const int MaxQuizChoices = 6;

    /// <summary>거래 제안 하나에 담을 수 있는 아이템 종류 수.</summary>
    public const int MaxTradeItemStacks = 20;
}
