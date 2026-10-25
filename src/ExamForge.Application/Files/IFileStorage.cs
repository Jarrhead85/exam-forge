using ExamForge.Domain.Files;

namespace ExamForge.Application.Files;

/// <summary>
/// Приватное файловое хранилище (ADR-004): сохранение с валидацией MIME/сигнатуры/размера,
/// отдача только через авторизованный endpoint. Бинарные данные не попадают в JSON заданий.
/// </summary>
public interface IFileStorage
{
    /// <summary>Сохраняет файл и создаёт FileAsset. Бросает FileStorageException при нарушении лимитов/формата.</summary>
    Task<Domain.Files.FileAsset> SaveAsync(
        Stream content, string originalFileName, string declaredMimeType,
        FileAccessClass accessClass, Guid? ownerTeacherId, CancellationToken ct);

    Task<Stream> OpenReadAsync(Domain.Files.FileAsset asset, CancellationToken ct);
}

public sealed class FileStorageException(string message) : Exception(message);
