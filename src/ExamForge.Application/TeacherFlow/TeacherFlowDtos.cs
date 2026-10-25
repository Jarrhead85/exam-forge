namespace ExamForge.Application.TeacherFlow;

/// <summary>Актор преподавателя: владелец или администратор с явным доступом (ТЗ §3.1–3.2).</summary>
public sealed record TeacherActor(Guid TeacherId, bool IsAdministrator);

public sealed record CreateAssignmentRequest(
    Guid TestVersionId,
    string Mode,
    DateTimeOffset OpensAtUtc,
    DateTimeOffset? ClosesAtUtc,
    int MaxAttempts,
    int? PerQuestionTimeLimitSeconds,
    int? AttemptTotalTimeLimitSeconds,
    bool NoBacktracking);

public sealed record TeacherAssignmentDto(
    Guid AssignmentId,
    string TestTitle,
    string Mode,
    DateTimeOffset OpensAtUtc,
    DateTimeOffset? ClosesAtUtc,
    int MaxAttempts,
    int StudentsInvited,
    int AttemptsStarted,
    bool IsOpen);

public sealed record AddStudentRequest(string LastName, string FirstName, string? MiddleName, string? GroupName);

public sealed record InvitationDto(
    Guid InvitationId,
    Guid StudentProfileId,
    string StudentName,
    string? StudentGroup,
    DateTimeOffset CreatedAtUtc,
    string Status,
    Guid? UsedByReplacedInvitationId,
    /// <summary>Код показывается ровно один раз при создании; далее не хранится (ADR-002).</summary>
    string? OneTimeCode);

public sealed record ExtraGrantRequest(string Reason, Guid StudentProfileId);

public sealed record TeacherAttemptDto(
    Guid AttemptId,
    string StudentName,
    string? StudentGroup,
    int AttemptNumber,
    string Status,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    decimal? EarnedPoints,
    decimal? MaxPoints,
    int? RoundedPercent,
    bool IsBest);

public sealed record TeacherAssignmentDetailsDto(
    TeacherAssignmentDto Assignment,
    IReadOnlyList<InvitationDto> Invitations,
    IReadOnlyList<TeacherAttemptDto> Attempts);
