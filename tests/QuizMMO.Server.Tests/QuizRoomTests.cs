using QuizMMO.Protocol;
using QuizMMO.Server.Core;
using QuizMMO.Server.Gameplay;
using QuizMMO.Server.Gameplay.Data;
using Xunit;

namespace QuizMMO.Server.Tests;

public class QuizRoomTests
{
    private static ServerOptions.QuizOptions Options() => new()
    {
        StartDelaySec = 1, QuestionTimeLimitSec = 10, RevealSec = 1, ResultSec = 1, BasePoints = 100, SpeedBonusMaxPoints = 50,
    };

    private static List<QuizQuestion> Questions() => new()
    {
        new QuizQuestion { Id = 1, Text = "q1", Choices = new() { "a", "b" }, Answer = 1 },
        new QuizQuestion { Id = 2, Text = "q2", Choices = new() { "a", "b", "c" }, Answer = 0 },
    };

    private static (QuizRoom room, Player a, FakeSink sinkA, Player b, FakeSink sinkB) Setup()
    {
        var room = new QuizRoom(5, "Quiz-5", Options(), Questions());
        var sinkA = new FakeSink();
        var sinkB = new FakeSink();
        var a = new Player(1, "A", sinkA);
        var b = new Player(2, "B", sinkB);
        room.Enter(a);
        room.Enter(b);
        room.Begin();
        return (room, a, sinkA, b, sinkB);
    }

    [Fact]
    public void Begin_SendsQuizStartToEveryone()
    {
        var (room, _, sinkA, _, sinkB) = Setup();

        foreach (var sink in new[] { sinkA, sinkB })
        {
            var start = Assert.Single(sink.Of<SQuizStart>());
            Assert.Equal(2u, start.QuestionCount);
            Assert.Equal(2u, start.PlayerCount);
        }
        Assert.Equal(QuizRoom.Phase.Starting, room.CurrentPhase);
    }

    [Fact]
    public void AfterStartDelay_FirstQuestionIsSent()
    {
        var (room, _, sinkA, _, _) = Setup();

        room.Update(0.5f);
        Assert.Empty(sinkA.Of<SQuizQuestion>());
        room.Update(0.5f);

        var question = Assert.Single(sinkA.Of<SQuizQuestion>());
        Assert.Equal(0u, question.Index);
        Assert.Equal(2u, question.Total);
        Assert.Equal("q1", question.Text);
        Assert.Equal(new[] { "a", "b" }, question.Choices);
        Assert.Equal(QuizRoom.Phase.Question, room.CurrentPhase);
    }

    [Fact]
    public void CorrectImmediateAnswer_ScoresBasePlusFullSpeedBonus()
    {
        var (room, a, sinkA, b, _) = Setup();
        room.Update(1.0f);   // → Question 0 (정답 b = 1)

        Assert.True(room.TryAnswer(a, 0, 1));          // A 정답, 즉시
        room.Update(5.0f);                             // 5초 지난 뒤 B 가 오답
        Assert.True(room.TryAnswer(b, 0, 0));
        room.Update(0.01f);                            // 전원 답함 → 공개

        var reveal = Assert.Single(sinkA.Of<SQuizReveal>());
        Assert.Equal(1u, reveal.CorrectChoice);
        var scoreA = reveal.Scores.Single(s => s.PlayerId == 1);
        var scoreB = reveal.Scores.Single(s => s.PlayerId == 2);
        Assert.Equal(150u, scoreA.Score);              // 100 + 50 x (남은 비율 1.0)
        Assert.True(scoreA.Correct);
        Assert.Equal(1u, scoreA.CorrectCount);
        Assert.Equal(0u, scoreB.Score);
        Assert.False(scoreB.Correct);
        Assert.True(scoreB.Answered);
        Assert.Equal(QuizRoom.Phase.Reveal, room.CurrentPhase);
    }

