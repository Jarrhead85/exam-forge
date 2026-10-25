namespace ExamForge.Application.StudentFlow;

/// <summary>
/// Идентичность студента из сессии (ADR-002): приглашение/общая ссылка/аккаунт.
/// Код приглашения — секрет доступа, не доказательство личности (ТЗ §2).
/// AssignmentId заполнен для кода/ссылки; у аккаунта — null (доступ ко всем своим назначениям).
/// GroupName — введённая при входе по общей ссылке группа (уходит в снимок попытки).
/// </summary>
public sealed record StudentIdentity(
    Guid? InvitationId,
    Guid? AssignmentId,
    Guid? StudentProfileId,
    string? GroupName = null);

/// <summary>Ошибка студенческого потока с HTTP-кодом; сообщение показывается студенту без технических деталей.</summary>
public sealed class StudentFlowException(int statusCode, string errorCode, string message)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string ErrorCode { get; } = errorCode;

    public static StudentFlowException NotFound(string message = "Объект не найден.")
        => new(404, "not_found", message);

    public static StudentFlowException Conflict(string errorCode, string message)
        => new(409, errorCode, message);
}

// ---------- DTO: строго без ключей, правильных ответов и правил оценивания (ТЗ §17, AC-11) ----------

public sealed record StudentOptionDto(string Id, string Text);

public sealed record StudentTileDto(string TileId, char Letter);

/// <summary>Вопрос для отображения: только формулировка, display options, media-поля и время.</summary>
public sealed record StudentQuestionDto(
    Guid QuestionSessionId,
    Guid QuestionVersionId,
    int OrderIndex,
    string Type,
    string Prompt,
    string? Instruction,
    int? TimeLimitSeconds,
    IReadOnlyList<StudentOptionDto>? Options,
    IReadOnlyList<StudentTileDto>? Tiles,
    IReadOnlyList<StudentOptionDto>? MatchingLeft,
    IReadOnlyList<StudentOptionDto>? MatchingRight,
    IReadOnlyList<StudentOptionDto>? OrderingItems,
    IReadOnlyList<StudentOptionDto>? StructuredFields,
    IReadOnlyList<string>? BlankIds);

public sealed record SessionBriefDto(int OrderIndex, string State, int Revision, DateTimeOffset? DeadlineUtc);

public sealed record AttemptStateDto(
    Guid AttemptId,
    string Status,
    int AttemptNumber,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? DeadlineUtc,
    int TotalQuestions,
    IReadOnlyList<SessionBriefDto> Sessions,
    StudentQuestionDto? CurrentQuestion);

/// <summary>Ответ на сохранение (ТЗ §17): acceptedRevision/savedAt/serverNow/deadline/state; без «правильно/неправильно».</summary>
public sealed record SaveAnswerDto(
    bool Saved,
    int? AcceptedRevision,
    DateTimeOffset ServerNowUtc,
    DateTimeOffset? DeadlineUtc,
    string SessionState,
    string? RejectionReason);

public sealed record LockCurrentDto(string Outcome, int OrderIndex, string SessionState);

public sealed record FinishAttemptDto(
    string Status,
    bool Provisional,
    decimal? EarnedPoints,
    decimal? MaxPoints,
    int? RoundedPercent);

/// <summary>Результат для студента (ТЗ §12): provisional/finalized, баллы и процент; ключей и пояснений нет.</summary>
public sealed record StudentResultDto(
    string Status,
    bool Provisional,
    decimal? EarnedPoints,
    decimal MaxPoints,
    int? RoundedPercent,
    DateTimeOffset? CompletedAtUtc,
    int AttemptNumber);

public sealed record StudentAssignmentDto(
    Guid AssignmentId,
    string Title,
    string Mode,
    DateTimeOffset OpensAtUtc,
    DateTimeOffset? ClosesAtUtc,
    int MaxAttempts,
    int UsedAttempts,
    bool HasActiveAttempt,
    Guid? ActiveAttemptId,
    IReadOnlyList<StudentAttemptBriefDto> Attempts);

/// <summary>Краткая сводка попытки студента: баллы/процент без ключей и пояснений (ТЗ §9, §12).</summary>
public sealed record StudentAttemptBriefDto(
    Guid AttemptId,
    int AttemptNumber,
    string Status,
    decimal? EarnedPoints,
    decimal? MaxPoints,
    int? RoundedPercent,
    DateTimeOffset? CompletedAtUtc);

/// <summary>Лекция, доступная студенту (ТЗ §14): без внутренних идентификаторов файлов.</summary>
public sealed record StudentMaterialDto(
    Guid MaterialId,
    string Title,
    string? Description,
    int OrderIndex,
    long FileSizeBytes);

public sealed record ExchangeRequest(string Code);

public sealed record ExchangeResponse(Guid AssignmentId, Guid InvitationId);

/// <summary>Вход по общей ссылке (ТЗ §5.1): ФИО+группа вводятся перед стартом; не совпадение с профилями.</summary>
public sealed record CommonLinkRequest(string Code, string LastName, string FirstName, string? MiddleName, string? Group);

/// <summary>Вход постоянного аккаунта студента (ТЗ §5.3).</summary>
public sealed record StudentLoginRequest(string Login, string Password);
