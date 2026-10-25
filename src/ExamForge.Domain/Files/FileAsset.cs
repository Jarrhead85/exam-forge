using ExamForge.Domain.Common;

namespace ExamForge.Domain.Files;

/// <summary>Класс доступа к файлу — определяет, кто и через какую проверку прав его получает (ТЗ §15).</summary>
public enum FileAccessClass
{
    Lecture = 1,
    QuestionMedia = 2,
    Scan = 3,
    PrintArtifact = 4,
    ImportSource = 5,
}

/// <summary>
/// Файл вне JSON задания: владелец, ключ хранилища, sha256, MIME, размер, класс доступа (ТЗ §15).
/// Отдаётся только через авторизованный endpoint; публичные URL запрещены (ТЗ §14).
/// </summary>
public sealed class FileAsset
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid? OwnerTeacherId { get; private set; }
    public string StorageKey { get; private set; } = string.Empty;
    public string Sha256Hex { get; private set; } = string.Empty;
    public string MimeType { get; private set; } = string.Empty;
    public long SizeBytes { get; private set; }
    public FileAccessClass AccessClass { get; private set; }
    public string? OriginalFileName { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private FileAsset() { }

    public static FileAsset Create(
        string storageKey,
        string sha256Hex,
        string mimeType,
        long sizeBytes,
        FileAccessClass accessClass,
        DateTimeOffset nowUtc,
        Guid? ownerTeacherId,
        string? originalFileName)
    {
        if (sizeBytes <= 0)
            throw new DomainException("Размер файла должен быть положительным.");
        if (sha256Hex.Length != 64)
            throw new DomainException("Ожидается SHA-256 в hex (64 символа).");
        return new()
        {
            OwnerTeacherId = ownerTeacherId,
            StorageKey = Guard.Required(storageKey, "Ключ хранилища", 500),
            Sha256Hex = sha256Hex.ToUpperInvariant(),
            MimeType = Guard.Required(mimeType, "MIME-тип", 100),
            SizeBytes = sizeBytes,
            AccessClass = accessClass,
            OriginalFileName = string.IsNullOrWhiteSpace(originalFileName) ? null : Guard.Required(originalFileName, "Имя файла", 255),
            CreatedAtUtc = nowUtc,
        };
    }
}
