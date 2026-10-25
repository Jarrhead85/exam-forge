using System.Text;
using ExamForge.Application.StudentFlow;
using ExamForge.Application.TeacherFlow;
using ExamForge.Domain.Attempts;
using ExamForge.Domain.Grading;
using ExamForge.Domain.Questions;
using ExamForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ExamForge.Infrastructure.Services;

public sealed record ReviewQueueItemDto(
    Guid AttemptId,
    string StudentName,
    string? StudentGroup,
    int AttemptNumber,
    string Status,
    int PendingCriteria,
    int TotalCriteria,
    DateTimeOffset SubmittedAtUtc);

public sealed record ReviewCriterionDto(
    Guid QuestionSessionId,
    string CriterionId,
    string Label,
    decimal MaxPoints,
    decimal EarnedPoints,
    string Reason,
    string State);

public sealed record ReviewQuestionDto(
    Guid QuestionSessionId,
    int OrderIndex,
    string Type,
    string Prompt,
    string AnswerDisplay,
    string? KeyDisplay,
    IReadOnlyList<ReviewCriterionDto> Criteria);

public sealed record ReviewAttemptDto(
    Guid AttemptId,
    string StudentName,
    string? StudentGroup,
    int AttemptNumber,
    string Status,
    decimal ConfirmedEarned,
    decimal MaxPoints,
    IReadOnlyList<ReviewQuestionDto> Questions);

public sealed record CriterionScoreDto(Guid QuestionSessionId, string CriterionId, decimal EarnedPoints, string Reason);

/// <summary>
/// Ручная проверка и переоценка (ТЗ §12): очередь по назначению, ответ+ключ+критерии для преподавателя,
/// оценка pending-критериев с финализацией попытки, override с причиной и историей до/после (ТЗ §11).
/// </summary>
public sealed class ManualReviewService(ExamForgeDbContext db, TimeProvider timeProvider)
{
    private DateTimeOffset Now => timeProvider.GetUtcNow();

