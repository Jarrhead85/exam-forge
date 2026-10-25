using ExamForge.Domain.Common;
using ExamForge.Domain.Questions;

namespace ExamForge.Domain.Testing;

/// <summary>
/// Зафиксированный набор и порядок заданий и вариантов ответа (ТЗ §2, §8).
/// Хранит seed и фактический порядок; печать и оценивание используют именно вариант.
/// </summary>
public sealed class Variant
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid TestVersionId { get; private set; }
    /// <summary>Номер варианта: 1..N; минимум два для первой приёмки печати (ТЗ §8).</summary>
    public int VariantNumber { get; private set; }
    public string Seed { get; private set; } = string.Empty;

    /// <summary>Фактический порядок версий заданий в этом варианте.</summary>
    public IReadOnlyList<Guid> QuestionOrder { get; private set; } = [];

    /// <summary>
    /// Фактический порядок перемешиваемых списков: questionVersionId → optionIds/tileIds.
    /// Применимо к вариантам выбора, правой колонке сопоставления и плиткам букв.
    /// </summary>
    public IReadOnlyDictionary<string, List<string>> OptionOrders { get; private set; } = new Dictionary<string, List<string>>();

    public DateTimeOffset CreatedAtUtc { get; private set; }

    private Variant() { }

    public static Variant Create(
        Guid testVersionId, int variantNumber, string seed,
        IReadOnlyList<Guid> questionOrder,
        IReadOnlyDictionary<string, List<string>> optionOrders,
        DateTimeOffset nowUtc)
    {
        if (variantNumber < 1)
            throw new DomainException("Номер варианта начинается с 1.");
        Guard.Required(seed, "Seed варианта", 200);
        if (questionOrder.Count == 0)
            throw new DomainException("Вариант требует хотя бы одно задание.");
        if (questionOrder.Distinct().Count() != questionOrder.Count)
            throw new DomainException("Дубликаты заданий внутри варианта не допускаются (ТЗ §8).");
        return new()
        {
            TestVersionId = testVersionId,
            VariantNumber = variantNumber,
            Seed = seed,
            QuestionOrder = questionOrder,
            OptionOrders = optionOrders,
            CreatedAtUtc = nowUtc,
        };
    }

    /// <summary>Порядок отображения списка по варианту; без записи — исходный порядок определения (ТЗ §8).</summary>
    public IReadOnlyList<string> OrderFor(Guid questionVersionId, IReadOnlyList<string> source)
        => OptionOrders.TryGetValue(questionVersionId.ToString(), out var order)
            ? order.Where(source.Contains).ToList()
            : source;

    /// <summary>Замена одного вопроса без пересборки варианта (ТЗ §8).</summary>
    public void ReplaceQuestion(Guid oldQuestionVersionId, Guid newQuestionVersionId, List<string>? newOptionOrder)
    {
        var index = QuestionOrder.ToList().IndexOf(oldQuestionVersionId);
        if (index < 0)
            throw new DomainException("Заменяемый вопрос не входит в вариант.");
        var order = QuestionOrder.ToList();
        order[index] = newQuestionVersionId;
        QuestionOrder = order;

        var mutable = OptionOrders.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        mutable.Remove(oldQuestionVersionId.ToString());
        if (newOptionOrder is { Count: > 0 })
            mutable[newQuestionVersionId.ToString()] = newOptionOrder;
        OptionOrders = mutable;
    }
}

/// <summary>
/// Генерация вариантов: перемешивание по stable ID с детерминированным seed (ТЗ §8);
/// ключ проверки не меняется — проверяются идентификаторы, а не позиции.
/// </summary>
public static class VariantGenerator
{
    /// <summary>
    /// Создать count вариантов версии теста, нумерация с firstVariantNumber.
    /// Порядок вопросов перемешивается только по явному флагу.
    /// </summary>
    public static List<Variant> Generate(
        TestVersion testVersion,
        IReadOnlyDictionary<Guid, QuestionDefinition> definitions,
        int count,
        string baseSeed,
        bool shuffleQuestions,
        DateTimeOffset nowUtc,
        int firstVariantNumber = 1)
    {
        if (count is < 1 or > 20)
            throw new DomainException("Число вариантов должно быть в диапазоне 1–20.");
        if (firstVariantNumber < 1)
            throw new DomainException("Номер первого варианта начинается с 1.");

        var result = new List<Variant>(count);
        for (var number = firstVariantNumber; number < firstVariantNumber + count; number++)
        {
            var seed = $"{baseSeed}:{testVersion.Id}:{number}";
            var questionOrder = shuffleQuestions
                ? OptionShuffler.Shuffle(testVersion.QuestionVersionIds, seed + ":questions").ToList()
                : testVersion.QuestionVersionIds.ToList();

            var optionOrders = new Dictionary<string, List<string>>();
            foreach (var questionVersionId in questionOrder)
            {
                if (!definitions.TryGetValue(questionVersionId, out var definition))
                    throw new DomainException("Версия теста ссылается на неизвестное задание.");
                var shuffleable = ShuffleableIds(definition);
                if (shuffleable.Count > 1)
                    optionOrders[questionVersionId.ToString()] = OptionShuffler.Shuffle(shuffleable, seed + ":" + questionVersionId).ToList();
            }

            result.Add(Variant.Create(testVersion.Id, number, seed, questionOrder, optionOrders, nowUtc));
        }
        return result;
    }

    /// <summary>Список, который допустимо перемешивать: варианты ответа, правая колонка сопоставления, плитки букв.</summary>
    private static List<string> ShuffleableIds(QuestionDefinition d) => d.Type switch
    {
        QuestionType.SingleChoice or QuestionType.MultipleChoice
            => [.. d.Options.Select(o => o.OptionId)],
        QuestionType.Matching => [.. d.MatchingPairs.Select(p => p.RightId)],
        QuestionType.LetterAssembly => [.. d.LetterTiles.Select(t => t.TileId)],
        _ => [],
    };
}