    [Fact]
    public void LateCorrectAnswer_GetsSmallerSpeedBonus()
    {
        var (room, a, sinkA, _, _) = Setup();
        room.Update(1.0f);
        room.Update(5.0f);   // 제한시간 10초 중 5초 경과

        room.TryAnswer(a, 0, 1);
        room.Update(5.0f);   // 시간 만료 → 공개

        var reveal = Assert.Single(sinkA.Of<SQuizReveal>());
        Assert.Equal(125u, reveal.Scores.Single(s => s.PlayerId == 1).Score);   // 100 + 50 x 0.5
    }

    [Fact]
    public void DuplicateOrWrongIndexOrOutOfRangeAnswer_IsRejected()
    {
        var (room, a, sinkA, _, _) = Setup();
        Assert.False(room.TryAnswer(a, 0, 0));          // Starting 단계: 아직 문제 없음
        room.Update(1.0f);

        Assert.False(room.TryAnswer(a, 1, 0));          // 다른 문제 번호
        Assert.False(room.TryAnswer(a, 0, 5));          // 보기 범위 밖
        Assert.True(room.TryAnswer(a, 0, 1));
        Assert.False(room.TryAnswer(a, 0, 1));          // 중복

        var acks = sinkA.Of<SQuizAnswerAck>().ToList();
        Assert.Equal(5, acks.Count);
        Assert.Equal(new[] { false, false, false, true, false }, acks.Select(x => x.Accepted));
    }

    [Fact]
    public void AfterLastReveal_ResultRankingIsSentAndRoomFinishes()
    {
        var (room, a, sinkA, b, sinkB) = Setup();
        var resultCallbacks = 0;
        room.OnResult = (_, ranking) => { resultCallbacks++; ranking[0].GoldReward = 777; };

        room.Update(1.0f);           // Q0
        room.TryAnswer(a, 0, 1);     // A 정답
        room.TryAnswer(b, 0, 0);
        room.Update(0.01f);          // Reveal 0
        room.Update(1.0f);           // Q1
        room.TryAnswer(a, 1, 2);     // A 오답
        room.TryAnswer(b, 1, 0);     // B 정답
        room.Update(0.01f);          // Reveal 1
        room.Update(1.0f);           // Result

        Assert.Equal(QuizRoom.Phase.Result, room.CurrentPhase);
        Assert.Equal(1, resultCallbacks);
        var result = Assert.Single(sinkB.Of<SQuizResult>());
        Assert.Equal(2, result.Ranking.Count);
        // A 와 B 모두 정답 1개를 즉시 맞혀 150점 동점 → 둘 다 1등, 순서는 플레이어 id 오름차순
        Assert.Equal(1u, result.Ranking[0].Rank);
        Assert.Equal(1UL, result.Ranking[0].PlayerId);
        Assert.Equal(1u, result.Ranking[1].Rank);
        Assert.Equal(777, result.Ranking[0].GoldReward);    // OnResult 가 채운 보상이 그대로 나간다
        Assert.Single(sinkA.Of<SQuizResult>());

        room.Update(1.0f);           // Result 끝
        Assert.True(room.IsFinished);
    }

    [Fact]
    public void Leave_LastParticipant_FinishesRoomImmediately()
    {
        var (room, a, _, b, _) = Setup();
        room.Update(1.0f);

        room.Leave(a.Id);
        Assert.False(room.IsFinished);
        room.Leave(b.Id);

        Assert.True(room.IsFinished);
        Assert.Equal(0, room.PlayerCount);
    }

    [Fact]
    public void AnswerFromPlayerWhoLeft_IsRejected()
    {
        var (room, a, sinkA, _, _) = Setup();
        room.Update(1.0f);
        room.Leave(a.Id);
        sinkA.Received.Clear();

        Assert.False(room.TryAnswer(a, 0, 1));
        Assert.False(Assert.Single(sinkA.Of<SQuizAnswerAck>()).Accepted);
    }

    [Fact]
    public void Begin_WithoutQuestions_FinishesImmediately()
    {
        var room = new QuizRoom(6, "Quiz-6", Options(), new List<QuizQuestion>());
        room.Enter(new Player(1, "A", new FakeSink()));

        room.Begin();

        Assert.True(room.IsFinished);
    }
}
