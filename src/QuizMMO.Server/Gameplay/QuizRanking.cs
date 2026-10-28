using QuizMMO.Protocol;

namespace QuizMMO.Server.Gameplay;

/// <summary>한 참가자의 최종 성적.</summary>
public readonly record struct QuizParticipantResult(ulong PlayerId, string Nickname, uint Score, uint CorrectCount);

/// <summary>퀴즈 성적을 등수로 바꾼다. 순수 함수라 테스트가 쉽다.</summary>
public static class QuizRanking
{
    /// <summary>
    /// 점수 내림차순, 정답 수 내림차순, 플레이어 id 오름차순으로 정렬한다.
    /// 점수와 정답 수가 모두 같으면 같은 등수를 받고 다음 등수는 건너뛴다 (1, 2, 2, 4).
    /// 보상 필드는 비워 둔다 (RewardCalculator 가 채운다).
    /// </summary>
    public static List<QuizRank> Rank(IReadOnlyList<QuizParticipantResult> results)
    {
        var ordered = results
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.CorrectCount)
            .ThenBy(r => r.PlayerId)
            .ToList();

        var ranking = new List<QuizRank>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var result = ordered[i];
            var tiedWithPrevious = i > 0 && ordered[i - 1].Score == result.Score && ordered[i - 1].CorrectCount == result.CorrectCount;
            var rank = tiedWithPrevious ? ranking[i - 1].Rank : (uint)(i + 1);
            ranking.Add(new QuizRank
            {
                Rank = rank,
                PlayerId = result.PlayerId,
                Nickname = result.Nickname,
                Score = result.Score,
                CorrectCount = result.CorrectCount,
            });
        }
        return ranking;
    }
}
