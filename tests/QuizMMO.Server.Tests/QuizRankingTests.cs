using QuizMMO.Protocol;
using QuizMMO.Server.Core;
using QuizMMO.Server.Gameplay;
using QuizMMO.Server.Gameplay.Data;
using Xunit;

namespace QuizMMO.Server.Tests;

public class QuizRankingTests
{
    [Fact]
    public void Rank_OrdersByScoreThenCorrectCount_AndSharesTiedRanks()
    {
        var results = new List<QuizParticipantResult>
        {
            new(1, "p1", 100, 1),
            new(2, "p2", 300, 2),
            new(3, "p3", 100, 1),
            new(4, "p4", 100, 2),
            new(5, "p5", 0, 0),
        };

        var ranking = QuizRanking.Rank(results);

        Assert.Equal(new ulong[] { 2, 4, 1, 3, 5 }, ranking.Select(r => r.PlayerId));
        Assert.Equal(new uint[] { 1, 2, 3, 3, 5 }, ranking.Select(r => r.Rank));   // 1번과 3번은 동점 → 3등 공유, 다음은 5등
        Assert.All(ranking, r => Assert.Equal(0, r.GoldReward));
    }

    [Fact]
    public void Rank_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(QuizRanking.Rank(new List<QuizParticipantResult>()));
    }
}

public class RewardCalculatorTests
{
    private static ServerOptions.RewardOptions Options() => new()
    {
        ParticipationGold = 10, GoldPerCorrect = 5,
        RankBonusGold = new List<long> { 100, 50 },
        RankItemIds = new List<uint> { 1001, 1002 },
        ParticipationItemId = 2001,
    };

    private static ItemCatalog Catalog() => ItemCatalog.FromItems(new[]
    {
        new ItemDefinition { Id = 1001, Name = "gold trophy" },
        new ItemDefinition { Id = 1002, Name = "silver trophy" },
        new ItemDefinition { Id = 2001, Name = "badge", Tradable = false },
    });

    [Fact]
    public void Apply_FillsRewardsAndUpdatesInventories()
    {
        var first = new Player(1, "first", new FakeSink());
        var second = new Player(2, "second", new FakeSink());
        var third = new Player(3, "third", new FakeSink());
        var players = new Dictionary<ulong, Player> { [1] = first, [2] = second, [3] = third };
        var ranking = QuizRanking.Rank(new List<QuizParticipantResult> { new(1, "first", 300, 3), new(2, "second", 100, 1), new(3, "third", 0, 0) });

        var changed = RewardCalculator.Apply(ranking, Options(), Catalog(), id => players.GetValueOrDefault(id));

        Assert.Equal(3, changed.Count);
        Assert.Equal(10 + 5 * 3 + 100, ranking[0].GoldReward);
        Assert.Equal(new uint[] { 2001, 1001 }, ranking[0].ItemRewards.Select(i => i.ItemId));
        Assert.Equal(10 + 5 * 1 + 50, ranking[1].GoldReward);
        Assert.Equal(new uint[] { 2001, 1002 }, ranking[1].ItemRewards.Select(i => i.ItemId));
        Assert.Equal(10, ranking[2].GoldReward);                                     // 3등 보너스는 설정에 없음
        Assert.Equal(new uint[] { 2001 }, ranking[2].ItemRewards.Select(i => i.ItemId));

        Assert.Equal(125, first.Inventory.Gold);
        Assert.Equal(1u, first.Inventory.Count(1001));
        Assert.Equal(1u, first.Inventory.Count(2001));
        Assert.Equal(10, third.Inventory.Gold);
    }

    [Fact]
    public void Apply_SkipsInventoryOfPlayersWhoLeft_ButStillFillsEntry()
    {
        var ranking = QuizRanking.Rank(new List<QuizParticipantResult> { new(1, "gone", 50, 1) });

        var changed = RewardCalculator.Apply(ranking, Options(), Catalog(), _ => null);

        Assert.Empty(changed);
        Assert.Equal(10 + 5 + 100, ranking[0].GoldReward);
    }

    [Fact]
    public void Apply_IgnoresRewardItemsMissingFromCatalog()
    {
        var options = Options();
        options.RankItemIds = new List<uint> { 9999 };
        options.ParticipationItemId = 0;
        var player = new Player(1, "p", new FakeSink());
        var ranking = QuizRanking.Rank(new List<QuizParticipantResult> { new(1, "p", 50, 1) });

        RewardCalculator.Apply(ranking, options, Catalog(), _ => player);

        Assert.Empty(ranking[0].ItemRewards);
        Assert.Empty(player.Inventory.Items);
    }
}