    /// <summary>
    /// Очередь проверки по нескольким назначениям одним батчем (обзор «Ждут проверки»):
    /// три запроса на все назначения вместо N×2 — первый рендер обзора не платит за N+1.
    /// </summary>
    public async Task<IReadOnlyList<(Guid AssignmentId, ReviewQueueItemDto Item)>> GetOpenQueueAsync(
        TeacherActor actor, IReadOnlyList<Guid> assignmentIds, CancellationToken ct)
    {
        if (assignmentIds.Count == 0)
            return [];

        var ownedIds = await db.Assignments.AsNoTracking()
            .Where(a => assignmentIds.Contains(a.Id)
                        && (actor.IsAdministrator || a.CreatedByTeacherId == actor.TeacherId))
            .Select(a => a.Id)
            .ToListAsync(ct);
        if (ownedIds.Count == 0)
            return [];

        var attempts = await db.Attempts.AsNoTracking()
            .Where(a => ownedIds.Contains(a.AssignmentId)
                        && (a.State == AttemptState.PendingReview || a.HasPendingManualReview))
            .OrderBy(a => a.StudentNameSnapshot).ThenBy(a => a.AttemptNumber)
            .ToListAsync(ct);
        var attemptIds = attempts.Select(a => a.Id).ToList();
        var pendingByAttempt = await db.QuestionGrades.AsNoTracking()
            .Where(g => attemptIds.Contains(g.AttemptId) && g.State == VerificationState.PendingManual)
            .GroupBy(g => g.AttemptId)
            .Select(g => new { AttemptId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AttemptId, x => x.Count, ct);

        return [.. attempts
            .Select(a => (a.AssignmentId, new ReviewQueueItemDto(
                a.Id, a.StudentNameSnapshot, a.StudentGroupSnapshot, a.AttemptNumber,
                a.State.ToString(), pendingByAttempt.GetValueOrDefault(a.Id), 0,
                a.SubmittedAtUtc ?? a.CompletedAtUtc ?? a.CreatedAtUtc)))
            .Take(10)];
    }

    public async Task<IReadOnlyList<ReviewQueueItemDto>> GetQueueAsync(
        TeacherActor actor, Guid assignmentId, string? filter, CancellationToken ct)
    {
        await EnsureOwnAssignmentAsync(actor, assignmentId, ct);
        var attempts = await db.Attempts.AsNoTracking()
            .Where(a => a.AssignmentId == assignmentId)
            .OrderBy(a => a.StudentNameSnapshot).ThenBy(a => a.AttemptNumber)
            .ToListAsync(ct);
        var attemptIds = attempts.Select(a => a.Id).ToList();
        var grades = await db.QuestionGrades.AsNoTracking()
            .Where(g => attemptIds.Contains(g.AttemptId))
            .ToListAsync(ct);

        var result = new List<ReviewQueueItemDto>();
        foreach (var attempt in attempts)
        {
            var criteria = grades.Where(g => g.AttemptId == attempt.Id).ToList();
            var pending = criteria.Count(c => c.State == VerificationState.PendingManual);
            var include = filter switch
            {
                "pending" => attempt.State == AttemptState.PendingReview || pending > 0,
                "finalized" => attempt.State == AttemptState.Finalized,
                _ => true,
            };
            if (!include)
                continue;
            result.Add(new ReviewQueueItemDto(
                attempt.Id, attempt.StudentNameSnapshot, attempt.StudentGroupSnapshot, attempt.AttemptNumber,
                attempt.State.ToString(), pending, criteria.Count, attempt.SubmittedAtUtc ?? attempt.CompletedAtUtc ?? attempt.CreatedAtUtc));
        }
        return result;
    }

    /// <summary>Детали для проверки: ответ студента + ключ + критерии (только преподавателю, ТЗ §12).</summary>
    public async Task<ReviewAttemptDto> GetAttemptAsync(TeacherActor actor, Guid attemptId, CancellationToken ct)
    {
        var attempt = await db.Attempts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == attemptId, ct)
            ?? throw TeacherFlowErrors.NotFound("Попытка не найдена.");
        await EnsureOwnAssignmentAsync(actor, attempt.AssignmentId, ct);

        var sessions = await db.QuestionSessions.AsNoTracking()
            .Where(s => s.AttemptId == attemptId)
            .OrderBy(s => s.OrderIndex)
            .ToListAsync(ct);
        var grades = await db.QuestionGrades.AsNoTracking()
            .Where(g => g.AttemptId == attemptId)
            .ToListAsync(ct);
        var answers = await db.AnswerRevisions
            .Where(r => r.AttemptId == attemptId)
            .GroupBy(r => r.QuestionSessionId)
            .Select(g => g.OrderByDescending(r => r.Revision).First())
            .ToDictionaryAsync(r => r.QuestionSessionId, ct);

        var questionVersionIds = sessions.Select(s => s.QuestionVersionId).ToList();
        var versionRows = await db.QuestionVersions.AsNoTracking()
            .Where(v => questionVersionIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, ct);

        var questions = new List<ReviewQuestionDto>();
        foreach (var session in sessions)
        {
            var version = versionRows[session.QuestionVersionId];
            var definition = QuestionDefinitionJson.Deserialize(version.DefinitionJson);
            var hasAnswer = answers.TryGetValue(session.Id, out var answer);
            questions.Add(new ReviewQuestionDto(
                session.Id,
                session.OrderIndex,
                definition.Type.ToString(),
                definition.Prompt,
                hasAnswer ? DisplayAnswer(definition, QuestionDefinitionJson.DeserializeAnswer(answer!.AnswerJson)) : "— нет ответа —",
                BuildKeyDisplay(definition),
                grades.Where(g => g.QuestionSessionId == session.Id)
                    .Select(g => new ReviewCriterionDto(g.QuestionSessionId, g.CriterionId, g.Label, g.MaxPoints, g.EarnedPoints, g.Reason, g.State.ToString()))
                    .ToList()));
        }

        return new ReviewAttemptDto(
            attemptId,
            attempt.StudentNameSnapshot,
            attempt.StudentGroupSnapshot,
            attempt.AttemptNumber,
            attempt.State.ToString(),
            grades.Where(g => g.State != VerificationState.PendingManual).Sum(g => g.EarnedPoints),
            grades.Sum(g => g.MaxPoints),
            questions);
    }

