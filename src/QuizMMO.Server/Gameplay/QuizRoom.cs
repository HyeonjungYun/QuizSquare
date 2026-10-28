using QuizMMO.Protocol;
using QuizMMO.Server.Core;
using QuizMMO.Server.Gameplay.Data;

namespace QuizMMO.Server.Gameplay;

/// <summary>
/// 퀴즈 인스턴스. 대기열에서 뽑힌 플레이어들이 들어와 한 판을 치르고, 끝나면 GameServer 가 월드로 되돌린다.
///
/// 상태 머신 (Update(dt) 가 남은 시간을 줄이며 전이한다):
///   Starting --StartDelaySec--> Question --(TimeLimit 또는 전원 답함)--> Reveal --RevealSec--> (다음 Question | Result) --ResultSec--> Finished
/// 점수 = BasePoints + SpeedBonusMaxPoints x (남은 시간 비율). 결과 단계에 들어갈 때 <see cref="OnResult"/> 로 보상을 채운다.
/// <b>로직 스레드 전용.</b>
/// </summary>
public sealed class QuizRoom : Room
{
    public enum Phase { Starting, Question, Reveal, Result, Finished }

    private sealed class Participant
    {
        public Participant(Player player) { Player = player; }
        public Player Player { get; }
        public uint Score;
        public uint CorrectCount;
        public bool Answered;
        public bool Correct;
        public float AnswerElapsedSec;
    }

    private readonly ServerOptions.QuizOptions _options;
    private readonly IReadOnlyList<QuizQuestion> _questions;
    private readonly Dictionary<ulong, Participant> _participants = new();
    private int _questionIndex = -1;
    private float _phaseRemainingSec;
    private bool _begun;

    public QuizRoom(uint id, string name, ServerOptions.QuizOptions options, IReadOnlyList<QuizQuestion> questions) : base(id, name)
    {
        _options = options;
        _questions = questions;
        _phaseRemainingSec = (float)options.StartDelaySec;
    }

    public override RoomType Type => RoomType.Quiz;
    public Phase CurrentPhase { get; private set; } = Phase.Starting;
    public bool IsFinished => CurrentPhase == Phase.Finished;
    public int QuestionIndex => _questionIndex;
    public int QuestionCount => _questions.Count;
    public float PhaseRemainingSec => _phaseRemainingSec;
    public QuizQuestion? CurrentQuestion => _questionIndex >= 0 && _questionIndex < _questions.Count ? _questions[_questionIndex] : null;

    /// <summary>결과 단계에 들어갈 때 한 번 호출된다. GameServer 가 보상을 계산해 ranking 항목을 채우면 그대로 SQuizResult 로 나간다.</summary>
    public Action<QuizRoom, List<QuizRank>>? OnResult { get; set; }

    public override void Enter(Player player)
    {
        base.Enter(player);
        if (!_participants.ContainsKey(player.Id)) _participants[player.Id] = new Participant(player);
    }

    public override void Leave(ulong playerId)
    {
        base.Leave(playerId);
        _participants.Remove(playerId);
        if (_participants.Count == 0 && !IsFinished) CurrentPhase = Phase.Finished;   // 아무도 없으면 바로 끝
    }

    /// <summary>모든 참가자가 입장한 뒤 한 번 호출한다. SQuizStart 를 보내고 카운트다운을 시작한다.</summary>
    public void Begin()
    {
        if (_begun) return;
        _begun = true;
        if (_questions.Count == 0 || _participants.Count == 0)
        {
            CurrentPhase = Phase.Finished;
            return;
        }
        Broadcast(new SQuizStart
        {
            RoomId = Id,
            QuestionCount = (uint)_questions.Count,
            PlayerCount = (uint)_participants.Count,
            StartsInSec = (float)_options.StartDelaySec,
        });
    }

