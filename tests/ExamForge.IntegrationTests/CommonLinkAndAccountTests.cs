using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ExamForge.Domain.Questions;
using ExamForge.Infrastructure.Identity;
using ExamForge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ExamForge.IntegrationTests;

/// <summary>
/// Общая ссылка (ТЗ §5.1, AC-18) и постоянные аккаунты студентов (ТЗ §5.3).
/// </summary>
[Collection("database")]
public sealed class CommonLinkAndAccountTests(ExamForgeWebFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ExamForgeDbContext NewDb()
    {
        var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ExamForgeDbContext>();
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement?>(Json)).GetValueOrDefault();

    private async Task<HttpClient> LoginTeacherAsync()
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<TeacherUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        if (!await roleManager.RoleExistsAsync(AppRoles.Teacher))
            await roleManager.CreateAsync(new IdentityRole<Guid>(AppRoles.Teacher));
        var login = "p4t-teacher-" + Guid.NewGuid().ToString("N")[..8];
        var teacher = new TeacherUser { UserName = login, DisplayName = "Хвост П4" };
        Assert.True((await userManager.CreateAsync(teacher, "LatynskieGoda1")).Succeeded);
        await userManager.AddToRoleAsync(teacher, AppRoles.Teacher);

        // Shared login helper: static SSR form post is flaky under test-host load.
        var client = await TeacherLogin.LoginExistingAsync(factory, login);
        return client;
    }

    /// <summary>Создать назначение от вошедшего преподавателя; вернуть (assignmentId, testVersionId).</summary>
    private async Task<(Guid AssignmentId, Guid TestVersionId)> CreateAssignmentAsync(HttpClient teacher)
    {
        await using var db = NewDb();
        var now = factory.FakeTime.GetUtcNow();
        var subject = Domain.Subjects.Subject.Create("Латынь " + Guid.NewGuid().ToString("N")[..6], null, now);
        var author = new TeacherUser { UserName = "p4t-seed-" + Guid.NewGuid().ToString("N")[..8], DisplayName = "С" };
        db.Subjects.Add(subject);
        db.Users.Add(author);
        await db.SaveChangesAsync();

        var definition = QuestionDefinitionJson.Serialize(new QuestionDefinition
        {
            Type = QuestionType.SingleChoice,
            Prompt = "Перевод «auris»",
            Options = [new("a", "ухо"), new("b", "глаз"), new("c", "нос")],
            CorrectOptionId = "a",
            MaxPoints = 1m,
            ProposedTimeLimitSeconds = 40,
        });
        var question = Domain.Questions.Question.CreateDraft(subject.Id, null, author.Id, now);
        db.Questions.Add(question);
        await db.SaveChangesAsync();
        var version = Domain.Questions.QuestionVersion.Create(question.Id, 1, QuestionType.SingleChoice, definition, 1, 1m, 40, now);
        var test = Domain.Testing.Test.Create(subject.Id, author.Id, "Хвостовая контрольная", now);
        db.QuestionVersions.Add(version);
        db.Tests.Add(test);
        await db.SaveChangesAsync();
        var testVersion = Domain.Testing.TestVersion.Create(test.Id, 1, "Хвостовая контрольная", [version.Id], 1m, now);
        testVersion.Publish(now);
        db.TestVersions.Add(testVersion);
        await db.SaveChangesAsync();

        // назначение создаём от имени вошедшего преподавателя (владелец = он), версия может быть чужой
        var create = await teacher.PostAsJsonAsync("/api/assignments", new
        {
            testVersionId = testVersion.Id, mode = "Assessment",
            opensAtUtc = now.AddHours(-1), closesAtUtc = (DateTimeOffset?)null,
            maxAttempts = 2, perQuestionTimeLimitSeconds = 40, attemptTotalTimeLimitSeconds = (int?)null,
            noBacktracking = true,
        });
        create.EnsureSuccessStatusCode();
        var created = JsonSerializer.Deserialize<JsonElement>(await create.Content.ReadAsStringAsync());
        return (created.GetProperty("assignmentId").GetGuid(), testVersion.Id);
    }

    private static async Task<HttpClient> AuthenticatedStudentAsync(
        ExamForgeWebFactory factory, Func<HttpClient, Task<HttpResponseMessage>> authenticate)
    {
        var client = factory.CreateClient();
        (await authenticate(client)).EnsureSuccessStatusCode();
        var af = await client.GetFromJsonAsync<JsonElement>("/api/access/antiforgery");
        client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", af.GetProperty("requestToken").GetString()!);
        return client;
    }

    // ---------- Общая ссылка ----------

    [Fact]
    public async Task CommonLink_DisabledByDefault_EntryRejected()
    {
        var teacher = await LoginTeacherAsync();
        var (_, _) = await CreateAssignmentAsync(teacher); // ссылка по умолчанию выключена (ТЗ §5.1)

        var anon = factory.CreateClient();
        var response = await anon.PostAsJsonAsync("/api/access/common-link",
            new { code = "ABCD2345EFGH", lastName = "Иванов", firstName = "Иван", middleName = (string?)null, group = "ЛД-1" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CommonLink_EntryDoesNotExposeOthersHistory_Ac18()
    {
        // AC-18: тот же ФИО по общей ссылке не открывает существующую чужую историю.
        var teacher = await LoginTeacherAsync();
        var (assignmentId, _) = await CreateAssignmentAsync(teacher);

        var enable = await teacher.PostAsync($"/api/assignments/{assignmentId}/common-link", null);
        enable.EnsureSuccessStatusCode();
        var linkCode = (await BodyAsync(enable)).GetProperty("code").GetString()!;

        // Первый вход: сдаёт попытку
        var first = await AuthenticatedStudentAsync(factory, c => c.PostAsJsonAsync("/api/access/common-link",
            new { code = linkCode, lastName = "Иванова", firstName = "Анна", middleName = (string?)null, group = "ЛД-201" }));
        var start1 = await first.PostAsJsonAsync($"/api/assignments/{assignmentId}/attempts", new { });
        start1.EnsureSuccessStatusCode();
        var attempt1 = await BodyAsync(start1);
        await first.PostAsJsonAsync($"/api/attempts/{attempt1.GetProperty("attemptId").GetGuid()}/finish", new { });

        // Второй вход с ТЕМ ЖЕ ФИО: новая сессия, история первой недоступна
        var second = await AuthenticatedStudentAsync(factory, c => c.PostAsJsonAsync("/api/access/common-link",
            new { code = linkCode, lastName = "Иванова", firstName = "Анна", middleName = (string?)null, group = "ЛД-201" }));

        var foreign = await second.GetAsync($"/api/attempts/{attempt1.GetProperty("attemptId").GetGuid()}/state");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        // своя история пуста: попыток не видно
        var assignments = await second.GetFromJsonAsync<JsonElement[]>("/api/student/assignments", Json);
        Assert.Single(assignments!);
        Assert.Equal(0, assignments![0].GetProperty("usedAttempts").GetInt32());
        Assert.False(assignments[0].GetProperty("hasActiveAttempt").GetBoolean());
    }

    [Fact]
    public async Task CommonLink_AttemptStoresGroupSnapshot()
    {
        var teacher = await LoginTeacherAsync();
        var (assignmentId, _) = await CreateAssignmentAsync(teacher);
        var enable = await teacher.PostAsync($"/api/assignments/{assignmentId}/common-link", null);
        var linkCode = (await BodyAsync(enable)).GetProperty("code").GetString()!;

        var student = await AuthenticatedStudentAsync(factory, c => c.PostAsJsonAsync("/api/access/common-link",
            new { code = linkCode, lastName = "Петров", firstName = "Пётр", middleName = (string?)null, group = "ЛД-202" }));
        var start = await student.PostAsJsonAsync($"/api/assignments/{assignmentId}/attempts", new { });
        start.EnsureSuccessStatusCode();
        var attemptId = (await BodyAsync(start)).GetProperty("attemptId").GetGuid();

        await using var db = NewDb();
        var attempt = await db.Attempts.AsNoTracking().SingleAsync(a => a.Id == attemptId);
        Assert.Equal("ЛД-202", attempt.StudentGroupSnapshot); // группа ушла в снимок (ТЗ §4)
        Assert.Contains("Петров", attempt.StudentNameSnapshot);
    }

    [Fact]
    public async Task CommonLink_Disable_StopsNewEntries()
    {
        var teacher = await LoginTeacherAsync();
        var (assignmentId, _) = await CreateAssignmentAsync(teacher);
        var enable = await teacher.PostAsync($"/api/assignments/{assignmentId}/common-link", null);
        var linkCode = (await BodyAsync(enable)).GetProperty("code").GetString()!;

        var disable = await teacher.DeleteAsync($"/api/assignments/{assignmentId}/common-link");
        disable.EnsureSuccessStatusCode();

        var anon = factory.CreateClient();
        var response = await anon.PostAsJsonAsync("/api/access/common-link",
            new { code = linkCode, lastName = "И", firstName = "И", middleName = (string?)null, group = "Г" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------- Постоянные аккаунты ----------

    [Fact]
    public async Task StudentAccount_LoginSeesAllInvitedAssignments_AndCanWork()
    {
        var teacher = await LoginTeacherAsync();
        var (assignmentId, _) = await CreateAssignmentAsync(teacher);

        var addStudent = await teacher.PostAsJsonAsync($"/api/assignments/{assignmentId}/students",
            new { lastName = "Сидорова", firstName = "Мария", middleName = (string?)null, groupName = (string?)null });
        var invitation = await BodyAsync(addStudent);
        var profileId = invitation.GetProperty("studentProfileId").GetGuid();

        var createAccount = await teacher.PostAsJsonAsync($"/api/assignments/{assignmentId}/student-accounts",
            new { studentProfileId = profileId });
        createAccount.EnsureSuccessStatusCode();
        var account = await BodyAsync(createAccount);
        var login = account.GetProperty("login").GetString()!;
        var password = account.GetProperty("oneTimePassword").GetString()!;
        Assert.StartsWith("stud_", login);
        Assert.False(string.IsNullOrWhiteSpace(password));

        // вход аккаунтом → список всех своих назначений (одно) → старт и сдача
        var student = await AuthenticatedStudentAsync(factory,
            c => c.PostAsJsonAsync("/api/access/login", new { login, password }));

        var assignments = await student.GetFromJsonAsync<JsonElement[]>("/api/student/assignments", Json);
        Assert.Single(assignments!);
        Assert.Equal(assignmentId, assignments![0].GetProperty("assignmentId").GetGuid());

        var start = await student.PostAsJsonAsync($"/api/assignments/{assignmentId}/attempts", new { });
        start.EnsureSuccessStatusCode();
        var attempt = await BodyAsync(start);
        var finish = await student.PostAsJsonAsync($"/api/attempts/{attempt.GetProperty("attemptId").GetGuid()}/finish", new { });
        finish.EnsureSuccessStatusCode();
        Assert.Equal("Finalized", (await BodyAsync(finish)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task StudentAccount_CannotStartNotInvitedAssignment()
    {
        // Аккаунт видит только то, куда приглашён профиль (ТЗ §3.3): чужое назначение — 404.
        var teacher = await LoginTeacherAsync();
        var (mine, _) = await CreateAssignmentAsync(teacher);
        var (other, _) = await CreateAssignmentAsync(teacher);

        var addStudent = await teacher.PostAsJsonAsync($"/api/assignments/{mine}/students",
            new { lastName = "Козлов", firstName = "Кирилл", middleName = (string?)null, groupName = (string?)null });
        var profileId = (await BodyAsync(addStudent)).GetProperty("studentProfileId").GetGuid();

        var createAccount = await teacher.PostAsJsonAsync($"/api/assignments/{mine}/student-accounts",
            new { studentProfileId = profileId });
        var account = await BodyAsync(createAccount);
        var login = account.GetProperty("login").GetString()!;
        var password = account.GetProperty("oneTimePassword").GetString()!;

        var student = await AuthenticatedStudentAsync(factory,
            c => c.PostAsJsonAsync("/api/access/login", new { login, password }));
        var start = await student.PostAsJsonAsync($"/api/assignments/{other}/attempts", new { });
        Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
    }

    [Fact]
    public async Task StudentAccount_WrongPassword_Rejected_AndLockout()
    {
        var teacher = await LoginTeacherAsync();
        var (assignmentId, _) = await CreateAssignmentAsync(teacher);
        var addStudent = await teacher.PostAsJsonAsync($"/api/assignments/{assignmentId}/students",
            new { lastName = "Морозова", firstName = "Ольга", middleName = (string?)null, groupName = (string?)null });
        var profileId = (await BodyAsync(addStudent)).GetProperty("studentProfileId").GetGuid();
        var createAccount = await teacher.PostAsJsonAsync($"/api/assignments/{assignmentId}/student-accounts",
            new { studentProfileId = profileId });
        var account = await BodyAsync(createAccount);
        var login = account.GetProperty("login").GetString()!;

        var anon = factory.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            var bad = await anon.PostAsJsonAsync("/api/access/login", new { login, password = "WrongPassword99" });
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        }

        // после 5 неудач вход заблокирован даже с верным паролем
        var correct = await anon.PostAsJsonAsync("/api/access/login",
            new { login, password = account.GetProperty("oneTimePassword").GetString()! });
        Assert.Equal(HttpStatusCode.Unauthorized, correct.StatusCode);
    }

    [Fact]
    public async Task StudentAccount_PasswordResetByTeacher()
    {
        var teacher = await LoginTeacherAsync();
        var (assignmentId, _) = await CreateAssignmentAsync(teacher);
        var addStudent = await teacher.PostAsJsonAsync($"/api/assignments/{assignmentId}/students",
            new { lastName = "Волков", firstName = "Виктор", middleName = (string?)null, groupName = (string?)null });
        var profileId = (await BodyAsync(addStudent)).GetProperty("studentProfileId").GetGuid();
        var createAccount = await teacher.PostAsJsonAsync($"/api/assignments/{assignmentId}/student-accounts",
            new { studentProfileId = profileId });
        var account = await BodyAsync(createAccount);
        var login = account.GetProperty("login").GetString()!;
        var newPasswordResponse = await teacher.PostAsJsonAsync(
            $"/api/student-accounts/{account.GetProperty("accountId").GetGuid()}/reset-password?assignmentId={assignmentId}", new { });
        newPasswordResponse.EnsureSuccessStatusCode();
        var newPassword = (await BodyAsync(newPasswordResponse)).GetProperty("oneTimePassword").GetString()!;

        var client = factory.CreateClient();
        var loginResponse = await client.PostAsJsonAsync("/api/access/login", new { login, password = newPassword });
        Assert.True(loginResponse.IsSuccessStatusCode, "вход по новому паролю должен работать");
    }
}