    /// <summary>
    /// Оценить pending-критерии попытки. Когда pending не остаётся — попытка финализируется
    /// по сумме всех критериев (ТЗ §11: не проверено ≠ 0; итог после проверки).
    /// </summary>
    public async Task<ReviewAttemptDto> GradeAsync(
        TeacherActor actor, Guid attemptId, IReadOnlyList<CriterionScoreDto> scores, CancellationToken ct)
    {
        var attempt = await db.Attempts.SingleOrDefaultAsync(a => a.Id == attemptId, ct)
            ?? throw TeacherFlowErrors.NotFound("Попытка не найдена.");
        await EnsureOwnAssignmentAsync(actor, attempt.AssignmentId, ct);
        if (attempt.State is not (AttemptState.PendingReview or AttemptState.Submitted))
            throw TeacherFlowErrors.Conflict("not_pending", $"Проверять можно сданную работу ({attempt.State}).");

        var grades = await db.QuestionGrades.Where(g => g.AttemptId == attemptId).ToListAsync(ct);
        foreach (var score in scores)
        {
            var grade = grades.SingleOrDefault(g =>
                    g.QuestionSessionId == score.QuestionSessionId && g.CriterionId == score.CriterionId)
                ?? throw TeacherFlowErrors.NotFound($"Критерий {score.CriterionId} не найден.");
            if (grade.State == VerificationState.PendingManual)
                grade.GradeManually(score.EarnedPoints, score.Reason, actor.TeacherId, Now);
        }

        await FinalizeIfCompleteAsync(attempt, grades, ct);
        return await GetAttemptAsync(actor, attemptId, ct);
    }

    /// <summary>Переоценка подтверждённого критерия финализированной попытки: причина + история до/после (ТЗ §11).</summary>
    public async Task<ReviewAttemptDto> OverrideAsync(
        TeacherActor actor, Guid attemptId, CriterionScoreDto score, CancellationToken ct)
    {
        var attempt = await db.Attempts.SingleOrDefaultAsync(a => a.Id == attemptId, ct)
            ?? throw TeacherFlowErrors.NotFound("Попытка не найдена.");
        await EnsureOwnAssignmentAsync(actor, attempt.AssignmentId, ct);
        if (attempt.State != AttemptState.Finalized)
            throw TeacherFlowErrors.Conflict("not_finalized", "Переоценка доступна для финализированной попытки.");

        var grades = await db.QuestionGrades.Where(g => g.AttemptId == attemptId).ToListAsync(ct);
        var grade = grades.SingleOrDefault(g =>
                g.QuestionSessionId == score.QuestionSessionId && g.CriterionId == score.CriterionId)
            ?? throw TeacherFlowErrors.NotFound($"Критерий {score.CriterionId} не найден.");

        var oldEarned = grade.EarnedPoints;
        var oldAttemptEarned = attempt.Score?.EarnedPoints ?? 0m;
        grade.Override(score.EarnedPoints, score.Reason, actor.TeacherId, Now);

        var newAttemptEarned = grades.Sum(g => g.EarnedPoints);
        var max = grades.Sum(g => g.MaxPoints);
        attempt.Regrade(newAttemptEarned, max);

        db.GradeRevisions.Add(GradeRevision.Create(
            attemptId, score.QuestionSessionId, score.CriterionId,
            oldEarned, score.EarnedPoints, oldAttemptEarned, newAttemptEarned,
            score.Reason, actor.TeacherId, Now));
        await db.SaveChangesAsync(ct);
        return await GetAttemptAsync(actor, attemptId, ct);
    }

    private async Task FinalizeIfCompleteAsync(Attempt attempt, List<QuestionGrade> grades, CancellationToken ct)
    {
        if (grades.Any(g => g.State == VerificationState.PendingManual))
            return;
        var earned = grades.Sum(g => g.EarnedPoints);
        var max = grades.Sum(g => g.MaxPoints);
        attempt.Finalize(earned, max, Now);
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureOwnAssignmentAsync(TeacherActor actor, Guid assignmentId, CancellationToken ct)
    {
        var assignment = await db.Assignments.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == assignmentId, ct);
        if (assignment is null || (!actor.IsAdministrator && assignment.CreatedByTeacherId != actor.TeacherId))
            throw TeacherFlowErrors.NotFound("Назначение не найдено.");
    }

