using ExamForge.Domain.Common;

namespace ExamForge.Domain.Assignments;

/// <summary>Сценарии работы (ТЗ §1.1): Assessment (контрольная) и Practice (тренировка).</summary>
public enum AssignmentMode
{
    Assessment = 1,
    Practice = 2,
}

/// <summary>Политика обратной связи (ТЗ §9). Контрольная по умолчанию — только баллы и процент.</summary>
public enum FeedbackPolicy
{
    /// <summary>Только баллы/процент; без ключей, пояснений и правильности отдельных ответов.</summary>
    ScoresOnly = 1,
    /// <summary>Practice: явно разрешённые подсказки/объяснения/правильные ответы.</summary>
    PracticeFeedback = 2,
}

/// <summary>
/// Назначение версии теста получателям с режимом и правилами (ТЗ §9).
/// Фактические настройки фиксируются при создании назначения и не наследуют изменения шаблона.
/// </summary>
public sealed class Assignment
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid TestVersionId { get; private set; }
    public AssignmentMode Mode { get; private set; }
    public FeedbackPolicy Feedback { get; private set; }

    public DateTimeOffset OpensAtUtc { get; private set; }
    public DateTimeOffset? ClosesAtUtc { get; private set; }

    /// <summary>Обычные попытки; две по умолчанию (ТЗ §1.1, §10.4). Extra grants — отдельно.</summary>
    public int MaxAttempts { get; private set; } = 2;

    public int? PerQuestionTimeLimitSeconds { get; private set; }
    public int? AttemptTotalTimeLimitSeconds { get; private set; }
    public bool NoBacktracking { get; private set; } = true;

    /// <summary>Общая ссылка выключена по умолчанию для оценочной работы (ТЗ §5.1).</summary>
    public bool AllowCommonLink { get; private set; }

    /// <summary>Код общей ссылки: открывает конкретное назначение; выдаётся при явном включении.</summary>
    public string? CommonLinkCode { get; private set; }

    public Guid CreatedByTeacherId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private Assignment() { }

    public static Assignment Create(
        Guid testVersionId,
        AssignmentMode mode,
        FeedbackPolicy feedback,
        DateTimeOffset opensAtUtc,
        DateTimeOffset? closesAtUtc,
        int maxAttempts,
        int? perQuestionTimeLimitSeconds,
        int? attemptTotalTimeLimitSeconds,
        bool noBacktracking,
        bool allowCommonLink,
        Guid teacherId,
        DateTimeOffset nowUtc)
    {
        if (maxAttempts is < 1 or > 10)
            throw new DomainException("Число обычных попыток должно быть в диапазоне 1–10.");
        if (closesAtUtc.HasValue && closesAtUtc.Value <= opensAtUtc)
            throw new DomainException("Закрытие назначения должно быть позже открытия.");
        if (perQuestionTimeLimitSeconds is < 1 or > 3600)
            throw new DomainException("Лимит времени вопроса должен быть в диапазоне 1–3600 секунд.");
        if (attemptTotalTimeLimitSeconds is < 1)
            throw new DomainException("Общий лимит попытки должен быть положительным.");
        if (mode == AssignmentMode.Assessment && allowCommonLink)
            throw new DomainException("Общая ссылка для контрольной включается явно отдельным решением после создания (ТЗ §5.1).");
        if (mode == AssignmentMode.Assessment && feedback == FeedbackPolicy.PracticeFeedback)
            throw new DomainException("Контрольная не показывает ключи/объяснения (ТЗ §9).");

        return new()
        {
            TestVersionId = testVersionId,
            Mode = mode,
            Feedback = feedback,
            OpensAtUtc = opensAtUtc,
            ClosesAtUtc = closesAtUtc,
            MaxAttempts = maxAttempts,
            PerQuestionTimeLimitSeconds = perQuestionTimeLimitSeconds,
            AttemptTotalTimeLimitSeconds = attemptTotalTimeLimitSeconds,
            NoBacktracking = noBacktracking,
            AllowCommonLink = allowCommonLink,
            CreatedByTeacherId = teacherId,
            CreatedAtUtc = nowUtc,
        };
    }

    /// <summary>
    /// Включить общую ссылку (ТЗ §5.1): преподаватель делает это явно и осознанно.
    /// ФИО+группа при входе — не личность: строгой защиты повторного входа нет.
    /// </summary>
    public void EnableCommonLink(string linkCode, DateTimeOffset nowUtc)
    {
        AllowCommonLink = true;
        CommonLinkCode = Guard.Required(linkCode, "Код общей ссылки", 64);
    }

    public void DisableCommonLink(DateTimeOffset nowUtc)
    {
        AllowCommonLink = false;
        CommonLinkCode = null;
    }

    public bool IsOpenAt(DateTimeOffset nowUtc)
        => nowUtc >= OpensAtUtc && (ClosesAtUtc is null || nowUtc < ClosesAtUtc);
}

