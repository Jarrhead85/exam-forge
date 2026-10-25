using ExamForge.Domain.Common;

namespace ExamForge.Domain.Materials;

/// <summary>Kind материала (ТЗ §14): PDF сейчас; WebLecture — предусмотренное развитие (P7).</summary>
public enum MaterialKind
{
    Pdf = 1,
    Web = 2,
}

public enum MaterialStatus
{
    Draft = 1,
    Published = 2,
    Archived = 3,
}

/// <summary>
/// Лекционный материал (ТЗ §14): PDF, заголовок, описание, порядок, автор, источник/права,
/// версия; доступ через grants. Скачивание только авторизованным; публичные URL запрещены.
/// </summary>
public sealed class Material
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid SubjectId { get; private set; }
    public Guid? TopicId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public MaterialKind Kind { get; private set; } = MaterialKind.Pdf;
    public int Version { get; private set; } = 1;
    public MaterialStatus Status { get; private set; } = MaterialStatus.Draft;
    public Guid AuthorTeacherId { get; private set; }
    public Guid FileAssetId { get; private set; }
    public int OrderIndex { get; private set; }
    public string? SourceNote { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? PublishedAtUtc { get; private set; }

    private Material() { }

    public static Material CreatePdf(
        Guid subjectId, Guid? topicId, string title, string? description,
        Guid fileAssetId, Guid authorTeacherId, int orderIndex, string? sourceNote, DateTimeOffset nowUtc)
        => new()
        {
            SubjectId = subjectId,
            TopicId = topicId,
            Title = Guard.Required(title, "Название материала", 300),
            Description = string.IsNullOrWhiteSpace(description) ? null : Guard.Required(description, "Описание", 2000),
            Kind = MaterialKind.Pdf,
            FileAssetId = fileAssetId,
            AuthorTeacherId = authorTeacherId,
            OrderIndex = orderIndex,
            SourceNote = string.IsNullOrWhiteSpace(sourceNote) ? null : Guard.Required(sourceNote, "Источник/права", 1000),
            Status = MaterialStatus.Draft,
            CreatedAtUtc = nowUtc,
        };

    public void Publish(DateTimeOffset nowUtc)
    {
        if (Status != MaterialStatus.Draft)
            throw new DomainException($"Опубликовать можно только черновик ({Status}).");
        Status = MaterialStatus.Published;
        PublishedAtUtc = nowUtc;
    }

    /// <summary>Архив: материал скрывается, файлы и grants не удаляются (ТЗ §14).</summary>
    public void Archive(DateTimeOffset nowUtc)
    {
        if (Status != MaterialStatus.Published)
            throw new DomainException($"В архив можно отправить опубликованный материал ({Status}).");
        Status = MaterialStatus.Archived;
    }

    /// <summary>Правка названия/описания: метаданные, файл и grants не затрагивает.</summary>
    public void Rename(string title, string? description)
    {
        Title = Guard.Required(title, "Название материала", 300);
        Description = string.IsNullOrWhiteSpace(description) ? null : Guard.Required(description, "Описание", 2000);
    }
}

/// <summary>Доступ группы/студента к материалу (ТЗ §14): ровно один адресат на grant.</summary>
public sealed class MaterialGrant
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid MaterialId { get; private set; }
    public Guid? GroupId { get; private set; }
    public Guid? StudentProfileId { get; private set; }
    public Guid GrantedByTeacherId { get; private set; }
    public DateTimeOffset GrantedAtUtc { get; private set; }

    private MaterialGrant() { }

    public static MaterialGrant ForStudent(Guid materialId, Guid studentProfileId, Guid teacherId, DateTimeOffset nowUtc)
        => Create(materialId, null, studentProfileId, teacherId, nowUtc);

    public static MaterialGrant ForGroup(Guid materialId, Guid groupId, Guid teacherId, DateTimeOffset nowUtc)
        => Create(materialId, groupId, null, teacherId, nowUtc);

    private static MaterialGrant Create(Guid materialId, Guid? groupId, Guid? studentProfileId, Guid teacherId, DateTimeOffset nowUtc)
    {
        if ((groupId is null) == (studentProfileId is null))
            throw new DomainException("Grant выдаётся либо группе, либо студенту.");
        return new()
        {
            MaterialId = materialId,
            GroupId = groupId,
            StudentProfileId = studentProfileId,
            GrantedByTeacherId = teacherId,
            GrantedAtUtc = nowUtc,
        };
    }
}
