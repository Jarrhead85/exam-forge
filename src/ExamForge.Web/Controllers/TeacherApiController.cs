using ExamForge.Application.TeacherFlow;
using ExamForge.Infrastructure.Identity;
using ExamForge.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ExamForge.Web.Controllers;

/// <summary>
/// Teacher-API назначений (ТЗ §17). Cookie ef.teacher (SameSite=Lax) защищает не-GET от CSRF;
/// проверка владения объектом — в сервисе, чужой объект даёт 404.
/// </summary>
[ApiController]
[Authorize]
[Route("/api")]
public sealed class TeacherApiController(
    TeacherAssignmentsService service,
    Infrastructure.Services.TestVersionService testVersions,
    Infrastructure.Services.TeacherQuestionsService questions,
    Infrastructure.Persistence.ExamForgeDbContext db,
    UserManager<TeacherUser> userManager) : ControllerBase
{
    private Infrastructure.Persistence.ExamForgeDbContext Db => db;
    private async Task<TeacherActor> ActorAsync()
    {
        var user = await userManager.GetUserAsync(User)
            ?? throw TeacherFlowErrors.NotFound("Пользователь не найден.");
        var isAdmin = await userManager.IsInRoleAsync(user, AppRoles.Administrator);
        return new TeacherActor(user.Id, isAdmin);
    }

    public sealed record GenerateVariantsRequest(int Count, bool ShuffleQuestions);

    [HttpPost("test-versions/{testVersionId:guid}/variants")]
    public async Task<IActionResult> GenerateVariants(Guid testVersionId, [FromBody] GenerateVariantsRequest request, CancellationToken ct)
    {
        var variants = await testVersions.GenerateVariantsAsync(
            await ActorAsync(), testVersionId, request.Count, request.ShuffleQuestions, ct);
        return Ok(variants.Select(v => new { v.Id, v.VariantNumber, questionCount = v.QuestionOrder.Count }));
    }

    [HttpGet("test-versions/{testVersionId:guid}/variants")]
    public async Task<IActionResult> ListVariants(Guid testVersionId, CancellationToken ct)
    {
        var variants = await testVersions.ListVariantsAsync(await ActorAsync(), testVersionId, ct);
        return Ok(variants.Select(v => new { v.Id, v.VariantNumber, questionCount = v.QuestionOrder.Count }));
    }

    // ---------- Банк заданий (§7) и конструктор тестов (§8) ----------

    [HttpPost("questions")]
    public async Task<IActionResult> CreateQuestion([FromBody] QuestionPayload payload, CancellationToken ct)
    {
        var actor = await ActorAsync();
        var definition = payload.ToDefinition();
        var version = await questions.CreateDraftAsync(actor, payload.SubjectId, definition, ct);
        return Ok(new { questionId = version.QuestionId, versionNumber = version.VersionNumber });
    }

    [HttpPut("questions/{questionId:guid}")]
    public async Task<IActionResult> EditQuestion(Guid questionId, [FromBody] QuestionPayload payload, CancellationToken ct)
    {
        var version = await questions.EditAsync(await ActorAsync(), questionId, payload.ToDefinition(), ct);
        return Ok(new { questionId, versionNumber = version.VersionNumber });
    }

    [HttpPost("questions/{questionId:guid}/publish")]
    public async Task<IActionResult> PublishQuestion(Guid questionId, CancellationToken ct)
    {
        var version = await questions.PublishAsync(await ActorAsync(), questionId, ct);
        return Ok(new { published = true, versionNumber = version.VersionNumber });
    }

    [HttpGet("questions")]
    public async Task<IActionResult> ListQuestions(CancellationToken ct)
        => Ok(await questions.ListAsync(await ActorAsync(), ct));

    [HttpPost("tests")]
    public async Task<IActionResult> CreateTest([FromBody] CreateTestRequest request, CancellationToken ct)
    {
        var test = await TeacherTestsService.CreateTestAsync(Db, await ActorAsync(), request.Title, request.SubjectId, ct);
        return Ok(new { testId = test.Id });
    }

    public sealed record CreateTestRequest(string Title, Guid? SubjectId);

    [HttpGet("tests")]
    public async Task<IActionResult> ListTests(CancellationToken ct)
        => Ok(await TeacherTestsService.ListAsync(Db, await ActorAsync(), ct));

    public sealed record CreateVersionRequest(Guid TestId, string Title, IReadOnlyList<Guid> QuestionVersionIds);

    [HttpPost("test-versions")]
    public async Task<IActionResult> CreateVersion([FromBody] CreateVersionRequest request, CancellationToken ct)
    {
        var version = await TeacherTestsService.CreateVersionAsync(
            Db, await ActorAsync(), request.TestId, request.Title, request.QuestionVersionIds, ct);
        return Ok(new { versionId = version.Id, versionNumber = version.VersionNumber });
    }

    [HttpPost("test-versions/{versionId:guid}/publish")]
    public async Task<IActionResult> PublishVersion(Guid versionId, CancellationToken ct)
    {
        await TeacherTestsService.PublishVersionAsync(Db, await ActorAsync(), versionId, ct);
        return Ok(new { published = true });
    }

    public sealed record BlueprintPreviewRequest(IReadOnlyList<BlueprintLineRequest> Lines, string Seed);

    [HttpPost("tests/blueprint-preview")]
    public async Task<IActionResult> BlueprintPreview([FromBody] BlueprintPreviewRequest request, CancellationToken ct)
        => Ok(await TeacherTestsService.PreviewBlueprintAsync(Db, await ActorAsync(), request.Lines, request.Seed, ct));

    [HttpGet("assignments")]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await service.ListAssignmentsAsync(await ActorAsync(), ct));

    [HttpPost("assignments")]
    public async Task<IActionResult> Create([FromBody] CreateAssignmentRequest request, CancellationToken ct)
    {
        var assignment = await service.CreateAssignmentAsync(await ActorAsync(), request, ct);
        return Created($"/api/assignments/{assignment.Id}", new { assignmentId = assignment.Id });
    }

    [HttpGet("assignments/{assignmentId:guid}")]
    public async Task<IActionResult> Get(Guid assignmentId, CancellationToken ct)
        => Ok(await service.GetAssignmentAsync(await ActorAsync(), assignmentId, ct));

    [HttpPost("assignments/{assignmentId:guid}/students")]
    public async Task<IActionResult> AddStudent(Guid assignmentId, [FromBody] AddStudentRequest request, CancellationToken ct)
        => Ok(await service.AddStudentWithInvitationAsync(await ActorAsync(), assignmentId, request, ct));

    [HttpPost("assignments/{assignmentId:guid}/invitations")]
    public async Task<IActionResult> CreateInvitation(Guid assignmentId, [FromBody] CreateInvitationRequest request, CancellationToken ct)
        => Ok(await service.CreateInvitationForStudentAsync(await ActorAsync(), assignmentId, request.StudentProfileId, ct));

    [HttpPost("invitations/{invitationId:guid}/revoke")]
    public async Task<IActionResult> RevokeInvitation(Guid invitationId, CancellationToken ct)
    {
        await service.RevokeInvitationAsync(await ActorAsync(), invitationId, ct);
        return Ok(new { revoked = true });
    }

    /// <summary>Перевыпуск: старый код отзывается, новый показывается один раз (ТЗ §5.2: замена).</summary>
    [HttpPost("invitations/{invitationId:guid}/reissue")]
    public async Task<IActionResult> ReissueInvitation(Guid invitationId, CancellationToken ct)
        => Ok(await service.ReissueInvitationAsync(await ActorAsync(), invitationId, ct));

    [HttpPost("assignments/{assignmentId:guid}/grants")]
    public async Task<IActionResult> CreateGrant(Guid assignmentId, [FromBody] ExtraGrantRequest request, CancellationToken ct)
        => Ok(await service.CreateExtraGrantAsync(await ActorAsync(), assignmentId, request, ct));

    [HttpPost("assignments/{assignmentId:guid}/common-link")]
    public async Task<IActionResult> EnableCommonLink(Guid assignmentId, CancellationToken ct)
        => Ok(new { code = await service.EnableCommonLinkAsync(await ActorAsync(), assignmentId, ct) });

    [HttpDelete("assignments/{assignmentId:guid}/common-link")]
    public async Task<IActionResult> DisableCommonLink(Guid assignmentId, CancellationToken ct)
    {
        await service.DisableCommonLinkAsync(await ActorAsync(), assignmentId, ct);
        return Ok(new { disabled = true });
    }

    public sealed record StudentAccountDto(Guid AccountId, Guid StudentProfileId, string Login, string OneTimePassword);

    [HttpPost("assignments/{assignmentId:guid}/student-accounts")]
    public async Task<IActionResult> CreateStudentAccount(Guid assignmentId, [FromBody] CreateAccountRequest request, CancellationToken ct)
    {
        var (account, oneTimePassword) = await service.CreateStudentAccountAsync(
            await ActorAsync(), assignmentId, request.StudentProfileId, ct);
        return Ok(new StudentAccountDto(account.Id, account.StudentProfileId, account.Login, oneTimePassword));
    }

    [HttpPost("student-accounts/{studentAccountId:guid}/reset-password")]
    public async Task<IActionResult> ResetPassword(Guid studentAccountId, [FromQuery] Guid assignmentId, CancellationToken ct)
        => Ok(new { oneTimePassword = await service.ResetStudentAccountPasswordAsync(
            await ActorAsync(), assignmentId, studentAccountId, ct) });

    public sealed record CreateAccountRequest(Guid StudentProfileId);

    public sealed record CreateInvitationRequest(Guid StudentProfileId);
}
