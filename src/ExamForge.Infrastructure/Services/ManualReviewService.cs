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
    IReadOnlyList<ReviewQuestionDto> Questions,
    Guid AssignmentId);

public sealed record CriterionScoreDto(Guid QuestionSessionId, string CriterionId, decimal EarnedPoints, string Reason);

/// <summary>
/// Ручная проверка и переоценка (ТЗ §12): очередь по назначению, ответ+ключ+критерии для преподавателя,
/// оценка pending-критериев с финализацией попытки, override с причиной и историей до/после (ТЗ §11).
/// </summary>
public sealed class ManualReviewService(IDbContextFactory<ExamForgeDbContext> contextFactory, TimeProvider timeProvider)
{
    private DateTimeOffset Now => timeProvider.GetUtcNow();

    public async Task<IReadOnlyList<(Guid AssignmentId, ReviewQueueItemDto Item)>> GetOpenQueueAsync(
        TeacherActor actor, IReadOnlyList<Guid>? assignmentIds, CancellationToken ct)
    {
        if (assignmentIds is { Count: 0 })
            return [];

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var owned = db.Assignments.AsNoTracking()
            .Where(assignment => actor.IsAdministrator || assignment.CreatedByTeacherId == actor.TeacherId);
        if (assignmentIds is not null)
            owned = owned.Where(assignment => assignmentIds.Contains(assignment.Id));

        var attempts = await db.Attempts.AsNoTracking()
            .Where(attempt => owned.Any(assignment => assignment.Id == attempt.AssignmentId) &&
                (attempt.State == AttemptState.PendingReview || attempt.HasPendingManualReview))
            .OrderBy(attempt => attempt.SubmittedAtUtc).ThenBy(attempt => attempt.Id).Take(10)
            .Select(attempt => new
            {
                attempt.AssignmentId,
                Item = new ReviewQueueItemDto(attempt.Id, attempt.StudentNameSnapshot, attempt.StudentGroupSnapshot,
                    attempt.AttemptNumber, attempt.State.ToString(),
                    db.QuestionGrades.Count(grade => grade.AttemptId == attempt.Id && grade.State == VerificationState.PendingManual),
                    db.QuestionGrades.Count(grade => grade.AttemptId == attempt.Id),
                    attempt.SubmittedAtUtc ?? attempt.CompletedAtUtc ?? attempt.CreatedAtUtc),
            })
            .ToListAsync(ct);
        return [.. attempts.Select(attempt => (attempt.AssignmentId, attempt.Item))];
    }

    public Task<IReadOnlyList<ReviewQueueItemDto>> GetQueueAsync(
        TeacherActor actor, Guid assignmentId, string? filter, CancellationToken ct)
        => GetQueueAsync(actor, assignmentId, filter, null, null, 0, null, ct);

    public async Task<IReadOnlyList<ReviewQueueItemDto>> GetQueueAsync(
        TeacherActor actor, Guid assignmentId, string? filter, string? group, Guid? questionVersionId,
        int skip, int? take, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await EnsureOwnAssignmentAsync(db, actor, assignmentId, ct);
        var attempts = db.Attempts.AsNoTracking().Where(attempt => attempt.AssignmentId == assignmentId);
        attempts = filter switch
        {
            "pending" => attempts.Where(attempt => attempt.State == AttemptState.PendingReview || attempt.HasPendingManualReview),
            "finalized" => attempts.Where(attempt => attempt.State == AttemptState.Finalized),
            _ => attempts,
        };
        if (!string.IsNullOrWhiteSpace(group))
            attempts = attempts.Where(attempt => attempt.StudentGroupSnapshot == group);
        if (questionVersionId is { } id)
            attempts = attempts.Where(attempt => db.QuestionGrades.Any(grade =>
                grade.AttemptId == attempt.Id && grade.QuestionVersionId == id &&
                (filter != "pending" || grade.State == VerificationState.PendingManual)));

        IQueryable<Attempt> page = attempts.OrderBy(attempt => attempt.StudentNameSnapshot)
            .ThenBy(attempt => attempt.AttemptNumber).ThenBy(attempt => attempt.Id).Skip(skip);
        if (take is { } limit)
            page = page.Take(limit);

        return await page.Select(attempt => new ReviewQueueItemDto(
            attempt.Id, attempt.StudentNameSnapshot, attempt.StudentGroupSnapshot, attempt.AttemptNumber,
            attempt.State.ToString(),
            db.QuestionGrades.Count(grade => grade.AttemptId == attempt.Id && grade.State == VerificationState.PendingManual),
            db.QuestionGrades.Count(grade => grade.AttemptId == attempt.Id),
            attempt.SubmittedAtUtc ?? attempt.CompletedAtUtc ?? attempt.CreatedAtUtc)).ToListAsync(ct);
    }

    public async Task<(IReadOnlyList<string> Groups, IReadOnlyList<(Guid Id, string Prompt)> Questions)> GetFiltersAsync(
        TeacherActor actor, Guid assignmentId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var assignment = await EnsureOwnAssignmentAsync(db, actor, assignmentId, ct);
        var groups = await db.Attempts.AsNoTracking()
            .Where(attempt => attempt.AssignmentId == assignmentId && attempt.StudentGroupSnapshot != null)
            .Select(attempt => attempt.StudentGroupSnapshot!).Distinct().OrderBy(group => group).ToListAsync(ct);
        var ids = await db.TestVersions.AsNoTracking().Where(version => version.Id == assignment.TestVersionId)
            .Select(version => version.QuestionVersionIds).SingleAsync(ct);
        var questions = await db.QuestionVersions.AsNoTracking().Where(version => ids.Contains(version.Id))
            .ToDictionaryAsync(version => version.Id, version => QuestionDefinitionJson.Deserialize(version.DefinitionJson).Prompt, ct);
        return (groups, [.. ids.Distinct().Select(id => (id, questions[id]))]);
    }

