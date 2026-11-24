using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using ExamForge.Application.StudentFlow;
using ExamForge.Application.TeacherFlow;
using ExamForge.Domain.Assignments;
using ExamForge.Domain.Attempts;
using ExamForge.Domain.Grading;
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

public sealed record GradebookRow(
    Guid? StudentProfileId, string Student, string? Group, int? AttemptNumber,
    string Status, decimal? Earned, decimal? Max, int? Percent, bool IsBest, string? Origin);

public sealed record Gradebook(string Title, IReadOnlyList<GradebookRow> Rows);

public sealed class ResultsService(IDbContextFactory<ExamForgeDbContext> contextFactory)
{
    private static readonly string[] Headers =
    [
        "Фамилия и имя", "Группа", "Попытка", "Статус", "Баллы", "Максимум", "Процент", "Лучшая", "Происхождение",
    ];

    public async Task<AssignmentStatsDto> StatsAsync(TeacherActor actor, Guid assignmentId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await LoadTitleAsync(db, actor, assignmentId, ct);
        var invited = await InvitedProfiles(db, assignmentId).CountAsync(ct);
        var attempts = await db.Attempts.AsNoTracking().Where(attempt => attempt.AssignmentId == assignmentId)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Total = group.Count(),
                Finalized = group.Count(attempt => attempt.State == AttemptState.Finalized),
                Pending = group.Count(attempt => attempt.State == AttemptState.PendingReview || attempt.State == AttemptState.Submitted),
                Graded = group.Count(attempt => attempt.ScoreEarnedPoints != null && attempt.ScoreMaxPoints > 0),
                Average = group.Average(attempt => attempt.ScoreMaxPoints > 0
                    ? attempt.ScoreEarnedPoints / attempt.ScoreMaxPoints * 100m
                    : null),
            })
            .SingleOrDefaultAsync(ct);
        var paper = await db.PaperWorks.AsNoTracking().CountAsync(work => work.AssignmentId == assignmentId, ct);

        return new AssignmentStatsDto(invited, attempts?.Total ?? 0, attempts?.Finalized ?? 0,
            attempts?.Pending ?? 0, Math.Round(attempts?.Average ?? 0m, 1), attempts?.Graded ?? 0, paper);
    }

    public async Task<Gradebook> GradebookAsync(TeacherActor actor, Guid assignmentId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var title = await LoadTitleAsync(db, actor, assignmentId, ct);
        var attempts = await db.Attempts.AsNoTracking()
            .Where(attempt => attempt.AssignmentId == assignmentId)
            .ToListAsync(ct);
        var best = AttemptRules.BestAttemptIdsByStudent(attempts);
        var attempted = attempts.Where(attempt => attempt.StudentProfileId != null)
            .Select(attempt => attempt.StudentProfileId!.Value).ToHashSet();
        var invitedIds = InvitedProfiles(db, assignmentId);
        var invited = await db.StudentProfiles.AsNoTracking().Where(profile => invitedIds.Contains(profile.Id))
            .Select(profile => new { profile.Id, profile.LastName, profile.FirstName, profile.MiddleName })
            .ToListAsync(ct);
        var paper = await db.PaperWorks.AsNoTracking().Where(work => work.AssignmentId == assignmentId)
            .Select(work => new { work.StudentNameSnapshot, work.StudentGroupSnapshot, work.TotalEarned, work.TotalMax })
            .ToListAsync(ct);

        var online = attempts.Select(attempt =>
        {
            var score = attempt.Score;
            return new GradebookRow(attempt.StudentProfileId, attempt.StudentNameSnapshot, attempt.StudentGroupSnapshot,
                attempt.AttemptNumber, AttemptStatus(attempt.State), score?.EarnedPoints, score?.MaxPoints,
                score?.RoundedPercent, best.Contains(attempt.Id), "Online");
        });
        var notStarted = invited.Where(profile => !attempted.Contains(profile.Id)).Select(profile =>
            new GradebookRow(profile.Id, $"{profile.LastName} {profile.FirstName} {profile.MiddleName}".TrimEnd(),
                null, null, "Не начал", null, null, null, false, null));
        var onPaper = paper.Select(work => new GradebookRow(null, work.StudentNameSnapshot, work.StudentGroupSnapshot,
            null, "Оценено", work.TotalEarned, work.TotalMax,
            GradeCalculator.Compute(work.TotalEarned, work.TotalMax).RoundedPercent, false, "Paper"));

        return new Gradebook(title, online.Concat(notStarted).Concat(onPaper)
            .OrderBy(row => row.Student).ThenBy(row => row.StudentProfileId).ThenBy(row => row.AttemptNumber).ToList());
    }

    public async Task<byte[]> GradebookXlsxAsync(TeacherActor actor, Guid assignmentId, CancellationToken ct)
    {
        var gradebook = await GradebookAsync(actor, assignmentId, ct);
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Ведомость");
        sheet.Cell(1, 1).Value = gradebook.Title;
        for (var i = 0; i < Headers.Length; i++)
        {
            var cell = sheet.Cell(3, i + 1);
            cell.Value = Headers[i];
            cell.Style.Font.SetBold();
        }

        var index = 4;
        foreach (var row in gradebook.Rows)
        {
            ct.ThrowIfCancellationRequested();
            sheet.Cell(index, 1).Value = row.Student;
            sheet.Cell(index, 2).Value = row.Group ?? "";
            if (row.AttemptNumber is { } number) sheet.Cell(index, 3).Value = number;
            sheet.Cell(index, 4).Value = row.Status;
            if (row.Earned is { } earned) sheet.Cell(index, 5).Value = (double)earned;
            if (row.Max is { } max) sheet.Cell(index, 6).Value = (double)max;
            if (row.Percent is { } percent) sheet.Cell(index, 7).Value = percent;
            sheet.Cell(index, 8).Value = row.IsBest ? "да" : "";
            sheet.Cell(index, 9).Value = OriginText(row.Origin);
            index++;
        }

        sheet.SheetView.FreezeRows(3);
        sheet.Column(1).Width = 36;
        sheet.Columns(2, 9).Width = 17;
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public async Task<byte[]> GradebookCsvAsync(TeacherActor actor, Guid assignmentId, CancellationToken ct)
    {
        var gradebook = await GradebookAsync(actor, assignmentId, ct);
        var csv = new StringBuilder().AppendLine(string.Join(';', Headers));
        foreach (var row in gradebook.Rows)
        {
            ct.ThrowIfCancellationRequested();
            csv.AppendLine(string.Join(';',
                CsvText(row.Student), CsvText(row.Group), row.AttemptNumber?.ToString(CultureInfo.InvariantCulture),
                CsvText(row.Status), row.Earned?.ToString("0.##", CultureInfo.CurrentCulture),
                row.Max?.ToString("0.##", CultureInfo.CurrentCulture), row.Percent?.ToString(CultureInfo.InvariantCulture),
                row.IsBest ? "да" : "", OriginText(row.Origin)));
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())];
    }

    private static string OriginText(string? origin) => origin switch
    {
        "Online" => "онлайн",
        "Paper" => "бумажная",
        _ => "",
    };

    private static string CsvText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        var first = value.AsSpan().TrimStart();
        if (value[0] is '\t' or '\r' or '\n' || first is [('=' or '+' or '-' or '@' or '＝' or '＋' or '－' or '＠'), ..])
            value = "'" + value;
        return value.AsSpan().ContainsAny(";\r\n\"") ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    private static IQueryable<Guid> InvitedProfiles(ExamForgeDbContext db, Guid assignmentId)
        => db.Invitations.AsNoTracking()
            .Where(invitation => invitation.AssignmentId == assignmentId && invitation.Status == InvitationStatus.Active &&
                invitation.StudentProfileId != null)
            .Select(invitation => invitation.StudentProfileId!.Value)
            .Distinct();

    private static async Task<string> LoadTitleAsync(
        ExamForgeDbContext db, TeacherActor actor, Guid assignmentId, CancellationToken ct)
        => await (from assignment in db.Assignments.AsNoTracking()
                  join version in db.TestVersions.AsNoTracking() on assignment.TestVersionId equals version.Id
                  where assignment.Id == assignmentId && (actor.IsAdministrator || assignment.CreatedByTeacherId == actor.TeacherId)
                  select version.Title).SingleOrDefaultAsync(ct)
            ?? throw StudentFlowException.NotFound("Назначение не найдено.");

    private static string AttemptStatus(AttemptState state) => state switch
    {
        AttemptState.Created => "Не начал",
        AttemptState.InProgress => "Проходит",
        AttemptState.Submitted => "Сдано",
        AttemptState.PendingReview => "Ждёт проверки",
        AttemptState.Finalized => "Оценено",
        AttemptState.Cancelled => "Отменено",
        _ => state.ToString(),
    };
}
