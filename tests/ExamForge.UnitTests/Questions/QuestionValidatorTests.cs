using ExamForge.Domain.Questions;
using Xunit;

namespace ExamForge.UnitTests.Questions;

public class QuestionValidatorTests
{
    private static QuestionDefinition ValidSingle() => new()
    {
        Type = QuestionType.SingleChoice,
        Prompt = "Выберите перевод",
        Options = [new("a", "глаз"), new("b", "ухо")],
        CorrectOptionId = "a",
        MaxPoints = 1m,
        ProposedTimeLimitSeconds = 45,
    };

    [Fact]
    public void ValidDefinition_HasNoErrors()
    {
        Assert.Empty(QuestionValidator.ValidateForPublish(ValidSingle()));
    }

    [Fact]
    public void EmptyPrompt_IsRejected()
    {
        var def = new QuestionDefinition
        {
            Type = QuestionType.SingleChoice,
            Prompt = "  ",
            Options = [new("a", "глаз"), new("b", "ухо")],
            CorrectOptionId = "a",
            MaxPoints = 1m,
        };
        Assert.Contains(QuestionValidator.ValidateForPublish(def), e => e.Contains("формулировка", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ZeroMaxPoints_IsRejected()
    {
        var def = new QuestionDefinition
        {
            Type = QuestionType.SingleChoice, Prompt = "x",
            Options = [new("a", "1"), new("b", "2")], CorrectOptionId = "a", MaxPoints = 0m,
        };
        Assert.Contains(QuestionValidator.ValidateForPublish(def), e => e.Contains("MaxPoints"));
    }

    [Fact]
    public void MissingKey_IsRejected()
    {
        var def = new QuestionDefinition
        {
            Type = QuestionType.SingleChoice, Prompt = "x",
            Options = [new("a", "1"), new("b", "2")], CorrectOptionId = "zz", MaxPoints = 1m,
        };
        Assert.Contains(QuestionValidator.ValidateForPublish(def), e => e.Contains("ключ", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Matching_AnyPairCount_RubricSumsToMax()
    {
        // Равные веса: 10 на 3 пары; сумма критериев обязана давать максимум (ТЗ §7.2, §11).
        var def = new QuestionDefinition
        {
            Type = QuestionType.Matching, Prompt = "x", MaxPoints = 10m,
            MatchingPairs =
            [
                new("L0", "a", "R0", "x"), new("L1", "b", "R1", "y"), new("L2", "c", "R2", "z"),
            ],
        };
        Assert.Empty(QuestionValidator.ValidateForPublish(def));
        Assert.Equal(10m, def.BuildRubric().Sum(c => c.MaxPoints));

        // и полная сборка всех пар даёт максимум при оценивании
        var all = AnswerGrader.Grade(def, new QuestionAnswer(PairSelections: new Dictionary<string, string>
        {
            ["L0"] = "R0", ["L1"] = "R1", ["L2"] = "R2",
        }));
        Assert.Equal(10m, all.EarnedPoints);
    }

    [Fact]
    public void Matching_EqualDecimalWeights_Accepted()
    {
        // 3 балла на 4 пары = 0.75 за пару; сумма критериев равна максимуму — валидно.
        var def = new QuestionDefinition
        {
            Type = QuestionType.Matching, Prompt = "x", MaxPoints = 3m,
            MatchingPairs =
            [
                new("L0", "a", "R0", "x"), new("L1", "b", "R1", "y"),
                new("L2", "c", "R2", "z"), new("L3", "d", "R3", "w"),
            ],
        };
        Assert.Empty(QuestionValidator.ValidateForPublish(def));
    }

    [Fact]
    public void LetterTiles_MustMatchTarget()
    {
        var def = new QuestionDefinition
        {
            Type = QuestionType.LetterAssembly, Prompt = "x", MaxPoints = 1m,
            TargetSequence = "oculus",
            LetterTiles = [new("t1", 'o'), new("t2", 'c')],
        };
        Assert.Contains(QuestionValidator.ValidateForPublish(def), e => e.Contains("плиток"));
    }

    [Fact]
    public void Structured_SumMustEqualMax()
    {
        var def = new QuestionDefinition
        {
            Type = QuestionType.Structured, Prompt = "x", MaxPoints = 3m,
            StructuredFields = [new("f1", "Форма", "liquid", 1m), new("f2", "Род", "f", 1m)],
        };
        Assert.Contains(QuestionValidator.ValidateForPublish(def), e => e.Contains("равняться"));
    }

    [Fact]
    public void LongText_RequiresManualReview()
    {
        var def = new QuestionDefinition
        {
            Type = QuestionType.LongText, Prompt = "x", MaxPoints = 5m, RequiresManualReview = false,
        };
        Assert.Contains(QuestionValidator.ValidateForPublish(def), e => e.Contains("ручн"));
    }

    [Fact]
    public void TimeLimit_Bounds()
    {
        foreach (var seconds in new[] { 0, 3601 })
        {
            var def = new QuestionDefinition
            {
                Type = QuestionType.SingleChoice, Prompt = "x",
                Options = [new("a", "1"), new("b", "2")], CorrectOptionId = "a",
                MaxPoints = 1m, ProposedTimeLimitSeconds = seconds,
            };
            Assert.Contains(QuestionValidator.ValidateForPublish(def), e => e.Contains("Лимит времени"));
        }
    }
}

public class OptionShufflerTests
{
    private static readonly OptionDef[] Options =
    [
        new("a", "глаз"), new("b", "ухо"), new("c", "нос"), new("d", "рот"),
    ];

    [Fact]
    public void SameSeed_SameOrder_Deterministic()
    {
        var x = OptionShuffler.Shuffle(Options, "seed-1");
        var y = OptionShuffler.Shuffle(Options, "seed-1");
        Assert.Equal(y.Select(o => o.OptionId), x.Select(o => o.OptionId));
    }

    [Fact]
    public void Shuffle_PermutesWithoutLoss()
    {
        var shuffled = OptionShuffler.Shuffle(Options, "seed-2");
        Assert.Equal(Options.Length, shuffled.Count);
        Assert.True(Options.Select(o => o.OptionId).ToHashSet().SetEquals(shuffled.Select(o => o.OptionId)));
    }

    [Fact]
    public void GradingAfterShuffle_UsesStableIds_KeyIntact()
    {
        // Перемешивание не меняет ключ: проверка идёт по optionId (ТЗ §7.2).
        var def = new QuestionDefinition
        {
            Type = QuestionType.SingleChoice, Prompt = "x",
            Options = OptionShuffler.Shuffle(Options, "seed-3"),
            CorrectOptionId = "b", MaxPoints = 1m,
        };
        var outcome = AnswerGrader.Grade(def, new QuestionAnswer(SelectedOptionId: "b"));
        Assert.Equal(1m, outcome.EarnedPoints);
    }
}

public class BankPickerTests
{
    private static List<BankCandidate> Bank(int matching, int other)
    {
        var list = new List<BankCandidate>();
        for (var i = 0; i < matching; i++)
            list.Add(new BankCandidate(Guid.NewGuid(), QuestionType.SingleChoice, null, ["vocab"], 1m));
        for (var i = 0; i < other; i++)
            list.Add(new BankCandidate(Guid.NewGuid(), QuestionType.Matching, null, [], 2m));
        return list;
    }

    [Fact]
    public void InsufficientBank_WarnsExplicitly_NoSilentDuplicates()
    {
        // AC-14: недостаток банка — явное предупреждение, без молчаливых повторов.
        var result = BankPicker.Pick(Bank(matching: 2, other: 5),
            [new BlueprintLine(QuestionType.SingleChoice, null, "vocab", Count: 5, PointsEach: 1m)], "seed");

        Assert.Equal(2, result.Picked.Count);
        Assert.Equal(2, result.Picked.ToHashSet().Count); // дубликатов нет
        Assert.True(result.HasWarnings);
        Assert.Contains(result.Warnings, w => w.Contains("даёт 2"));
    }

    [Fact]
    public void SufficientBank_PicksWithoutDuplicates()
    {
        var result = BankPicker.Pick(Bank(matching: 6, other: 5),
            [new BlueprintLine(QuestionType.SingleChoice, null, "vocab", Count: 4, PointsEach: 1m)], "seed");

        Assert.Equal(4, result.Picked.Count);
        Assert.Equal(4, result.Picked.ToHashSet().Count);
        Assert.False(result.HasWarnings);
    }

    [Fact]
    public void Lines_DoNotReuseQuestionsAcrossLines()
    {
        var result = BankPicker.Pick(Bank(matching: 3, other: 0),
        [
            new BlueprintLine(QuestionType.SingleChoice, null, "vocab", Count: 2, PointsEach: 1m),
            new BlueprintLine(null, null, null, Count: 2, PointsEach: 1m),
        ], "seed");

        Assert.Equal(3, result.Picked.Count); // вторая линия добирает только остаток
        Assert.Equal(3, result.Picked.ToHashSet().Count);
        Assert.True(result.HasWarnings);
    }
}
