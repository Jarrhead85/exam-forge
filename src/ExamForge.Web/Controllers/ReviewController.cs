using ExamForge.Application.TeacherFlow;
using ExamForge.Infrastructure.Identity;
using ExamForge.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace ExamForge.Web.Controllers;

/// <summary>
/// Ручная проверка и ведомость (ТЗ §12). Ключи и критерии — только преподавателю;
/// CSV с защитой от injection (=, +, -, @); ведомость не по открытой ссылке.
/// </summary>
[ApiController]
[Authorize]
public sealed class ReviewController(
    ManualReviewService reviews,
    ResultsService results,
    UserManager<TeacherUser> userManager) : ControllerBase
{
    private async Task<TeacherActor> ActorAsync()
    {
        var user = await userManager.GetUserAsync(User)
            ?? throw TeacherFlowErrors.NotFound("Пользователь не найден.");
        return new TeacherActor(user.Id, await userManager.IsInRoleAsync(user, AppRoles.Administrator));
    }

    [HttpGet("/api/assignments/{assignmentId:guid}/reviews")]
    public async Task<IActionResult> Queue(Guid assignmentId, [FromQuery] string? filter, CancellationToken ct)
        => Ok(await reviews.GetQueueAsync(await ActorAsync(), assignmentId, filter, ct));

    [HttpGet("/api/reviews/{attemptId:guid}")]
    public async Task<IActionResult> GetAttempt(Guid attemptId, CancellationToken ct)
        => Ok(await reviews.GetAttemptAsync(await ActorAsync(), attemptId, ct));

    [HttpPost("/api/reviews/{attemptId:guid}/grade")]
    public async Task<IActionResult> Grade(Guid attemptId, [FromBody] GradeRequest request, CancellationToken ct)
        => Ok(await reviews.GradeAsync(await ActorAsync(), attemptId, request.Scores, ct));

    [HttpPost("/api/reviews/{attemptId:guid}/override")]
    public async Task<IActionResult> Override(Guid attemptId, [FromBody] CriterionScoreDto score, CancellationToken ct)
        => Ok(await reviews.OverrideAsync(await ActorAsync(), attemptId, score, ct));

    public sealed record GradeRequest(IReadOnlyList<CriterionScoreDto> Scores);

    /// <summary>Ведомость CSV: все приглашённые студенты и их попытки + лучшая (Finalized); UTF-8 BOM для Excel.</summary>
    [HttpGet("/api/assignments/{assignmentId:guid}/gradebook.csv")]
    public async Task<IActionResult> GradebookCsv(Guid assignmentId, CancellationToken ct)
    {
        var bytes = await results.GradebookCsvAsync(await ActorAsync(), assignmentId, ct);
        return File(bytes, "text/csv; charset=utf-8", $"gradebook-{assignmentId:N}.csv");
    }
}
