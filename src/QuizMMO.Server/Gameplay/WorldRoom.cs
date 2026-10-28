using QuizMMO.Protocol;
using QuizMMO.Server.Core;

namespace QuizMMO.Server.Gameplay;

/// <summary>
/// 거대한 공유 맵(광장). 로그인한 플레이어는 여기서 시작하고, 퀴즈가 끝나면 여기로 돌아온다.
/// 이동은 서버 권위다: 클라이언트가 보낸 좌표를 범위와 속도로 검증하고, 통과하면 다른 플레이어에게 전파하고 아니면 되돌린다.
/// 수치는 모두 Server:World 설정에서 온다. <b>로직 스레드 전용.</b>
/// </summary>
public sealed class WorldRoom : Room
{
    private readonly ServerOptions.WorldOptions _options;
    private readonly Random _random;

    public WorldRoom(uint id, string name, ServerOptions.WorldOptions options, Random? random = null) : base(id, name)
    {
        _options = options;
        _random = random ?? Random.Shared;
    }

    public override RoomType Type => RoomType.World;

    /// <summary>스폰 지점 주변 SpawnRadius 안의 무작위 위치에 놓고 이동 시계를 리셋한다.</summary>
    protected override void PlacePlayer(Player player)
    {
        var angle = _random.NextDouble() * Math.PI * 2;
        var radius = _random.NextDouble() * _options.SpawnRadius;
        player.X = Math.Clamp(_options.SpawnX + (float)(Math.Cos(angle) * radius), 0f, _options.Width);
        player.Y = Math.Clamp(_options.SpawnY + (float)(Math.Sin(angle) * radius), 0f, _options.Height);
        player.LastMoveAtMs = Clock.NowMs;
    }

    /// <summary>
    /// 이동 요청을 검증한다. 범위 안이고 마지막 수락 위치에서 허용 거리 안이면 위치를 갱신하고 다른 플레이어에게 SMove 를 보낸다.
    /// 거절되면 본인에게 corrected=true 인 SMove 로 서버 위치를 되돌려 주고 false.
    /// 허용 거리 = MaxSpeed x SpeedTolerance x dt, dt 는 [MoveMinIntervalMs, MaxMoveDeltaSec] 로 잘라 순간이동을 막는다.
    /// </summary>
    public bool TryMove(Player player, float x, float y, long nowMs)
    {
        if (FindPlayer(player.Id) is null) return false;

        var inBounds = float.IsFinite(x) && float.IsFinite(y) && x >= 0f && x <= _options.Width && y >= 0f && y <= _options.Height;
        var minElapsedSec = _options.MoveMinIntervalMs / 1000.0;
        var maxElapsedSec = Math.Max(minElapsedSec, _options.MaxMoveDeltaSec);
        var elapsedSec = Math.Clamp((nowMs - player.LastMoveAtMs) / 1000.0, minElapsedSec, maxElapsedSec);
        var allowed = _options.MaxSpeed * _options.SpeedTolerance * elapsedSec;
        var dx = x - player.X;
        var dy = y - player.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);

        if (!inBounds || distance > allowed)
        {
            player.Send(new SMove { PlayerId = player.Id, X = player.X, Y = player.Y, Corrected = true });
            return false;
        }

        player.X = x;
        player.Y = y;
        player.LastMoveAtMs = nowMs;
        Broadcast(new SMove { PlayerId = player.Id, X = x, Y = y }, except: player.Id);
        return true;
    }
}
