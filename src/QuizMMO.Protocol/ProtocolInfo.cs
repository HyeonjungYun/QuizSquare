namespace QuizMMO.Protocol;

/// <summary>프로토콜 버전과 필드 범위. 수신 시 범위를 검증한다 (서버 권위, 클라이언트 값은 믿지 않는다).</summary>
public static class ProtocolInfo
{
    /// <summary>메시지 레이아웃이 바뀌면 올린다. CLogin.protocol_version 과 비교해 불일치는 거절한다.</summary>
    public const uint Version = 1;

    public const int NicknameMinBytes = 1;
    public const int NicknameMaxBytes = 32;
    public const int ChatMaxBytes = 400;
    public const int RoomNameMaxBytes = 64;
}
