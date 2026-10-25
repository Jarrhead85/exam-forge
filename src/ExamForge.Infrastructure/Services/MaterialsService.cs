using ExamForge.Application.Files;
using ExamForge.Application.StudentFlow;
using ExamForge.Application.TeacherFlow;
using ExamForge.Domain.Files;
using ExamForge.Domain.Materials;
using ExamForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ExamForge.Infrastructure.Services;

public sealed record MaterialDto(
    Guid MaterialId,
    string Title,
    string? Description,
    string Kind,
    string Status,
    int OrderIndex,
    int GrantCount,
    DateTimeOffset? PublishedAtUtc,
    long FileSizeBytes);

/// <summary>
/// Лекционные материалы (ТЗ §14): загрузка PDF, публикация/архив, grants группам и студентам,
/// скачивание только авторизованным и по праву. Публичные ссылки на файлы запрещены.
/// </summary>
public sealed class MaterialsService(ExamForgeDbContext db, TimeProvider timeProvider, IFileStorage storage)
{
    private DateTimeOffset Now => timeProvider.GetUtcNow();

    public async Task<Material> UploadPdfAsync(
        TeacherActor actor, Stream content, string fileName, string contentType,
        Guid subjectId, Guid? topicId, string title, string? description, int orderIndex, string? sourceNote,
        CancellationToken ct)
    {
        var asset = await storage.SaveAsync(content, fileName, contentType, FileAccessClass.Lecture, actor.TeacherId, ct);
        var material = Material.CreatePdf(subjectId, topicId, title, description, asset.Id, actor.TeacherId, orderIndex, sourceNote, Now);
        db.Materials.Add(material);
        await db.SaveChangesAsync(ct);
        return material;
    }

    public async Task PublishAsync(TeacherActor actor, Guid materialId, CancellationToken ct)
    {
        var material = await LoadOwnedAsync(actor, materialId, ct);
        material.Publish(Now);
        await db.SaveChangesAsync(ct);
    }

