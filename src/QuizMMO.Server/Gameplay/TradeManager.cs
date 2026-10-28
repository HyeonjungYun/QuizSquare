using Microsoft.Extensions.Logging;
using QuizMMO.Protocol;
using QuizMMO.Server.Core;
using QuizMMO.Server.Gameplay.Data;

namespace QuizMMO.Server.Gameplay;

/// <summary>
/// 1:1 거래. 요청 → 수락 → 양쪽 제안 → 양쪽 확정 → 교환. 월드 룸에 있는 두 플레이어만 가능하고 한 사람은 한 거래만.
/// 제안이 바뀌면 양쪽 확정이 풀리고, 교환 직전에 양쪽 인벤토리를 다시 검증한다 (서버 권위).
/// 시간 초과, 접속 종료, 월드 이탈은 거래를 취소한다. 수치는 Server:Trade 설정. <b>로직 스레드 전용.</b>
/// </summary>
public sealed class TradeManager
{
    private enum State { Pending, Open }

    private sealed class Offer
    {
        public long Gold;
        public readonly Dictionary<uint, uint> Items = new();
        public bool Confirmed;
    }

    private sealed class Trade
    {
        public Trade(uint id, Player initiator, Player target)
        {
            Id = id;
            Initiator = initiator;
            Target = target;
        }

        public uint Id { get; }
        public Player Initiator { get; }
        public Player Target { get; }
        public State State = State.Pending;
        public readonly Offer InitiatorOffer = new();
        public readonly Offer TargetOffer = new();
        public float RemainingSec;

        public bool Involves(Player player) => player.Id == Initiator.Id || player.Id == Target.Id;
        public Player PartnerOf(Player player) => player.Id == Initiator.Id ? Target : Initiator;
        public Offer OfferOf(Player player) => player.Id == Initiator.Id ? InitiatorOffer : TargetOffer;
    }

    private readonly ServerOptions.TradeOptions _options;
    private readonly ItemCatalog _items;
    private readonly uint _worldRoomId;
    private readonly ILogger _logger;
    private readonly Dictionary<uint, Trade> _trades = new();
    private readonly List<uint> _expired = new();
    private uint _nextId = 1;

    public TradeManager(ServerOptions.TradeOptions options, ItemCatalog items, uint worldRoomId, ILogger logger)
    {
        _options = options;
        _items = items;
        _worldRoomId = worldRoomId;
        _logger = logger;
    }

    public int ActiveCount => _trades.Count;

    /// <summary>거래를 요청한다. 거절 사유는 요청자에게 SError 로 간다.</summary>
    public bool Request(Player from, Player? target)
    {
        if (target is null || !target.IsConnected) return Fail(from, ErrorCode.TargetNotFound, "target player not found");
        if (target.Id == from.Id) return Fail(from, ErrorCode.SelfTarget, "cannot trade with yourself");
        if (from.RoomId != _worldRoomId || target.RoomId != _worldRoomId) return Fail(from, ErrorCode.NotInWorld, "both players must be in the world");
        if (from.TradeId != 0) return Fail(from, ErrorCode.AlreadyInTrade, "you are already in a trade");
        if (target.TradeId != 0) return Fail(from, ErrorCode.TargetBusy, "target is already in a trade");

        var trade = new Trade(_nextId++, from, target) { RemainingSec = (float)_options.RequestTimeoutSec };
        _trades[trade.Id] = trade;
        from.TradeId = trade.Id;
        target.TradeId = trade.Id;

        target.Send(new STradeRequested
        {
            TradeId = trade.Id,
            FromPlayerId = from.Id,
            FromNickname = from.Nickname,
            ExpiresInSec = (float)_options.RequestTimeoutSec,
        });
        _logger.LogInformation("trade {TradeId}: {From} -> {To} requested", trade.Id, from.Nickname, target.Nickname);
        return true;
    }

