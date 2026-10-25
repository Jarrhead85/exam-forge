using ExamForge.Domain.Common;

namespace ExamForge.Domain.Dictionary;

/// <summary>Статус словарной записи (ТЗ §6): импорт не публикуется автоматически.</summary>
public enum DictionaryEntryStatus
{
    ImportedDraft = 1,
    Confirmed = 2,
    Rejected = 3,
}

/// <summary>
/// Подтверждённые грамматические признаки (ТЗ §6): «жидкая/твёрдая форма», «род», «склонение», «падеж».
/// Неизвестные признаки не выдумываются: остаются null, запись помечается NeedsReview.
/// </summary>
public sealed record GrammaticalFeatures
{
    public string? FormKind { get; init; }     // liquid / solid
    public string? Gender { get; init; }       // m / f / n
    public string? Declension { get; init; }   // I–V
    public string? GrammaticalCase { get; init; }

    /// <summary>Известный словарь допустимых значений; расширение — явным решением.</summary>
    public static readonly IReadOnlySet<string> KnownFormKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "liquid", "solid" };
    public static readonly IReadOnlySet<string> KnownGenders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "m", "f", "n" };
    public static readonly IReadOnlySet<string> KnownDeclensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "I", "II", "III", "IV", "V" };
    public static readonly IReadOnlySet<string> KnownCases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "nominative", "genitive", "dative", "accusative", "ablative", "vocative" };

    public bool IsKnown =>
        (FormKind is null || KnownFormKinds.Contains(FormKind))
        && (Gender is null || KnownGenders.Contains(Gender))
        && (Declension is null || KnownDeclensions.Contains(Declension))
        && (GrammaticalCase is null || KnownCases.Contains(GrammaticalCase));
}

public sealed class DictionaryEntry
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid DictionaryId { get; private set; }

    /// <summary>Оригинальная запись источника, лемма и переводы; не все поля обязательны (ТЗ §6).</summary>
    public string OriginalText { get; private set; } = string.Empty;
    public string Lemma { get; private set; } = string.Empty;
    public IReadOnlyList<string> Translations { get; private set; } = [];
    public GrammaticalFeatures? Features { get; private set; }
    public IReadOnlyList<string> Topics { get; private set; } = [];
    public string? SourceRef { get; private set; }
    public DictionaryEntryStatus Status { get; private set; } = DictionaryEntryStatus.ImportedDraft;
    /// <summary>True, если признаки не распознаны и требуют решения преподавателя.</summary>
    public bool NeedsReview { get; private set; }
    public int Version { get; private set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    private DictionaryEntry() { }

    public static DictionaryEntry Import(
        Guid dictionaryId, string originalText, string lemma,
        IReadOnlyList<string> translations, GrammaticalFeatures? features,
        IReadOnlyList<string> topics, string? sourceRef, DateTimeOffset nowUtc)
    {
        var entry = new DictionaryEntry
        {
            DictionaryId = dictionaryId,
            OriginalText = Guard.Required(originalText, "Оригинальная запись", 1000),
            Lemma = Guard.Required(lemma, "Лемма", 200),
            Translations = translations,
            Features = features,
            Topics = topics,
            SourceRef = string.IsNullOrWhiteSpace(sourceRef) ? null : Guard.Required(sourceRef, "Ссылка на источник", 200),
            Status = DictionaryEntryStatus.ImportedDraft,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };
        entry.NeedsReview = features is null || !features.IsKnown;
        return entry;
    }

    /// <summary>Подтверждение преподавателем: фиксирует признаки и перевод (ТЗ §6).</summary>
    public void Confirm(GrammaticalFeatures? features, IReadOnlyList<string> translations, DateTimeOffset nowUtc)
    {
        if (Status == DictionaryEntryStatus.Confirmed)
            throw new DomainException("Запись уже подтверждена; изменение — через новую версию.");
        if (features is { IsKnown: false })
            throw new DomainException("Нельзя подтвердить запись с неизвестными грамматическими признаками (ТЗ §6).");
        Features = features;
        Translations = translations;
        NeedsReview = false;
        Status = DictionaryEntryStatus.Confirmed;
        Version++;
        UpdatedAtUtc = nowUtc;
    }

    public void Reject(DateTimeOffset nowUtc)
    {
        Status = DictionaryEntryStatus.Rejected;
        UpdatedAtUtc = nowUtc;
    }
}

public sealed class Dictionary
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid SubjectId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private Dictionary() { }

    public static Dictionary Create(Guid subjectId, string name, DateTimeOffset nowUtc)
        => new() { SubjectId = subjectId, Name = Guard.Required(name, "Название словаря", 200), CreatedAtUtc = nowUtc };

    public void Rename(string name) => Name = Guard.Required(name, "Название словаря", 200);
}

/// <summary>Фоновое извлечение текста PDF с таблицей предварительных записей (ТЗ §6).</summary>
public enum ImportJobStatus
{
    Queued = 1,
    Extracting = 2,
    PreviewReady = 3,
    Confirmed = 4,
    Failed = 5,
}

public sealed class ImportJob
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid DictionaryId { get; private set; }
    public Guid FileAssetId { get; private set; }
    public Guid StartedByTeacherId { get; private set; }
    public ImportJobStatus Status { get; private set; } = ImportJobStatus.Queued;
    public string? Error { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? FinishedAtUtc { get; private set; }

    private ImportJob() { }

    public static ImportJob Create(Guid dictionaryId, Guid fileAssetId, Guid teacherId, DateTimeOffset nowUtc)
        => new()
        {
            DictionaryId = dictionaryId,
            FileAssetId = fileAssetId,
            StartedByTeacherId = teacherId,
            Status = ImportJobStatus.Queued,
            CreatedAtUtc = nowUtc,
        };

    public void MarkPreviewReady(DateTimeOffset nowUtc)
    {
        RequireStatus(ImportJobStatus.Extracting, ImportJobStatus.Queued);
        Status = ImportJobStatus.PreviewReady;
        FinishedAtUtc = nowUtc;
    }

    public void MarkFailed(string error, DateTimeOffset nowUtc)
    {
        Status = ImportJobStatus.Failed;
        Error = Guard.Required(error, "Причина ошибки импорта", 1000);
        FinishedAtUtc = nowUtc;
    }

    /// <summary>Запись в словарь только после подтверждения предпросмотра (ТЗ §6, AC-13).</summary>
    public void MarkConfirmed(DateTimeOffset nowUtc)
    {
        RequireStatus(ImportJobStatus.PreviewReady);
        Status = ImportJobStatus.Confirmed;
        FinishedAtUtc = nowUtc;
    }

    private void RequireStatus(params ImportJobStatus[] allowed)
    {
        if (!allowed.Contains(Status))
            throw new DomainException($"Недопустимый переход из статуса {Status}.");
    }
}
