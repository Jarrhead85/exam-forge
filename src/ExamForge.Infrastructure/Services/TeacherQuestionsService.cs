using ExamForge.Application.StudentFlow;
using ExamForge.Application.TeacherFlow;
using ExamForge.Domain.Questions;
using ExamForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ExamForge.Infrastructure.Services;

public sealed record QuestionVersionDto(
    Guid QuestionId,
    int VersionNumber,
    string Status,
    string Type,
    decimal MaxPoints,
    string Prompt,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? PublishedAtUtc);

public sealed record SaveQuestionRequest(
    string Type,
    string Prompt,
    string? Instruction,
    decimal MaxPoints,
    int? TimeLimitSeconds,
    // per-type:
    IReadOnlyList<(string Id, string Text)>? Options,
    string? CorrectOptionId,
    IReadOnlyList<string>? CorrectOptionIds,
    string? Scoring,
    IReadOnlyList<string>? AcceptedAnswers,
    bool? IgnoreCase,
    bool? IgnoreDiacritics,
    bool? SendToReview,
    IReadOnlyList<(string Id, string Label, string Correct, decimal Points)>? Fields,
    IReadOnlyList<(string LeftId, string Left, string RightId, string Right)>? Pairs,
    string? TargetWord,
    IReadOnlyList<string>? OrderItems,
    IReadOnlyList<string>? BlankIds,
    IReadOnlyDictionary<string, string>? BlankAccepted,
    IReadOnlyDictionary<string, string>? BlankPoints);

/// <summary>
/// Банк заданий преподавателя (ТЗ §7): черновики → ревью → публикация (валидация §7.1);
/// изменение опубликованного создаёт новую редакцию, чужие работы не затрагиваются (AC-15).
/// </summary>
public sealed class TeacherQuestionsService(ExamForgeDbContext db, TimeProvider timeProvider)
{
    private DateTimeOffset Now => timeProvider.GetUtcNow();

