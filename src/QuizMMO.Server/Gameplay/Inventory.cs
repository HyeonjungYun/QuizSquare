using QuizMMO.Protocol;

namespace QuizMMO.Server.Gameplay;

/// <summary>
/// 플레이어의 골드와 아이템. <b>로직 스레드 전용.</b> 인메모리이며 서버가 꺼지면 사라진다 (DB 영속화는 브리프 Out of scope).
/// 음수나 보유량 초과 차감은 거절되고 false 를 돌려준다. 바뀐 뒤에는 <see cref="ToMessage"/> 로 전체 상태를 클라이언트에 보낸다.
/// </summary>
public sealed class Inventory
{
    private readonly Dictionary<uint, uint> _items = new();

    public long Gold { get; private set; }
    public IReadOnlyDictionary<uint, uint> Items => _items;

    public uint Count(uint itemId) => _items.TryGetValue(itemId, out var count) ? count : 0;
    public bool Has(uint itemId, uint count) => count == 0 || Count(itemId) >= count;

    public bool AddGold(long amount)
    {
        if (amount < 0) return false;
        Gold += amount;
        return true;
    }

    public bool TryRemoveGold(long amount)
    {
        if (amount < 0 || amount > Gold) return false;
        Gold -= amount;
        return true;
    }

    public void AddItem(uint itemId, uint count)
    {
        if (count == 0) return;
        _items[itemId] = Count(itemId) + count;
    }

    public bool TryRemoveItem(uint itemId, uint count)
    {
        if (count == 0) return true;
        var have = Count(itemId);
        if (have < count) return false;
        if (have == count) _items.Remove(itemId);
        else _items[itemId] = have - count;
        return true;
    }

    public SInventory ToMessage()
    {
        var message = new SInventory { Gold = Gold };
        foreach (var (itemId, count) in _items)
        {
            message.Items.Add(new ItemStack { ItemId = itemId, Count = count });
        }
        return message;
    }
}
