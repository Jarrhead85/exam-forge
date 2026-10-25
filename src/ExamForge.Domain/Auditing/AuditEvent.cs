namespace ExamForge.Domain.Auditing;

/// <summary>
/// Аудит важных действий (ТЗ §19): без ФИО, ответов и кодов доступа; только идентификаторы.
/// </summary>
public sealed class AuditEvent
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Kind { get; private set; } = string.Empty;
    public Guid? ActorTeacherId { get; private set; }
    public string TargetType { get; private set; } = string.Empty;
    public Guid TargetId { get; private set; }
    public string Details { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private AuditEvent() { }

    public static AuditEvent Create(
        string kind, Guid? actorTeacherId, string targetType, Guid targetId,
        string details, DateTimeOffset nowUtc)
        => new()
        {
            Kind = kind,
            ActorTeacherId = actorTeacherId,
            TargetType = targetType,
            TargetId = targetId,
            Details = details,
            CreatedAtUtc = nowUtc,
        };
}