    public async Task<QuestionVersion> CreateDraftAsync(TeacherActor actor, Guid subjectId, QuestionDefinition definition, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(definition.Prompt))
            throw TeacherFlowErrors.Conflict("bad_request", "Формулировка вопроса обязательна.");
        var question = Question.CreateDraft(subjectId, null, actor.TeacherId, Now);
        db.Questions.Add(question);
        await db.SaveChangesAsync(ct);
        var version = QuestionVersion.Create(question.Id, 1, definition.Type,
            QuestionDefinitionJson.Serialize(definition), definition.SchemaVersion,
            definition.MaxPoints, definition.ProposedTimeLimitSeconds, Now);
        db.QuestionVersions.Add(version);
        await db.SaveChangesAsync(ct);
        return version;
    }

    /// <summary>Правка: для черновика заменяет текущую версию; для опубликованного — новая редакция (AC-15).</summary>
    public async Task<QuestionVersion> EditAsync(TeacherActor actor, Guid questionId, QuestionDefinition definition, CancellationToken ct)
    {
        var (question, latest) = await LoadOwnedLatestAsync(actor, questionId, ct);
        if (question.Status == QuestionStatus.Draft)
        {
            var replacement = QuestionVersion.Create(question.Id, latest.VersionNumber, definition.Type,
                QuestionDefinitionJson.Serialize(definition), definition.SchemaVersion,
                definition.MaxPoints, definition.ProposedTimeLimitSeconds, Now);
            db.QuestionVersions.Remove(latest);
            db.QuestionVersions.Add(replacement);
            await db.SaveChangesAsync(ct);
            return replacement;
        }
        var next = QuestionVersion.Create(question.Id, latest.VersionNumber + 1, definition.Type,
            QuestionDefinitionJson.Serialize(definition), definition.SchemaVersion,
            definition.MaxPoints, definition.ProposedTimeLimitSeconds, Now);
        db.QuestionVersions.Add(next);
        question.ReturnToDraft();
        await db.SaveChangesAsync(ct);
        return next;
    }

    public async Task<QuestionVersion> PublishAsync(TeacherActor actor, Guid questionId, CancellationToken ct)
    {
        var (question, _) = await LoadOwnedLatestAsync(actor, questionId, ct);
        // загружаем с tracking: MarkPublished должен сохраниться в БД
        var latestTracked = await db.QuestionVersions
            .Where(v => v.QuestionId == questionId)
            .OrderByDescending(v => v.VersionNumber)
            .FirstAsync(ct);
        var definition = QuestionDefinitionJson.Deserialize(latestTracked.DefinitionJson);
        var errors = QuestionValidator.ValidateForPublish(definition);
        if (errors.Count > 0)
            throw TeacherFlowErrors.Conflict("invalid_question", "Опубликовать нельзя: " + string.Join(" ", errors));

        question.Publish(Now, definition);
        latestTracked.MarkPublished(Now);
        await db.SaveChangesAsync(ct);
        return latestTracked;
    }

    /// <summary>Загрузка задания для правки в форме: домен + последняя версия (права автора/админа).</summary>
    public async Task<(Guid QuestionId, Guid SubjectId, QuestionDefinition Definition)> GetForEditAsync(
        TeacherActor actor, Guid questionId, CancellationToken ct)
    {
        var (question, latest) = await LoadOwnedLatestAsync(actor, questionId, ct);
        return (question.Id, question.SubjectId, QuestionDefinitionJson.Deserialize(latest.DefinitionJson));
    }

    /// <summary>Удаление задания: только черновик. Опубликованные версии фиксируются в тестах (ТЗ §8) и не удаляются.</summary>
    public async Task DeleteDraftAsync(TeacherActor actor, Guid questionId, CancellationToken ct)
    {
        var question = await db.Questions.SingleOrDefaultAsync(q => q.Id == questionId, ct)
            ?? throw TeacherFlowErrors.NotFound("Задание не найдено.");
        if (!actor.IsAdministrator && question.AuthorTeacherId != actor.TeacherId)
            throw TeacherFlowErrors.NotFound("Задание не найдено.");
        if (question.Status != QuestionStatus.Draft)
            throw TeacherFlowErrors.Conflict("not_a_draft",
                "Удалять можно только черновики. Опубликованные задания используются версиями тестов (ТЗ §8).");
        var versions = await db.QuestionVersions.Where(v => v.QuestionId == questionId).ToListAsync(ct);
        db.QuestionVersions.RemoveRange(versions);
        db.Questions.Remove(question);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<QuestionVersionDto>> ListAsync(TeacherActor actor, CancellationToken ct)
    {
        var query = db.Questions.AsNoTracking().AsQueryable();
        if (!actor.IsAdministrator)
            query = query.Where(q => q.AuthorTeacherId == actor.TeacherId);
        var questions = await query.Select(q => new { q.Id, q.Status }).ToListAsync(ct);
        var ids = questions.Select(q => q.Id).ToList();

        // Только последняя версия каждого задания: лёгкая проекция без DefinitionJson,
        // затем один запрос полных строк по Id — история редакций не гоняется по сети.
        var versionKeys = await db.QuestionVersions.AsNoTracking()
            .Where(v => ids.Contains(v.QuestionId))
            .Select(v => new { v.Id, v.QuestionId, v.VersionNumber })
            .ToListAsync(ct);
        var latestIds = versionKeys
            .GroupBy(v => v.QuestionId)
            .Select(g => g.OrderByDescending(v => v.VersionNumber).First().Id)
            .ToList();
        var latestVersions = await db.QuestionVersions.AsNoTracking()
            .Where(v => latestIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.QuestionId, ct);

        var result = new List<QuestionVersionDto>();
        foreach (var q in questions)
        {
            if (!latestVersions.TryGetValue(q.Id, out var latest))
                continue;
            var definition = QuestionDefinitionJson.Deserialize(latest.DefinitionJson);
            result.Add(new QuestionVersionDto(
                q.Id, latest.VersionNumber, q.Status.ToString(), latest.Type.ToString(),
                latest.MaxPoints, definition.Prompt, latest.CreatedAtUtc, latest.PublishedAtUtc));
        }
        return result.OrderByDescending(r => r.CreatedAtUtc).ToList();
    }

    public async Task<(Domain.Questions.Question Question, QuestionVersion Latest)> LoadOwnedLatestAsync(
        TeacherActor actor, Guid questionId, CancellationToken ct)
    {
        var question = await db.Questions.SingleOrDefaultAsync(q => q.Id == questionId, ct)
            ?? throw TeacherFlowErrors.NotFound("Вопрос не найден.");
        if (!actor.IsAdministrator && question.AuthorTeacherId != actor.TeacherId)
            throw TeacherFlowErrors.NotFound("Вопрос не найден.");
        var latest = await db.QuestionVersions.AsNoTracking()
            .Where(v => v.QuestionId == questionId)
            .OrderByDescending(v => v.VersionNumber)
            .FirstAsync(ct);
        return (question, latest);
    }
}
