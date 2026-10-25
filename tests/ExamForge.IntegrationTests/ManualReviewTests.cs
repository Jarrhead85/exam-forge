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
/// Ручная проверка и ведомость (ТЗ §11–12): полный цикл AC-09 (provisional → ручная проверка → итог),
/// override с причиной и историей до/после, CSV с защитой от injection, IDOR.
/// </summary>
[Collection("database")]
public sealed class ManualReviewTests(ExamForgeWebFactory factory)
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
        var login = "p5-teacher-" + Guid.NewGuid().ToString("N")[..8];
        var teacher = new TeacherUser { UserName = login, DisplayName = "Проверка Тестова" };
        Assert.True((await userManager.CreateAsync(teacher, "LatynskieGoda1")).Succeeded);
        await userManager.AddToRoleAsync(teacher, AppRoles.Teacher);

        // Shared login helper: static SSR form post is flaky under test-host load.
        var client = await TeacherLogin.LoginExistingAsync(factory, login);
        return client;
    }

    private sealed record SeededAssignment(Guid AssignmentId, HttpClient Teacher, Guid AuthorId);

    /// <summary>Назначение с одним авто-вопросом (1 б.) и одним LongText (4 б., ручная проверка).</summary>
    private async Task<SeededAssignment> SeedAsync()
    {
        var teacher = await LoginTeacherAsync();
        await using var db = NewDb();
        var now = factory.FakeTime.GetUtcNow();
        var subject = Domain.Subjects.Subject.Create("Латынь " + Guid.NewGuid().ToString("N")[..6], null, now);
        var author = new TeacherUser { UserName = "p5-seed-" + Guid.NewGuid().ToString("N")[..8], DisplayName = "С" };
        db.Subjects.Add(subject);
        db.Users.Add(author);
        await db.SaveChangesAsync();

        var choice = QuestionDefinitionJson.Serialize(new QuestionDefinition
        {
            Type = QuestionType.SingleChoice, Prompt = "Перевод «dens»",
            Options = [new("a", "зуб"), new("b", "глаз"), new("c", "нос")],
            CorrectOptionId = "a", MaxPoints = 1m, ProposedTimeLimitSeconds = 40,
        });
        var longText = QuestionDefinitionJson.Serialize(new QuestionDefinition
        {
            Type = QuestionType.LongText, Prompt = "Объясните употребление аблятива",
            MaxPoints = 4m, RequiresManualReview = true, ProposedTimeLimitSeconds = 600,
        });
        var question = Domain.Questions.Question.CreateDraft(subject.Id, null, author.Id, now);
        db.Questions.Add(question);
        await db.SaveChangesAsync();
        var choiceVersion = Domain.Questions.QuestionVersion.Create(question.Id, 1, QuestionType.SingleChoice, choice, 1, 1m, 40, now);
        var longVersion = Domain.Questions.QuestionVersion.Create(question.Id, 2, QuestionType.LongText, longText, 1, 4m, 600, now);
        var test = Domain.Testing.Test.Create(subject.Id, author.Id, "Работа с ручной проверкой", now);
        db.QuestionVersions.AddRange(choiceVersion, longVersion);
        db.Tests.Add(test);
        await db.SaveChangesAsync();
        var testVersion = Domain.Testing.TestVersion.Create(test.Id, 1, "Работа с ручной проверкой",
            [choiceVersion.Id, longVersion.Id], 5m, now);
        testVersion.Publish(now);
        db.TestVersions.Add(testVersion);

        var assignment = Domain.Assignments.Assignment.Create(
            testVersion.Id, Domain.Assignments.AssignmentMode.Assessment, Domain.Assignments.FeedbackPolicy.ScoresOnly,
            now.AddHours(-1), null, 2, 40, null, true, false, author.Id, now);
        db.Assignments.Add(assignment);
        await db.SaveChangesAsync();

        // назначение создано автором seed-теста; вошедший преподаватель — не владелец.
        // Для владения используем API-создание от вошедшего:
        var create = await teacher.PostAsJsonAsync("/api/assignments", new
        {
            testVersionId = testVersion.Id, mode = "Assessment",
            opensAtUtc = now.AddHours(-1), closesAtUtc = (DateTimeOffset?)null,
            maxAttempts = 2, perQuestionTimeLimitSeconds = 40, attemptTotalTimeLimitSeconds = (int?)null,
            noBacktracking = true,
        });
        create.EnsureSuccessStatusCode();
        var own = JsonSerializer.Deserialize<JsonElement>(await create.Content.ReadAsStringAsync());
        return new SeededAssignment(own.GetProperty("assignmentId").GetGuid(), teacher, author.Id);
    }

    private async Task<HttpClient> StudentWithAttemptAsync(Guid assignmentId, HttpClient teacher, bool answerLongText)
    {
        var addStudent = await teacher.PostAsJsonAsync($"/api/assignments/{assignmentId}/students",
            new { lastName = "Николаев", firstName = "Николай", middleName = (string?)null, groupName = "ЛД-301" });
        var invitation = await BodyAsync(addStudent);
        var code = invitation.GetProperty("oneTimeCode").GetString()!;

        var student = factory.CreateClient();
        var exchange = await student.PostAsJsonAsync("/api/access/exchange", new { code });
        exchange.EnsureSuccessStatusCode();
        var af = await student.GetFromJsonAsync<JsonElement>("/api/access/antiforgery");
        student.DefaultRequestHeaders.Add("X-XSRF-TOKEN", af.GetProperty("requestToken").GetString()!);

        var start = await student.PostAsJsonAsync($"/api/assignments/{assignmentId}/attempts", new { });
        start.EnsureSuccessStatusCode();
        var attemptId = (await BodyAsync(start)).GetProperty("attemptId").GetGuid();

        var open1 = await student.PostAsJsonAsync($"/api/attempts/{attemptId}/open-next", new { });
        var q1 = (await BodyAsync(open1)).GetProperty("currentQuestion");
        await student.PutAsJsonAsync($"/api/attempts/{attemptId}/answers/{q1.GetProperty("questionSessionId").GetGuid()}",
            new { answer = new { selectedOptionId = "a" } }); // верно
        await student.PostAsJsonAsync($"/api/attempts/{attemptId}/lock-current", new { });

        var open2 = await student.PostAsJsonAsync($"/api/attempts/{attemptId}/open-next", new { });
        var q2 = (await BodyAsync(open2)).GetProperty("currentQuestion");
        if (answerLongText)
        {
            await student.PutAsJsonAsync($"/api/attempts/{attemptId}/answers/{q2.GetProperty("questionSessionId").GetGuid()}",
                new { answer = new { text = "Аблятив употребляется для обозначения места и орудия действия." } });
        }
        await student.PostAsJsonAsync($"/api/attempts/{attemptId}/lock-current", new { });

        var finish = await student.PostAsJsonAsync($"/api/attempts/{attemptId}/finish", new { });
        finish.EnsureSuccessStatusCode();
        return student;
    }

    [Fact]
    public async Task FullCycle_PendingReview_ManualGrade_Finalized_Ac09()
    {
        // AC-09 до конца: не проверено ≠ финал; после ручной проверки — итоговая оценка.
        var seed = await SeedAsync();
        var student = await StudentWithAttemptAsync(seed.AssignmentId, seed.Teacher, answerLongText: true);

        // найти attemptId через очередь проверки
        var queue = await seed.Teacher.GetFromJsonAsync<JsonElement>($"/api/assignments/{seed.AssignmentId}/reviews?filter=pending", Json);
        Assert.Equal(1, queue.GetArrayLength());
        var attemptId = queue[0].GetProperty("attemptId").GetGuid();
        Assert.Equal("PendingReview", queue[0].GetProperty("status").GetString());
        Assert.Equal(1, queue[0].GetProperty("pendingCriteria").GetInt32());

        // студент видит provisional без процента
        var provisional = await student.GetFromJsonAsync<JsonElement>($"/api/attempts/{attemptId}/result", Json);
        Assert.True(provisional.GetProperty("provisional").GetBoolean());

        // детали проверки: ответ + ключ (teacher-only)
        var details = await seed.Teacher.GetFromJsonAsync<JsonElement>($"/api/reviews/{attemptId}", Json);
        Assert.Equal(2, details.GetProperty("questions").GetArrayLength());
        var longQuestion = details.GetProperty("questions")[1];
        Assert.Equal("LongText", longQuestion.GetProperty("type").GetString());
        Assert.Contains("Аблятив", longQuestion.GetProperty("answerDisplay").GetString());
        Assert.NotNull(longQuestion.GetProperty("keyDisplay").GetString());
        var pendingCriterion = longQuestion.GetProperty("criteria")[0];
        Assert.Equal("PendingManual", pendingCriterion.GetProperty("state").GetString());
        var sessionId = longQuestion.GetProperty("questionSessionId").GetGuid();
        var criterionId = pendingCriterion.GetProperty("criterionId").GetString()!;

        // авто-критерий уже зафиксирован (1 балл), ручной ждёт
        Assert.Equal(1m, details.GetProperty("confirmedEarned").GetDecimal());

        // оценить ручной критерий: 3 из 4
        var grade = await seed.Teacher.PostAsJsonAsync($"/api/reviews/{attemptId}/grade", new
        {
            scores = new[] { new { questionSessionId = sessionId, criterionId, earnedPoints = 3m, reason = "Неполное объяснение, без примера" } },
        });
        grade.EnsureSuccessStatusCode();

        // попытка финализирована: 1 + 3 = 4 из 5 → 80%
        var after = await BodyAsync(grade);
        Assert.Equal("Finalized", after.GetProperty("status").GetString());
        var studentResult = await student.GetFromJsonAsync<JsonElement>($"/api/attempts/{attemptId}/result", Json);
        Assert.False(studentResult.GetProperty("provisional").GetBoolean());
        Assert.Equal(4m, studentResult.GetProperty("earnedPoints").GetDecimal());
        Assert.Equal(5m, studentResult.GetProperty("maxPoints").GetDecimal());
        Assert.Equal(80, studentResult.GetProperty("roundedPercent").GetInt32());
    }

    [Fact]
    public async Task Override_RequiresReason_RecordsBeforeAndAfter()
    {
        var seed = await SeedAsync();
        await StudentWithAttemptAsync(seed.AssignmentId, seed.Teacher, answerLongText: true);
        var queue = await seed.Teacher.GetFromJsonAsync<JsonElement>($"/api/assignments/{seed.AssignmentId}/reviews", Json);
        var attemptId = queue[0].GetProperty("attemptId").GetGuid();
        var details = await seed.Teacher.GetFromJsonAsync<JsonElement>($"/api/reviews/{attemptId}", Json);
        var choiceQuestion = details.GetProperty("questions")[0];
        var sessionId = choiceQuestion.GetProperty("questionSessionId").GetGuid();
        var criterionId = choiceQuestion.GetProperty("criteria")[0].GetProperty("criterionId").GetString()!;

        // сначала завершить ручную проверку
        var longQuestion = details.GetProperty("questions")[1];
        var longSessionId = longQuestion.GetProperty("questionSessionId").GetGuid();
        var longCriterion = longQuestion.GetProperty("criteria")[0].GetProperty("criterionId").GetString()!;
        await seed.Teacher.PostAsJsonAsync($"/api/reviews/{attemptId}/grade", new
        {
            scores = new[] { new { questionSessionId = longSessionId, criterionId = longCriterion, earnedPoints = 4m, reason = "Полный ответ" } },
        });

        // переоценка авто-критерия: было 1 (верно «зуб»), преподаватель меняет на 0.5
        var overrideResponse = await seed.Teacher.PostAsJsonAsync($"/api/reviews/{attemptId}/override", new
        {
            questionSessionId = sessionId, criterionId, earnedPoints = 0.5m,
            reason = "Студент обратился: засчитать спорный ответ частично",
        });
        overrideResponse.EnsureSuccessStatusCode();
        var after = await BodyAsync(overrideResponse);
        Assert.Equal("Finalized", after.GetProperty("status").GetString());

        await using (var db = NewDb())
        {
            var attempt = await db.Attempts.AsNoTracking().SingleAsync(a => a.Id == attemptId);
            Assert.Equal(4.5m, attempt.ScoreEarnedPoints); // 0.5 + 4
            // история до/после зафиксирована (ТЗ §11)
            var revision = await db.GradeRevisions.AsNoTracking().SingleAsync(r => r.AttemptId == attemptId);
            Assert.Equal(1m, revision.OldEarnedPoints);
            Assert.Equal(0.5m, revision.NewEarnedPoints);
            Assert.Equal(5m, revision.OldAttemptEarned);
            Assert.Equal(4.5m, revision.NewAttemptEarned);
            Assert.Equal("Студент обратился: засчитать спорный ответ частично", revision.Reason);
            // CompletedAt не изменился (ТЗ §20: редактирование оценки не запускает срок заново)
        }

        // без причины переоценка запрещена
        var noReason = await seed.Teacher.PostAsJsonAsync($"/api/reviews/{attemptId}/override", new
        {
            questionSessionId = sessionId, criterionId, earnedPoints = 1m, reason = "",
        });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
    }

    [Fact]
    public async Task GradebookCsv_ContainsBestAndSanitizesInjection()
    {
        var seed = await SeedAsync();
        // студент с «опасной» фамилией для проверки CSV injection
        var addStudent = await seed.Teacher.PostAsJsonAsync($"/api/assignments/{seed.AssignmentId}/students",
            new { lastName = "=SUM(A1:A9)", firstName = "Опасный", middleName = (string?)null, groupName = (string?)null });
        addStudent.EnsureSuccessStatusCode();

        var csv = await seed.Teacher.GetAsync($"/api/assignments/{seed.AssignmentId}/gradebook.csv");
        csv.EnsureSuccessStatusCode();
        var contentType = csv.Content.Headers.ContentType!.ToString();
        Assert.Contains("text/csv", contentType);
        var text = await csv.Content.ReadAsStringAsync();

        Assert.Contains("Фамилия и имя;Группа;Попытка;Статус", text);
        // формула префиксована апострофом (CSV injection protection, ТЗ §12)
        Assert.Contains("'=SUM(A1:A9)", text);
        Assert.DoesNotContain("\"=SUM", text.Replace("'=", "x"));

        // анониму ведомость недоступна
        var anon = factory.CreateClient();
        var anonCsv = await anon.GetAsync($"/api/assignments/{seed.AssignmentId}/gradebook.csv");
        Assert.False(anonCsv.IsSuccessStatusCode);
    }

    [Fact]
    public async Task ReviewApi_ForeignTeacher_Gets404()
    {
        var seed = await SeedAsync();
        await StudentWithAttemptAsync(seed.AssignmentId, seed.Teacher, answerLongText: true);

        var stranger = await LoginTeacherAsync(); // другой преподаватель
        var queue = await stranger.GetAsync($"/api/assignments/{seed.AssignmentId}/reviews");
        Assert.Equal(HttpStatusCode.NotFound, queue.StatusCode);

        var ownQueue = await seed.Teacher.GetFromJsonAsync<JsonElement>($"/api/assignments/{seed.AssignmentId}/reviews", Json);
        var attemptId = ownQueue[0].GetProperty("attemptId").GetGuid();
        var details = await stranger.GetAsync($"/api/reviews/{attemptId}");
        var grade = await stranger.PostAsJsonAsync($"/api/reviews/{attemptId}/grade", new { scores = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.NotFound, details.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, grade.StatusCode);
    }
}
