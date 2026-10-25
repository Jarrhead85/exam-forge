using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ExamForge.Domain.Assignments;
using ExamForge.Domain.Questions;
using ExamForge.Domain.Subjects;
using ExamForge.Domain.Testing;
using ExamForge.Infrastructure.Identity;
using ExamForge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ExamForge.IntegrationTests;

/// <summary>
/// Teacher-API назначений (ТЗ §3.2, §17): владение объектом, выдача кода один раз,
/// extra grant с причиной. Вход — учительская cookie через форму логина.
/// </summary>
[Collection("database")]
public sealed class TeacherFlowTests(ExamForgeWebFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ExamForgeDbContext NewDb()
    {
        var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ExamForgeDbContext>();
    }

    private async Task<Guid> CreateTeacherAsync(string login)
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<TeacherUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        if (!await roleManager.RoleExistsAsync(AppRoles.Teacher))
            await roleManager.CreateAsync(new IdentityRole<Guid>(AppRoles.Teacher));
        var teacher = new TeacherUser { UserName = login, DisplayName = "Преподаватель " + login };
        var result = await userManager.CreateAsync(teacher, "LatynskieGoda1");
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
        await userManager.AddToRoleAsync(teacher, AppRoles.Teacher);
        return teacher.Id;
    }

    private async Task<HttpClient> LoginTeacherAsync(string login)
    {
        await CreateTeacherAsync(login);
        // Shared login helper: static SSR form post is flaky under test-host load.
        var client = await TeacherLogin.LoginExistingAsync(factory, login);
        return client;
    }

    private async Task<(Guid AssignmentId, Guid TestVersionId)> SeedAssignmentAsync()
    {
        await using var db = NewDb();
        var now = factory.FakeTime.GetUtcNow();
        var subject = Subject.Create("Латынь " + Guid.NewGuid().ToString("N")[..6], null, now);
        var teacher = new TeacherUser { UserName = "seed-t-" + Guid.NewGuid().ToString("N")[..8], DisplayName = "С" };
        db.Subjects.Add(subject);
        db.Users.Add(teacher);
        await db.SaveChangesAsync();

        var definition = QuestionDefinitionJson.Serialize(new QuestionDefinition
        {
            Type = QuestionType.SingleChoice, Prompt = "p",
            Options = [new("a", "1"), new("b", "2")], CorrectOptionId = "a",
            MaxPoints = 1m, ProposedTimeLimitSeconds = 40,
        });
        var question = Question.CreateDraft(subject.Id, null, teacher.Id, now);
        db.Questions.Add(question);
        await db.SaveChangesAsync();
        var version = QuestionVersion.Create(question.Id, 1, QuestionType.SingleChoice, definition, 1, 1m, 40, now);
        var test = Test.Create(subject.Id, teacher.Id, "t", now);
        db.QuestionVersions.Add(version);
        db.Tests.Add(test);
        await db.SaveChangesAsync();
        var testVersion = TestVersion.Create(test.Id, 1, "t", [version.Id], 1m, now);
        testVersion.Publish(now);
        db.TestVersions.Add(testVersion);
        var assignment = Assignment.Create(testVersion.Id, AssignmentMode.Assessment, FeedbackPolicy.ScoresOnly,
            now.AddHours(-1), null, 2, 40, null, true, false, teacher.Id, now);
        db.TestVersions.Add(testVersion);
        db.Assignments.Add(assignment);
        await db.SaveChangesAsync();
        return (assignment.Id, testVersion.Id);
    }

    [Fact]
    public async Task CreateAssignment_AndAddStudent_CodeReturnedOnce()
    {
        var client = await LoginTeacherAsync("t-create-" + Guid.NewGuid().ToString("N")[..8]);
        var (_, testVersionId) = await SeedAssignmentAsync();

        var create = await client.PostAsJsonAsync("/api/assignments", new
        {
            testVersionId, mode = "Assessment",
            opensAtUtc = factory.FakeTime.GetUtcNow().AddHours(-1), closesAtUtc = (DateTimeOffset?)null,
            maxAttempts = 2, perQuestionTimeLimitSeconds = 40, attemptTotalTimeLimitSeconds = (int?)null,
            noBacktracking = true,
        });
        create.EnsureSuccessStatusCode();
        var created = JsonSerializer.Deserialize<JsonElement>(await create.Content.ReadAsStringAsync());
        var assignmentId = created.GetProperty("assignmentId").GetGuid();

        var addStudent = await client.PostAsJsonAsync($"/api/assignments/{assignmentId}/students",
            new { lastName = "Иванова", firstName = "Анна", middleName = (string?)null, groupName = (string?)null });
        addStudent.EnsureSuccessStatusCode();
        var invitation = JsonSerializer.Deserialize<JsonElement>(await addStudent.Content.ReadAsStringAsync());
        var code = invitation.GetProperty("oneTimeCode").GetString();
        Assert.False(string.IsNullOrWhiteSpace(code));
        Assert.Matches("^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$", code);

        // код не повторяется в деталях: oneTimeCode только у ответа создания
        var details = await client.GetFromJsonAsync<JsonElement>($"/api/assignments/{assignmentId}", Json);
        var list = details.GetProperty("invitations");
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal(JsonValueKind.Null, list[0].GetProperty("oneTimeCode").ValueKind);
    }

    [Fact]
    public async Task ForeignTeacher_Gets404_ForOthersAssignment()
    {
        // IDOR: чужой объект неотличим от несуществующего (ТЗ §3.3).
        var ownerLogin = "t-owner-" + Guid.NewGuid().ToString("N")[..8];
        var owner = await LoginTeacherAsync(ownerLogin);
        var (assignmentId, _) = await SeedAssignmentAsync(); // создан другим (seed) преподавателем

        // владелец seed-назначения — seed-учитель; наш owner тоже чужой. Оба должны получить 404.
        var stranger = await LoginTeacherAsync("t-other-" + Guid.NewGuid().ToString("N")[..8]);
        var get = await stranger.GetAsync($"/api/assignments/{assignmentId}");
        var addStudent = await stranger.PostAsJsonAsync($"/api/assignments/{assignmentId}/students",
            new { lastName = "X", firstName = "Y", middleName = (string?)null, groupName = (string?)null });
        var grant = await stranger.PostAsJsonAsync($"/api/assignments/{assignmentId}/grants",
            new { reason = "тест", studentProfileId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, addStudent.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, grant.StatusCode);
    }

    [Fact]
    public async Task Anonymous_Gets401_ForTeacherApi()
    {
        await SeedAssignmentAsync();
        var anon = factory.CreateClient();
        var response = await anon.GetAsync("/api/assignments");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RevokeInvitation_StudentCannotExchange()
    {
        var teacherLogin = "t-revoke-" + Guid.NewGuid().ToString("N")[..8];
        var teacher = await LoginTeacherAsync(teacherLogin);
        var (assignmentId, _) = await SeedAssignmentAsync();

        // создать собственное назначение с правом доступа
        var create = await teacher.PostAsJsonAsync("/api/assignments", new
        {
            testVersionId = await PublishedVersionIdAsync(), mode = "Assessment",
            opensAtUtc = factory.FakeTime.GetUtcNow().AddHours(-1), closesAtUtc = (DateTimeOffset?)null,
            maxAttempts = 2, perQuestionTimeLimitSeconds = 40, attemptTotalTimeLimitSeconds = (int?)null,
            noBacktracking = true,
        });
        create.EnsureSuccessStatusCode();
        var own = JsonSerializer.Deserialize<JsonElement>(await create.Content.ReadAsStringAsync());
        var ownId = own.GetProperty("assignmentId").GetGuid();

        var addStudent = await teacher.PostAsJsonAsync($"/api/assignments/{ownId}/students",
            new { lastName = "Петров", firstName = "Пётр", middleName = (string?)null, groupName = (string?)null });
        var invitation = JsonSerializer.Deserialize<JsonElement>(await addStudent.Content.ReadAsStringAsync());
        var invitationId = invitation.GetProperty("invitationId").GetGuid();
        var code = invitation.GetProperty("oneTimeCode").GetString()!;

        var revoke = await teacher.PostAsync($"/api/invitations/{invitationId}/revoke", null);
        Assert.True(revoke.IsSuccessStatusCode);

        var student = factory.CreateClient();
        var exchange = await student.PostAsJsonAsync("/api/access/exchange", new { code });
        Assert.Equal(HttpStatusCode.Unauthorized, exchange.StatusCode);
    }

    private async Task<Guid> PublishedVersionIdAsync()
    {
        await using var db = NewDb();
        return await db.TestVersions.AsNoTracking()
            .Where(v => v.PublishedAtUtc != null)
            .OrderByDescending(v => v.CreatedAtUtc)
            .Select(v => v.Id)
            .FirstAsync();
    }
}