    /// <summary>요청받은 쪽이 수락/거절한다.</summary>
    public bool Respond(Player who, uint tradeId, bool accept)
    {
        if (!TryGet(who, tradeId, out var trade)) return false;
        if (trade.State != State.Pending || who.Id != trade.Target.Id) return Fail(who, ErrorCode.InvalidRequest, "trade is not awaiting your response");

        if (!accept)
        {
            CancelTrade(trade, TradeCancelReason.Declined);
            return true;
        }

        trade.State = State.Open;
        trade.RemainingSec = (float)_options.IdleTimeoutSec;
        trade.Initiator.Send(new STradeStarted { TradeId = trade.Id, PartnerPlayerId = trade.Target.Id, PartnerNickname = trade.Target.Nickname });
        trade.Target.Send(new STradeStarted { TradeId = trade.Id, PartnerPlayerId = trade.Initiator.Id, PartnerNickname = trade.Initiator.Nickname });
        _logger.LogInformation("trade {TradeId}: accepted", trade.Id);
        return true;
    }

    /// <summary>내 제안을 통째로 바꾼다. 인벤토리에 없는 것, 거래 불가 아이템은 거절되고 기존 제안이 유지된다. 양쪽 확정이 풀린다.</summary>
    public bool SetOffer(Player who, uint tradeId, long gold, IReadOnlyList<ItemStack> items)
    {
        if (!TryGet(who, tradeId, out var trade)) return false;
        if (trade.State != State.Open) return Fail(who, ErrorCode.InvalidRequest, "trade is not open yet");
        if (gold < 0 || gold > who.Inventory.Gold) return Fail(who, ErrorCode.InsufficientItems, "not enough gold");

        var merged = new Dictionary<uint, uint>();
        foreach (var stack in items)
        {
            if (stack.Count == 0) return Fail(who, ErrorCode.InvalidRequest, "item count must be positive");
            if (!_items.IsTradable(stack.ItemId)) return Fail(who, ErrorCode.InsufficientItems, $"item {stack.ItemId} is not tradable");
            merged[stack.ItemId] = (merged.TryGetValue(stack.ItemId, out var existing) ? existing : 0u) + stack.Count;
        }
        if (merged.Count > ProtocolInfo.MaxTradeItemStacks) return Fail(who, ErrorCode.InvalidRequest, "too many item stacks");
        foreach (var (itemId, count) in merged)
        {
            if (!who.Inventory.Has(itemId, count)) return Fail(who, ErrorCode.InsufficientItems, $"not enough of item {itemId}");
        }

        var offer = trade.OfferOf(who);
        offer.Gold = gold;
        offer.Items.Clear();
        foreach (var (itemId, count) in merged) offer.Items[itemId] = count;
        trade.InitiatorOffer.Confirmed = false;
        trade.TargetOffer.Confirmed = false;
        trade.RemainingSec = (float)_options.IdleTimeoutSec;

        var message = new STradeOffer { TradeId = trade.Id, PlayerId = who.Id, Gold = gold };
        foreach (var (itemId, count) in merged) message.Items.Add(new ItemStack { ItemId = itemId, Count = count });
        var packet = PacketSerializer.Serialize(message);
        trade.Initiator.Send(packet);
        trade.Target.Send(packet);
        return true;
    }

    /// <summary>내 제안을 확정한다. 양쪽이 확정하면 교환한다.</summary>
    public bool Confirm(Player who, uint tradeId)
    {
        if (!TryGet(who, tradeId, out var trade)) return false;
        if (trade.State != State.Open) return Fail(who, ErrorCode.InvalidRequest, "trade is not open yet");

        trade.OfferOf(who).Confirmed = true;
        trade.RemainingSec = (float)_options.IdleTimeoutSec;
        var confirmed = PacketSerializer.Serialize(new STradeConfirmed { TradeId = trade.Id, PlayerId = who.Id });
        trade.Initiator.Send(confirmed);
        trade.Target.Send(confirmed);

        if (trade.InitiatorOffer.Confirmed && trade.TargetOffer.Confirmed) Execute(trade);
        return true;
    }

