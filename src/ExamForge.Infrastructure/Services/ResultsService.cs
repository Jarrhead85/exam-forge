using ClosedXML.Excel;
using ExamForge.Application.TeacherFlow;
using ExamForge.Domain.Attempts;
using ExamForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ExamForge.Infrastructure.Services;

public sealed record AssignmentStatsDto(
    int StudentsInvited,
    int AttemptsTotal,
    int AttemptsFinalized,
    int AttemptsPending,
    decimal AveragePercent,
    int GradedCount,
    int PaperWorks);

/// <summary>
/// Результаты и аналитика (ТЗ §12): явные знаменатели, доли; descriptive-статистика,
/// не «доказательство знания темы». XLSX-ведомость (ClosedXML, MIT — ADR-005).
/// </summary>
public sealed class ResultsService(ExamForgeDbContext db)
{
    public async Task<AssignmentStatsDto> StatsAsync(TeacherActor actor, Guid assignmentId, CancellationToken ct)
    {
        var invited = await db.Invitations.AsNoTracking()
            .CountAsync(i => i.AssignmentId == assignmentId, ct);
        var attempts = await db.Attempts.AsNoTracking()
            .Where(a => a.AssignmentId == assignmentId)
            .ToListAsync(ct);
        var graded = attempts.Where(a => a.Score is not null).ToList();
        var paper = await db.PaperWorks.AsNoTracking()
            .CountAsync(w => w.AssignmentId == assignmentId, ct);

        return new AssignmentStatsDto(
            invited,
            attempts.Count,
            attempts.Count(a => a.State == AttemptState.Finalized),
            attempts.Count(a => a.State is AttemptState.PendingReview or AttemptState.Submitted),
            graded.Count == 0 ? 0m : Math.Round(graded.Average(a => a.Score!.Value.RawPercent), 1),
            graded.Count,
            paper);
    }

    public byte[] GradebookXlsx(TeacherActor actor, Guid assignmentId, string testTitle, CancellationToken ct)
    {
        var attempts = db.Attempts.AsNoTracking()
            .Where(a => a.AssignmentId == assignmentId)
            .OrderBy(a => a.StudentNameSnapshot).ThenBy(a => a.AttemptNumber)
            .ToList();
        var best = AttemptRules.SelectBest(attempts);

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Ведомость");
        sheet.Cell(1, 1).Value = testTitle;
        sheet.Cell(2, 1).Value = "Знаменатель: все приглашённые студенты; попытки онлайн (Origin=Online) и бумажные (Origin=Paper) отдельными строками.";
        var headers = new[] { "Фамилия и имя", "Группа", "Происхождение", "Попытка", "Статус", "Баллы", "Максимум", "Процент", "Лучшая" };
        for (var i = 0; i < headers.Length; i++)
        {
            var cell = sheet.Cell(4, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.SetBold();
        }
        var row = 5;
        foreach (var attempt in attempts)
        {
            var score = attempt.Score;
            sheet.Cell(row, 1).Value = attempt.StudentNameSnapshot;
            sheet.Cell(row, 2).Value = attempt.StudentGroupSnapshot ?? "";
            sheet.Cell(row, 3).Value = "Online";
            sheet.Cell(row, 4).Value = attempt.AttemptNumber;
            sheet.Cell(row, 5).Value = StatusText(attempt.State);
            if (score is not null)
            {
                sheet.Cell(row, 6).Value = (double)score.Value.EarnedPoints;
                sheet.Cell(row, 7).Value = (double)score.Value.MaxPoints;
                sheet.Cell(row, 8).Value = score.Value.RoundedPercent;
            }
            sheet.Cell(row, 9).Value = best?.Id == attempt.Id ? "да" : "";
            row++;
        }
        var papers = db.PaperWorks.AsNoTracking()
            .Where(w => w.AssignmentId == assignmentId)
            .ToList();
        foreach (var work in papers)
        {
            sheet.Cell(row, 1).Value = work.StudentNameSnapshot;
            sheet.Cell(row, 2).Value = work.StudentGroupSnapshot ?? "";
            sheet.Cell(row, 3).Value = "Paper";
            sheet.Cell(row, 4).Value = "бумажная";
            sheet.Cell(row, 5).Value = "итог";
            sheet.Cell(row, 6).Value = (double)work.TotalEarned;
            sheet.Cell(row, 7).Value = (double)work.TotalMax;
            sheet.Cell(row, 8).Value = Domain.Grading.GradeCalculator.Compute(work.TotalEarned, work.TotalMax).RoundedPercent;
            row++;
        }
        sheet.Columns().AdjustToContents();
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

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
