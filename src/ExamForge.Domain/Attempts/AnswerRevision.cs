using ExamForge.Domain.Common;

namespace ExamForge.Domain.Attempts;

/// <summary>
/// Зафиксированная ревизия ответа, принятая сервером до deadline (ТЗ §10.3, §15).
/// Используется последняя принятая ревизия каждой сессии при оценивании.
/// </summary>
public sealed class AnswerRevision
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid QuestionSessionId { get; private set; }
    public Guid AttemptId { get; private set; }
    public int Revision { get; private set; }
    public string AnswerJson { get; private set; } = string.Empty;
    public DateTimeOffset SavedAtUtc { get; private set; }

    private AnswerRevision() { }

    public static AnswerRevision Create(
        Guid questionSessionId, Guid attemptId, int revision, string answerJson, DateTimeOffset nowUtc)
        => new()
        {
            QuestionSessionId = questionSessionId,
            AttemptId = attemptId,
            Revision = revision,
            AnswerJson = Guard.Required(answerJson, "Ответ", 20_000),
            SavedAtUtc = nowUtc,
        };
}