public enum InvitationStatus
{
    Active = 1,
    Revoked = 2,
}

/// <summary>
/// Разрешение студенту открыть назначение (ТЗ §2, §5.2). Код — секрет доступа, не личность.
/// Хранится только HMAC-значение; секрет не логируется (ADR-002).
/// </summary>
public sealed class Invitation
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid AssignmentId { get; private set; }
    public Guid? StudentProfileId { get; private set; }

    /// <summary>HMAC-SHA256 нормализованного кода; уникален.</summary>
    public string CodeHash { get; private set; } = string.Empty;
    public InvitationStatus Status { get; private set; } = InvitationStatus.Active;
    public DateTimeOffset? ExpiresAtUtc { get; private set; }

    public int FailedAttempts { get; private set; }
    public DateTimeOffset? LockedUntilUtc { get; private set; }

    public Guid CreatedByTeacherId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public Guid? ReplacedByInvitationId { get; private set; }

    private Invitation() { }

    public static Invitation Create(
        Guid assignmentId, Guid? studentProfileId, string codeHash,
        DateTimeOffset? expiresAtUtc, Guid teacherId, DateTimeOffset nowUtc)
    {
        if (codeHash.Length != 64)
            throw new DomainException("Ожидается HMAC-SHA256 в hex (64 символа).");
        return new()
        {
            AssignmentId = assignmentId,
            StudentProfileId = studentProfileId,
            CodeHash = codeHash,
            ExpiresAtUtc = expiresAtUtc,
            CreatedByTeacherId = teacherId,
            CreatedAtUtc = nowUtc,
        };
    }

    /// <summary>Проверка возможности обмена. Возвращает причину отказа или null, если обмен разрешён.</summary>
    public string? CheckExchangeable(DateTimeOffset nowUtc)
    {
        if (Status == InvitationStatus.Revoked)
            return "Код отозван. Обратитесь к преподавателю.";
        if (LockedUntilUtc is { } lockedUntil && nowUtc < lockedUntil)
            return "Код временно заблокирован после нескольких неудачных попыток. Повторите позже.";
        if (ExpiresAtUtc is { } expires && nowUtc >= expires)
            return "Срок действия кода истёк.";
        return null;
    }

    public void RegisterFailedAttempt(DateTimeOffset nowUtc)
    {
        FailedAttempts++;
        if (FailedAttempts >= MaxFailedAttempts)
            LockedUntilUtc = nowUtc.Add(Lockout);
    }

    public void RegisterSuccessfulExchange(DateTimeOffset nowUtc)
    {
        FailedAttempts = 0;
        LockedUntilUtc = null;
    }

    /// <summary>Отзыв: код перестаёт действовать, история не удаляется (ТЗ §5.2).</summary>
    public void Revoke(DateTimeOffset nowUtc) => Status = InvitationStatus.Revoked;

    /// <summary>Замена: старый код отзывается, ссылка на новый сохраняется.</summary>
    public void ReplaceWith(Invitation replacement, DateTimeOffset nowUtc)
    {
        Status = InvitationStatus.Revoked;
        ReplacedByInvitationId = replacement.Id;
    }
}

/// <summary>
/// Дополнительная попытка: отдельный grant с причиной и автором; не стирает историю (ТЗ §10.4, AC-23).
/// </summary>
public sealed class ExtraAttemptGrant
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid AssignmentId { get; private set; }
    public Guid StudentProfileId { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public Guid GrantedByTeacherId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public Guid? UsedByAttemptId { get; private set; }

    private ExtraAttemptGrant() { }

    public static ExtraAttemptGrant Create(
        Guid assignmentId, Guid studentProfileId, string reason, Guid teacherId, DateTimeOffset nowUtc)
        => new()
        {
            AssignmentId = assignmentId,
            StudentProfileId = studentProfileId,
            Reason = Guard.Required(reason, "Причина дополнительной попытки", 1000),
            GrantedByTeacherId = teacherId,
            CreatedAtUtc = nowUtc,
        };

    public bool IsUsed => UsedByAttemptId is not null;

    public void MarkUsed(Guid attemptId)
    {
        if (UsedByAttemptId is not null && UsedByAttemptId != attemptId)
            throw new DomainException("Grant уже использован другой попыткой.");
        UsedByAttemptId = attemptId;
    }
}
