using ExamForge.Domain.Common;

namespace ExamForge.Domain.People;

/// <summary>
/// Постоянный аккаунт студента (ТЗ §4, §5.3): системный логин без email, пароль хранится хешем.
/// Отделён от учительских учётных записей; вход даёт студенческую сессию, а не учительские права.
/// Сброс пароля выполняет преподаватель/администратор (ТЗ §4).
/// </summary>
public sealed class StudentAccount
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid StudentProfileId { get; private set; }

    /// <summary>Системный логин вида stud_ab12cd, выдаётся при создании; уникален.</summary>
    public string Login { get; private set; } = string.Empty;

    /// <summary>PBKDF2-хеш (Identity PasswordHasher); пароль нигде не хранится открыто.</summary>
    public string PasswordHash { get; private set; } = string.Empty;

    public bool IsDisabled { get; private set; }
    public int FailedAttempts { get; private set; }
    public DateTimeOffset? LockedUntilUtc { get; private set; }

    public Guid CreatedByTeacherId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? LastLoginAtUtc { get; private set; }

    private StudentAccount() { }

    public static StudentAccount Create(
        Guid studentProfileId, string login, string passwordHash,
        Guid createdByTeacherId, DateTimeOffset nowUtc)
        => new()
        {
            StudentProfileId = studentProfileId,
            Login = Guard.Required(login, "Логин студента", 64),
            PasswordHash = Guard.Required(passwordHash, "Хеш пароля", 500),
            CreatedByTeacherId = createdByTeacherId,
            CreatedAtUtc = nowUtc,
        };

    public void RegisterFailedAttempt(DateTimeOffset nowUtc)
    {
        FailedAttempts++;
        if (FailedAttempts >= 5)
            LockedUntilUtc = nowUtc.Add(TimeSpan.FromMinutes(15));
    }

    public void RegisterSuccessfulLogin(DateTimeOffset nowUtc)
    {
        FailedAttempts = 0;
        LockedUntilUtc = null;
        LastLoginAtUtc = nowUtc;
    }

    public void ChangePassword(string newPasswordHash, DateTimeOffset nowUtc)
    {
        PasswordHash = Guard.Required(newPasswordHash, "Хеш пароля", 500);
        FailedAttempts = 0;
        LockedUntilUtc = null;
    }

    public void Disable(DateTimeOffset nowUtc) => IsDisabled = true;

    /// <summary>Повторное включение аккаунта преподавателем (после Disable).</summary>
    public void Enable(DateTimeOffset nowUtc)
    {
        IsDisabled = false;
        FailedAttempts = 0;
        LockedUntilUtc = null;
    }
}
