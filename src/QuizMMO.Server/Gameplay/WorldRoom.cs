using QuizMMO.Protocol;

namespace QuizMMO.Server.Gameplay;

/// <summary>
/// 거대한 공유 맵(광장). 로그인한 플레이어는 여기서 시작하고, 퀴즈가 끝나면 여기로 돌아온다.
/// 확장 지점: 이동 동기화(스토리 006)와 1:1 거래(스토리 007)는 이 클래스에 Update(dt) 와 핸들러로 붙인다.
/// </summary>
public sealed class WorldRoom : Room
{
    public WorldRoom(uint id, string name) : base(id, name) { }

    public override RoomType Type => RoomType.World;
}
