using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ExamForge.Domain.Assignments;
using ExamForge.Domain.Questions;
using ExamForge.Domain.Subjects;
using ExamForge.Domain.Testing;
using ExamForge.Infrastructure.Identity;
using ExamForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ExamForge.IntegrationTests;

/// <summary>
/// P0-06: IDOR в ResultsService/assignment statistics — чужое назначение неотличимо
/// от несуществующего (404), негативные и позитивные проверки на сервере.
/// </summary>
[Collection("database")]
public sealed class ResultsAuthorizationTests(ExamForgeWebFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<(HttpClient OwnerClient, Guid OwnerId)> OwnerAsync()
        => await TeacherLogin.CreateAndLoginAsync(factory);

    /// <summary>Назначение, созданное напрямую в БД и принадлежащее заданному преподавателю.</summary>
    private async Task<Guid> SeedAssignmentAsync(Guid teacherId)
    {
        await using var db = factory.Services.CreateScope()
            .ServiceProvider.GetRequiredService<ExamForgeDbContext>();
        var now = factory.FakeTime.GetUtcNow();

        var subject = Subject.Create("Subject-" + Guid.NewGuid().ToString("N")[..6], null, now);
        var question = Question.CreateDraft(subject.Id, null, teacherId, now);
        db.Questions.Add(question);
        await db.SaveChangesAsync();
        var definition = QuestionDefinitionJson.Serialize(new QuestionDefinition
        {
            Type = QuestionType.SingleChoice,
            Prompt = "Выберите перевод «oculus»",
            Options = [new("a", "глаз"), new("b", "ухо")],
            CorrectOptionId = "a",
            MaxPoints = 1m,
        });
        var version = QuestionVersion.Create(question.Id, 1, QuestionType.SingleChoice, definition, 1, 1m, 40, now);
        var test = Test.Create(subject.Id, teacherId, "Контрольная IDOR", now);
        var testVersion = TestVersion.Create(test.Id, 1, "Контрольная IDOR", [version.Id], 1m, now);
        testVersion.Publish(now);
        var assignment = Assignment.Create(
            testVersion.Id, AssignmentMode.Assessment, FeedbackPolicy.ScoresOnly,
            now.AddHours(-1), null, 2, 40, null, true, false, teacherId, now);

        db.Subjects.Add(subject);
        db.QuestionVersions.Add(version);
        db.Tests.Add(test);
        db.TestVersions.Add(testVersion);
        db.Assignments.Add(assignment);
        await db.SaveChangesAsync();

        return assignment.Id;
    }

    [Fact]
    public async Task ForeignTeacher_Stats_Is404()
    {
        var (owner, ownerId) = await OwnerAsync();
        var assignmentId = await SeedAssignmentAsync(ownerId);
        var (stranger, _) = await TeacherLogin.CreateAndLoginAsync(factory);

        var response = await stranger.GetAsync($"/api/assignments/{assignmentId}/stats");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Владелец видит своё назначение — изоляция обусловлена authorization, а не поломкой.
        var own = await owner.GetAsync($"/api/assignments/{assignmentId}/stats");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    [Fact]
    public async Task ForeignTeacher_Gradebook_Is404()
    {
        var (_, ownerId) = await OwnerAsync();
        var assignmentId = await SeedAssignmentAsync(ownerId);
        var (stranger, _) = await TeacherLogin.CreateAndLoginAsync(factory);

        var response = await stranger.GetAsync($"/api/assignments/{assignmentId}/gradebook.xlsx");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Administrator_Stats_IsAllowed()
    {
        var (_, ownerId) = await OwnerAsync();
        var assignmentId = await SeedAssignmentAsync(ownerId);
        var (admin, _) = await TeacherLogin.CreateAndLoginAsync(factory, administrator: true);

        var response = await admin.GetAsync($"/api/assignments/{assignmentId}/stats");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stats = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(0, stats.GetProperty("attemptsTotal").GetInt32());
    }

    [Fact]
    public async Task UnknownAssignment_Stats_Is404()
    {
        await TeacherLogin.CreateAndLoginAsync(factory);
        var (teacher, _) = await TeacherLogin.CreateAndLoginAsync(factory);
        var response = await teacher.GetAsync($"/api/assignments/{Guid.NewGuid()}/stats");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
