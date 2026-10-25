using ExamForge.Domain.Dictionary;
using ExamForge.Domain.Files;
using ExamForge.Domain.Questions;
using ExamForge.Domain.Subjects;
using ExamForge.Infrastructure.Identity;
using ExamForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ExamForge.IntegrationTests;

[Collection("database")]
public sealed class QuestionDictionaryTests(ExamForgeWebFactory factory)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private ExamForgeDbContext NewDb()
    {
        var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ExamForgeDbContext>();
    }

    private static async Task<TeacherUser> NewTeacherAsync(ExamForgeDbContext db, string login)
    {
        var teacher = new TeacherUser { UserName = login, DisplayName = "Преподаватель " + login };
        db.Users.Add(teacher);
        await db.SaveChangesAsync();
        return teacher;
    }

    [Fact]
    public async Task QuestionLifecycle_DraftToPublished_VersionIsImmutableSnapshot()
    {
        await using var db = NewDb();
        var subject = Subject.Create("Медицинская латынь", "latmed", Now);
        var teacher = await NewTeacherAsync(db, "q-teacher-1");
        db.Subjects.Add(subject);
        await db.SaveChangesAsync();

        var definition = new QuestionDefinition
        {
            Type = QuestionType.SingleChoice,
            Prompt = "Выберите перевод термина «oculus»",
            Options = [new("a", "глаз"), new("b", "ухо"), new("c", "нос")],
            CorrectOptionId = "a",
            MaxPoints = 1m,
            ProposedTimeLimitSeconds = 40,
        };

        var question = Question.CreateDraft(subject.Id, null, teacher.Id, Now);
        db.Questions.Add(question);
        await db.SaveChangesAsync();

        var version = QuestionVersion.Create(
            questionId: question.Id, versionNumber: 1, type: definition.Type,
            definitionJson: QuestionDefinitionJson.Serialize(definition),
            schemaVersion: 1, maxPoints: definition.MaxPoints,
            proposedTimeLimitSeconds: definition.ProposedTimeLimitSeconds, nowUtc: Now);
        db.QuestionVersions.Add(version);
        await db.SaveChangesAsync();

        // публикация возможна только при валидном определении
        question.Publish(Now, definition);
        version.MarkPublished(Now);
        await db.SaveChangesAsync();

        var reloaded = await db.QuestionVersions.AsNoTracking()
            .SingleAsync(v => v.QuestionId == question.Id);
        Assert.NotNull(reloaded.PublishedAtUtc);
        Assert.Equal(1, reloaded.VersionNumber);
        Assert.Equal(1m, reloaded.MaxPoints);

        // повторная публикация той же версии запрещена: версия неизменяема
        var again = await db.QuestionVersions.SingleAsync(v => v.Id == version.Id);
        Assert.Throws<ExamForge.Domain.Common.DomainException>(() => again.MarkPublished(Now.AddMinutes(1)));
    }

    [Fact]
    public async Task QuestionVersion_UniquePerQuestionAndNumber()
    {
        await using var db = NewDb();
        var subject = Subject.Create("Медицинская латынь " + Guid.NewGuid().ToString("N")[..6], null, Now);
        var teacher = await NewTeacherAsync(db, "q-teacher-2");
        db.Subjects.Add(subject);
        await db.SaveChangesAsync();
        var question = Question.CreateDraft(subject.Id, null, teacher.Id, Now);
        db.Questions.Add(question);
        await db.SaveChangesAsync();

        var v1 = QuestionVersion.Create(question.Id, 1, QuestionType.ShortText,
            QuestionDefinitionJson.Serialize(new QuestionDefinition
            {
                Type = QuestionType.ShortText,
                Prompt = "Тест уникальности версии",
                AcceptedAnswers = [new AcceptedAnswer("ответ")],
                MaxPoints = 1m,
            }), 1, 1m, null, Now);
        db.QuestionVersions.Add(v1);
        await db.SaveChangesAsync();

        var duplicate = QuestionVersion.Create(question.Id, 1, QuestionType.ShortText, "{}", 1, 1m, null, Now);
        db.QuestionVersions.Add(duplicate);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task DictionaryImport_UnknownFeaturesStayForReview_AfterConfirm()
    {
        await using var db = NewDb();
        var subject = Subject.Create("Медицинская латынь " + Guid.NewGuid().ToString("N")[..6], null, Now);
        db.Subjects.Add(subject);
        var dictionary = Dictionary.Create(subject.Id, "Латинско-русский словарь", Now);
        db.Dictionaries.Add(dictionary);
        await db.SaveChangesAsync();

        var entry = DictionaryEntry.Import(
            dictionary.Id, "bacillum, i n — палочка", "bacillum", ["палочка"],
            null, [], "стр. 41", Now);
        db.DictionaryEntries.Add(entry);
        await db.SaveChangesAsync();

        var stored = await db.DictionaryEntries.AsNoTracking().SingleAsync(e => e.Id == entry.Id);
        Assert.True(stored.NeedsReview); // признаки не выдуманы
        Assert.Equal(DictionaryEntryStatus.ImportedDraft, stored.Status);

        var loaded = await db.DictionaryEntries.SingleAsync(e => e.Id == entry.Id);
        loaded.Confirm(new GrammaticalFeatures { Declension = "II", Gender = "n" }, ["палочка"], Now);
        await db.SaveChangesAsync();

        var confirmed = await db.DictionaryEntries.AsNoTracking().SingleAsync(e => e.Id == entry.Id);
        Assert.Equal(DictionaryEntryStatus.Confirmed, confirmed.Status);
        Assert.Equal("II", confirmed.Features!.Declension);
    }

    [Fact]
    public async Task ImportJob_RequiresPreviewBeforeConfirm()
    {
        await using var db = NewDb();
        var subject = Subject.Create("Медицинская латынь " + Guid.NewGuid().ToString("N")[..6], null, Now);
        db.Subjects.Add(subject);
        var dictionary = Dictionary.Create(subject.Id, "Словарь " + Guid.NewGuid().ToString("N")[..6], Now);
        var teacher = await NewTeacherAsync(db, "q-teacher-3");
        var asset = FileAsset.Create(
            "uploads/import/" + Guid.NewGuid(), new string('a', 64),
            "application/pdf", 1234, FileAccessClass.ImportSource, Now, teacher.Id, "dictionary.pdf");
        db.Dictionaries.Add(dictionary);
        db.FileAssets.Add(asset);
        await db.SaveChangesAsync();

        var job = ImportJob.Create(dictionary.Id, asset.Id, teacher.Id, Now);
        db.ImportJobs.Add(job);
        await db.SaveChangesAsync();

        Assert.Throws<ExamForge.Domain.Common.DomainException>(() => job.MarkConfirmed(Now));

        job.MarkPreviewReady(Now);
        job.MarkConfirmed(Now);
        await db.SaveChangesAsync();
        Assert.Equal(ImportJobStatus.Confirmed, job.Status);
    }
}
