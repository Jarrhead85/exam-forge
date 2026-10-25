using ExamForge.Domain.Common;
using ExamForge.Domain.Grading;

namespace ExamForge.Domain.Attempts;

/// <summary>
/// Попытка студента в назначении. Переходы состояний — только через методы;
/// сервер владеет состоянием, время передаётся явно в UTC (ТЗ §10, ADR-003).
/// </summary>
public sealed class Attempt
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid AssignmentId { get; private set; }
    public Guid? StudentProfileId { get; private set; }

    /// <summary>Зафиксированный вариант, по которому собраны вопросы попытки (ТЗ §8). Null — базовый порядок версии.</summary>
    public Guid? VariantId { get; private set; }

    /// <summary>Снимок ФИО/группы на момент попытки: смена группы не переписывает историю (ТЗ §4).</summary>
    public string StudentNameSnapshot { get; private set; } = string.Empty;
    public string? StudentGroupSnapshot { get; private set; }

    /// <summary>1–2 — обычные попытки; 3+ только при ExtraAttemptGrant (ТЗ §10.4).</summary>
    public int AttemptNumber { get; private set; }
    public Guid? ExtraGrantId { get; private set; }

    public AttemptState State { get; private set; } = AttemptState.Created;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? StartedAtUtc { get; private set; }
    /// <summary>Общий лимит попытки, если задан: StartedAt + лимит; deadline вопроса не может его превышать (ТЗ §9).</summary>
    public DateTimeOffset? DeadlineUtc { get; private set; }
    public DateTimeOffset? SubmittedAtUtc { get; private set; }

    /// <summary>Дата завершения — точка отсчёта пятилетнего хранения (ТЗ §20).</summary>
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    /// <summary>Сохранённые точные баллы итога (exact rational, ТЗ §11); оценка вычисляется.</summary>
    public decimal? ScoreEarnedPoints { get; private set; }
    public decimal? ScoreMaxPoints { get; private set; }

    public GradeResult? Score =>
        ScoreEarnedPoints is { } earned && ScoreMaxPoints is { } max ? GradeCalculator.Compute(earned, max) : null;

    /// <summary>Есть ли ответы, требующие ручной проверки (LongText и т.п.). Не проверено ≠ 0 (ТЗ §11).</summary>
    public bool HasPendingManualReview { get; private set; }

    public bool IsEligibleForBest => State == AttemptState.Finalized && Score is not null;

    private Attempt() { }

    public static Attempt Create(
        Guid assignmentId,
        int attemptNumber,
        DateTimeOffset nowUtc,
        Guid? studentProfileId,
        string studentNameSnapshot,
        string? studentGroupSnapshot,
        Guid? extraGrantId = null,
        Guid? variantId = null)
    {
        Guard.Required(studentNameSnapshot, "ФИО студента", 300);
        if (attemptNumber < 1)
            throw new DomainException("Номер попытки начинается с 1.");
        if (attemptNumber > 2 && extraGrantId is null)
            throw new DomainException("Попытка свыше второй требует ExtraAttemptGrant (ТЗ §10.4).");

        return new Attempt
        {
            AssignmentId = assignmentId,
            StudentProfileId = studentProfileId,
            StudentNameSnapshot = studentNameSnapshot,
            StudentGroupSnapshot = string.IsNullOrWhiteSpace(studentGroupSnapshot) ? null : studentGroupSnapshot.Trim(),
            AttemptNumber = attemptNumber,
            ExtraGrantId = extraGrantId,
            VariantId = variantId,
            State = AttemptState.Created,
            CreatedAtUtc = nowUtc,
        };
    }

    /// <summary>Created → InProgress. Идемпотентно: повторный start не создаёт новую попытку и не сбрасывает время (AC-07).</summary>
    public void Start(DateTimeOffset nowUtc, TimeSpan? totalTimeLimit = null)
    {
        switch (State)
        {
            case AttemptState.Created:
                StartedAtUtc = nowUtc;
                DeadlineUtc = totalTimeLimit is { } limit ? nowUtc.Add(limit) : null;
                State = AttemptState.InProgress;
                break;
            case AttemptState.InProgress:
                return; // идемпотентность
            default:
                throw new DomainException($"Попытку в состоянии {State} нельзя запустить заново.");
        }
    }

    /// <summary>InProgress → Submitted. Идемпотентно (AC-07).</summary>
    public void Submit(DateTimeOffset nowUtc, bool hasPendingManualReview)
    {
        switch (State)
        {
            case AttemptState.InProgress:
                SubmittedAtUtc = nowUtc;
                HasPendingManualReview = hasPendingManualReview;
                State = hasPendingManualReview ? AttemptState.PendingReview : AttemptState.Submitted;
                break;
            case AttemptState.PendingReview:
                HasPendingManualReview = true;
                return;
            case AttemptState.Submitted:
            case AttemptState.Finalized:
                return; // повторная отправка уже принятой работы
            default:
                throw new DomainException($"Попытку в состоянии {State} нельзя сдать.");
        }
    }

    /// <summary>
    /// Submitted/PendingReview → Finalized. Ручная проверка завершена (или не требовалась).
    /// Итоговое округление — только здесь, один раз (ТЗ §11).
    /// </summary>
    public GradeResult Finalize(decimal earnedPoints, decimal maxPoints, DateTimeOffset nowUtc)
    {
        if (State is not (AttemptState.Submitted or AttemptState.PendingReview))
            throw new DomainException($"Незавершённую попытку ({State}) нельзя финализировать.");
        var score = GradeCalculator.Compute(earnedPoints, maxPoints);
        ScoreEarnedPoints = score.EarnedPoints;
        ScoreMaxPoints = score.MaxPoints;
        CompletedAtUtc = nowUtc;
        State = AttemptState.Finalized;
        return score;
    }

    /// <summary>Отмена по явному решению; отменённая попытка не eligible для лучшей (ТЗ §10.4). История не стирается.</summary>
    public void Cancel(DateTimeOffset nowUtc)
    {
        if (State is AttemptState.Finalized or AttemptState.Cancelled)
            throw new DomainException($"Финализированную/отменённую попытку нельзя отменить ({State}).");
        State = AttemptState.Cancelled;
        CompletedAtUtc = nowUtc;
    }

    /// <summary>
    /// Переоценка финализированной попытки после ручного override критерия (ТЗ §11).
    /// CompletedAtUtc не меняется: редактирование оценки не запускает срок хранения заново (ТЗ §20).
    /// История до/после фиксируется GradeRevision снаружи.
    /// </summary>
    public GradeResult Regrade(decimal earnedPoints, decimal maxPoints)
    {
        if (State != AttemptState.Finalized)
            throw new DomainException($"Переоценить можно только финализированную попытку ({State}).");
        var score = GradeCalculator.Compute(earnedPoints, maxPoints);
        ScoreEarnedPoints = score.EarnedPoints;
        ScoreMaxPoints = score.MaxPoints;
        return score;
    }
}
