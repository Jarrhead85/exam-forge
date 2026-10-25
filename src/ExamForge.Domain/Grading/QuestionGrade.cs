using ExamForge.Domain.Common;

namespace ExamForge.Domain.Grading;

/// <summary>Состояние проверки критерия (ТЗ §11): не проверено ≠ 0.</summary>
public enum VerificationState
{
    AutoGraded = 1,
    PendingManual = 2,
    ManuallyGraded = 3,
}

/// <summary>
/// Оценка критерия одного вопроса попытки (ТЗ §11, §15): criterionId, label, maxPoints,
/// earnedPoints, rule-reason, состояние проверки. Подтверждённые авто-баллы фиксируются
/// при сдаче; ручные критерии ждут преподавателя.
/// </summary>
public sealed class QuestionGrade
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid AttemptId { get; private set; }
    public Guid QuestionSessionId { get; private set; }
    public Guid QuestionVersionId { get; private set; }

    public string CriterionId { get; private set; } = string.Empty;
    public string Label { get; private set; } = string.Empty;
    public decimal MaxPoints { get; private set; }
    public decimal EarnedPoints { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public VerificationState State { get; private set; }

    public string? TeacherComment { get; private set; }
    public Guid? GradedByTeacherId { get; private set; }
    public DateTimeOffset? GradedAtUtc { get; private set; }

    private QuestionGrade() { }

    public static QuestionGrade Create(
        Guid attemptId, Guid questionSessionId, Guid questionVersionId,
        string criterionId, string label, decimal maxPoints, decimal earnedPoints,
        string reason, VerificationState state, DateTimeOffset nowUtc)
        => new()
        {
            AttemptId = attemptId,
            QuestionSessionId = questionSessionId,
            QuestionVersionId = questionVersionId,
            CriterionId = Guard.Required(criterionId, "Идентификатор критерия", 100),
            Label = Guard.Required(label, "Название критерия", 300),
            MaxPoints = maxPoints,
            EarnedPoints = earnedPoints,
            Reason = Guard.Required(reason, "Основание", 1000),
            State = state,
        };

    /// <summary>Ручная оценка критерия преподавателем; причина обязательна (ТЗ §11).</summary>
    public void GradeManually(decimal earnedPoints, string reason, Guid teacherId, DateTimeOffset nowUtc)
    {
        Guard.Required(reason, "Основание ручной оценки", 1000);
        if (earnedPoints < 0m || earnedPoints > MaxPoints)
            throw new DomainException($"Баллы критерия должны быть в [0; {MaxPoints}].");
        if (State == VerificationState.AutoGraded)
            throw new DomainException("Автооцененный критерий меняется через переоценку с причиной, а не через проверку.");
        EarnedPoints = earnedPoints;
        Reason = reason;
        State = VerificationState.ManuallyGraded;
        GradedByTeacherId = teacherId;
        GradedAtUtc = nowUtc;
    }

    /// <summary>Переоценка подтверждённого критерия: меняет баллы, фиксируя до/после снаружи (GradeRevision).</summary>
    public void Override(decimal earnedPoints, string reason, Guid teacherId, DateTimeOffset nowUtc)
    {
        Guard.Required(reason, "Причина переоценки", 1000);
        if (earnedPoints < 0m || earnedPoints > MaxPoints)
            throw new DomainException($"Баллы критерия должны быть в [0; {MaxPoints}].");
        EarnedPoints = earnedPoints;
        Reason = reason;
        State = VerificationState.ManuallyGraded;
        GradedByTeacherId = teacherId;
        GradedAtUtc = nowUtc;
    }

    public void SetTeacherComment(string? comment)
        => TeacherComment = string.IsNullOrWhiteSpace(comment) ? null : Guard.Required(comment, "Комментарий", 1000);
}

/// <summary>
/// История изменения оценки (ТЗ §11): ручной override требует причины и истории до/после.
/// Редактирование оценки не запускает пятилетний срок заново (ТЗ §20).
/// </summary>
public sealed class GradeRevision
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid AttemptId { get; private set; }
    public Guid QuestionSessionId { get; private set; }
    public string CriterionId { get; private set; } = string.Empty;
    public decimal OldEarnedPoints { get; private set; }
    public decimal NewEarnedPoints { get; private set; }
    public decimal OldAttemptEarned { get; private set; }
    public decimal NewAttemptEarned { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public Guid TeacherId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private GradeRevision() { }

    public static GradeRevision Create(
        Guid attemptId, Guid questionSessionId, string criterionId,
        decimal oldEarned, decimal newEarned,
        decimal oldAttemptEarned, decimal newAttemptEarned,
        string reason, Guid teacherId, DateTimeOffset nowUtc)
        => new()
        {
            AttemptId = attemptId,
            QuestionSessionId = questionSessionId,
            CriterionId = Guard.Required(criterionId, "Критерий", 100),
            OldEarnedPoints = oldEarned,
            NewEarnedPoints = newEarned,
            OldAttemptEarned = oldAttemptEarned,
            NewAttemptEarned = newAttemptEarned,
            Reason = Guard.Required(reason, "Причина", 1000),
            TeacherId = teacherId,
            CreatedAtUtc = nowUtc,
        };
}
