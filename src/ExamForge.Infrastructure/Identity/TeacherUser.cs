using Microsoft.AspNetCore.Identity;

namespace ExamForge.Infrastructure.Identity;

/// <summary>
/// Преподаватель/администратор. Логин выдаётся системой, email не требуется (ТЗ §4).
/// Студенческие доступы не используют этот класс (ADR-002).
/// </summary>
public sealed class TeacherUser : IdentityUser<Guid>
{
    /// <summary>Отображаемое ФИО преподавателя.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Явное право администратора на доступ к персональным результатам (ТЗ §3.1) — выдаётся отдельно от роли.</summary>
    public bool HasPersonalResultsAccess { get; set; }
}
