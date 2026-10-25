using ExamForge.Application.StudentFlow;
using ExamForge.Domain.Access;
using ExamForge.Infrastructure.Persistence;
using ExamForge.Infrastructure.Services;
using ExamForge.Web.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace ExamForge.Web.Controllers;

/// <summary>Обмен кода приглашения на ограниченную студенческую сессию (ТЗ §5.2, §17).</summary>
[ApiController]
public sealed class AccessController(
    StudentFlowService flow,
    InvitationCodeHasher hasher,
    IAntiforgery antiforgery) : ControllerBase
{
    [HttpPost("/api/access/exchange")]
    [AllowAnonymous]
    [EnableRateLimiting("access-exchange")]
    public async Task<IActionResult> Exchange([FromBody] ExchangeRequest request, CancellationToken ct)
    {
        var (invitation, assignment) = await flow.ValidateExchangeAsync(hasher, request.Code, ct);
        await HttpContext.SignInStudentAsync(StudentAuth.Scheme, StudentAuth.CreatePrincipal(invitation));
        return Ok(new ExchangeResponse(assignment.Id, invitation.Id));
    }

    /// <summary>Вход по общей ссылке (ТЗ §5.1): ФИО+группа перед стартом; без доступа к чужой истории.</summary>
    [HttpPost("/api/access/common-link")]
    [AllowAnonymous]
    [EnableRateLimiting("access-exchange")]
    public async Task<IActionResult> CommonLink([FromBody] CommonLinkRequest request, CancellationToken ct)
    {
        var (profile, assignment) = await flow.EnterByCommonLinkAsync(
            request.Code, request.LastName, request.FirstName, request.MiddleName, request.Group, ct);
        await HttpContext.SignInStudentAsync(StudentAuth.Scheme,
            StudentAuth.CreateCommonLinkPrincipal(assignment.Id, profile.Id, request.Group));
        return Ok(new ExchangeResponse(assignment.Id, profile.Id));
    }

    /// <summary>Вход постоянного аккаунта студента (ТЗ §5.3).</summary>
    [HttpPost("/api/access/login")]
    [AllowAnonymous]
    [EnableRateLimiting("access-exchange")]
    public async Task<IActionResult> Login([FromBody] StudentLoginRequest request, CancellationToken ct)
    {
        var account = await flow.ValidateStudentLoginAsync(request.Login?.Trim() ?? "", request.Password ?? "", ct);
        if (account is null)
            return UnauthorizedProblem("Неверный логин или пароль.");
        await HttpContext.SignInStudentAsync(StudentAuth.Scheme, StudentAuth.CreateAccountPrincipal(account));
        return Ok(new { account.Login });
    }

    private IActionResult UnauthorizedProblem(string message) =>
        Problem(statusCode: 401, title: message, type: "https://tools.ietf.org/html/rfc9110#section-15.5.2");

    /// <summary>
    /// Antiforgery-токен студента: cookie + заголовок X-XSRF-TOKEN на изменяющих запросах.
    /// Выдаётся ПОСЛЕ обмена: токен привязан к студенческой principal (ADR-002 п.5).
    /// </summary>
    [HttpGet("/api/access/antiforgery")]
    [Authorize(AuthenticationSchemes = StudentAuth.Scheme)]
    public IActionResult Antiforgery()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new { requestToken = tokens.RequestToken });
    }

    /// <summary>Antiforgery-токен преподавателя (для multipart-загрузок, привязан к teacher principal).</summary>
    [HttpGet("/api/access/antiforgery-teacher")]
    [Authorize]
    public IActionResult AntiforgeryTeacher()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new { requestToken = tokens.RequestToken });
    }
}

[ApiController]
[Authorize(AuthenticationSchemes = StudentAuth.Scheme)]
public sealed class StudentApiController(
    ExamForgeDbContext db,
    TimeProvider timeProvider,
    Infrastructure.Services.MaterialsService materials) : ControllerBase
{
    /// <summary>Лекции, доступные студенту: Published + grants по профилю/группе (ТЗ §14).</summary>
    [HttpGet("/api/student/materials")]
    public async Task<IActionResult> MyMaterials(CancellationToken ct)
    {
        var identity = User.StudentIdentity()
            ?? throw StudentFlowException.NotFound();
        if (identity.StudentProfileId is not { } profileId)
            return Ok(Array.Empty<object>());
        return Ok(await materials.ListForStudentAsync(profileId, ct));
    }

    /// <summary>Скачивание материала студентом с grant; публичных ссылок нет (ТЗ §14).</summary>
    [HttpGet("/api/materials/{materialId:guid}/download")]
    public async Task<IActionResult> DownloadMaterial(Guid materialId, CancellationToken ct)
    {
        var identity = User.StudentIdentity()
            ?? throw StudentFlowException.NotFound();
        var (material, asset, content) = await materials.OpenForDownloadAsync(
            null, identity.StudentProfileId, materialId, ct);
        return File(content, asset.MimeType, asset.OriginalFileName ?? $"{material.Title}.pdf");
    }

    [HttpGet("/api/student/assignments")]
    public async Task<IActionResult> MyAssignments(CancellationToken ct)
    {
        var identity = User.StudentIdentity()
            ?? throw StudentFlowException.NotFound();
        var now = timeProvider.GetUtcNow();

        // Сессия кода/ссылки видит своё назначение; аккаунт — все, куда приглашён профиль (ТЗ §5.3).
        var assignmentIds = identity.AssignmentId is { } single
            ? new List<Guid> { single }
            : identity.StudentProfileId is { } profileId
                ? await db.Invitations.AsNoTracking()
                    .Where(i => i.StudentProfileId == profileId && i.Status == Domain.Assignments.InvitationStatus.Active)
                    .Select(i => i.AssignmentId).Distinct().ToListAsync(ct)
                : [];

        var rows = await db.Assignments.AsNoTracking()
            .Where(a => assignmentIds.Contains(a.Id))
            .Join(db.TestVersions.AsNoTracking(),
                a => a.TestVersionId, v => v.Id, (a, v) => new { a, v.Title })
            .ToListAsync(ct);

        var dtos = new List<StudentAssignmentDto>();
        foreach (var row in rows)
        {
            var attempts = await db.Attempts.AsNoTracking()
                .Where(a => a.AssignmentId == row.a.Id && a.StudentProfileId == identity.StudentProfileId)
                .ToListAsync(ct);
            var active = attempts.FirstOrDefault(a =>
                a.State == Domain.Attempts.AttemptState.Created || a.State == Domain.Attempts.AttemptState.InProgress);
            // История своих результатов: баллы/процент без ключей (ТЗ §9, §12).
            var briefs = attempts
                .Where(a => a.State != Domain.Attempts.AttemptState.Created)
                .OrderBy(a => a.AttemptNumber)
                .Select(a => new StudentAttemptBriefDto(
                    a.Id, a.AttemptNumber, a.State.ToString(),
                    a.ScoreEarnedPoints, a.ScoreMaxPoints, a.Score?.RoundedPercent, a.CompletedAtUtc))
                .ToList();
            dtos.Add(new StudentAssignmentDto(
                row.a.Id, row.Title, row.a.Mode.ToString(), row.a.OpensAtUtc, row.a.ClosesAtUtc,
                row.a.MaxAttempts,
                attempts.Count(a => a.State != Domain.Attempts.AttemptState.Cancelled),
                active is not null, active?.Id, briefs));
        }
        return Ok(dtos);
    }
}
