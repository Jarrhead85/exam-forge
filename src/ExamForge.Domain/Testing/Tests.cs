using ExamForge.Domain.Common;

namespace ExamForge.Domain.Testing;

/// <summary>Авторский тест/контрольная. Содержимое публикуется версиями (ТЗ §2).</summary>
public sealed class Test
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid SubjectId { get; private set; }
    public Guid AuthorTeacherId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private Test() { }

    public static Test Create(Guid subjectId, Guid authorTeacherId, string title, DateTimeOffset nowUtc)
        => new()
        {
            SubjectId = subjectId,
            AuthorTeacherId = authorTeacherId,
            Title = Guard.Required(title, "Название теста", 300),
            CreatedAtUtc = nowUtc,
        };

    /// <summary>Переименование теста: заголовок карточки; версии и назначения не меняются.</summary>
    public void Rename(string title) => Title = Guard.Required(title, "Название теста", 300);
}

/// <summary>
/// Опубликованное содержание теста: зафиксированный набор версий заданий и критерии (ТЗ §2, §8).
/// Назначение связывается с конкретной TestVersion; редактирование оригинала её не меняет.
/// </summary>
public sealed class TestVersion
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid TestId { get; private set; }
    public int VersionNumber { get; private set; }
    public string Title { get; private set; } = string.Empty;

    /// <summary>Порядок версий заданий в базовой компоновке; вариант может фиксировать свой порядок/перемешивание.</summary>
    public IReadOnlyList<Guid> QuestionVersionIds { get; private set; } = [];

    public decimal MaxPoints { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? PublishedAtUtc { get; private set; }

    private TestVersion() { }

    public static TestVersion Create(
        Guid testId, int versionNumber, string title,
        IReadOnlyList<Guid> questionVersionIds, decimal maxPoints, DateTimeOffset nowUtc)
    {
        if (versionNumber < 1)
            throw new DomainException("Номер версии начинается с 1.");
        if (questionVersionIds.Count == 0)
            throw new DomainException("Версия теста требует хотя бы одно задание.");
        Guard.Positive(maxPoints, "Максимум баллов теста");
        var distinctCount = questionVersionIds.Distinct().Count();
        if (distinctCount != questionVersionIds.Count)
            throw new DomainException("Дубликаты заданий внутри варианта не допускаются молча (ТЗ §8).");
        return new()
        {
            TestId = testId,
            VersionNumber = versionNumber,
            Title = Guard.Required(title, "Название версии", 300),
            QuestionVersionIds = questionVersionIds,
            MaxPoints = maxPoints,
            CreatedAtUtc = nowUtc,
        };
    }

    /// <summary>Однократная публикация; изменённое содержание — новая версия.</summary>
    public void Publish(DateTimeOffset nowUtc)
    {
        if (PublishedAtUtc is not null)
            throw new DomainException("Версия теста уже опубликована.");
        PublishedAtUtc = nowUtc;
    }
}
