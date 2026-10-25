using ExamForge.Domain.Common;

namespace ExamForge.Domain.Questions;

public sealed record OptionDef(string OptionId, string Text);

/// <summary>Допустимый ответ короткого текста (ТЗ §7.2): ключ + та же нормализация, что у вопроса.</summary>
public sealed record AcceptedAnswer(string Value);

/// <summary>Поле структурированного ответа: отдельный ключ и баллы (форма, род, падеж и т.п.).</summary>
public sealed record StructuredFieldDef(string FieldId, string Label, string CorrectValue, decimal Points);

/// <summary>Пара сопоставления: правильная пара LeftId→RightId; баллы за каждую верную пару (ТЗ §7.2).</summary>
public sealed record MatchingPairDef(string LeftId, string LeftText, string RightId, string RightText);

/// <summary>Плитка буквы: повторяющиеся буквы имеют отдельные стабильные tileId (AC-12).</summary>
public sealed record LetterTileDef(string TileId, char Letter);

/// <summary>Пропуск: свободный ввод или выбор; отдельный blankId и баллы (ТЗ §7.2).</summary>
public sealed record GapBlankDef(string BlankId, string[] AcceptedValues, decimal Points);

/// <summary>Ответ студента на вопрос: заполняются поля соответствующего типа.</summary>
public sealed record QuestionAnswer(
    string? SelectedOptionId = null,
    IReadOnlyCollection<string>? SelectedOptionIds = null,
    string? Text = null,
    IReadOnlyDictionary<string, string>? FieldValues = null,
    IReadOnlyDictionary<string, string>? PairSelections = null,
    IReadOnlyList<string>? TileSequence = null,
    IReadOnlyList<string>? Order = null,
    IReadOnlyDictionary<string, string>? BlankValues = null);

/// <summary>
/// Определение вопроса одной версии. Хранится versioned (JSONB со schemaVersion в БД);
/// Published редакция неизменяема (ТЗ §2, §7.1).
/// </summary>
public sealed class QuestionDefinition
{
    public required QuestionType Type { get; init; }
    public required string Prompt { get; init; }
    public string? Instruction { get; init; }
    public int SchemaVersion { get; init; } = 1;

    // SingleChoice / MultipleChoice
    public IReadOnlyList<OptionDef> Options { get; init; } = [];
    public string? CorrectOptionId { get; init; }
    public IReadOnlyCollection<string> CorrectOptionIds { get; init; } = [];
    public MultipleChoiceScoring MultipleChoiceScoring { get; init; } = MultipleChoiceScoring.AllOrNothing;

    // ShortText
    public IReadOnlyList<AcceptedAnswer> AcceptedAnswers { get; init; } = [];
    public ShortTextNormalization Normalization { get; init; } = new();
    /// <summary>Неизвестный ответ не считать автоматически неверным — отправить на ручную проверку (ТЗ §11).</summary>
    public bool SendUnknownShortAnswersToReview { get; init; }

    // Structured
    public IReadOnlyList<StructuredFieldDef> StructuredFields { get; init; } = [];

    // Matching
    public IReadOnlyList<MatchingPairDef> MatchingPairs { get; init; } = [];

    // LetterAssembly
    public IReadOnlyList<LetterTileDef> LetterTiles { get; init; } = [];
    public string? TargetSequence { get; init; }

    // Ordering
    /// <summary>Допустимые последовательности (первая — каноническая); при одной — exact.</summary>
    public IReadOnlyList<IReadOnlyList<string>> AcceptedOrders { get; init; } = [];
    public IReadOnlyList<OptionDef> OrderingItems { get; init; } = [];
    public OrderingScoring OrderingScoring { get; init; } = OrderingScoring.Exact;

    // GapFill
    public IReadOnlyList<GapBlankDef> GapBlanks { get; init; } = [];
    public string? GapTextWithPlaceholders { get; init; }

    // LongText
    public bool RequiresManualReview { get; init; }

    public required decimal MaxPoints { get; init; }
    public int? ProposedTimeLimitSeconds { get; init; }

    /// <summary>Критерии рубрики (ТЗ §11): сумма максимумов обязана равняться MaxPoints.</summary>
    public IReadOnlyList<RubricCriterion> BuildRubric() => RubricBuilder.Build(this);
}

public sealed record RubricCriterion(string CriterionId, string Label, decimal MaxPoints);

public static class RubricBuilder
{
    public static IReadOnlyList<RubricCriterion> Build(QuestionDefinition d) => d.Type switch
    {
        QuestionType.SingleChoice => [new("answer", "Ответ", d.MaxPoints)],
        QuestionType.MultipleChoice => d.MultipleChoiceScoring == MultipleChoiceScoring.AllOrNothing
            ? [new("answer", "Ответ", d.MaxPoints)]
            : [new("answer", "Ответ (за каждый верный вариант)", d.MaxPoints)],
        QuestionType.ShortText => [new("answer", "Ответ", d.MaxPoints)],
        QuestionType.Structured =>
            [.. d.StructuredFields.Select(f => new RubricCriterion(f.FieldId, f.Label, f.Points))],
        QuestionType.Matching =>
            [.. d.MatchingPairs.Select(p => new RubricCriterion($"pair:{p.LeftId}", $"Пара «{p.LeftText}»", d.MaxPoints / GuardPositive(d.MatchingPairs.Count)))],
        QuestionType.LetterAssembly => [new("answer", "Итоговая последовательность", d.MaxPoints)],
        QuestionType.Ordering => d.OrderingScoring == OrderingScoring.Exact
            ? [new("answer", "Порядок", d.MaxPoints)]
            : [new("answer", "Позиции", d.MaxPoints)],
        QuestionType.GapFill =>
            [.. d.GapBlanks.Select(b => new RubricCriterion(b.BlankId, $"Пропуск {b.BlankId}", b.Points))],
        QuestionType.LongText => [new("answer", "Развёрнутый ответ (ручная проверка)", d.MaxPoints)],
        _ => throw new DomainException($"Неизвестный тип вопроса {d.Type}."),
    };

    private static decimal GuardPositive(int count)
        => count > 0 ? count : throw new DomainException("Сопоставление требует хотя бы одну пару.");
}
