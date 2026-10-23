using QuizMMO.Protocol;

namespace QuizMMO.Server.Gameplay;

/// <summary>
/// 퀴즈 인스턴스. 대기열에서 뽑힌 플레이어들이 들어와 라운드를 치르고, 끝나면 GameServer 가 월드로 되돌린다.
/// 뼈대 단계에서는 duration 동안 카운트다운만 한다 — 룸 이동과 복귀 흐름을 검증하기 위한 자리표시.
/// 확장 지점(스토리 004/005): Update(dt) 에 Waiting → Question → Reveal → Result 상태 머신,
/// 문제 데이터 로드, 답안 패킷 처리, 점수/순위, 보상 지급을 붙인다.
/// </summary>
public sealed class QuizRoom : Room
{
    public QuizRoom(uint id, string name, float durationSec) : base(id, name)
    {
        RemainingSec = durationSec;
    }

    public override RoomType Type => RoomType.Quiz;
    public float RemainingSec { get; private set; }
    public bool IsFinished { get; private set; }

    public override void Update(float dt)
    {
        if (IsFinished) return;
        RemainingSec -= dt;
        if (RemainingSec <= 0f)
        {
            RemainingSec = 0f;
            IsFinished = true;
        }
    }
}
