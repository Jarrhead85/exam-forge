namespace ExamForge.Domain.Attempts;

/// <summary>Состояния попытки (ТЗ §10.1). Cancelled/Expired — по согласованной политике (P4).</summary>
public enum AttemptState
{
    Created = 1,
    InProgress = 2,
    Submitted = 3,
    PendingReview = 4,
    Finalized = 5,
    Cancelled = 6,
    Expired = 7,
}

/// <summary>Состояния серверной сессии вопроса (ТЗ §10.1: NotOpened → Active → Locked(Submitted/TimedOut)).</summary>
public enum QuestionSessionState
{
    NotOpened = 1,
    Active = 2,
    LockedSubmitted = 3,
    LockedTimedOut = 4,
}
