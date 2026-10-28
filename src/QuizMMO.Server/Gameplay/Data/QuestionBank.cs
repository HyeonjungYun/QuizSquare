using System.Text.Json;
using QuizMMO.Protocol;

namespace QuizMMO.Server.Gameplay.Data;

/// <summary>퀴즈 문제 하나. Data/questions.json 의 항목과 1:1.</summary>
public sealed class QuizQuestion
{
    public int Id { get; init; }
    public string Text { get; init; } = "";
    public List<string> Choices { get; init; } = new();
    /// <summary>정답인 보기의 인덱스 (0부터).</summary>
    public int Answer { get; init; }
}

/// <summary>
/// 문제 데이터 저장소. 시작 시 JSON 을 읽어 검증하고, 퀴즈방마다 무작위로 N개를 뽑아 준다.
/// 로드 후에는 읽기만 하므로 어느 스레드에서든 안전하다.
/// </summary>
public sealed class QuestionBank
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly List<QuizQuestion> _questions;

    private QuestionBank(List<QuizQuestion> questions)
    {
        _questions = questions;
    }

    public int Count => _questions.Count;
    public IReadOnlyList<QuizQuestion> All => _questions;

    public static QuestionBank LoadFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"question file not found: {path}", path);
        return Parse(File.ReadAllText(path));
    }

    /// <summary>JSON 텍스트를 읽어 검증한다. 문제가 하나라도 잘못되었으면 어떤 것이 왜 잘못되었는지 담은 예외를 던진다.</summary>
    public static QuestionBank Parse(string json)
    {
        var file = JsonSerializer.Deserialize<QuestionFile>(json, JsonOptions)
                   ?? throw new InvalidDataException("question file is empty");
        if (file.Questions.Count == 0) throw new InvalidDataException("question file has no questions");

        var errors = new List<string>();
        var ids = new HashSet<int>();
        foreach (var q in file.Questions)
        {
            if (!ids.Add(q.Id)) errors.Add($"question {q.Id}: duplicate id");
            if (string.IsNullOrWhiteSpace(q.Text)) errors.Add($"question {q.Id}: empty text");
            if (q.Choices.Count < ProtocolInfo.MinQuizChoices || q.Choices.Count > ProtocolInfo.MaxQuizChoices)
                errors.Add($"question {q.Id}: {q.Choices.Count} choices (need {ProtocolInfo.MinQuizChoices}..{ProtocolInfo.MaxQuizChoices})");
            if (q.Answer < 0 || q.Answer >= q.Choices.Count)
                errors.Add($"question {q.Id}: answer index {q.Answer} out of range");
            if (q.Choices.Any(string.IsNullOrWhiteSpace)) errors.Add($"question {q.Id}: empty choice text");
        }
        if (errors.Count > 0) throw new InvalidDataException("invalid questions: " + string.Join("; ", errors));

        return new QuestionBank(file.Questions);
    }

    /// <summary>count 개를 무작위로 뽑는다 (중복 없음). 보유 문제보다 많이 요청하면 전부를 섞어 돌려준다.</summary>
    public List<QuizQuestion> Sample(int count, Random random)
    {
        var pool = new List<QuizQuestion>(_questions);
        for (var i = pool.Count - 1; i > 0; i--)   // Fisher-Yates
        {
            var j = random.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        if (count < pool.Count) pool.RemoveRange(count, pool.Count - count);
        return pool;
    }

    private sealed class QuestionFile
    {
        public List<QuizQuestion> Questions { get; set; } = new();
    }
}