    /// <summary>Детали для проверки: ответ студента + ключ + критерии (только преподавателю, ТЗ §12).</summary>
    public async Task<ReviewAttemptDto> GetAttemptAsync(TeacherActor actor, Guid attemptId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var attempt = await db.Attempts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == attemptId, ct)
            ?? throw TeacherFlowErrors.NotFound("Попытка не найдена.");
        await EnsureOwnAssignmentAsync(db, actor, attempt.AssignmentId, ct);

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
            questions,
            attempt.AssignmentId);
    }

    /// <summary>
    /// Оценить pending-критерии попытки. Когда pending не остаётся — попытка финализируется
    /// по сумме всех критериев (ТЗ §11: не проверено ≠ 0; итог после проверки).
    /// </summary>
    public Task<ReviewAttemptDto> GradeAsync(
        TeacherActor actor, Guid attemptId, IReadOnlyList<CriterionScoreDto> scores, CancellationToken ct)
        => UpdateAsync(actor, attemptId, async (db, attempt) =>
        {
            if (attempt.State is not (AttemptState.PendingReview or AttemptState.Submitted))
                throw TeacherFlowErrors.Conflict("not_pending", "Проверять можно только сданную работу, которая ещё не оценена.");

            var grades = await db.QuestionGrades.Where(grade => grade.AttemptId == attemptId).ToListAsync(ct);
            var byCriterion = grades.ToDictionary(grade => (grade.QuestionSessionId, grade.CriterionId));
            foreach (var score in scores)
            {
                if (!byCriterion.TryGetValue((score.QuestionSessionId, score.CriterionId), out var grade))
                    throw TeacherFlowErrors.NotFound("Критерий не найден.");
                if (grade.State == VerificationState.PendingManual)
                    grade.GradeManually(score.EarnedPoints, score.Reason, actor.TeacherId, Now);
            }

            if (grades.All(grade => grade.State != VerificationState.PendingManual))
                attempt.Finalize(grades.Sum(grade => grade.EarnedPoints), grades.Sum(grade => grade.MaxPoints), Now);
        }, ct);

    public Task<ReviewAttemptDto> OverrideAsync(
        TeacherActor actor, Guid attemptId, CriterionScoreDto score, CancellationToken ct)
        => UpdateAsync(actor, attemptId, async (db, attempt) =>
        {
            if (attempt.State != AttemptState.Finalized)
                throw TeacherFlowErrors.Conflict("not_finalized", "Переоценить можно только проверенную работу.");

            var grades = await db.QuestionGrades.Where(grade => grade.AttemptId == attemptId).ToListAsync(ct);
            var criterion = grades.SingleOrDefault(grade =>
                    grade.QuestionSessionId == score.QuestionSessionId && grade.CriterionId == score.CriterionId)
                ?? throw TeacherFlowErrors.NotFound("Критерий не найден.");

            var oldEarned = criterion.EarnedPoints;
            var oldAttemptEarned = attempt.Score?.EarnedPoints ?? 0m;
            criterion.Override(score.EarnedPoints, score.Reason, actor.TeacherId, Now);

            var newAttemptEarned = grades.Sum(grade => grade.EarnedPoints);
            attempt.Regrade(newAttemptEarned, grades.Sum(grade => grade.MaxPoints));
            db.GradeRevisions.Add(GradeRevision.Create(
                attemptId, score.QuestionSessionId, score.CriterionId,
                oldEarned, criterion.EarnedPoints, oldAttemptEarned, newAttemptEarned,
                score.Reason, actor.TeacherId, Now));
        }, ct);

    private async Task<ReviewAttemptDto> UpdateAsync(
        TeacherActor actor, Guid attemptId, Func<ExamForgeDbContext, Attempt, Task> update, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var attempt = await db.Attempts
                .FromSql($"""SELECT attempts.*, xmin FROM attempts WHERE "Id" = {attemptId} FOR UPDATE""")
                .SingleOrDefaultAsync(item => db.Assignments.Any(assignment => assignment.Id == item.AssignmentId &&
                    (actor.IsAdministrator || assignment.CreatedByTeacherId == actor.TeacherId)), ct)
                ?? throw TeacherFlowErrors.NotFound("Попытка не найдена.");
            await update(db, attempt);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });

        return await GetAttemptAsync(actor, attemptId, ct);
    }

    private static async Task<Domain.Assignments.Assignment> EnsureOwnAssignmentAsync(
        ExamForgeDbContext db, TeacherActor actor, Guid assignmentId, CancellationToken ct)
        => await db.Assignments.AsNoTracking().SingleOrDefaultAsync(assignment => assignment.Id == assignmentId &&
            (actor.IsAdministrator || assignment.CreatedByTeacherId == actor.TeacherId), ct)
            ?? throw TeacherFlowErrors.NotFound("Назначение не найдено.");

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
