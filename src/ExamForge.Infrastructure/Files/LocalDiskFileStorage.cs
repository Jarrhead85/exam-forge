using System.Security.Cryptography;
using ExamForge.Application.Files;
using ExamForge.Domain.Files;
using ExamForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ExamForge.Infrastructure.Files;

/// <summary>
/// Дисковое хранилище (ADR-004): ключ uploads/{accessClass}/{guid}/{name}; MIME+сигнатура+
/// расширение из allowlist; лимиты по классам (ТЗ §14: PDF 25 MiB, изображение 10 MiB,
/// скан-пакет 50 MiB на работу). EXIF-удаление не выполняется: политика сканов не утверждена (D-05).
/// </summary>
public sealed class LocalDiskFileStorage(ExamForgeDbContext db, ILogger<LocalDiskFileStorage> logger) : IFileStorage
{
    private static readonly Dictionary<string, (string Mime, byte[] Signature)> Allowed = new()
    {
        [".pdf"] = ("application/pdf", [0x25, 0x50, 0x44, 0x46]),                 // %PDF
        [".jpg"] = ("image/jpeg", [0xFF, 0xD8, 0xFF]),
        [".jpeg"] = ("image/jpeg", [0xFF, 0xD8, 0xFF]),
        [".png"] = ("image/png", [0x89, 0x50, 0x4E, 0x47]),                        // ‰PNG
    };

    private static readonly Dictionary<FileAccessClass, long> SizeLimits = new()
    {
        [FileAccessClass.Lecture] = 25 * 1024 * 1024,      // PDF-лекция
        [FileAccessClass.QuestionMedia] = 10 * 1024 * 1024,
        [FileAccessClass.Scan] = 50 * 1024 * 1024,          // скан-пакет на работу
        [FileAccessClass.PrintArtifact] = 25 * 1024 * 1024,
        [FileAccessClass.ImportSource] = 25 * 1024 * 1024,
    };

    private readonly string _root = ResolveRoot();

    private static string ResolveRoot()
    {
        var configured = Environment.GetEnvironmentVariable("EXAMFORGE_STORAGE_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);
        return Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".storage");
    }

    public async Task<FileAsset> SaveAsync(
        Stream content, string originalFileName, string declaredMimeType,
        FileAccessClass accessClass, Guid? ownerTeacherId, CancellationToken ct)
    {
        var extension = Path.GetExtension(originalFileName ?? string.Empty).ToLowerInvariant();
        if (!Allowed.TryGetValue(extension, out var allowed))
            throw new FileStorageException($"Тип файла «{extension}» не разрешён. Допустимы: PDF, JPEG, PNG.");

        var limit = SizeLimits[accessClass];
        var id = Guid.NewGuid();
        var safeName = (Path.GetFileNameWithoutExtension(originalFileName) is { Length: > 0 } name ? name : "file")
            .Replace("..", "_", StringComparison.Ordinal);
        var key = Path.Combine("uploads", accessClass.ToString().ToLowerInvariant(), id.ToString(), safeName + extension)
            .Replace('\\', '/');
        var fullPath = Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        byte[] hash;
        long size;
        using (var target = File.Create(fullPath))
        {
            // проверяем сигнатуру до копирования остального потока
            var head = new byte[4];
            var read = await FillHeadAsync(content, head, ct);
            if (read < allowed.Signature.Length || !head.AsSpan(0, allowed.Signature.Length).SequenceEqual(allowed.Signature))
                throw new FileStorageException("Содержимое файла не соответствует заявленному типу.");
            await target.WriteAsync(head.AsMemory(0, read), ct);

            if (declaredMimeType is { Length: > 0 } && !string.Equals(declaredMimeType, allowed.Mime, StringComparison.OrdinalIgnoreCase))
                logger.LogInformation("Заявлен MIME {Declared}, сохранён как {Actual}", declaredMimeType, allowed.Mime);

            await content.CopyToAsync(target, ct);
            await target.FlushAsync(ct);
            size = target.Length;
            if (size > limit)
            {
                target.Close();
                File.Delete(fullPath);
                throw new FileStorageException($"Файл больше лимита {limit / (1024 * 1024)} MiB для этого назначения.");
            }
            // хеш по записанному потоку (Windows блокирует параллельное открытие того же файла)
            target.Position = 0;
            hash = await SHA256.HashDataAsync(target, ct);
        }

        var asset = FileAsset.Create(key, Convert.ToHexString(hash), allowed.Mime, size, accessClass,
            DateTimeOffset.UtcNow, ownerTeacherId, originalFileName);
        db.FileAssets.Add(asset);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Сохранён файл {FileId} класс {AccessClass}, {Size} байт", asset.Id, accessClass, size);
        return asset;
    }

    private static async Task<int> FillHeadAsync(Stream source, byte[] head, CancellationToken ct)
    {
        var total = 0;
        while (total < head.Length)
        {
            var read = await source.ReadAsync(head.AsMemory(total), ct);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    public Task<Stream> OpenReadAsync(FileAsset asset, CancellationToken ct)
    {
        var fullPath = Path.Combine(_root, asset.StorageKey.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
            throw new FileStorageException("Файл не найден в хранилище.");
        return Task.FromResult<Stream>(File.OpenRead(fullPath));
    }
}
