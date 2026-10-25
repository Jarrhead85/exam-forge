using ExamForge.Domain.Common;

namespace ExamForge.Domain.Questions;

/// <summary>Авторское задание; редактирование опубликованной версии создаёт новую редакцию (ТЗ §2, §7.1).</summary>
public sealed class Question
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid SubjectId { get; private set; }
    public Guid? TopicId { get; private set; }
    public Guid AuthorTeacherId { get; private set; }
    public QuestionStatus Status { get; private set; } = QuestionStatus.Draft;
    public int CurrentVersionNumber { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private Question() { }

    public static Question CreateDraft(Guid subjectId, Guid? topicId, Guid authorTeacherId, DateTimeOffset nowUtc)
        => new()
        {
            SubjectId = subjectId,
            TopicId = topicId,
            AuthorTeacherId = authorTeacherId,
            Status = QuestionStatus.Draft,
            CreatedAtUtc = nowUtc,
        };

    public void SendToReview(DateTimeOffset nowUtc)
    {
        if (Status != QuestionStatus.Draft)
            throw new DomainException($"В Review можно отправить только черновик (текущий: {Status}).");
        Status = QuestionStatus.Review;
    }

    /// <summary>Публикация возможна только после успешной валидации определения.</summary>
    public void Publish(DateTimeOffset nowUtc, QuestionDefinition definition)
    {
        var errors = QuestionValidator.ValidateForPublish(definition);
        if (errors.Count > 0)
            throw new DomainException("Опубликовать нельзя: " + string.Join(" ", errors));
        if (Status is not (QuestionStatus.Draft or QuestionStatus.Review))
            throw new DomainException($"Опубликовать можно только черновик/ревью (текущий: {Status}).");
        Status = QuestionStatus.Published;
    }

    public void Archive(DateTimeOffset nowUtc)
    {
        if (Status != QuestionStatus.Published)
            throw new DomainException($"В архив можно отправить только опубликованное (текущий: {Status}).");
        Status = QuestionStatus.Archived;
    }

    /// <summary>Новая редакция после публикации: вопрос возвращается в черновик (ТЗ §7.1, AC-15).</summary>
    public void ReturnToDraft()
    {
        if (Status is not (QuestionStatus.Published or QuestionStatus.Archived or QuestionStatus.Review))
            return;
        Status = QuestionStatus.Draft;
    }
}

/// <summary>
/// Неизменяемая опубликованная редакция. Изменение оригинала не меняет чужую копию
/// или уже утверждённую контрольную (ТЗ §3.4, AC-15).
/// </summary>
public sealed class QuestionVersion
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid QuestionId { get; private set; }
    public int VersionNumber { get; private set; }
    public QuestionType Type { get; private set; }
    public required string DefinitionJson { get; init; }
    public int SchemaVersion { get; private set; }
    public decimal MaxPoints { get; private set; }
    public int? ProposedTimeLimitSeconds { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? PublishedAtUtc { get; private set; }

    private QuestionVersion() { }

    public static QuestionVersion Create(
        Guid questionId, int versionNumber, QuestionType type,
        string definitionJson, int schemaVersion, decimal maxPoints,
        int? proposedTimeLimitSeconds, DateTimeOffset nowUtc)
    {
        if (versionNumber < 1)
            throw new DomainException("Номер версии начинается с 1.");
        Guard.Required(definitionJson, "Определение вопроса", 100_000);
        Guard.Positive(maxPoints, "Максимум баллов");
        return new()
        {
            QuestionId = questionId,
            VersionNumber = versionNumber,
            Type = type,
            DefinitionJson = definitionJson,
            SchemaVersion = schemaVersion,
            MaxPoints = maxPoints,
            ProposedTimeLimitSeconds = proposedTimeLimitSeconds,
            CreatedAtUtc = nowUtc,
        };
    }

    /// <summary>Однократная фиксация публикации; редакции после публикации нет.</summary>
    public void MarkPublished(DateTimeOffset nowUtc)
    {
        if (PublishedAtUtc is not null)
            throw new DomainException("Версия уже опубликована.");
        PublishedAtUtc = nowUtc;
    }
}