    public bool Cancel(Player who, uint tradeId)
    {
        if (!TryGet(who, tradeId, out var trade)) return false;
        CancelTrade(trade, TradeCancelReason.Cancelled);
        return true;
    }

    /// <summary>플레이어가 월드를 떠나거나 접속을 끊었다. 관련 거래를 취소한다.</summary>
    public void OnPlayerLeft(Player player, TradeCancelReason reason)
    {
        if (player.TradeId == 0) return;
        if (_trades.TryGetValue(player.TradeId, out var trade) && trade.Involves(player)) CancelTrade(trade, reason);
        else player.TradeId = 0;
    }

    /// <summary>매 틱. 시간 초과한 거래를 취소한다.</summary>
    public void Update(float dt)
    {
        _expired.Clear();
        foreach (var (id, trade) in _trades)
        {
            trade.RemainingSec -= dt;
            if (trade.RemainingSec <= 0f) _expired.Add(id);
        }
        foreach (var id in _expired)
        {
            if (_trades.TryGetValue(id, out var trade)) CancelTrade(trade, TradeCancelReason.Timeout);
        }
    }

    private bool TryGet(Player who, uint tradeId, out Trade trade)
    {
        if (_trades.TryGetValue(tradeId, out trade!) && trade.Involves(who)) return true;
        Fail(who, ErrorCode.TradeNotFound, "no such trade");
        return false;
    }

    private static bool IsBacked(Offer offer, Inventory inventory)
    {
        if (offer.Gold > inventory.Gold) return false;
        foreach (var (itemId, count) in offer.Items)
        {
            if (!inventory.Has(itemId, count)) return false;
        }
        return true;
    }

    private void Execute(Trade trade)
    {
        var a = trade.Initiator;
        var b = trade.Target;
        if (!IsBacked(trade.InitiatorOffer, a.Inventory) || !IsBacked(trade.TargetOffer, b.Inventory))
        {
            CancelTrade(trade, TradeCancelReason.InvalidOffer);
            return;
        }

        Transfer(trade.InitiatorOffer, a.Inventory, b.Inventory);
        Transfer(trade.TargetOffer, b.Inventory, a.Inventory);

        _trades.Remove(trade.Id);
        a.TradeId = 0;
        b.TradeId = 0;

        var completed = PacketSerializer.Serialize(new STradeCompleted { TradeId = trade.Id });
        a.Send(completed);
        b.Send(completed);
        a.Send(a.Inventory.ToMessage());
        b.Send(b.Inventory.ToMessage());
        _logger.LogInformation("trade {TradeId}: completed ({A} gave {GoldA} gold/{ItemsA} stacks, {B} gave {GoldB} gold/{ItemsB} stacks)",
            trade.Id, a.Nickname, trade.InitiatorOffer.Gold, trade.InitiatorOffer.Items.Count, b.Nickname, trade.TargetOffer.Gold, trade.TargetOffer.Items.Count);
    }

    private static void Transfer(Offer offer, Inventory from, Inventory to)
    {
        from.TryRemoveGold(offer.Gold);
        to.AddGold(offer.Gold);
        foreach (var (itemId, count) in offer.Items)
        {
            from.TryRemoveItem(itemId, count);
            to.AddItem(itemId, count);
        }
    }

    private void CancelTrade(Trade trade, TradeCancelReason reason)
    {
        _trades.Remove(trade.Id);
        trade.Initiator.TradeId = 0;
        trade.Target.TradeId = 0;
        var packet = PacketSerializer.Serialize(new STradeCancelled { TradeId = trade.Id, Reason = reason });
        trade.Initiator.Send(packet);
        trade.Target.Send(packet);
        _logger.LogInformation("trade {TradeId}: cancelled ({Reason})", trade.Id, reason);
    }

    private static bool Fail(Player player, ErrorCode code, string detail)
    {
        player.Send(new SError { Code = code, Detail = detail });
        return false;
    }
}