    // ---------- отображение ответа/ключа ----------

    private static string DisplayAnswer(QuestionDefinition d, QuestionAnswer a) => d.Type switch
    {
        QuestionType.SingleChoice => OptionText(d, a.SelectedOptionId),
        QuestionType.MultipleChoice => string.Join("; ", (a.SelectedOptionIds ?? Enumerable.Empty<string>()).Select(id => OptionText(d, id))),
        QuestionType.ShortText or QuestionType.LongText => a.Text is { Length: > 0 } ? a.Text : "—",
        QuestionType.Structured => string.Join("; ", (a.FieldValues ?? EmptyFields)
            .Select(kv => $"{FieldName(d, kv.Key)}: {kv.Value}")),
        QuestionType.Matching => string.Join("; ", (a.PairSelections ?? EmptyFields)
            .Select(kv => $"{LeftText(d, kv.Key)} → {RightText(d, kv.Value)}")),
        QuestionType.LetterAssembly => BuildLetterString(d, a.TileSequence),
        QuestionType.Ordering => string.Join(" – ", (a.Order ?? Enumerable.Empty<string>()).Select(id => ItemText(d, id))),
        QuestionType.GapFill => string.Join("; ", (a.BlankValues ?? EmptyFields).Select(kv => $"{kv.Key}: {kv.Value}")),
        _ => "—",
    };

    private static readonly IReadOnlyDictionary<string, string> EmptyFields = new Dictionary<string, string>();

    private static string BuildLetterString(QuestionDefinition d, IReadOnlyList<string>? tileSequence)
    {
        if (tileSequence is null || tileSequence.Count == 0)
            return "—";
        var chars = tileSequence
            .Select(id => d.LetterTiles.FirstOrDefault(t => t.TileId == id)?.Letter ?? '?')
            .ToArray();
        return new string(chars);
    }

    private static string? BuildKeyDisplay(QuestionDefinition d) => d.Type switch
    {
        QuestionType.SingleChoice => OptionText(d, d.CorrectOptionId),
        QuestionType.MultipleChoice => string.Join("; ", d.CorrectOptionIds.Select(id => OptionText(d, id))),
        QuestionType.ShortText => string.Join("; ", d.AcceptedAnswers.Select(x => x.Value)),
        QuestionType.LongText => "Ручная проверка по рубрике",
        QuestionType.Structured => string.Join("; ", d.StructuredFields.Select(f => $"{f.Label}: {f.CorrectValue}")),
        QuestionType.Matching => string.Join("; ", d.MatchingPairs.Select(p => $"{p.LeftText} → {p.RightText}")),
        QuestionType.LetterAssembly => d.TargetSequence,
        QuestionType.Ordering => string.Join(" – ", d.AcceptedOrders[0].Select(id => ItemText(d, id))),
        QuestionType.GapFill => string.Join("; ", d.GapBlanks.Select(b => $"{b.BlankId}: {string.Join("/", b.AcceptedValues)}")),
        _ => null,
    };

    private static string OptionText(QuestionDefinition d, string? optionId)
        => d.Options.FirstOrDefault(o => o.OptionId == optionId)?.Text ?? optionId ?? "—";

    private static string ItemText(QuestionDefinition d, string itemId)
        => d.OrderingItems.FirstOrDefault(i => i.OptionId == itemId)?.Text ?? itemId;

    private static string LeftText(QuestionDefinition d, string leftId)
        => d.MatchingPairs.FirstOrDefault(p => p.LeftId == leftId)?.LeftText ?? leftId;

    private static string RightText(QuestionDefinition d, string? rightId)
        => d.MatchingPairs.FirstOrDefault(p => p.RightId == rightId)?.RightText ?? rightId ?? "—";

    private static string FieldName(QuestionDefinition d, string fieldId)
        => d.StructuredFields.FirstOrDefault(f => f.FieldId == fieldId)?.Label ?? fieldId;
}