    public async Task ArchiveAsync(TeacherActor actor, Guid materialId, CancellationToken ct)
    {
        var material = await LoadOwnedAsync(actor, materialId, ct);
        material.Archive(Now);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Правка названия/описания материала: файл, права и статусы не затрагиваются.</summary>
    public async Task RenameAsync(TeacherActor actor, Guid materialId, string title, string? description, CancellationToken ct)
    {
        var material = await LoadOwnedAsync(actor, materialId, ct);
        material.Rename(title, description);
        await db.SaveChangesAsync(ct);
    }

    public async Task<MaterialGrant> GrantAsync(
        TeacherActor actor, Guid materialId, Guid? groupId, Guid? studentProfileId, CancellationToken ct)
    {
        var material = await LoadOwnedAsync(actor, materialId, ct);
        var grant = groupId is { } g
            ? MaterialGrant.ForGroup(material.Id, g, actor.TeacherId, Now)
            : studentProfileId is { } p
                ? MaterialGrant.ForStudent(material.Id, p, actor.TeacherId, Now)
                : throw TeacherFlowErrors.Conflict("bad_request", "Укажите группу или студента.");
        db.MaterialGrants.Add(grant);
        await db.SaveChangesAsync(ct);
        return grant;
    }

    public async Task<IReadOnlyList<MaterialDto>> ListForTeacherAsync(TeacherActor actor, CancellationToken ct)
    {
        var query = db.Materials.AsNoTracking().AsQueryable();
        if (!actor.IsAdministrator)
            query = query.Where(m => m.AuthorTeacherId == actor.TeacherId);
        var materials = await query.OrderBy(m => m.SubjectId).ThenBy(m => m.OrderIndex).ToListAsync(ct);
        var ids = materials.Select(m => m.Id).ToList();
        var grantCounts = await db.MaterialGrants.AsNoTracking()
            .Where(g => ids.Contains(g.MaterialId))
            .GroupBy(g => g.MaterialId)
            .Select(g => new { MaterialId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.MaterialId, x => x.Count, ct);
        var sizes = await db.FileAssets.AsNoTracking()
            .Where(f => materials.Select(m => m.FileAssetId).Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, f => f.SizeBytes, ct);

        return [.. materials.Select(m => new MaterialDto(
            m.Id, m.Title, m.Description, m.Kind.ToString(), m.Status.ToString(),
            m.OrderIndex, grantCounts.GetValueOrDefault(m.Id), m.PublishedAtUtc,
            sizes.GetValueOrDefault(m.FileAssetId)))];
    }

    /// <summary>Материалы студента: Published + прямой grant или grant группе с членством (ТЗ §14).</summary>
    public async Task<IReadOnlyList<StudentMaterialDto>> ListForStudentAsync(Guid studentProfileId, CancellationToken ct)
    {
        var groupIds = await db.GroupMemberships.AsNoTracking()
            .Where(m => m.StudentProfileId == studentProfileId)
            .Select(m => m.GroupId)
            .ToListAsync(ct);

        var materials = await db.Materials.AsNoTracking()
            .Where(m => m.Status == MaterialStatus.Published
                        && (db.MaterialGrants.Any(g => g.MaterialId == m.Id && g.StudentProfileId == studentProfileId)
                            || db.MaterialGrants.Any(g => g.MaterialId == m.Id && g.GroupId != null && groupIds.Contains(g.GroupId.Value))))
            .OrderBy(m => m.SubjectId).ThenBy(m => m.OrderIndex)
            .ToListAsync(ct);
        var sizes = await db.FileAssets.AsNoTracking()
            .Where(f => materials.Select(m => m.FileAssetId).Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, f => f.SizeBytes, ct);

        return [.. materials.Select(m => new StudentMaterialDto(
            m.Id, m.Title, m.Description, m.OrderIndex, sizes.GetValueOrDefault(m.FileAssetId)))];
    }

    /// <summary>Скачивание: владелец/администратор или студент с grant на Published (ТЗ §14).</summary>
    public async Task<(Material Material, FileAsset Asset, Stream Content)> OpenForDownloadAsync(
        TeacherActor? teacher, Guid? studentProfileId, Guid materialId, CancellationToken ct)
    {
        var material = await db.Materials.AsNoTracking()
            .SingleOrDefaultAsync(m => m.Id == materialId, ct)
            ?? throw TeacherFlowErrors.NotFound("Материал не найден.");

        var allowed = false;
        if (teacher is { } actor)
        {
            allowed = actor.IsAdministrator || material.AuthorTeacherId == actor.TeacherId;
        }
        else if (studentProfileId is { } profileId && material.Status == MaterialStatus.Published)
        {
            var groupIds = await db.GroupMemberships.AsNoTracking()
                .Where(m => m.StudentProfileId == profileId)
                .Select(m => m.GroupId)
                .ToListAsync(ct);
            allowed = await db.MaterialGrants.AsNoTracking().AnyAsync(
                g => g.MaterialId == materialId
                     && (g.StudentProfileId == profileId
                         || (g.GroupId != null && groupIds.Contains(g.GroupId.Value))), ct);
        }

        if (!allowed)
            throw TeacherFlowErrors.NotFound("Материал не найден."); // не раскрываем существование

        var asset = await db.FileAssets.AsNoTracking().SingleAsync(f => f.Id == material.FileAssetId, ct);
        var content = await storage.OpenReadAsync(asset, ct);
        return (material, asset, content);
    }

    private async Task<Material> LoadOwnedAsync(TeacherActor actor, Guid materialId, CancellationToken ct)
    {
        var material = await db.Materials.SingleOrDefaultAsync(m => m.Id == materialId, ct)
            ?? throw TeacherFlowErrors.NotFound("Материал не найден.");
        if (!actor.IsAdministrator && material.AuthorTeacherId != actor.TeacherId)
            throw TeacherFlowErrors.NotFound("Материал не найден.");
        return material;
    }
}
