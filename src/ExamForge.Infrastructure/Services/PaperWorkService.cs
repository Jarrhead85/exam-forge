using ExamForge.Application.Files;
using ExamForge.Application.StudentFlow;
using ExamForge.Application.TeacherFlow;
using ExamForge.Domain.Assignments;
using ExamForge.Domain.Files;
using ExamForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ExamForge.Infrastructure.Services;

public sealed record PaperScoreInput(int OrderIndex, string Label, decimal Earned, decimal Max, string? Comment);

public sealed record CreatePaperWorkRequest(
    string StudentName, string? StudentGroup, string? Comment,
    IReadOnlyList<PaperScoreInput> Scores, Guid? StudentProfileId);

public sealed record PaperWorkDto(
    Guid PaperWorkId,
    string StudentName,
    string? StudentGroup,
    decimal TotalEarned,
    decimal TotalMax,
    string? Comment,
    DateTimeOffset CreatedAtUtc,
    int ScanCount);

/// <summary>
/// Бумажные работы (ТЗ §13, AC-17): ввод баллов преподавателем, Origin=Paper, приватные сканы,
/// аудит. Ручные баллы не выдаются за автоматические (отдельная сущность от онлайн-попыток).
/// </summary>
public sealed class PaperWorkService(
    ExamForgeDbContext db,
    TimeProvider timeProvider,
    IFileStorage storage)
{
    private DateTimeOffset Now => timeProvider.GetUtcNow();

    public async Task<PaperWork> CreateAsync(TeacherActor actor, Guid assignmentId, CreatePaperWorkRequest request, CancellationToken ct)
    {
        var assignment = await db.Assignments.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == assignmentId, ct)
            ?? throw TeacherFlowErrors.NotFound("Назначение не найдено.");
        if (!actor.IsAdministrator && assignment.CreatedByTeacherId != actor.TeacherId)
            throw TeacherFlowErrors.NotFound("Назначение не найдено.");

        var work = PaperWork.Create(
            assignmentId, request.StudentName, request.StudentGroup, actor.TeacherId,
            [.. request.Scores.Select(s => (s.OrderIndex, s.Label, s.Earned, s.Max, s.Comment))],
            request.Comment, request.StudentProfileId, Now);
        db.PaperWorks.Add(work);

        // AC-17: аудит ввода бумажных баллов — идентификаторы без содержимого ответов.
        db.AuditEvents.Add(Domain.Auditing.AuditEvent.Create(
            "paper-work-entered", actor.TeacherId, "PaperWork", work.Id,
            $"assignment={assignmentId}; scores={request.Scores.Count}; totalEarned={work.TotalEarned:0.###}", Now));
        await db.SaveChangesAsync(ct);
        return work;
    }

    /// <summary>Прикрепить сканы (приватные вложения; лимиты и сигнатуры — в хранилище, ТЗ §13–14).</summary>
    public async Task<IReadOnlyList<FileAsset>> AttachScansAsync(
        TeacherActor actor, Guid paperWorkId,
        IReadOnlyList<(Stream Content, string FileName, string? ContentType)> files, CancellationToken ct)
    {
        var work = await LoadOwnedAsync(actor, paperWorkId, ct);
        var attached = new List<FileAsset>();
        foreach (var (content, fileName, contentType) in files)
        {
            var asset = await storage.SaveAsync(content, fileName, contentType ?? string.Empty, FileAccessClass.Scan, actor.TeacherId, ct);
            db.Set<PaperScan>().Add(PaperScan.Create(work.Id, asset.Id, Now));
            attached.Add(asset);
        }
        db.AuditEvents.Add(Domain.Auditing.AuditEvent.Create(
            "paper-scans-attached", actor.TeacherId, "PaperWork", work.Id,
            $"files={attached.Count}", Now));
        await db.SaveChangesAsync(ct);
        return attached;
    }

    public async Task<IReadOnlyList<PaperWorkDto>> ListAsync(TeacherActor actor, Guid assignmentId, CancellationToken ct)
    {
        var assignment = await db.Assignments.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == assignmentId, ct)
            ?? throw TeacherFlowErrors.NotFound("Назначение не найдено.");
        if (!actor.IsAdministrator && assignment.CreatedByTeacherId != actor.TeacherId)
            throw TeacherFlowErrors.NotFound("Назначение не найдено.");

        var works = await db.PaperWorks.AsNoTracking()
            .Where(w => w.AssignmentId == assignmentId)
            .OrderByDescending(w => w.CreatedAtUtc)
            .ToListAsync(ct);
        var ids = works.Select(w => w.Id).ToList();
        var scanCounts = await db.Set<PaperScan>().AsNoTracking()
            .Where(s => ids.Contains(s.PaperWorkId))
            .GroupBy(s => s.PaperWorkId)
            .Select(g => new { PaperWorkId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PaperWorkId, x => x.Count, ct);

        return [.. works.Select(w => new PaperWorkDto(
            w.Id, w.StudentNameSnapshot, w.StudentGroupSnapshot,
            w.TotalEarned, w.TotalMax, w.Comment, w.CreatedAtUtc,
            scanCounts.GetValueOrDefault(w.Id)))];
    }

    /// <summary>Скан для скачивания: только владелец назначения или администратор (ТЗ §13).</summary>
    public async Task<(FileAsset Asset, Stream Content)> OpenScanAsync(TeacherActor actor, Guid paperWorkId, Guid fileAssetId, CancellationToken ct)
    {
        var work = await LoadOwnedAsync(actor, paperWorkId, ct);
        var scan = await db.Set<PaperScan>().AsNoTracking()
            .SingleOrDefaultAsync(s => s.PaperWorkId == work.Id && s.FileAssetId == fileAssetId, ct)
            ?? throw TeacherFlowErrors.NotFound("Вложение не найдено.");
        var asset = await db.FileAssets.AsNoTracking().SingleAsync(f => f.Id == scan.FileAssetId, ct);
        var content = await storage.OpenReadAsync(asset, ct);
        return (asset, content);
    }

    /// <summary>Баллы бумажной работы (для ведомости): Origin=Paper отображается отдельно (ТЗ §12).</summary>
    public async Task<IReadOnlyList<PaperWorkDto>> ListForGradebookAsync(Guid assignmentId, CancellationToken ct)
        => await ListAsync(new TeacherActor(Guid.Empty, IsAdministrator: true), assignmentId, ct);

    private async Task<PaperWork> LoadOwnedAsync(TeacherActor actor, Guid paperWorkId, CancellationToken ct)
    {
        var work = await db.PaperWorks.AsNoTracking()
            .SingleOrDefaultAsync(w => w.Id == paperWorkId, ct)
            ?? throw TeacherFlowErrors.NotFound("Бумажная работа не найдена.");
        var assignment = await db.Assignments.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == work.AssignmentId, ct)
            ?? throw TeacherFlowErrors.NotFound("Назначение не найдено.");
        if (!actor.IsAdministrator && assignment.CreatedByTeacherId != actor.TeacherId)
            throw TeacherFlowErrors.NotFound("Бумажная работа не найдена.");
        return work;
    }
}
