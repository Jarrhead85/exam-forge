namespace ExamForge.Domain.Questions;

/// <summary>Типы взаимодействия (ТЗ §7.2). Тип универсальный; предметный шаблон задаёт структуру.</summary>
public enum QuestionType
{
    SingleChoice = 1,
    MultipleChoice = 2,
    ShortText = 3,
    Structured = 4,
    Matching = 5,
    LetterAssembly = 6,
    Ordering = 7,
    GapFill = 8,
    LongText = 9,
}

/// <summary>Состояния задания (ТЗ §7.1): Draft → Review → Published → Archived.</summary>
public enum QuestionStatus
{
    Draft = 1,
    Review = 2,
    Published = 3,
    Archived = 4,
}

/// <summary>Стратегия MultipleChoice (ТЗ §7.2): явно задать all-or-nothing или частичные баллы; скрытых штрафов нет.</summary>
public enum MultipleChoiceScoring
{
    AllOrNothing = 1,
    /// <summary>Баллы за каждый корректно отмеченный верный вариант; ложные отметки баллов не дают и не вычитают.</summary>
    PartialPerCorrect = 2,
}

/// <summary>Стратегия Ordering: exact или per-position (ТЗ §7.2).</summary>
public enum OrderingScoring
{
    Exact = 1,
    PerPosition = 2,
}

/// <summary>Нормализация короткого текстового ответа — строго задаётся заданием (ТЗ §11); глобального «простить опечатки» нет.</summary>
public sealed record ShortTextNormalization
{
    public bool TrimWhitespace { get; init; } = true;
    public bool CollapseWhitespace { get; init; } = true;
    public bool IgnoreCase { get; init; } = true;
    public bool IgnoreDiacritics { get; init; } = true;
    public bool IgnorePunctuation { get; init; } = false;

    public static ShortTextNormalization Strict { get; } = new()
    {
        TrimWhitespace = true, CollapseWhitespace = true,
        IgnoreCase = false, IgnoreDiacritics = false, IgnorePunctuation = false,
    };
}
