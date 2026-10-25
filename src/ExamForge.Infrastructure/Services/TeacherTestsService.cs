using ExamForge.Application.TeacherFlow;
using ExamForge.Domain.Questions;
using ExamForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ExamForge.Infrastructure.Services;

public sealed record TestListDto(Guid TestId, string Title, DateTimeOffset CreatedAtUtc, IReadOnlyList<TestVersionBriefDto> Versions);
public sealed record TestVersionBriefDto(Guid VersionId, int VersionNumber, string Title, bool IsPublished, int QuestionCount, decimal MaxPoints);

public sealed record BlueprintLineRequest(string? QuestionType, int Count);
public sealed record BlueprintPreviewRow(Guid QuestionVersionId, string Type, string Prompt, decimal MaxPoints);
public sealed record BlueprintPreviewDto(
    IReadOnlyList<BlueprintPreviewRow> Picked,
    IReadOnlyList<string> Warnings,
    decimal TotalMaxPoints);

/// <summary>
/// Тесты и версии (ТЗ §8): конструктор с ручным подбором и Blueprint-предпросмотром
/// (явные предупреждения о недостатке банка, без молчаливых дублей — AC-14).
/// </summary>
public static class TeacherTestsService
{
    public static async Task<Domain.Testing.Test> CreateTestAsync(
        ExamForgeDbContext db, TeacherActor actor, string title, Guid? subjectId, CancellationToken ct)
    {
        Guid subject = subjectId
            ?? await db.Subjects.AsNoTracking().OrderBy(s => s.CreatedAtUtc).Select(s => s.Id).FirstAsync(ct);
        var test = Domain.Testing.Test.Create(subject, actor.TeacherId, title, DateTime.UtcNow);
        db.Tests.Add(test);
        await db.SaveChangesAsync(ct);
        return test;
    }

