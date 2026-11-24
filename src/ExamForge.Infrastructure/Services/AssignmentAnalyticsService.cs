using ExamForge.Application.TeacherFlow;
using ExamForge.Domain.Attempts;
using ExamForge.Domain.Questions;
using ExamForge.Domain.Testing;
using ExamForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ExamForge.Infrastructure.Services;

public sealed record QuestionPerformance(
    Guid QuestionVersionId, string Prompt, Guid? TopicId, string? TopicName,
    int SampleSize, int FullCredit, int PartialCredit, int NoCredit, int Skipped)
{
    public int? FullCreditPercent => SampleSize == 0 ? null : (int)Math.Round(100m * FullCredit / SampleSize);
}

public sealed class AssignmentAnalyticsService(IDbContextFactory<ExamForgeDbContext> contextFactory, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<QuestionPerformance>> AnalyzeAsync(TeacherActor actor, Guid assignmentId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var version = await LoadVersionAsync(db, actor, assignmentId, ct);
        var ids = version.QuestionVersionIds.Distinct().ToArray();

        var bySession = await (from grade in db.QuestionGrades.AsNoTracking()
                               join attempt in db.Attempts.AsNoTracking() on grade.AttemptId equals attempt.Id
                               join session in db.QuestionSessions.AsNoTracking() on grade.QuestionSessionId equals session.Id
                               where attempt.AssignmentId == assignmentId && attempt.State == AttemptState.Finalized
                               group grade by new { grade.QuestionSessionId, grade.QuestionVersionId, session.AnswerRevision } into result
                               select new
                               {
                                   result.Key.QuestionVersionId,
                                   result.Key.AnswerRevision,
                                   Earned = result.Sum(grade => grade.EarnedPoints),
                                   Max = result.Sum(grade => grade.MaxPoints),
                               }).ToListAsync(ct);
        var byQuestion = bySession.ToLookup(result => result.QuestionVersionId);
        var questions = await (from item in db.QuestionVersions.AsNoTracking()
                               join question in db.Questions.AsNoTracking() on item.QuestionId equals question.Id
                               join topic in db.Topics.AsNoTracking() on question.TopicId equals (Guid?)topic.Id into topics
                               from topic in topics.DefaultIfEmpty()
                               where ids.Contains(item.Id)
                               select new { item.Id, item.DefinitionJson, question.TopicId, TopicName = topic == null ? null : topic.Name })
            .ToDictionaryAsync(item => item.Id, ct);

        return [.. ids.Select(id =>
        {
            var sample = byQuestion[id];
            var question = questions[id];
            return new QuestionPerformance(id, QuestionDefinitionJson.Deserialize(question.DefinitionJson).Prompt,
                question.TopicId, question.TopicName, sample.Count(),
                sample.Count(item => item.Earned == item.Max), sample.Count(item => item.Earned > 0 && item.Earned < item.Max),
                sample.Count(item => item.Earned == 0 && item.AnswerRevision > 0),
                sample.Count(item => item.Earned == 0 && item.AnswerRevision == 0));
        })];
    }

    public async Task<Guid> CreateTrainingDraftAsync(
        TeacherActor actor, Guid assignmentId, IReadOnlyCollection<Guid> questionVersionIds, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var source = await LoadVersionAsync(db, actor, assignmentId, ct);
        var ids = source.QuestionVersionIds.Where(questionVersionIds.Contains).Distinct().ToArray();
        if (ids.Length == 0 || ids.Length != questionVersionIds.Count)
            throw TeacherFlowErrors.Conflict("invalid_training_questions", "Выберите задания из этого назначения.");

        var subjectId = await db.Tests.AsNoTracking()
            .Where(test => test.Id == source.TestId).Select(test => test.SubjectId).SingleAsync(ct);
        var max = await db.QuestionVersions.AsNoTracking().Where(item => ids.Contains(item.Id)).SumAsync(item => item.MaxPoints, ct);
        var name = "Тренировка — " + source.Title;
        name = name[..Math.Min(name.Length, 300)];
        var now = timeProvider.GetUtcNow();
        var test = Test.Create(subjectId, actor.TeacherId, name, now);
        db.Tests.Add(test);
        db.TestVersions.Add(TestVersion.Create(test.Id, 1, name, ids, max, now));
        await db.SaveChangesAsync(ct);
        return test.Id;
    }

    private static async Task<TestVersion> LoadVersionAsync(
        ExamForgeDbContext db, TeacherActor actor, Guid assignmentId, CancellationToken ct)
        => await (from assignment in db.Assignments.AsNoTracking()
                  join version in db.TestVersions.AsNoTracking() on assignment.TestVersionId equals version.Id
                  where assignment.Id == assignmentId && (actor.IsAdministrator || assignment.CreatedByTeacherId == actor.TeacherId)
                  select version).SingleOrDefaultAsync(ct)
            ?? throw TeacherFlowErrors.NotFound("Назначение не найдено.");
}
