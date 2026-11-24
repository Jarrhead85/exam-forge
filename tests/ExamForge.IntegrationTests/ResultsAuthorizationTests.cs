using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using FluentAssertions;
using ExamForge.Application.TeacherFlow;
using ExamForge.Domain.Attempts;
using ExamForge.Domain.People;
using ExamForge.Infrastructure.Services;
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
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ExamForgeDbContext>();
        var now = factory.FakeTime.GetUtcNow();

        var subject = Subject.Create("Subject-" + Guid.NewGuid().ToString("N")[..6], null, now);
        var question = Question.CreateDraft(subject.Id, null, teacherId, now);
        db.Questions.Add(question);
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

    [Fact]
    public async Task Gradebook_PicksBestPerProfile_AndKeepsNamesakesWithoutAttempts()
    {
        var (client, ownerId) = await OwnerAsync();
        var assignmentId = await SeedAssignmentAsync(ownerId);
        var now = factory.FakeTime.GetUtcNow();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ExamForgeDbContext>();
        var first = StudentProfile.Create("Тестов", "Иван", null, now, ownerId);
        var namesake = StudentProfile.Create("Тестов", "Иван", null, now, ownerId);
        var other = StudentProfile.Create("Орлова", "Анна", null, now, ownerId);
        db.StudentProfiles.AddRange(first, namesake, other);

        var index = 0;
        foreach (var profile in new[] { first, first, namesake, other })
        {
            db.Invitations.Add(Invitation.Create(assignmentId, profile.Id, (++index).ToString("X64"), null, ownerId, now));
        }
        foreach (var item in new[] { (first, 1, .25m), (first, 2, 1m), (other, 1, .5m) })
        {
            var attempt = Attempt.Create(assignmentId, item.Item2, now, item.Item1.Id, item.Item1.Name.FullName, null);
            attempt.Start(now);
            attempt.Submit(now, false);
            attempt.Finalize(item.Item3, 1m, now);
            db.Attempts.Add(attempt);
        }
        await db.SaveChangesAsync();

        var results = scope.ServiceProvider.GetRequiredService<ResultsService>();
        var actor = new TeacherActor(ownerId, false);
        var stats = await results.StatsAsync(actor, assignmentId, CancellationToken.None);
        var book = await results.GradebookAsync(actor, assignmentId, CancellationToken.None);
        stats.StudentsInvited.Should().Be(3);
        book.Rows.Should().HaveCount(4);
        book.Rows.Should().ContainSingle(row => row.StudentProfileId == namesake.Id && row.Status == "Не начал");
        book.Rows.Where(row => row.IsBest).Should().HaveCount(2);

        var csv = await client.GetStringAsync($"/api/assignments/{assignmentId}/gradebook.csv");
        Assert.Contains("Не начал", csv);
        using var file = new MemoryStream(await client.GetByteArrayAsync($"/api/assignments/{assignmentId}/gradebook.xlsx"));
        using var workbook = new XLWorkbook(file);
        var rows = workbook.Worksheet(1).RowsUsed().Skip(2).ToList();
        Assert.Equal(4, rows.Count);
        Assert.Equal(2, rows.Count(row => row.Cell(8).GetString() == "да"));
        Assert.Single(rows, row => row.Cell(4).GetString() == "Не начал");
    }

    [Fact]
    public async Task GroupImportAndInvitation_UseReviewableMatches_AndDoNotReissueCodes()
    {
        var (_, ownerId) = await OwnerAsync();
        var assignmentId = await SeedAssignmentAsync(ownerId);
        var (_, outsiderId) = await OwnerAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var actor = new TeacherActor(ownerId, false);
        var groups = scope.ServiceProvider.GetRequiredService<GroupService>();
        var imports = scope.ServiceProvider.GetRequiredService<GroupRosterImportService>();
        var invites = scope.ServiceProvider.GetRequiredService<GroupInvitationService>();
        var group = await groups.CreateAsync(actor, "МЛ-101", CancellationToken.None);
        await groups.AddNewStudentAsync(actor, group.Id, "Тестов", "Иван", null, CancellationToken.None);

        const string csv = "Фамилия;Имя;Отчество\nТестов;Иван;\nОрлова;Анна;\nПустов;;\n";
        var preview = await imports.PreviewAsync(actor, group.Id, csv, CancellationToken.None);
        Assert.True(preview[0].PossibleMatch);
        Assert.False(preview[1].PossibleMatch);
        Assert.NotNull(preview[2].Error);
        Assert.Equal(1, await imports.ImportAsync(actor, group.Id, [preview[1].Name!], CancellationToken.None));

        var issued = await invites.IssueAsync(actor, assignmentId, group.Id, CancellationToken.None);
        Assert.Equal(2, issued.Issued.Count);
        Assert.All(issued.Issued, invitation =>
        {
            Assert.NotNull(invitation.OneTimeCode);
            Assert.Equal(group.Name, invitation.StudentGroup);
        });

        var repeat = await invites.IssueAsync(actor, assignmentId, group.Id, CancellationToken.None);
        Assert.Empty(repeat.Issued);
        Assert.Equal(2, repeat.AlreadyInvited);

        await Assert.ThrowsAsync<ExamForge.Application.StudentFlow.StudentFlowException>(() =>
            invites.IssueAsync(new TeacherActor(outsiderId, false), assignmentId, group.Id, CancellationToken.None));
    }

}
