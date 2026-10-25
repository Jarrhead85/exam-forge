using ExamForge.Domain.Common;

namespace ExamForge.Domain.Subjects;

/// <summary>Предмет. Предметные правила не смешиваются с универсальным движком (ТЗ §4).</summary>
public sealed class Subject
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Name { get; private set; } = string.Empty;
    public string? Code { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private Subject() { }

    public static Subject Create(string name, string? code, DateTimeOffset nowUtc)
        => new()
        {
            Name = Guard.Required(name, "Название предмета", 200),
            Code = string.IsNullOrWhiteSpace(code) ? null : Guard.Required(code, "Код предмета", 32),
            CreatedAtUtc = nowUtc,
        };

    /// <summary>Переименование предмета: метаданные, содержимое заданий не затрагивает.</summary>
    public void Rename(string name, string? code)
    {
        Name = Guard.Required(name, "Название предмета", 200);
        Code = string.IsNullOrWhiteSpace(code) ? null : Guard.Required(code, "Код предмета", 32);
    }
}

/// <summary>Иерархическая тема предмета.</summary>
public sealed class Topic
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid SubjectId { get; private set; }
    public Guid? ParentTopicId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private Topic() { }

    public static Topic Create(Guid subjectId, string name, Guid? parentTopicId, DateTimeOffset nowUtc)
    {
        if (parentTopicId == subjectId)
            throw new DomainException("Тема не может быть родителем самой себя.");
        return new()
        {
            SubjectId = subjectId,
            ParentTopicId = parentTopicId,
            Name = Guard.Required(name, "Название темы", 200),
            CreatedAtUtc = nowUtc,
        };
    }
}
