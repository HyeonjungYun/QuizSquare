using QuizMMO.Protocol;
using QuizMMO.Server.Core;
using QuizMMO.Server.Gameplay.Data;

namespace QuizMMO.Server.Gameplay;

/// <summary>퀴즈 결과에 보상을 매긴다. 수치는 Server:Rewards 설정, 아이템은 ItemCatalog 에 있는 것만. <b>로직 스레드 전용.</b></summary>
public static class RewardCalculator
{
    /// <summary>
    /// 등수별 보상을 계산해 ranking 항목(GoldReward, ItemRewards)에 채우고, 아직 방에 있는 플레이어의 인벤토리에 넣는다.
    /// 골드 = 참가 + 정답당 + 등수 보너스. 아이템 = 참가 배지 + 등수 아이템.
    /// 인벤토리가 바뀐 플레이어 목록을 돌려준다 (호출자가 SInventory 를 보낸다).
    /// </summary>
    public static List<Player> Apply(List<QuizRank> ranking, ServerOptions.RewardOptions options, ItemCatalog items, Func<ulong, Player?> findPlayer)
    {
        var changed = new List<Player>();
        foreach (var entry in ranking)
        {
            var rankIndex = (int)entry.Rank - 1;

            var gold = options.ParticipationGold + options.GoldPerCorrect * entry.CorrectCount;
            if (rankIndex >= 0 && rankIndex < options.RankBonusGold.Count) gold += options.RankBonusGold[rankIndex];
            entry.GoldReward = gold;

            entry.ItemRewards.Clear();
            if (options.ParticipationItemId != 0 && items.Contains(options.ParticipationItemId))
            {
                entry.ItemRewards.Add(new ItemStack { ItemId = options.ParticipationItemId, Count = 1 });
            }
            if (rankIndex >= 0 && rankIndex < options.RankItemIds.Count)
            {
                var rankItemId = options.RankItemIds[rankIndex];
                if (rankItemId != 0 && items.Contains(rankItemId))
                {
                    entry.ItemRewards.Add(new ItemStack { ItemId = rankItemId, Count = 1 });
                }
            }

            var player = findPlayer(entry.PlayerId);
            if (player is null) continue;   // 결과 전에 나간 플레이어는 보상을 받지 않는다

            player.Inventory.AddGold(gold);
            foreach (var stack in entry.ItemRewards) player.Inventory.AddItem(stack.ItemId, stack.Count);
            changed.Add(player);
        }
        return changed;
    }
}
