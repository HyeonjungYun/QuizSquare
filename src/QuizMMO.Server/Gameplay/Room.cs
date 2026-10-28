using Google.Protobuf;
using QuizMMO.Protocol;

namespace QuizMMO.Server.Gameplay;

/// <summary>
/// 플레이어 집합의 기본 단위. 월드(광장)도 퀴즈방도 Room 이다.
/// <b>로직 스레드 전용.</b> GameLoop 가 소유하고 그 스레드에서만 호출하므로 락이 없다.
/// I/O 스레드는 룸을 직접 만지지 말고 GameLoop.Post 로 작업을 넘긴다.
/// </summary>
public abstract class Room
{
    private readonly Dictionary<ulong, Player> _players = new();

    protected Room(uint id, string name)
    {
        Id = id;
        Name = name;
    }

    public uint Id { get; }
    public string Name { get; }
    public abstract RoomType Type { get; }
    public int PlayerCount => _players.Count;

    /// <summary>
    /// 플레이어를 넣는다. 먼저 <see cref="PlacePlayer"/> 로 위치를 정하고, 본인에게 SRoomChanged 와 기존 인원의 SPlayerEnter 를,
    /// 기존 인원에게 SPlayerEnter 를 보낸다. 이미 들어와 있으면 무시.
    /// (인원 전체를 보내는 것은 AOI 가 없는 뼈대의 선택이다 — 브리프 Out of scope 참고.)
    /// </summary>
    public virtual void Enter(Player player)
    {
        if (_players.ContainsKey(player.Id)) return;

        PlacePlayer(player);

        player.Send(new SRoomChanged { RoomId = Id, RoomType = Type, RoomName = Name, X = player.X, Y = player.Y });
        foreach (var other in _players.Values)
        {
            player.Send(new SPlayerEnter { PlayerId = other.Id, Nickname = other.Nickname, X = other.X, Y = other.Y });
        }
        Broadcast(new SPlayerEnter { PlayerId = player.Id, Nickname = player.Nickname, X = player.X, Y = player.Y });

        _players[player.Id] = player;
        player.RoomId = Id;
    }

    /// <summary>입장 시 위치를 정한다. 기본은 현재 위치 유지. 월드는 스폰 지점으로 덮어쓴다.</summary>
    protected virtual void PlacePlayer(Player player) { }

    /// <summary>플레이어를 뺀다. 남은 인원에게 SPlayerLeave. 없으면 무시.</summary>
    public virtual void Leave(ulong playerId)
    {
        if (!_players.Remove(playerId, out var player)) return;
        if (player.RoomId == Id) player.RoomId = 0;
        Broadcast(new SPlayerLeave { PlayerId = playerId });
    }

    /// <summary>매 로직 틱 호출. dt 는 초.</summary>
    public virtual void Update(float dt) { }

    /// <summary>룸 안 채팅. 보낸 사람을 포함한 전원에게 SChat.</summary>
    public void Chat(Player from, string text)
    {
        Broadcast(new SChat { PlayerId = from.Id, Nickname = from.Nickname, Text = text });
    }

    /// <summary>한 번만 직렬화해서 전원에게 같은 바이트를 보낸다. except 는 제외할 플레이어.</summary>
    public void Broadcast(IMessage message, ulong except = 0)
    {
        if (_players.Count == 0) return;
        var packet = PacketSerializer.Serialize(message);
        foreach (var player in _players.Values)
        {
            if (player.Id != except) player.Send(packet);
        }
    }

    public Player? FindPlayer(ulong playerId) => _players.TryGetValue(playerId, out var player) ? player : null;

    /// <summary>현재 인원의 복사본. 순회하면서 Enter/Leave 를 부를 때 쓴다.</summary>
    public List<Player> Players() => _players.Values.ToList();
}
