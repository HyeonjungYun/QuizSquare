using QuizMMO.Server.Gameplay.Data;
using Xunit;

namespace QuizMMO.Server.Tests;

public class QuestionBankTests
{
    private const string ValidJson = """
        {
          "questions": [
            { "id": 1, "text": "q1", "choices": ["a", "b"], "answer": 1 },
            { "id": 2, "text": "q2", "choices": ["a", "b", "c", "d"], "answer": 3 },
            { "id": 3, "text": "q3", "choices": ["x", "y"], "answer": 0 }
          ]
        }
        """;

    [Fact]
    public void Parse_ValidFile_LoadsEveryQuestion()
    {
        var bank = QuestionBank.Parse(ValidJson);

        Assert.Equal(3, bank.Count);
        Assert.Equal("q2", bank.All[1].Text);
        Assert.Equal(3, bank.All[1].Answer);
    }

    [Fact]
    public void Parse_AnswerOutOfRange_Throws()
    {
        const string json = """{ "questions": [ { "id": 1, "text": "q", "choices": ["a", "b"], "answer": 2 } ] }""";

        var error = Assert.Throws<InvalidDataException>(() => QuestionBank.Parse(json));
        Assert.Contains("answer index", error.Message);
    }

    [Fact]
    public void Parse_TooFewChoicesOrDuplicateId_Throws()
    {
        const string fewChoices = """{ "questions": [ { "id": 1, "text": "q", "choices": ["a"], "answer": 0 } ] }""";
        const string duplicate = """{ "questions": [ { "id": 1, "text": "q", "choices": ["a","b"], "answer": 0 }, { "id": 1, "text": "q", "choices": ["a","b"], "answer": 0 } ] }""";

        Assert.Throws<InvalidDataException>(() => QuestionBank.Parse(fewChoices));
        Assert.Throws<InvalidDataException>(() => QuestionBank.Parse(duplicate));
    }

    [Fact]
    public void Parse_EmptyList_Throws()
    {
        Assert.Throws<InvalidDataException>(() => QuestionBank.Parse("""{ "questions": [] }"""));
    }

    [Fact]
    public void Sample_ReturnsDistinctQuestions_AndCapsAtBankSize()
    {
        var bank = QuestionBank.Parse(ValidJson);
        var random = new Random(42);

        var two = bank.Sample(2, random);
        var many = bank.Sample(10, random);

        Assert.Equal(2, two.Count);
        Assert.Equal(2, two.Select(q => q.Id).Distinct().Count());
        Assert.Equal(3, many.Count);
        Assert.Equal(new[] { 1, 2, 3 }, many.Select(q => q.Id).OrderBy(id => id));
    }

    [Fact]
    public void ShippedDataFiles_AreValid()
    {
        // 저장소에 들어 있는 실제 데이터 파일이 깨지지 않았는지 (빌드 출력 디렉터리로 복사된 파일을 읽는다).
        var questions = QuestionBank.LoadFile(Path.Combine(AppContext.BaseDirectory, "Data", "questions.json"));
        var items = ItemCatalog.LoadFile(Path.Combine(AppContext.BaseDirectory, "Data", "items.json"));

        Assert.True(questions.Count >= 10);
        Assert.True(items.Contains(1001));
        Assert.False(items.IsTradable(2001));
    }
}

public class ItemCatalogTests
{
    [Fact]
    public void Parse_LoadsItemsAndTradability()
    {
        const string json = """
            { "items": [
              { "id": 1001, "name": "trophy", "tradable": true },
              { "id": 2001, "name": "badge", "description": "not for trade", "tradable": false },
              { "id": 3001, "name": "default tradable" }
            ] }
            """;

        var catalog = ItemCatalog.Parse(json);

        Assert.Equal(3, catalog.Count);
        Assert.True(catalog.IsTradable(1001));
        Assert.False(catalog.IsTradable(2001));
        Assert.True(catalog.IsTradable(3001));
        Assert.False(catalog.IsTradable(9999));
        Assert.True(catalog.TryGet(2001, out var badge));
        Assert.Equal("not for trade", badge.Description);
    }

    [Fact]
    public void Parse_DuplicateOrZeroId_Throws()
    {
        Assert.Throws<InvalidDataException>(() => ItemCatalog.Parse("""{ "items": [ { "id": 0, "name": "x" } ] }"""));
        Assert.Throws<InvalidDataException>(() => ItemCatalog.Parse("""{ "items": [ { "id": 1, "name": "x" }, { "id": 1, "name": "y" } ] }"""));
    }
}
