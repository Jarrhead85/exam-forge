using ExamForge.Application.StudentFlow;
using ExamForge.Application.TeacherFlow;
using ExamForge.Domain.Questions;
using ExamForge.Domain.Testing;
using ExamForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ExamForge.Infrastructure.Services;

/// <summary>
/// Управление вариантами версии теста (ТЗ §8) и доступ к печатным представлениям (ТЗ §13).
/// Владение: автор теста или администратор; чужое — 404 без раскрытия.
/// </summary>
public sealed class TestVersionService(ExamForgeDbContext db, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<Variant>> GenerateVariantsAsync(
        TeacherActor actor, Guid testVersionId, int count, bool shuffleQuestions, CancellationToken ct)
    {
        var testVersion = await LoadOwnedVersionAsync(actor, testVersionId, ct);
        if (testVersion.PublishedAtUtc is null)
            throw TeacherFlowErrors.Conflict("not_published", "Варианты генерируются для опубликованной версии теста.");

        // Повторная генерация: варианты, на которые ссылаются попытки, сохраняются (FK + история работ);
        // удаляются только не использованные. Новые варианты продолжают нумерацию.
        var existing = await db.Variants.Where(v => v.TestVersionId == testVersionId).ToListAsync(ct);
        var existingIds = existing.Select(v => v.Id).ToList();
        var referencedIds = await db.Attempts.AsNoTracking()
            .Where(a => a.VariantId != null && existingIds.Contains(a.VariantId.Value))
            .Select(a => a.VariantId!.Value)
            .Distinct()
            .ToListAsync(ct);
        var removable = existing.Where(v => !referencedIds.Contains(v.Id)).ToList();
        db.Variants.RemoveRange(removable);

        var definitions = await db.QuestionVersions.AsNoTracking()
            .Where(version => testVersion.QuestionVersionIds.Contains(version.Id))
            .ToDictionaryAsync(version => version.Id, version => QuestionDefinitionJson.Deserialize(version.DefinitionJson), ct);

        var nextNumber = existing.Count == 0 ? 1 : existing.Max(v => v.VariantNumber) + 1;
        var variants = VariantGenerator.Generate(
            testVersion, definitions, count,
            baseSeed: $"ef:{testVersionId}:{timeProvider.GetUtcNow().UtcTicks}",
            shuffleQuestions, timeProvider.GetUtcNow(), firstVariantNumber: nextNumber);
        db.Variants.AddRange(variants);
        await db.SaveChangesAsync(ct);
        return variants;
    }

    public async Task<Guid> DuplicateAsDraftAsync(TeacherActor actor, Guid versionId, CancellationToken ct)
    {
        var version = await LoadOwnedVersionAsync(actor, versionId, ct);
        var source = await db.Tests.AsNoTracking().SingleAsync(test => test.Id == version.TestId, ct);
        var name = "Копия — " + source.Title;
        var now = timeProvider.GetUtcNow();
        var copy = Test.Create(source.SubjectId, actor.TeacherId, name[..Math.Min(name.Length, 300)], now);
        var draft = TestVersion.Create(copy.Id, 1, version.Title, version.QuestionVersionIds, version.MaxPoints, now);
        db.Tests.Add(copy);
        db.TestVersions.Add(draft);
        await db.SaveChangesAsync(ct);
        return copy.Id;
    }

    public async Task<IReadOnlyList<Variant>> ListVariantsAsync(TeacherActor actor, Guid testVersionId, CancellationToken ct)
    {
        await LoadOwnedVersionAsync(actor, testVersionId, ct);
        return await db.Variants.AsNoTracking()
            .Where(v => v.TestVersionId == testVersionId)
            .OrderBy(v => v.VariantNumber)
            .ToListAsync(ct);
    }

    public async Task<TestVersion> LoadOwnedVersionAsync(TeacherActor actor, Guid testVersionId, CancellationToken ct)
    {
        var testVersion = await db.TestVersions.AsNoTracking()
            .SingleOrDefaultAsync(v => v.Id == testVersionId, ct);
        if (testVersion is null)
            throw TeacherFlowErrors.NotFound("Версия теста не найдена.");
        var authorId = await db.Tests.AsNoTracking()
            .Where(t => t.Id == testVersion.TestId)
            .Select(t => t.AuthorTeacherId)
            .SingleAsync(ct);
        if (!actor.IsAdministrator && authorId != actor.TeacherId)
            throw TeacherFlowErrors.NotFound("Версия теста не найдена.");
        return testVersion;
    }
}
