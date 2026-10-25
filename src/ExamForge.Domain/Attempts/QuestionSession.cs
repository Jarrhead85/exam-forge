using ExamForge.Domain.Common;

namespace ExamForge.Domain.Attempts;

public enum OpenQuestionResult
{
    Opened = 1,
    AlreadyActive = 2,
    AlreadyLocked = 3,
}

public enum AnswerDecision
{
    Accepted = 1,
    RejectedLate = 2,
    RejectedNotActive = 3,
}

public enum LockOutcome
{
    LockedSubmitted = 1,
    LockedTimedOut = 2,
    AlreadyLocked = 3,
}

/// <summary>
/// Серверное состояние текущего вопроса и его срока (ТЗ §10).
/// Deadline фиксируется один раз при открытии; повторные запросы не продлевают (AC-03).
/// Граница: ответ принимается при now &lt; deadline; now ≥ deadline — опоздание (ТЗ §10.1).
/// </summary>
public sealed class QuestionSession
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid AttemptId { get; private set; }
    public Guid QuestionVersionId { get; private set; }

    /// <summary>Порядок вопроса в зафиксированном варианте. NoBacktracking управляет доступом по нему.</summary>
    public int OrderIndex { get; private set; }

    public QuestionSessionState State { get; private set; } = QuestionSessionState.NotOpened;
    public DateTimeOffset? StartedAtUtc { get; private set; }
    public DateTimeOffset? DeadlineUtc { get; private set; }

    /// <summary>Монотонная ревизия принятого ответа; optimistic concurrency на клиенте по ней же.</summary>
    public int AnswerRevision { get; private set; }

    private QuestionSession() { }

    public static QuestionSession Create(Guid attemptId, Guid questionVersionId, int orderIndex)
        => new()
        {
            AttemptId = attemptId,
            QuestionVersionId = questionVersionId,
            OrderIndex = orderIndex,
            State = QuestionSessionState.NotOpened,
        };

    /// <summary>
    /// Атомарное открытие вопроса: фиксирует startedAt и deadline ровно один раз.
    /// Deadline = min(лимит вопроса, дедлайн попытки, закрытие назначения), если заданы несколько (ТЗ §9).
    /// </summary>
    public OpenQuestionResult Open(
        DateTimeOffset nowUtc,
        TimeSpan? perQuestionLimit,
        DateTimeOffset? attemptDeadlineUtc,
        DateTimeOffset? assignmentClosesAtUtc)
    {
        switch (State)
        {
            case QuestionSessionState.NotOpened:
                var candidates = new List<DateTimeOffset>(3);
                if (perQuestionLimit is { } limit)
                {
                    Guard.Positive((decimal)limit.TotalSeconds, "Лимит времени вопроса");
                    candidates.Add(nowUtc.Add(limit));
                }
                if (attemptDeadlineUtc is { } attemptDeadline)
                    candidates.Add(attemptDeadline);
                if (assignmentClosesAtUtc is { } closesAt)
                    candidates.Add(closesAt);
                if (candidates.Count == 0)
                    throw new DomainException("Должен быть задан хотя бы один лимит времени (вопрос/попытка/назначение).");

                StartedAtUtc = nowUtc;
                DeadlineUtc = candidates.Min();
                State = QuestionSessionState.Active;
                return OpenQuestionResult.Opened;

            case QuestionSessionState.Active:
                return OpenQuestionResult.AlreadyActive; // refresh/повторный запрос — тот же deadline (AC-03)

            default:
                return OpenQuestionResult.AlreadyLocked;
        }
    }

    /// <summary>Ленивая проверка срока сервером при каждом обращении (ADR-003 п.4). True — если перешёл в истёкшее именно сейчас.</summary>
    public bool ExpireIfDue(DateTimeOffset nowUtc)
    {
        if (State == QuestionSessionState.Active && nowUtc >= DeadlineUtc)
        {
            State = QuestionSessionState.LockedTimedOut;
            return true;
        }
        return false;
    }

    public bool CanAcceptAnswer(DateTimeOffset nowUtc)
        => State == QuestionSessionState.Active && nowUtc < DeadlineUtc!.Value;

    /// <summary>
    /// Зафиксировать ревизию ответа. Только при активной сессии и строго до deadline (AC-04/05).
    /// Принятая до deadline ревизия не затирается поздними отказами.
    /// </summary>
    public AnswerDecision SaveAnswer(DateTimeOffset nowUtc)
    {
        if (State != QuestionSessionState.Active)
            return AnswerDecision.RejectedNotActive; // закрытый вопрос нельзя изменить (AC-06)
        if (nowUtc >= DeadlineUtc!.Value)
        {
            State = QuestionSessionState.LockedTimedOut;
            return AnswerDecision.RejectedLate;
        }
        AnswerRevision++;
        return AnswerDecision.Accepted;
    }

    /// <summary>
    /// Закрыть вопрос: до deadline — как отправленный; на/после deadline — как истёкший (ТЗ §10.2).
    /// Идемпотентно для уже закрытого.
    /// </summary>
    public LockOutcome Lock(DateTimeOffset nowUtc)
    {
        var expiredJustNow = ExpireIfDue(nowUtc);
        switch (State)
        {
            case QuestionSessionState.Active:
                if (nowUtc < DeadlineUtc!.Value)
                {
                    State = QuestionSessionState.LockedSubmitted;
                    return LockOutcome.LockedSubmitted;
                }
                State = QuestionSessionState.LockedTimedOut;
                return LockOutcome.LockedTimedOut;
            case QuestionSessionState.LockedSubmitted:
                return LockOutcome.AlreadyLocked;
            case QuestionSessionState.LockedTimedOut:
                return expiredJustNow ? LockOutcome.LockedTimedOut : LockOutcome.AlreadyLocked;
            default:
                throw new DomainException("Неоткрытый вопрос нельзя закрыть: сначала Open.");
        }
    }
}
