using ExamForge.Domain.Common;
using ExamForge.Domain.Questions;
using Xunit;

namespace ExamForge.UnitTests.Questions;

public class AnswerGraderTests
{
    private static QuestionDefinition Single(decimal max = 1m) => new()
    {
        Type = QuestionType.SingleChoice,
        Prompt = "Выберите верный перевод «oculus»",
        Options = [new("a", "глаз"), new("b", "ухо"), new("c", "нос")],
        CorrectOptionId = "a",
        MaxPoints = max,
    };

    private static QuestionDefinition Matching(int pairs, decimal max) => new()
    {
        Type = QuestionType.Matching,
        Prompt = "Соотнесите латинские термины и переводы",
        MaxPoints = max,
        MatchingPairs = Enumerable.Range(0, pairs)
            .Select(i => new MatchingPairDef($"L{i}", $"термин {i}", $"R{i}", $"перевод {i}"))
            .ToList(),
    };

    [Fact]
    public void SingleChoice_Correct_GetsFullPoints()
    {
        var outcome = AnswerGrader.Grade(Single(), new QuestionAnswer(SelectedOptionId: "a"));
        Assert.Equal(1m, outcome.EarnedPoints);
        Assert.False(outcome.NeedsManualReview);
    }

    [Fact]
    public void SingleChoice_Wrong_GetsZero()
    {
        var outcome = AnswerGrader.Grade(Single(), new QuestionAnswer(SelectedOptionId: "b"));
        Assert.Equal(0m, outcome.EarnedPoints);
    }

    [Fact]
    public void MultipleChoice_AllOrNothing_RequiresExactSet()
    {
        var def = new QuestionDefinition
        {
            Type = QuestionType.MultipleChoice,
            Prompt = "Выберите все верные",
            Options = [new("a", "1"), new("b", "2"), new("c", "3")],
            CorrectOptionIds = ["a", "b"],
            MultipleChoiceScoring = MultipleChoiceScoring.AllOrNothing,
            MaxPoints = 2m,
        };
        var partial = AnswerGrader.Grade(def, new QuestionAnswer(SelectedOptionIds: ["a"]));
        var exact = AnswerGrader.Grade(def, new QuestionAnswer(SelectedOptionIds: ["b", "a"]));
        var withWrong = AnswerGrader.Grade(def, new QuestionAnswer(SelectedOptionIds: ["a", "b", "c"]));
        Assert.Equal(0m, partial.EarnedPoints);
        Assert.Equal(2m, exact.EarnedPoints);
        Assert.Equal(0m, withWrong.EarnedPoints);
    }

    [Fact]
    public void MultipleChoice_Partial_NoHiddenPenalties()
    {
        // Отмечен один верный из двух и один ложный: ложный не вычитает (ТЗ §7.2).
        var def = new QuestionDefinition
        {
            Type = QuestionType.MultipleChoice,
            Prompt = "Выберите все верные",
            Options = [new("a", "1"), new("b", "2"), new("c", "3")],
            CorrectOptionIds = ["a", "b"],
            MultipleChoiceScoring = MultipleChoiceScoring.PartialPerCorrect,
            MaxPoints = 2m,
        };
        var outcome = AnswerGrader.Grade(def, new QuestionAnswer(SelectedOptionIds: ["a", "c"]));
        Assert.Equal(1m, outcome.EarnedPoints);
    }

    [Fact]
    public void ShortText_NormalizationCaseWhitespaceDiacritics()
    {
        var def = new QuestionDefinition
        {
            Type = QuestionType.ShortText,
            Prompt = "Переведите термин",
            AcceptedAnswers = [new("Aqua destillāta")],
            Normalization = new() { IgnoreCase = true, IgnoreDiacritics = true, CollapseWhitespace = true },
            MaxPoints = 1m,
        };
        var outcome = AnswerGrader.Grade(def, new QuestionAnswer(Text: "  aqua   DESTILLATA "));
        Assert.Equal(1m, outcome.EarnedPoints);
    }

    [Fact]
    public void ShortText_StrictNormalization_RequiresExact()
    {
        var def = new QuestionDefinition
        {
            Type = QuestionType.ShortText,
            Prompt = "Словарная форма",
            AcceptedAnswers = [new("Aqua")],
            Normalization = ShortTextNormalization.Strict,
            MaxPoints = 1m,
        };
        Assert.Equal(0m, AnswerGrader.Grade(def, new QuestionAnswer(Text: "aqua")).EarnedPoints);
        Assert.Equal(1m, AnswerGrader.Grade(def, new QuestionAnswer(Text: "Aqua ")).EarnedPoints); // трим включён
    }

    [Fact]
    public void ShortText_UnknownAnswer_SentToReview_NotAutoWrong()
    {
        // ТЗ §11: неизвестный ответ нельзя автоматически считать семантически неверным.
        var def = new QuestionDefinition
        {
            Type = QuestionType.ShortText,
            Prompt = "Переведите",
            AcceptedAnswers = [new("глаз")],
            SendUnknownShortAnswersToReview = true,
            MaxPoints = 1m,
        };
        var outcome = AnswerGrader.Grade(def, new QuestionAnswer(Text: "орган зрения"));
        Assert.True(outcome.NeedsManualReview);
        Assert.Equal(0m, outcome.EarnedPoints);
    }

    [Fact]
    public void Matching_PartialCredit_PerPair()
    {
        // AC-02: 3 правильные пары из 4 по 1 баллу = 3/4.
        var def = Matching(4, 4m);
        var selections = new Dictionary<string, string>
        {
            ["L0"] = "R0", ["L1"] = "R1", ["L2"] = "R2", ["L3"] = "R0", // L3 неверна
        };
        var outcome = AnswerGrader.Grade(def, new QuestionAnswer(PairSelections: selections));
        Assert.Equal(3m, outcome.EarnedPoints);
        Assert.Equal(4m, outcome.MaxPoints);
        Assert.Equal(4, outcome.Criteria.Count);
    }

