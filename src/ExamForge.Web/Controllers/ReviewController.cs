using System.Text;
using ExamForge.Application.TeacherFlow;
using ExamForge.Domain.Attempts;
using ExamForge.Infrastructure.Identity;
using ExamForge.Infrastructure.Persistence;
using ExamForge.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ExamForge.Web.Controllers;

/// <summary>
/// Ручная проверка и ведомость (ТЗ §12). Ключи и критерии — только преподавателю;
/// CSV с защитой от injection (=, +, -, @); ведомость не по открытой ссылке.
/// </summary>
[ApiController]
[Authorize]
public sealed class ReviewController(
    ManualReviewService reviews,
    TeacherAssignmentsService assignments,
    ExamForgeDbContext db,
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
        var actor = await ActorAsync();
        // проверка владения через существующий сервис
        _ = await assignments.GetAssignmentAsync(actor, assignmentId, ct);

        var attempts = await db.Attempts.AsNoTracking()
            .Where(a => a.AssignmentId == assignmentId)
            .OrderBy(a => a.StudentNameSnapshot).ThenBy(a => a.AttemptNumber)
            .ToListAsync(ct);
        var best = Domain.Attempts.AttemptRules.SelectBest(attempts);

        // приглашённые студенты без попыток тоже входят в ведомость (ТЗ §12: ведомость группы)
        var invitedStudents = await db.Invitations.AsNoTracking()
            .Where(i => i.AssignmentId == assignmentId && i.StudentProfileId != null)
            .Join(db.StudentProfiles.AsNoTracking(),
                i => i.StudentProfileId!.Value, p => p.Id, (i, p) => new { p.LastName, p.FirstName, p.MiddleName })
            .ToListAsync(ct);
        var withoutAttempts = invitedStudents
            .Select(s => s.MiddleName is null ? $"{s.LastName} {s.FirstName}" : $"{s.LastName} {s.FirstName} {s.MiddleName}")
            .Where(name => attempts.All(a => a.StudentNameSnapshot != name))
            .Distinct()
            .OrderBy(name => name)
            .ToList();

        var csv = new StringBuilder();
        csv.AppendLine("Фамилия и имя;Группа;Попытка;Статус;Баллы;Максимум;Процент;Лучшая");
        foreach (var attempt in attempts)
        {
            var score = attempt.Score;
            csv.AppendLine(string.Join(";", new[]
            {
                Sanitize(attempt.StudentNameSnapshot),
                Sanitize(attempt.StudentGroupSnapshot ?? ""),
                attempt.AttemptNumber.ToString(),
                StatusText(attempt.State),
                score is null ? "" : score.Value.EarnedPoints.ToString("0.##"),
                score is null ? "" : score.Value.MaxPoints.ToString("0.##"),
                score is null ? "" : score.Value.RoundedPercent.ToString(),
                best?.Id == attempt.Id ? "да" : "",
            }));
        }
        foreach (var name in withoutAttempts)
            csv.AppendLine(string.Join(";", Sanitize(name), "", "—", "не начинал", "", "", "", ""));

        // Бумажные работы: отдельные строки с Origin=Paper (ТЗ §12–13), не смешиваются с онлайн-попытками.
        var paperWorks = await db.PaperWorks.AsNoTracking()
            .Where(w => w.AssignmentId == assignmentId)
            .OrderBy(w => w.StudentNameSnapshot)
            .ToListAsync(ct);
        foreach (var work in paperWorks)
        {
            csv.AppendLine(string.Join(";", new[]
            {
                Sanitize(work.StudentNameSnapshot),
                Sanitize(work.StudentGroupSnapshot ?? ""),
                "бумажная",
                "итог (Paper)",
                work.TotalEarned.ToString("0.##"),
                work.TotalMax.ToString("0.##"),
                Domain.Grading.GradeCalculator.Compute(work.TotalEarned, work.TotalMax).RoundedPercent.ToString(),
                "",
            }));
        }

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        return File(bytes, "text/csv; charset=utf-8", $"gradebook-{assignmentId:N}.csv");
    }

    /// <summary>CSV injection protection (ТЗ §12): префикс опасного начала ячейки.</summary>
    private static string Sanitize(string value)
        => value.Length > 0 && value[0] is '=' or '+' or '-' or '@' ? "'" + value : value;

    private static string StatusText(AttemptState state) => state switch
    {
        AttemptState.InProgress => "проходит",
        AttemptState.Submitted => "сдана",
        AttemptState.PendingReview => "на проверке",
        AttemptState.Finalized => "итог",
        AttemptState.Cancelled => "отменена",
        _ => state.ToString(),
    };
}