    /// <summary>
    /// 답안을 받는다. 현재 문제이고, 처음 답하는 것이고, 보기 범위 안이면 수락한다. 결과는 SQuizAnswerAck 로 알려준다.
    /// 전원이 답하면 남은 시간을 0 으로 만들어 다음 틱에 바로 공개한다.
    /// </summary>
    public bool TryAnswer(Player player, uint questionIndex, uint choice)
    {
        var accepted = false;
        if (CurrentPhase == Phase.Question
            && questionIndex == (uint)_questionIndex
            && CurrentQuestion is { } question
            && choice < (uint)question.Choices.Count
            && _participants.TryGetValue(player.Id, out var participant)
            && !participant.Answered)
        {
            participant.Answered = true;
            participant.Correct = (int)choice == question.Answer;
            participant.AnswerElapsedSec = Math.Max(0f, (float)_options.QuestionTimeLimitSec - _phaseRemainingSec);
            accepted = true;
        }

        player.Send(new SQuizAnswerAck { Index = questionIndex, Accepted = accepted });

        if (accepted && _participants.Values.All(p => p.Answered)) _phaseRemainingSec = 0f;
        return accepted;
    }

    public override void Update(float dt)
    {
        if (!_begun || IsFinished) return;
        _phaseRemainingSec -= dt;
        if (_phaseRemainingSec > 0f) return;

        switch (CurrentPhase)
        {
            case Phase.Starting:
                NextQuestion();
                break;
            case Phase.Question:
                Reveal();
                break;
            case Phase.Reveal:
                if (_questionIndex + 1 < _questions.Count) NextQuestion();
                else EnterResult();
                break;
            case Phase.Result:
                CurrentPhase = Phase.Finished;
                break;
        }
    }

    /// <summary>정답 점수. 즉시 답하면 BasePoints + SpeedBonusMaxPoints, 제한시간 끝에 답하면 BasePoints.</summary>
    public uint ScoreFor(float elapsedSec, float timeLimitSec)
    {
        var remainingRatio = timeLimitSec <= 0f ? 0f : Math.Clamp(1f - elapsedSec / timeLimitSec, 0f, 1f);
        return (uint)_options.BasePoints + (uint)Math.Round(_options.SpeedBonusMaxPoints * remainingRatio);
    }

    private void NextQuestion()
    {
        _questionIndex++;
        foreach (var participant in _participants.Values)
        {
            participant.Answered = false;
            participant.Correct = false;
            participant.AnswerElapsedSec = 0f;
        }

        var question = _questions[_questionIndex];
        var message = new SQuizQuestion
        {
            Index = (uint)_questionIndex,
            Total = (uint)_questions.Count,
            Text = question.Text,
            TimeLimitSec = (float)_options.QuestionTimeLimitSec,
        };
        message.Choices.AddRange(question.Choices);
        Broadcast(message);

        CurrentPhase = Phase.Question;
        _phaseRemainingSec = (float)_options.QuestionTimeLimitSec;
    }

    private void Reveal()
    {
        var question = _questions[_questionIndex];
        var timeLimit = (float)_options.QuestionTimeLimitSec;
        var reveal = new SQuizReveal { Index = (uint)_questionIndex, CorrectChoice = (uint)question.Answer };

        foreach (var participant in _participants.Values)
        {
            if (participant.Answered && participant.Correct)
            {
                participant.CorrectCount++;
                participant.Score += ScoreFor(participant.AnswerElapsedSec, timeLimit);
            }
            reveal.Scores.Add(new QuizScore
            {
                PlayerId = participant.Player.Id,
                Nickname = participant.Player.Nickname,
                Score = participant.Score,
                CorrectCount = participant.CorrectCount,
                Answered = participant.Answered,
                Correct = participant.Answered && participant.Correct,
            });
        }
        Broadcast(reveal);

        CurrentPhase = Phase.Reveal;
        _phaseRemainingSec = (float)_options.RevealSec;
    }

    private void EnterResult()
    {
        var results = _participants.Values
            .Select(p => new QuizParticipantResult(p.Player.Id, p.Player.Nickname, p.Score, p.CorrectCount))
            .ToList();
        var ranking = QuizRanking.Rank(results);
        OnResult?.Invoke(this, ranking);

        var result = new SQuizResult { ReturnsInSec = (float)_options.ResultSec };
        result.Ranking.AddRange(ranking);
        Broadcast(result);

        CurrentPhase = Phase.Result;
        _phaseRemainingSec = (float)_options.ResultSec;
    }
}
