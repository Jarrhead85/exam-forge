using ExamForge.Domain.Common;

namespace ExamForge.Domain.People;

/// <summary>Профиль студента: идентификатор, ФИО, членство в группах (ТЗ §4). Не является учётной записью.</summary>
public sealed class StudentProfile
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string LastName { get; private set; } = string.Empty;
    public string FirstName { get; private set; } = string.Empty;
    public string? MiddleName { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public Guid? CreatedByTeacherId { get; private set; }

    /// <summary>Является ли профиль идентифицированной личностью (в отличие от входа по общей ссылке).</summary>
    public bool IsIdentified { get; private set; }

    private StudentProfile() { }

    public static StudentProfile Create(
        string lastName, string firstName, string? middleName,
        DateTimeOffset nowUtc, Guid? createdByTeacherId, bool identified = true)
    {
        var name = PersonName.Create(lastName, firstName, middleName);
        return new StudentProfile
        {
            LastName = name.LastName,
            FirstName = name.FirstName,
            MiddleName = name.MiddleName,
            CreatedAtUtc = nowUtc,
            CreatedByTeacherId = createdByTeacherId,
            IsIdentified = identified,
        };
    }

    public PersonName Name => PersonName.Create(LastName, FirstName, MiddleName);

    /// <summary>Исправление ФИО (опечатки). Снимки ФИО в попытках не переписываются (ТЗ §4).</summary>
    public void Rename(string lastName, string firstName, string? middleName)
    {
        var name = PersonName.Create(lastName, firstName, middleName);
        LastName = name.LastName;
        FirstName = name.FirstName;
        MiddleName = name.MiddleName;
    }
}

/// <summary>Учебная группа. Изменение состава не переписывает снимки попыток (ТЗ §4).</summary>
public sealed class Group
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Name { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private Group() { }

    public static Group Create(string name, DateTimeOffset nowUtc)
        => new() { Name = Guard.Required(name, "Название группы", 200), CreatedAtUtc = nowUtc };

    public void Rename(string name) => Name = Guard.Required(name, "Название группы", 200);
}

public sealed class GroupMembership
{
    public Guid GroupId { get; private set; }
    public Guid StudentProfileId { get; private set; }
    public DateTimeOffset JoinedAtUtc { get; private set; }

    private GroupMembership() { }

    public static GroupMembership Create(Guid groupId, Guid studentProfileId, DateTimeOffset nowUtc)
        => new() { GroupId = groupId, StudentProfileId = studentProfileId, JoinedAtUtc = nowUtc };
}

/// <summary>Явное разрешение преподавателя работать с группой (ТЗ §3.2).</summary>
public sealed class TeacherGroupGrant
{
    public Guid TeacherId { get; private set; }
    public Guid GroupId { get; private set; }
    public DateTimeOffset GrantedAtUtc { get; private set; }

    private TeacherGroupGrant() { }

    public static TeacherGroupGrant Create(Guid teacherId, Guid groupId, DateTimeOffset nowUtc)
        => new() { TeacherId = teacherId, GroupId = groupId, GrantedAtUtc = nowUtc };
}