    [Fact]
    public void Matching_EmptyAnswer_AllZeros_NotNegative()
    {
        var outcome = AnswerGrader.Grade(Matching(3, 3m), new QuestionAnswer());
        Assert.Equal(0m, outcome.EarnedPoints);
    }

    [Fact]
    public void LetterAssembly_DuplicateLetters_DistinctTileIds()
    {
        // AC-12: две u в «oculus» — отдельные плитки; правильная итоговая строка принимает любую из плиток.
        var def = new QuestionDefinition
        {
            Type = QuestionType.LetterAssembly,
            Prompt = "Соберите термин «oculus»",
            TargetSequence = "oculus",
            LetterTiles =
            [
                new("t-o", 'o'), new("t-c", 'c'), new("t-u1", 'u'), new("t-l", 'l'),
                new("t-u2", 'u'), new("t-s", 's'),
            ],
            MaxPoints = 1m,
        };

        var viaFirstU = AnswerGrader.Grade(def, new QuestionAnswer(TileSequence: ["t-o", "t-c", "t-u1", "t-l", "t-u2", "t-s"]));
        var viaSecondU = AnswerGrader.Grade(def, new QuestionAnswer(TileSequence: ["t-o", "t-c", "t-u2", "t-l", "t-u1", "t-s"]));
        var wrongOrder = AnswerGrader.Grade(def, new QuestionAnswer(TileSequence: ["t-o", "t-c", "t-l", "t-u1", "t-u2", "t-s"]));

        Assert.Equal(1m, viaFirstU.EarnedPoints);
        Assert.Equal(1m, viaSecondU.EarnedPoints);
        Assert.Equal(0m, wrongOrder.EarnedPoints);
    }

    [Fact]
    public void Ordering_Exact_AcceptsAnyAllowedSequence()
    {
        var def = new QuestionDefinition
        {
            Type = QuestionType.Ordering,
            Prompt = "Расставьте слова",
            OrderingItems = [new("i1", "a"), new("i2", "b"), new("i3", "c")],
            AcceptedOrders = [["i1", "i2", "i3"], ["i2", "i1", "i3"]],
            OrderingScoring = OrderingScoring.Exact,
            MaxPoints = 3m,
        };
        Assert.Equal(3m, AnswerGrader.Grade(def, new QuestionAnswer(Order: ["i2", "i1", "i3"])).EarnedPoints);
        Assert.Equal(0m, AnswerGrader.Grade(def, new QuestionAnswer(Order: ["i3", "i2", "i1"])).EarnedPoints);
    }

    [Fact]
    public void Ordering_PerPosition_PartialCredit()
    {
        var def = new QuestionDefinition
        {
            Type = QuestionType.Ordering,
            Prompt = "Расставьте слова",
            OrderingItems = [new("i1", "a"), new("i2", "b"), new("i3", "c"), new("i4", "d")],
            AcceptedOrders = [["i1", "i2", "i3", "i4"]],
            OrderingScoring = OrderingScoring.PerPosition,
            MaxPoints = 4m,
        };
        // верны позиции 0 и 1 (i3/i4 переставлены)
        var outcome = AnswerGrader.Grade(def, new QuestionAnswer(Order: ["i1", "i2", "i4", "i3"]));
        Assert.Equal(2m, outcome.EarnedPoints);
    }

    [Fact]
    public void GapFill_PerBlankPoints()
    {
        var def = new QuestionDefinition
        {
            Type = QuestionType.GapFill,
            Prompt = "Заполните пропуски",
            GapTextWithPlaceholders = "Aqu__ __onsum",
            GapBlanks = [new("b1", ["a"], 1m), new("b2", ["c"], 1m)],
            MaxPoints = 2m,
        };
        var half = AnswerGrader.Grade(def, new QuestionAnswer(BlankValues: new Dictionary<string, string> { ["b1"] = "a", ["b2"] = "x" }));
        Assert.Equal(1m, half.EarnedPoints);
    }

    [Fact]
    public void Structured_PerFieldPoints()
    {
        var def = new QuestionDefinition
        {
            Type = QuestionType.Structured,
            Prompt = "Укажите форму и род",
            StructuredFields =
            [
                new StructuredFieldDef("form", "Форма", "liquid", 1m),
                new StructuredFieldDef("gender", "Род", "f", 1m),
            ],
            MaxPoints = 2m,
        };
        var outcome = AnswerGrader.Grade(def, new QuestionAnswer(
            FieldValues: new Dictionary<string, string> { ["form"] = "Liquid", ["gender"] = "m" }));
        Assert.Equal(1m, outcome.EarnedPoints);
    }

    [Fact]
    public void LongText_GoesToManualReview()
    {
        // AC-09: не проверено ≠ 0/финал.
        var def = new QuestionDefinition
        {
            Type = QuestionType.LongText,
            Prompt = "Объясните употребление конструкции",
            RequiresManualReview = true,
            MaxPoints = 5m,
        };
        var outcome = AnswerGrader.Grade(def, new QuestionAnswer(Text: "Развёрнутый ответ студента"));
        Assert.True(outcome.NeedsManualReview);
        Assert.Equal(0m, outcome.EarnedPoints);
        Assert.Equal(5m, outcome.MaxPoints);
    }

    [Fact]
    public void Grading_ZeroMax_Throws()
    {
        var def = Single(max: 0m);
        Assert.Throws<DomainException>(() => AnswerGrader.Grade(def, null));
    }
}