    public static async Task RenameTestAsync(
        ExamForgeDbContext db, TeacherActor actor, Guid testId, string title, CancellationToken ct)
    {
        var test = await LoadOwnedTestAsync(db, actor, testId, ct);
        test.Rename(title);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Удаление теста (manual delete, ТЗ §21): запрещено при любых попытках или назначениях —
    /// попытки хранятся 5 лет и каскадно удаляться не могут. Неиспользуемые версии и варианты
    /// удаляются вместе с тестом.
    /// </summary>
    public static async Task DeleteTestAsync(ExamForgeDbContext db, TeacherActor actor, Guid testId, CancellationToken ct)
    {
        var test = await LoadOwnedTestAsync(db, actor, testId, ct);
        var versionIds = await db.TestVersions.AsNoTracking()
            .Where(v => v.TestId == testId)
            .Select(v => v.Id)
            .ToListAsync(ct);

        var attemptCount = await db.Attempts.AsNoTracking()
            .Where(a => a.VariantId != null
                        && db.Variants.Any(v => v.Id == a.VariantId && versionIds.Contains(v.TestVersionId)))
            .CountAsync(ct);
        if (attemptCount > 0)
            throw TeacherFlowErrors.Conflict("test_has_attempts",
                $"По тесту есть попытки ({attemptCount}) — история не удаляется (ТЗ §21).");
        var assignmentCount = await db.Assignments.AsNoTracking()
            .Where(a => versionIds.Contains(a.TestVersionId)).CountAsync(ct);
        if (assignmentCount > 0)
            throw TeacherFlowErrors.Conflict("test_has_assignments",
                $"Версии теста использованы в назначениях ({assignmentCount}) — сначала удалите назначения.");

        var variants = await db.Variants.Where(v => versionIds.Contains(v.TestVersionId)).ToListAsync(ct);
        var versions = await db.TestVersions.Where(v => v.TestId == testId).ToListAsync(ct);
        db.Variants.RemoveRange(variants);
        db.TestVersions.RemoveRange(versions);
        db.Tests.Remove(test);
        await db.SaveChangesAsync(ct);
    }

    private static async Task<Domain.Testing.Test> LoadOwnedTestAsync(
        ExamForgeDbContext db, TeacherActor actor, Guid testId, CancellationToken ct)
    {
        var test = await db.Tests.SingleOrDefaultAsync(t => t.Id == testId, ct)
            ?? throw TeacherFlowErrors.NotFound("Тест не найден.");
        if (!actor.IsAdministrator && test.AuthorTeacherId != actor.TeacherId)
            throw TeacherFlowErrors.NotFound("Тест не найден.");
        return test;
    }

    public static async Task<List<TestListDto>> ListAsync(ExamForgeDbContext db, TeacherActor actor, CancellationToken ct)
    {
        var query = db.Tests.AsNoTracking().AsQueryable();
        if (!actor.IsAdministrator)
            query = query.Where(t => t.AuthorTeacherId == actor.TeacherId);
        var tests = await query.OrderByDescending(t => t.CreatedAtUtc).ToListAsync(ct);
        var testIds = tests.Select(t => t.Id).ToList();
        var versions = await db.TestVersions.AsNoTracking()
            .Where(v => testIds.Contains(v.TestId))
            .OrderBy(v => v.VersionNumber)
            .ToListAsync(ct);
        return [.. tests.Select(t => new TestListDto(t.Id, t.Title, t.CreatedAtUtc,
            [.. versions.Where(v => v.TestId == t.Id).Select(v => new TestVersionBriefDto(
                v.Id, v.VersionNumber, v.Title, v.PublishedAtUtc != null, v.QuestionVersionIds.Count, v.MaxPoints))]))];
    }

    /// <summary>Создать черновик версии из выбранных вопросов (порядок фиксирован, дубликаты запрещены).</summary>
    public static async Task<Domain.Testing.TestVersion> CreateVersionAsync(
        ExamForgeDbContext db, TeacherActor actor, Guid testId, string title,
        IReadOnlyList<Guid> questionVersionIds, CancellationToken ct)
    {
        var test = await db.Tests.SingleOrDefaultAsync(t => t.Id == testId, ct)
            ?? throw TeacherFlowErrors.NotFound("Тест не найден.");
        if (!actor.IsAdministrator && test.AuthorTeacherId != actor.TeacherId)
            throw TeacherFlowErrors.NotFound("Тест не найден.");

        // все версии заданий должны быть опубликованы (ТЗ §8: фиксируются использованные версии)
        var published = await db.QuestionVersions.AsNoTracking()
            .Where(v => questionVersionIds.Contains(v.Id) && v.PublishedAtUtc != null)
            .ToDictionaryAsync(v => v.Id, v => v.MaxPoints, ct);
        foreach (var id in questionVersionIds)
            if (!published.ContainsKey(id))
                throw TeacherFlowErrors.Conflict("not_published", "Версия теста собирается только из опубликованных заданий.");

        var maxPoints = published.Values.Sum();
        var next = (await db.TestVersions.AsNoTracking()
            .Where(v => v.TestId == testId)
            .Select(v => (int?)v.VersionNumber)
            .MaxAsync(ct) ?? 0) + 1;
        var version = Domain.Testing.TestVersion.Create(testId, next, title, questionVersionIds, maxPoints, DateTime.UtcNow);
        db.TestVersions.Add(version);
        await db.SaveChangesAsync(ct);
        return version;
    }

    public static async Task PublishVersionAsync(
        ExamForgeDbContext db, TeacherActor actor, Guid versionId, CancellationToken ct)
    {
        var version = await db.TestVersions.SingleOrDefaultAsync(v => v.Id == versionId, ct)
            ?? throw TeacherFlowErrors.NotFound("Версия теста не найдена.");
        var test = await db.Tests.AsNoTracking().SingleAsync(t => t.Id == version.TestId, ct);
        if (!actor.IsAdministrator && test.AuthorTeacherId != actor.TeacherId)
            throw TeacherFlowErrors.NotFound("Версия теста не найдена.");
        version.Publish(DateTime.UtcNow);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Blueprint-предпросмотр по банку опубликованных заданий автора (AC-14).</summary>
    public static async Task<BlueprintPreviewDto> PreviewBlueprintAsync(
        ExamForgeDbContext db, TeacherActor actor,
        IReadOnlyList<BlueprintLineRequest> lines, string seed, CancellationToken ct)
    {
        var myPublished = await db.QuestionVersions.AsNoTracking()
            .Where(v => v.PublishedAtUtc != null
                        && (actor.IsAdministrator || db.Questions.Any(q => q.Id == v.QuestionId && q.AuthorTeacherId == actor.TeacherId)))
            .Join(db.Questions.AsNoTracking(),
                v => v.QuestionId, q => q.Id, (v, q) => new { v.Id, v.Type, v.MaxPoints, q.TopicId, v.DefinitionJson })
            .ToListAsync(ct);

        var candidates = myPublished
            .Select(m => new BankCandidate(m.Id, m.Type, m.TopicId, [], m.MaxPoints))
            .ToList();
        var blueprint = lines
            .Select(l => new BlueprintLine(ParseType(l.QuestionType), null, null, l.Count, 0m))
            .ToList();
        var pick = BankPicker.Pick(candidates, blueprint, seed);
        var byId = myPublished.ToDictionary(m => m.Id);

        return new BlueprintPreviewDto(
            [.. pick.Picked.Select(id => new BlueprintPreviewRow(
                id, byId[id].Type.ToString(), ExtractPrompt(byId[id].DefinitionJson), byId[id].MaxPoints))],
            pick.Warnings,
            pick.Picked.Sum(id => byId[id].MaxPoints));
    }

    private static QuestionType? ParseType(string? value)
        => value is null ? null
           : Enum.TryParse<QuestionType>(value, ignoreCase: true, out var type) ? type
           : null;

    private static string ExtractPrompt(string definitionJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(definitionJson);
            if (doc.RootElement.TryGetProperty("Prompt", out var prompt) && prompt.GetString() is { } p)
                return p;
            if (doc.RootElement.TryGetProperty("prompt", out var p2) && p2.GetString() is { } p2s)
                return p2s;
            return "";
        }
        catch
        {
            return "";
        }
    }
}
