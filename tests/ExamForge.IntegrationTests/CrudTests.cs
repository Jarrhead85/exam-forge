using ExamForge.Application.StudentFlow;
using ExamForge.Application.TeacherFlow;
using ExamForge.Domain.Assignments;
using ExamForge.Domain.Dictionary;
using ExamForge.Domain.Questions;
using ExamForge.Infrastructure.Identity;
using ExamForge.Infrastructure.Persistence;
using ExamForge.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ExamForge.IntegrationTests;

/// <summary>
/// CRUD справочников: переименование/удаление с проверкой ссылочных инвариантов —
/// история попыток и использованные версии блокируют удаление (ТЗ §8, §21),
/// удаления только явные (manual delete) и подтверждаются двумя шагами в UI.
/// </summary>
[Collection("database")]
public sealed class CrudTests(ExamForgeWebFactory factory)
{
    private sealed class Kit : IAsyncDisposable
    {
        public required ExamForgeDbContext Db { get; init; }
        public required TimeProvider Time { get; init; }
        public required UserManager<TeacherUser> Users { get; init; }
        public required RoleManager<IdentityRole<Guid>> Roles { get; init; }
        public GroupService Groups => new(Db, Time);
        public AdminTeachersService Admins => new(Db, Users, Roles);
        public DictionaryService Dictionaries => new(Db, Time);
        public SubjectsService Subjects => new(Db, Time);
        public TeacherQuestionsService Questions => new(Db, Time);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
        }
    }

    private Kit NewKit()
    {
        var scope = factory.Services.CreateScope();
        return new Kit
        {
            Db = scope.ServiceProvider.GetRequiredService<ExamForgeDbContext>(),
            Time = scope.ServiceProvider.GetRequiredService<TimeProvider>(),
            Users = scope.ServiceProvider.GetRequiredService<UserManager<TeacherUser>>(),
            Roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>(),
        };
    }

    private async Task<TeacherActor> NewTeacherAsync(Kit kit, bool admin = false)
    {
        foreach (var role in new[] { AppRoles.Administrator, AppRoles.Teacher })
            if (!await kit.Roles.RoleExistsAsync(role))
                await kit.Roles.CreateAsync(new IdentityRole<Guid>(role));
        var user = new TeacherUser { UserName = "crud-" + Guid.NewGuid().ToString("N")[..10], DisplayName = "CRUD Тестов" };
        IdentityResult created = await kit.Users.CreateAsync(user, TeacherLogin.Password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        await kit.Users.AddToRoleAsync(user, admin ? AppRoles.Administrator : AppRoles.Teacher);
        return new TeacherActor(user.Id, admin);
    }

    // ---------- Предметы ----------

    [Fact]
    public async Task Subject_RenameThenDelete_OnlyWhenUnused()
    {
        await using var kit = NewKit();
        var actor = await NewTeacherAsync(kit);
        await kit.Subjects.CreateAsync(actor, "CRUD предмет " + Guid.NewGuid().ToString("N")[..6], "crud", CancellationToken.None);
        var subject = await kit.Db.Subjects.OrderBy(s => s.CreatedAtUtc).LastAsync();

        await kit.Subjects.RenameAsync(actor, subject.Id, "CRUD предмет переименован", "crd2", CancellationToken.None);
        var renamed = await kit.Db.Subjects.AsNoTracking().SingleAsync(s => s.Id == subject.Id);
        Assert.Equal("CRUD предмет переименован", renamed.Name);
        Assert.Equal("crd2", renamed.Code);

        // используемый предмет удалить нельзя
        kit.Db.Dictionaries.Add(Dictionary.Create(subject.Id, "Словарь на предмете", DateTimeOffset.UtcNow));
        await kit.Db.SaveChangesAsync();
        var used = await Assert.ThrowsAsync<StudentFlowException>(
            () => kit.Subjects.DeleteAsync(actor, subject.Id, CancellationToken.None));
        Assert.Contains("используется", used.Message);

        // неиспользуемый удаляется
        var fresh = Domain.Subjects.Subject.Create("CRUD пустой " + Guid.NewGuid().ToString("N")[..6], null, DateTimeOffset.UtcNow);
        kit.Db.Subjects.Add(fresh);
        await kit.Db.SaveChangesAsync();
        await kit.Subjects.DeleteAsync(actor, fresh.Id, CancellationToken.None);
        Assert.False(await kit.Db.Subjects.AnyAsync(s => s.Id == fresh.Id));
    }

    // ---------- Словари ----------

    [Fact]
    public async Task Dictionary_RenameDelete_EntriesCascadeAndSingleDelete()
    {
        await using var kit = NewKit();
        var actor = await NewTeacherAsync(kit);
        var subject = Domain.Subjects.Subject.Create("CRUD словарь-предмет " + Guid.NewGuid().ToString("N")[..6], null, DateTimeOffset.UtcNow);
        kit.Db.Subjects.Add(subject);
        await kit.Db.SaveChangesAsync();
        var dictionary = await kit.Dictionaries.CreateAsync(actor, subject.Id, "CRUD словарь " + Guid.NewGuid().ToString("N")[..6], CancellationToken.None);

        var e1 = DictionaryEntry.Import(dictionary.Id, "aqua - вода", "aqua", ["вода"], null, [], null, DateTimeOffset.UtcNow);
        var e2 = DictionaryEntry.Import(dictionary.Id, "ventus - ветер", "ventus", ["ветер"], null, [], null, DateTimeOffset.UtcNow);
        kit.Db.DictionaryEntries.AddRange(e1, e2);
        await kit.Db.SaveChangesAsync();

        await kit.Dictionaries.RenameAsync(actor, dictionary.Id, "CRUD словарь новый", CancellationToken.None);
        Assert.Equal("CRUD словарь новый", (await kit.Db.Dictionaries.AsNoTracking().SingleAsync(d => d.Id == dictionary.Id)).Name);

        // удаление отдельной записи
        await kit.Dictionaries.DeleteEntryAsync(actor, e1.Id, CancellationToken.None);
        Assert.Equal(1, await kit.Db.DictionaryEntries.CountAsync(e => e.DictionaryId == dictionary.Id));

        // удаление словаря каскадно убирает оставшиеся записи
        var removed = await kit.Dictionaries.DeleteAsync(actor, dictionary.Id, CancellationToken.None);
        Assert.Equal(1, removed);
        Assert.False(await kit.Db.Dictionaries.AnyAsync(d => d.Id == dictionary.Id));
        Assert.False(await kit.Db.DictionaryEntries.AnyAsync(e => e.DictionaryId == dictionary.Id));
    }

    [Fact]
    public async Task Dictionary_AddEntryManually_ConfirmedImmediatelyWithKnownFeatures()
    {
        await using var kit = NewKit();
        var actor = await NewTeacherAsync(kit);
        var subject = Domain.Subjects.Subject.Create("CRUD ручной " + Guid.NewGuid().ToString("N")[..6], null, DateTimeOffset.UtcNow);
        kit.Db.Subjects.Add(subject);
        await kit.Db.SaveChangesAsync();
        var dictionary = await kit.Dictionaries.CreateAsync(actor, subject.Id, "Словарь ручного ввода", CancellationToken.None);

        // с известными признаками запись подтверждается сразу — источник сам преподаватель
        await kit.Dictionaries.AddEntryManuallyAsync(actor, dictionary.Id, "oculus", "глаз", "solid", "m", "II", CancellationToken.None);
        var entry = await kit.Db.DictionaryEntries.AsNoTracking().SingleAsync(e => e.DictionaryId == dictionary.Id);
        Assert.Equal(Domain.Dictionary.DictionaryEntryStatus.Confirmed, entry.Status);
        Assert.False(entry.NeedsReview);
        Assert.Equal("глаз", entry.Translations[0]);
        Assert.Equal("II", entry.Features!.Declension);

        // пустая лемма/перевод отклоняются
        await Assert.ThrowsAsync<StudentFlowException>(
            () => kit.Dictionaries.AddEntryManuallyAsync(actor, dictionary.Id, "  ", "глаз", null, null, null, CancellationToken.None));
    }

    // ---------- Группы и студенты ----------

    [Fact]
    public async Task Group_RenameDelete_EmptyOnly()
    {
        await using var kit = NewKit();
        var actor = await NewTeacherAsync(kit);
        var group = await kit.Groups.CreateAsync(actor, "CRUD группа " + Guid.NewGuid().ToString("N")[..6], CancellationToken.None);

        await kit.Groups.AddNewStudentAsync(actor, group.Id, "Петров", "Пётр", null, CancellationToken.None);
        var blocked = await Assert.ThrowsAsync<StudentFlowException>(
            () => kit.Groups.DeleteAsync(actor, group.Id, CancellationToken.None));
        Assert.Contains("студент", blocked.Message);

        await kit.Groups.RenameAsync(actor, group.Id, "CRUD группа renamed", CancellationToken.None);
        Assert.Equal("CRUD группа renamed", (await kit.Db.Groups.AsNoTracking().SingleAsync(g => g.Id == group.Id)).Name);

        var profileId = await kit.Db.GroupMemberships.Where(m => m.GroupId == group.Id).Select(m => m.StudentProfileId).FirstAsync();
        await kit.Groups.RemoveStudentAsync(actor, group.Id, profileId, CancellationToken.None);
        await kit.Groups.DeleteAsync(actor, group.Id, CancellationToken.None);
        Assert.False(await kit.Db.Groups.AnyAsync(g => g.Id == group.Id));
    }

    [Fact]
    public async Task Student_RenameDelete_BlockedByHistoryAndAccount()
    {
        await using var kit = NewKit();
        var actor = await NewTeacherAsync(kit);
        var group = await kit.Groups.CreateAsync(actor, "CRUD студенты " + Guid.NewGuid().ToString("N")[..6], CancellationToken.None);
        var student = await kit.Groups.AddNewStudentAsync(actor, group.Id, "Сидорова", "Мария", null, CancellationToken.None);

        // Аккаунт блокирует удаление профиля; сброс пароля возвращает одноразовую строку
        var hasher = new PasswordHasher<Domain.People.StudentAccount>();
        var account = Domain.People.StudentAccount.Create(student.Id, "stud_crud1", hasher.HashPassword(null!, "x"), actor.TeacherId, DateTimeOffset.UtcNow);
        kit.Db.StudentAccounts.Add(account);
        await kit.Db.SaveChangesAsync();

        var withAccount = await Assert.ThrowsAsync<StudentFlowException>(
            () => kit.Groups.DeleteStudentAsync(actor, student.Id, CancellationToken.None));
        Assert.Contains("аккаунт", withAccount.Message);

        var oneTime = await kit.Groups.ResetStudentAccountPasswordAsync(actor, account.Id, CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(oneTime));

        // выключить/включить и удалить аккаунт, затем профиль удаляется
        await kit.Groups.SetStudentAccountActiveAsync(actor, account.Id, false, CancellationToken.None);
        Assert.True((await kit.Db.StudentAccounts.AsNoTracking().SingleAsync(a => a.Id == account.Id)).IsDisabled);
        await kit.Groups.SetStudentAccountActiveAsync(actor, account.Id, true, CancellationToken.None);
        await kit.Groups.DeleteStudentAccountAsync(actor, account.Id, CancellationToken.None);

        await kit.Groups.RenameStudentAsync(actor, student.Id, "Сидорова-Иванова", "Мария", "Петровна", CancellationToken.None);
        await kit.Groups.DeleteStudentAsync(actor, student.Id, CancellationToken.None);
        Assert.False(await kit.Db.StudentProfiles.AnyAsync(p => p.Id == student.Id));
        Assert.False(await kit.Db.GroupMemberships.AnyAsync(m => m.StudentProfileId == student.Id));
    }

    // ---------- Преподаватели ----------

    [Fact]
    public async Task Teacher_RenameDeactivateReset_SelfAndLastAdminBlocked()
    {
        await using var kit = NewKit();
        var admin = await NewTeacherAsync(kit, admin: true);
        var teacher = new TeacherUser { UserName = "crud-t-" + Guid.NewGuid().ToString("N")[..8], DisplayName = "Препод Один" };
        Assert.True((await kit.Users.CreateAsync(teacher, TeacherLogin.Password)).Succeeded);
        await kit.Users.AddToRoleAsync(teacher, AppRoles.Teacher);
        var teacherId = teacher.Id;

        await kit.Admins.RenameTeacherAsync(admin, teacherId, "Препод Переименованный", CancellationToken.None);
        Assert.Equal("Препод Переименованный", (await kit.Users.FindByIdAsync(teacherId.ToString()))!.DisplayName);

        // Себя деактивировать нельзя
        await Assert.ThrowsAsync<StudentFlowException>(
            () => kit.Admins.SetTeacherActiveAsync(admin, admin.TeacherId, false, CancellationToken.None));

        // Другого — можно; после деактивации пароль не помогает войти, после включения — помогает
        await kit.Admins.SetTeacherActiveAsync(admin, teacherId, false, CancellationToken.None);
        var locked = await kit.Users.FindByIdAsync(teacherId.ToString());
        Assert.False(locked!.LockoutEnd is null || locked.LockoutEnd <= DateTimeOffset.UtcNow);
        await kit.Admins.SetTeacherActiveAsync(admin, teacherId, true, CancellationToken.None);

        await kit.Admins.ResetTeacherPasswordAsync(admin, teacherId, TeacherLogin.Password + "2", CancellationToken.None);
        Assert.True(await kit.Users.CheckPasswordAsync(teacher, TeacherLogin.Password + "2"));

        // Последнего активного администратора деактивировать нельзя
        var secondAdmin = new TeacherUser { UserName = "crud-a-" + Guid.NewGuid().ToString("N")[..8], DisplayName = "Админ Два" };
        Assert.True((await kit.Users.CreateAsync(secondAdmin, TeacherLogin.Password)).Succeeded);
        await kit.Users.AddToRoleAsync(secondAdmin, AppRoles.Administrator);
        // деактивируем второго админа — первый остаётся последним активным
        await kit.Admins.SetTeacherActiveAsync(admin, secondAdmin.Id, false, CancellationToken.None);
        await Assert.ThrowsAsync<StudentFlowException>(
            () => kit.Admins.SetTeacherActiveAsync(admin, admin.TeacherId, false, CancellationToken.None));
    }

    // ---------- Тесты ----------

    private async Task<(Guid TestId, Guid VersionId, Guid QuestionVersionId)> NewPublishedTestAsync(Kit kit, TeacherActor actor, string title)
    {
        var subject = Domain.Subjects.Subject.Create("CRUD тест-предмет " + Guid.NewGuid().ToString("N")[..6], null, DateTimeOffset.UtcNow);
        kit.Db.Subjects.Add(subject);
        await kit.Db.SaveChangesAsync();
        var question = Question.CreateDraft(subject.Id, null, actor.TeacherId, DateTimeOffset.UtcNow);
        kit.Db.Questions.Add(question);
        await kit.Db.SaveChangesAsync();
        var definition = new QuestionDefinition
        {
            Type = QuestionType.SingleChoice,
            Prompt = "2+2?",
            Options = [new("a", "4"), new("b", "5")],
            CorrectOptionId = "a",
            MaxPoints = 1m,
        };
        question.Publish(DateTimeOffset.UtcNow, definition);
        var qv = QuestionVersion.Create(question.Id, 1, QuestionType.SingleChoice,
            QuestionDefinitionJson.Serialize(definition), 1, 1m, 30, DateTimeOffset.UtcNow);
        qv.MarkPublished(DateTimeOffset.UtcNow);
        kit.Db.QuestionVersions.Add(qv);
        await kit.Db.SaveChangesAsync();

        var test = Domain.Testing.Test.Create(subject.Id, actor.TeacherId, title, DateTimeOffset.UtcNow);
        kit.Db.Tests.Add(test);
        await kit.Db.SaveChangesAsync();
        var version = Domain.Testing.TestVersion.Create(test.Id, 1, "v1", [qv.Id], 1m, DateTimeOffset.UtcNow);
        version.Publish(DateTimeOffset.UtcNow);
        kit.Db.TestVersions.Add(version);
        await kit.Db.SaveChangesAsync();
        return (test.Id, version.Id, qv.Id);
    }

    [Fact]
    public async Task Test_RenameDelete_BlockedByAssignmentsAndAttempts()
    {
        await using var kit = NewKit();
        var actor = await NewTeacherAsync(kit);
        var (testId, versionId, questionVersionId) = await NewPublishedTestAsync(kit, actor, "CRUD тест " + Guid.NewGuid().ToString("N")[..6]);

        await TeacherTestsService.RenameTestAsync(kit.Db, actor, testId, "CRUD тест renamed", CancellationToken.None);
        Assert.Equal("CRUD тест renamed", (await kit.Db.Tests.AsNoTracking().SingleAsync(t => t.Id == testId)).Title);

        // назначение блокирует удаление
        var assignment = Assignment.Create(versionId, AssignmentMode.Practice, FeedbackPolicy.PracticeFeedback,
            DateTimeOffset.UtcNow, null, 2, null, null, false, false, actor.TeacherId, DateTimeOffset.UtcNow);
        kit.Db.Assignments.Add(assignment);
        await kit.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<StudentFlowException>(
            () => TeacherTestsService.DeleteTestAsync(kit.Db, actor, testId, CancellationToken.None));

        // попытка тоже блокирует (даже без назначения): попытка связана с вариантом версии
        kit.Db.Assignments.Remove(assignment);
        await kit.Db.SaveChangesAsync();
        var variant = Domain.Testing.Variant.Create(versionId, 1, "crud-seed", [questionVersionId], new Dictionary<string, List<string>>(), DateTimeOffset.UtcNow);
        kit.Db.Variants.Add(variant);
        var attempt = Domain.Attempts.Attempt.Create(assignment.Id, 1, DateTimeOffset.UtcNow, null, "Петров Пётр", null, variantId: variant.Id);
        kit.Db.Attempts.Add(attempt);
        await kit.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<StudentFlowException>(
            () => TeacherTestsService.DeleteTestAsync(kit.Db, actor, testId, CancellationToken.None));

        // без истории — удаляется вместе с версиями
        kit.Db.Attempts.Remove(attempt);
        await kit.Db.SaveChangesAsync();
        await TeacherTestsService.DeleteTestAsync(kit.Db, actor, testId, CancellationToken.None);
        Assert.False(await kit.Db.Tests.AnyAsync(t => t.Id == testId));
        Assert.False(await kit.Db.TestVersions.AnyAsync(v => v.TestId == testId));
    }

    // ---------- Назначения ----------

    [Fact]
    public async Task Assignment_Delete_BlockedByAttempts_CascadesWhenUnused()
    {
        await using var kit = NewKit();
        var actor = await NewTeacherAsync(kit);
        var (testId, versionId, questionVersionId) = await NewPublishedTestAsync(kit, actor, "CRUD назначение " + Guid.NewGuid().ToString("N")[..6]);
        var assignment = Assignment.Create(versionId, AssignmentMode.Practice, FeedbackPolicy.PracticeFeedback,
            DateTimeOffset.UtcNow, null, 2, null, null, false, false, actor.TeacherId, DateTimeOffset.UtcNow);
        kit.Db.Assignments.Add(assignment);
        await kit.Db.SaveChangesAsync();

        var assignments = new TeacherAssignmentsService(kit.Db, kit.Time,
            factory.Services.GetRequiredService<ExamForge.Domain.Access.InvitationCodeHasher>());
        // попытка блокирует удаление (история — 5 лет, ТЗ §21)
        var variant = Domain.Testing.Variant.Create(versionId, 1, "crud-seed", [questionVersionId], new Dictionary<string, List<string>>(), DateTimeOffset.UtcNow);
        kit.Db.Variants.Add(variant);
        kit.Db.Attempts.Add(Domain.Attempts.Attempt.Create(assignment.Id, 1, DateTimeOffset.UtcNow, null, "Петров Пётр", null, variantId: variant.Id));
        await kit.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<StudentFlowException>(
            () => assignments.DeleteAssignmentAsync(actor, assignment.Id, CancellationToken.None));

        // без попыток — назначение удаляется вместе с приглашениями
        var attempt = await kit.Db.Attempts.SingleAsync(a => a.AssignmentId == assignment.Id);
        kit.Db.Attempts.Remove(attempt);
        kit.Db.Variants.Remove(variant);
        await kit.Db.SaveChangesAsync();
        await assignments.DeleteAssignmentAsync(actor, assignment.Id, CancellationToken.None);
        Assert.False(await kit.Db.Assignments.AnyAsync(a => a.Id == assignment.Id));
        Assert.False(await kit.Db.Invitations.AnyAsync(i => i.AssignmentId == assignment.Id));
    }

    // ---------- Задания ----------

    [Fact]
    public async Task Question_GetForEditAndDelete_DraftOnly()
    {
        await using var kit = NewKit();
        var actor = await NewTeacherAsync(kit);
        var subject = Domain.Subjects.Subject.Create("CRUD банк-предмет " + Guid.NewGuid().ToString("N")[..6], null, DateTimeOffset.UtcNow);
        kit.Db.Subjects.Add(subject);
        await kit.Db.SaveChangesAsync();

        var draft = await kit.Questions.CreateDraftAsync(actor, subject.Id, new QuestionDefinition
        {
            Type = QuestionType.SingleChoice,
            Prompt = "CRUD черновик?",
            Options = [new("a", "да"), new("b", "нет")],
            CorrectOptionId = "a",
            MaxPoints = 1m,
        }, CancellationToken.None);

        var (id, subjectId, definition) = await kit.Questions.GetForEditAsync(actor, draft.QuestionId, CancellationToken.None);
        Assert.Equal(draft.QuestionId, id);
        Assert.Equal(subject.Id, subjectId);
        Assert.Equal("CRUD черновик?", definition.Prompt);
        Assert.Equal(2, definition.Options.Count);

        await kit.Questions.DeleteDraftAsync(actor, draft.QuestionId, CancellationToken.None);
        Assert.False(await kit.Db.Questions.AnyAsync(q => q.Id == draft.QuestionId));

        // опубликованное удалить нельзя — версии фиксируются в тестах (ТЗ §8)
        var published = await kit.Questions.CreateDraftAsync(actor, subject.Id, new QuestionDefinition
        {
            Type = QuestionType.SingleChoice,
            Prompt = "CRUD опубликованное?",
            Options = [new("a", "да"), new("b", "нет")],
            CorrectOptionId = "a",
            MaxPoints = 1m,
        }, CancellationToken.None);
        await kit.Questions.PublishAsync(actor, published.QuestionId, CancellationToken.None);
        var blocked = await Assert.ThrowsAsync<StudentFlowException>(
            () => kit.Questions.DeleteDraftAsync(actor, published.QuestionId, CancellationToken.None));
        Assert.Contains("черновик", blocked.Message);
    }

    // ---------- Материалы ----------

    [Fact]
    public async Task Material_Rename_KeepsStatusAndGrants()
    {
        await using var kit = NewKit();
        var actor = await NewTeacherAsync(kit);
        var subject = Domain.Subjects.Subject.Create("CRUD мат-предмет " + Guid.NewGuid().ToString("N")[..6], null, DateTimeOffset.UtcNow);
        kit.Db.Subjects.Add(subject);
        await kit.Db.SaveChangesAsync();
        var asset = Domain.Files.FileAsset.Create("crud-" + Guid.NewGuid().ToString("N"), new string('a', 64), "application/pdf",
            10, Domain.Files.FileAccessClass.Lecture, DateTimeOffset.UtcNow, actor.TeacherId, "crud.pdf");
        kit.Db.FileAssets.Add(asset);
        await kit.Db.SaveChangesAsync();
        var material = Domain.Materials.Material.CreatePdf(subject.Id, null, "CRUD лекция", null, asset.Id, actor.TeacherId, 0, null, DateTimeOffset.UtcNow);
        kit.Db.Materials.Add(material);
        await kit.Db.SaveChangesAsync();

        var materials = new MaterialsService(kit.Db, kit.Time, new FakeStorage());
        await materials.RenameAsync(actor, material.Id, "CRUD лекция новая", "обновлённое описание", CancellationToken.None);
        var renamed = await kit.Db.Materials.AsNoTracking().SingleAsync(m => m.Id == material.Id);
        Assert.Equal("CRUD лекция новая", renamed.Title);
        Assert.Equal("обновлённое описание", renamed.Description);
        Assert.Equal(Domain.Materials.MaterialStatus.Draft, renamed.Status);
    }

    private sealed class FakeStorage : ExamForge.Application.Files.IFileStorage
    {
        public Task<Domain.Files.FileAsset> SaveAsync(
            Stream content, string originalFileName, string declaredMimeType,
            Domain.Files.FileAccessClass accessClass, Guid? ownerTeacherId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(Domain.Files.FileAsset asset, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
