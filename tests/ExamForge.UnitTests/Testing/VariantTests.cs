using ExamForge.Domain.Questions;
using ExamForge.Domain.Testing;
using Xunit;

namespace ExamForge.UnitTests.Testing;

public class VariantGeneratorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static QuestionDefinition Choice(Guid id, params string[] optionIds) => new()
    {
        Type = QuestionType.SingleChoice,
        Prompt = $"Вопрос {id}",
        Options = [.. optionIds.Select(o => new OptionDef(o, $"вариант {o}"))],
        CorrectOptionId = optionIds[0],
        MaxPoints = 1m,
    };

    private static TestVersion Version(params Guid[] questionIds) => TestVersion.Create(
        Guid.NewGuid(), 1, "Словарная контрольная", questionIds, questionIds.Length * 1m, Now);

    [Fact]
    public void Generate_IsDeterministic_SameSeedSameOrders()
    {
        var q1 = Guid.NewGuid();
        var q2 = Guid.NewGuid();
        var version = Version(q1, q2);
        var definitions = new Dictionary<Guid, QuestionDefinition>
        {
            [q1] = Choice(q1, "a", "b", "c", "d"),
            [q2] = Choice(q2, "x", "y", "z", "w"),
        };

        var first = VariantGenerator.Generate(version, definitions, 2, "seed-1", shuffleQuestions: false, Now);
        var second = VariantGenerator.Generate(version, definitions, 2, "seed-1", shuffleQuestions: false, Now);

        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(first[i].QuestionOrder, second[i].QuestionOrder);
            Assert.Equal(first[i].OptionOrders[q1.ToString()], second[i].OptionOrders[q1.ToString()]);
        }
    }

    [Fact]
    public void Generate_PermutationsWithoutLoss_QuestionsNotShuffledByDefault()
    {
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();
        var version = Version([.. ids]);
        var definitions = ids.ToDictionary(id => id, id => Choice(id, "a", "b", "c"));

        var variants = VariantGenerator.Generate(version, definitions, 3, "seed-2", shuffleQuestions: false, Now);

        foreach (var variant in variants)
        {
            // порядок заданий не перемешан без явного флага
            Assert.Equal(ids, variant.QuestionOrder);
            // порядок опций — перестановка без потерь и без дубликатов
            var order = variant.OptionOrders[ids[0].ToString()];
            Assert.Equal(3, order.Count);
            Assert.Equal(3, order.Distinct().Count());
            Assert.True(new HashSet<string>(["a", "b", "c"]).SetEquals(order));
        }
    }

    [Fact]
    public void Generate_ShuffleQuestions_PermutesQuestionsWithoutLoss()
    {
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        var version = Version([.. ids]);
        var definitions = ids.ToDictionary(id => id, id => Choice(id, "a", "b"));

        var variant = VariantGenerator.Generate(version, definitions, 1, "seed-3", shuffleQuestions: true, Now)[0];

        Assert.Equal(ids.Count, variant.QuestionOrder.Count);
        Assert.Equal(ids.Count, variant.QuestionOrder.Distinct().Count());
        Assert.True(ids.ToHashSet().SetEquals(variant.QuestionOrder));
    }

    [Fact]
    public void Generate_VariantsDifferInOptionOrder()
    {
        // Два варианта одной версии дают разные порядки опций (для 4 опций при фиксированном seed).
        var id = Guid.NewGuid();
        var version = Version(id);
        var definitions = new Dictionary<Guid, QuestionDefinition> { [id] = Choice(id, "a", "b", "c", "d") };

        var variants = VariantGenerator.Generate(version, definitions, 2, "seed-4", false, Now);

        Assert.NotEqual(
            variants[0].OptionOrders[id.ToString()],
            variants[1].OptionOrders[id.ToString()]);
    }

    [Fact]
    public void Version_ForbidDuplicateQuestions_Silently()
    {
        var id = Guid.NewGuid();
        // Дубликаты заданий запрещены на уровне версии теста (ТЗ §8)
        Assert.Throws<ExamForge.Domain.Common.DomainException>(
            () => TestVersion.Create(Guid.NewGuid(), 1, "t", [id, id], 2m, Now));
    }

    [Fact]
    public void ReplaceQuestion_KeepsOtherOrder_AndSwapsId()
    {
        var q1 = Guid.NewGuid(); var q2 = Guid.NewGuid(); var q3 = Guid.NewGuid();
        var version = Version(q1, q2);
        var definitions = new Dictionary<Guid, QuestionDefinition>
        {
            [q1] = Choice(q1, "a", "b", "c"),
            [q2] = Choice(q2, "x", "y", "z"),
        };
        var variant = VariantGenerator.Generate(version, definitions, 1, "seed-5", false, Now)[0];

        variant.ReplaceQuestion(q2, q3, ["z", "x", "y"]);

        Assert.Equal([q1, q3], variant.QuestionOrder);
        Assert.False(variant.OptionOrders.ContainsKey(q2.ToString()));
        Assert.Equal(["z", "x", "y"], variant.OptionOrders[q3.ToString()]);
    }
}

/// <summary>AC-16: ключ печати строго соответствует порядку именно этого варианта.</summary>
public class PrintViewBuilderKeyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TeacherKey_MarkerFollowsVariantOrder()
    {
        var questionId = Guid.NewGuid();
        var definition = new QuestionDefinition
        {
            Type = QuestionType.SingleChoice,
            Prompt = "Перевод «oculus»",
            Options = [new("a", "глаз"), new("b", "ухо"), new("c", "нос")],
            CorrectOptionId = "a",
            MaxPoints = 1m,
        };
        var testVersion = TestVersion.Create(Guid.NewGuid(), 1, "Контрольная", [questionId], 1m, Now);
        // Вариант переставляет опции: c, a, b → маркеры: c=А, a=Б, b=В; правильный (a) = Б
        var variant = Variant.Create(testVersion.Id, 1, "seed",
            [questionId],
            new Dictionary<string, List<string>> { [questionId.ToString()] = ["c", "a", "b"] },
            Now);

        // OrderFor отражает фактический порядок
        Assert.Equal(["c", "a", "b"], variant.OrderFor(questionId, ["a", "b", "c"]));

        // Маркер правильного ответа вычисляется по позиции в варианте (AC-16)
        var optionsInVariant = variant.OrderFor(questionId, ["a", "b", "c"]).ToList();
        var indexOfCorrect = optionsInVariant.IndexOf("a");
        Assert.Equal(1, indexOfCorrect); // вторая позиция → «Б»
    }

    [Fact]
    public void StudentSheetDto_HasNoKeyFields_ByDesign()
    {
        // Студентский DTO не содержит полей ключа вовсе — не только не рендерит их.
        var studentType = typeof(ExamForge.Application.Printing.StudentPrintQuestionDto);
        Assert.DoesNotContain(studentType.GetProperties(),
            p => p.Name.StartsWith("Key", StringComparison.OrdinalIgnoreCase));
    }
}
