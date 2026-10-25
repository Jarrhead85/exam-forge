using ExamForge.Application.TeacherFlow;
using ExamForge.Infrastructure.Identity;
using ExamForge.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace ExamForge.Web.Controllers;

/// <summary>
/// Бумажные работы и материалы (ТЗ §13–14). Сканы и лекции отдаются только авторизованным
/// через эти endpoint'ы — публичных ссылок на файлы нет.
/// </summary>
[ApiController]
[Authorize]
public sealed class PaperMaterialsController(
    PaperWorkService paperWorks,
    MaterialsService materials,
    UserManager<TeacherUser> userManager) : ControllerBase
{
    private async Task<TeacherActor> ActorAsync()
    {
        var user = await userManager.GetUserAsync(User)
            ?? throw TeacherFlowErrors.NotFound("Пользователь не найден.");
        return new TeacherActor(user.Id, await userManager.IsInRoleAsync(user, AppRoles.Administrator));
    }

    // ---------- Бумажные работы (§13) ----------

    [HttpPost("/api/assignments/{assignmentId:guid}/paper-works")]
    public async Task<IActionResult> CreatePaperWork(Guid assignmentId, [FromBody] CreatePaperWorkRequest request, CancellationToken ct)
    {
        var work = await paperWorks.CreateAsync(await ActorAsync(), assignmentId, request, ct);
        return Ok(new { paperWorkId = work.Id, totalEarned = work.TotalEarned, totalMax = work.TotalMax });
    }

    [HttpGet("/api/assignments/{assignmentId:guid}/paper-works")]
    public async Task<IActionResult> ListPaperWorks(Guid assignmentId, CancellationToken ct)
        => Ok(await paperWorks.ListAsync(await ActorAsync(), assignmentId, ct));

    [HttpPost("/api/paper-works/{paperWorkId:guid}/scans")]
    [RequestSizeLimit(55 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 55 * 1024 * 1024)]
    public async Task<IActionResult> AttachScans(Guid paperWorkId, IFormFileCollection files, CancellationToken ct)
    {
        var opened = new List<(Stream Content, string FileName, string? ContentType)>();
        try
        {
            foreach (var file in files)
                opened.Add((file.OpenReadStream(), file.FileName, file.ContentType));
            var assets = await paperWorks.AttachScansAsync(await ActorAsync(), paperWorkId, opened, ct);
            return Ok(assets.Select(a => new { a.Id, a.MimeType, a.SizeBytes }));
        }
        finally
        {
            foreach (var (content, _, _) in opened)
                content.Dispose();
        }
    }

    /// <summary>Приватная отдача скана: владелец назначения или администратор (ТЗ §13).</summary>
    [HttpGet("/api/paper-works/{paperWorkId:guid}/scans/{fileAssetId:guid}")]
    public async Task<IActionResult> DownloadScan(Guid paperWorkId, Guid fileAssetId, CancellationToken ct)
    {
        var (asset, content) = await paperWorks.OpenScanAsync(await ActorAsync(), paperWorkId, fileAssetId, ct);
        return File(content, asset.MimeType, asset.OriginalFileName ?? "scan", enableRangeProcessing: false);
    }

    // ---------- Материалы (§14) ----------

    [HttpPost("/api/materials")]
    [RequestSizeLimit(30 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 30 * 1024 * 1024)]
    public async Task<IActionResult> UploadMaterial([FromForm] UploadMaterialForm form, IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { title = "Файл PDF обязателен." });
        await using var stream = file.OpenReadStream();
        var material = await materials.UploadPdfAsync(await ActorAsync(), stream, file.FileName, file.ContentType,
            form.SubjectId, form.TopicId, form.Title, form.Description, form.OrderIndex, form.SourceNote, ct);
        return Ok(new { materialId = material.Id, status = material.Status.ToString() });
    }

    public sealed record UploadMaterialForm(
        Guid SubjectId, Guid? TopicId, string Title, string? Description, int OrderIndex, string? SourceNote);

    [HttpGet("/api/materials")]
    public async Task<IActionResult> ListMaterials(CancellationToken ct)
        => Ok(await materials.ListForTeacherAsync(await ActorAsync(), ct));

    [HttpPost("/api/materials/{materialId:guid}/publish")]
    public async Task<IActionResult> Publish(Guid materialId, CancellationToken ct)
    {
        await materials.PublishAsync(await ActorAsync(), materialId, ct);
        return Ok(new { published = true });
    }

    [HttpPost("/api/materials/{materialId:guid}/archive")]
    public async Task<IActionResult> Archive(Guid materialId, CancellationToken ct)
    {
        await materials.ArchiveAsync(await ActorAsync(), materialId, ct);
        return Ok(new { archived = true });
    }

    public sealed record GrantRequest(Guid? GroupId, Guid? StudentProfileId);

    [HttpPost("/api/materials/{materialId:guid}/grants")]
    public async Task<IActionResult> Grant(Guid materialId, [FromBody] GrantRequest request, CancellationToken ct)
    {
        var grant = await materials.GrantAsync(await ActorAsync(), materialId, request.GroupId, request.StudentProfileId, ct);
        return Ok(new { grantId = grant.Id });
    }

    /// <summary>Скачивание материала преподавателем-владельцем/администратором (ТЗ §14).</summary>
    [HttpGet("/api/materials/{materialId:guid}/file")]
    public async Task<IActionResult> DownloadByTeacher(Guid materialId, CancellationToken ct)
    {
        var (material, asset, content) = await materials.OpenForDownloadAsync(await ActorAsync(), null, materialId, ct);
        return File(content, asset.MimeType, asset.OriginalFileName ?? $"{material.Title}.pdf");
    }
}
