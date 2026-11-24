using ExamForge.Application.TeacherFlow;
using ExamForge.Domain.Questions;
using ExamForge.Domain.Testing;
using ExamForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ExamForge.Infrastructure.Services;

public sealed record PreviewQuestion(QuestionVersion Version, QuestionDefinition Definition);
public sealed record ExamPreviewPlan(
    string Title, IReadOnlyList<PreviewQuestion> Questions, Variant? Variant,
    int? PerQuestionTimeLimitSeconds, int? TotalTimeLimitSeconds, DateTimeOffset? ClosesAtUtc = null);

public sealed class ExamPreviewService(
    IDbContextFactory<ExamForgeDbContext> contextFactory, TestVersionService versions)
{
    public async Task<ExamPreviewPlan> LoadAsync(
        TeacherActor actor, Guid? versionId, Guid? assignmentId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        int? perQuestion = null, totalTime = null;
        DateTimeOffset? closesAt = null;
        TestVersion version;

        if (assignmentId is { } id)
        {
            var assignment = await db.Assignments.AsNoTracking().SingleOrDefaultAsync(item =>
                item.Id == id && (actor.IsAdministrator || item.CreatedByTeacherId == actor.TeacherId), ct)
                ?? throw TeacherFlowErrors.NotFound("Назначение не найдено.");
            version = await db.TestVersions.AsNoTracking().SingleAsync(item => item.Id == assignment.TestVersionId, ct);
            perQuestion = assignment.PerQuestionTimeLimitSeconds;
            totalTime = assignment.AttemptTotalTimeLimitSeconds;
            closesAt = assignment.ClosesAtUtc;
        }
        else
        {
            version = await versions.LoadOwnedVersionAsync(actor,
                versionId ?? throw TeacherFlowErrors.NotFound("Версия теста не найдена."), ct);
        }

        var variant = await db.Variants.AsNoTracking().Where(item => item.TestVersionId == version.Id)
            .OrderBy(item => item.VariantNumber).FirstOrDefaultAsync(ct);
        var order = variant?.QuestionOrder ?? version.QuestionVersionIds;
        var byId = await db.QuestionVersions.AsNoTracking().Where(item => order.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, ct);
        var questions = order.Select(id => byId[id])
            .Select(item => new PreviewQuestion(item, QuestionDefinitionJson.Deserialize(item.DefinitionJson))).ToList();
        return new ExamPreviewPlan(version.Title, questions, variant, perQuestion, totalTime, closesAt);
    }
}
