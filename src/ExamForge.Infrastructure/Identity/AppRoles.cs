namespace ExamForge.Infrastructure.Identity;

/// <summary>Роли персонала. Студенческие доступы ролями не представлены (ADR-002).</summary>
public static class AppRoles
{
    public const string Administrator = "Administrator";
    public const string Teacher = "Teacher";
}
