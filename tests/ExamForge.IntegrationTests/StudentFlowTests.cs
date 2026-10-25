using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ExamForge.Domain.Access;
using ExamForge.Domain.Assignments;
using ExamForge.Domain.Attempts;
using ExamForge.Domain.People;
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
/// Сквозной студенческий поток: обмен кода → start → open → save → lock → finish (ТЗ §10, §17).
/// Время — FakeTimeProvider, deadline-сценарии без sleep (ТЗ §24).
/// </summary>
[Collection("database")]
public sealed class StudentFlowTests(ExamForgeWebFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement?>(Json)).GetValueOrDefault();

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
        => (await client.GetFromJsonAsync<JsonElement?>(url, Json)).GetValueOrDefault();

    private ExamForgeDbContext NewDb()
    {
        var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ExamForgeDbContext>();
    }

    private async Task<HttpClient> ExchangedClientAsync(string code)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/access/exchange", new { code });
        response.EnsureSuccessStatusCode();
        // Antiforgery-токен получается после установления сессии: он привязан к principal.
        var af = await GetJsonAsync(client, "/api/access/antiforgery");
        client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", af.GetProperty("requestToken").GetString()!);
        return client;
    }

    private sealed record FlowSeed(
        Assignment Assignment,
        StudentProfile Student,
        StudentProfile OtherStudent,
        Invitation Invitation,
        Invitation OtherInvitation,
        string StudentCode,
        string OtherStudentCode);

    private static InvitationCodeHasher Hasher => new(Convert.FromHexString(ExamForgeWebFactory.TestHmacKey));

    private async Task<FlowSeed> SeedAsync(
        int perQuestionSeconds = 40,
        DateTimeOffset? closesAt = null,
        int maxAttempts = 2)
    {
        await using var db = NewDb();
        var now = factory.FakeTime.GetUtcNow();

        var subject = Subject.Create("Медицинская латынь " + Guid.NewGuid().ToString("N")[..6], null, now);
        var teacher = new TeacherUser
        {
            UserName = "flow-teacher-" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "Преподаватель Потока",
        };
        db.Subjects.Add(subject);
        db.Users.Add(teacher);
        await db.SaveChangesAsync();

        var definitions = new (string Json, QuestionType Type, decimal Max)[]
        {
            (QuestionDefinitionJson.Serialize(new QuestionDefinition
            {
                Type = QuestionType.SingleChoice,
                Prompt = "Выберите перевод термина «oculus»",
                Options = [new("a", "глаз"), new("b", "ухо"), new("c", "нос")],
                CorrectOptionId = "a",
                MaxPoints = 1m,
                ProposedTimeLimitSeconds = perQuestionSeconds,
            }), QuestionType.SingleChoice, 1m),
            (QuestionDefinitionJson.Serialize(new QuestionDefinition
            {
                Type = QuestionType.Matching,
                Prompt = "Соотнесите термины и переводы",
                MaxPoints = 4m,
                ProposedTimeLimitSeconds = perQuestionSeconds,
                MatchingPairs =
                [
                    new("L0", "auris", "R0", "ухо"),
                    new("L1", "nasus", "R1", "нос"),
                    new("L2", "dens", "R2", "зуб"),
                    new("L3", "caput", "R3", "голова"),
                ],
            }), QuestionType.Matching, 4m),
            (QuestionDefinitionJson.Serialize(new QuestionDefinition
            {
                Type = QuestionType.LetterAssembly,
                Prompt = "Соберите термин «oculus»",
                MaxPoints = 1m,
                ProposedTimeLimitSeconds = perQuestionSeconds,
                TargetSequence = "oculus",
                LetterTiles =
                [
                    new("t-o", 'o'), new("t-c", 'c'), new("t-u1", 'u'),
                    new("t-l", 'l'), new("t-u2", 'u'), new("t-s", 's'),
                ],
            }), QuestionType.LetterAssembly, 1m),
        };

        var question = Question.CreateDraft(subject.Id, null, teacher.Id, now);
        db.Questions.Add(question);
        await db.SaveChangesAsync();

        var versionIds = new List<Guid>();
        for (var i = 0; i < definitions.Length; i++)
        {
            var version = QuestionVersion.Create(question.Id, i + 1, definitions[i].Type,
                definitions[i].Json, 1, definitions[i].Max, perQuestionSeconds, now);
            db.QuestionVersions.Add(version);
            versionIds.Add(version.Id);
        }

        var test = Test.Create(subject.Id, teacher.Id, "Словарная контрольная №1", now);
        db.Tests.Add(test);
        await db.SaveChangesAsync();

        var testVersion = TestVersion.Create(test.Id, 1, "Словарная контрольная №1", versionIds, 6m, now);
        testVersion.Publish(now);
        db.TestVersions.Add(testVersion);

        var assignment = Assignment.Create(
            testVersion.Id, AssignmentMode.Assessment, FeedbackPolicy.ScoresOnly,
            now.AddHours(-1), closesAt, maxAttempts,
            perQuestionTimeLimitSeconds: perQuestionSeconds,
            attemptTotalTimeLimitSeconds: null,
            noBacktracking: true, allowCommonLink: false, teacher.Id, now);
        db.Assignments.Add(assignment);

        var student = StudentProfile.Create("Иванова", "Анна", "Сергеевна", now, teacher.Id);
        var other = StudentProfile.Create("Петров", "Пётр", null, now, teacher.Id);
        db.StudentProfiles.AddRange(student, other);
        await db.SaveChangesAsync();

        var studentCode = InvitationCode.Generate();
        var otherStudentCode = InvitationCode.Generate();
        var invitation = Invitation.Create(assignment.Id, student.Id, Hasher.ComputeHash(InvitationCode.Normalize(studentCode)), null, teacher.Id, now);
        var otherInvitation = Invitation.Create(assignment.Id, other.Id, Hasher.ComputeHash(InvitationCode.Normalize(otherStudentCode)), null, teacher.Id, now);
        db.Invitations.AddRange(invitation, otherInvitation);
        await db.SaveChangesAsync();

        return new FlowSeed(assignment, student, other, invitation, otherInvitation, studentCode, otherStudentCode);
    }

    /// <summary>Открывает следующий вопрос; возвращает состояние попытки.</summary>
    private static async Task<JsonElement> OpenNextAsync(HttpClient client, Guid attemptId)
    {
        var response = await client.PostAsJsonAsync($"/api/attempts/{attemptId}/open-next", new { });
        response.EnsureSuccessStatusCode();
        return await BodyAsync(response);
    }

    private static async Task<Guid> StartAttemptAsync(HttpClient client, Guid assignmentId)
    {
        var response = await client.PostAsJsonAsync($"/api/assignments/{assignmentId}/attempts", new { });
        response.EnsureSuccessStatusCode();
        return (await BodyAsync(response)).GetProperty("attemptId").GetGuid();
    }

    private static async Task<Guid> CurrentSessionIdAsync(HttpClient client, Guid attemptId)
    {
        var state = await OpenNextAsync(client, attemptId);
        return state.GetProperty("currentQuestion").GetProperty("questionSessionId").GetGuid();
    }

    // ---------- Обмен кода ----------

    [Fact]
    public async Task Exchange_ValidCode_SetsStudentSession()
    {
        var seed = await SeedAsync();
        var client = await ExchangedClientAsync(seed.StudentCode);
        var assignments = await client.GetFromJsonAsync<JsonElement[]>("/api/student/assignments");
        Assert.Single(assignments!);
        Assert.Equal("Assessment", assignments![0].GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Exchange_WrongCode_Unauthorized()
    {
        await SeedAsync();
        var client = factory.CreateClient();
        // валидный формат (алфавит без 0/O/1/I), но неизвестный код
        var response = await client.PostAsJsonAsync("/api/access/exchange", new { code = "QRST2345WXYZ" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Exchange_RevokedCode_Unauthorized()
    {
        var seed = await SeedAsync();
        await using (var db = NewDb())
        {
            var invitation = await db.Invitations.SingleAsync(i => i.Id == seed.Invitation.Id);
            invitation.Revoke(factory.FakeTime.GetUtcNow());
            await db.SaveChangesAsync();
        }
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/access/exchange", new { code = seed.StudentCode });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task StudentApi_WithoutSession_Unauthorized()
    {
        await SeedAsync();
        var anon = factory.CreateClient();
        var response = await anon.GetAsync("/api/student/assignments");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------- Полный поток ----------

    [Fact]
    public async Task FullFlow_ThreeQuestions_AutoGradedOnFinish()
    {
        var seed = await SeedAsync();
        var client = await ExchangedClientAsync(seed.StudentCode);

        var start = await client.PostAsJsonAsync($"/api/assignments/{seed.Assignment.Id}/attempts", new { });
        start.EnsureSuccessStatusCode();
        var attempt = await BodyAsync(start);
        var attemptId = attempt.GetProperty("attemptId").GetGuid();
        Assert.Equal("InProgress", attempt.GetProperty("status").GetString());
        Assert.Equal(3, attempt.GetProperty("totalQuestions").GetInt32());

        // Вопрос 1: одиночный выбор
        var state1 = await OpenNextAsync(client, attemptId);
        var q1 = state1.GetProperty("currentQuestion");
        Assert.Equal("SingleChoice", q1.GetProperty("type").GetString());
        var deadline1 = state1.GetProperty("sessions")[0].GetProperty("deadlineUtc").GetDateTimeOffset();
        var save1 = await client.PutAsJsonAsync(
            $"/api/attempts/{attemptId}/answers/{q1.GetProperty("questionSessionId").GetGuid()}",
            new { answer = new { selectedOptionId = "a" } });
        save1.EnsureSuccessStatusCode();
        var saved1 = await BodyAsync(save1);
        Assert.True(saved1.GetProperty("saved").GetBoolean());
        Assert.Equal(1, saved1.GetProperty("acceptedRevision").GetInt32());
        Assert.Equal(deadline1, saved1.GetProperty("deadlineUtc").GetDateTimeOffset());
        await client.PostAsJsonAsync($"/api/attempts/{attemptId}/lock-current", new { });

        // Вопрос 2: сопоставление, 3 верные пары из 4 (AC-02)
        var state2 = await OpenNextAsync(client, attemptId);
        var q2 = state2.GetProperty("currentQuestion");
        Assert.Equal("Matching", q2.GetProperty("type").GetString());
        var save2 = await client.PutAsJsonAsync(
            $"/api/attempts/{attemptId}/answers/{q2.GetProperty("questionSessionId").GetGuid()}",
            new
            {
                answer = new
                {
                    pairSelections = new Dictionary<string, string>
                    {
                        ["L0"] = "R0", ["L1"] = "R1", ["L2"] = "R2", ["L3"] = "R0",
                    },
                },
            });
        save2.EnsureSuccessStatusCode();
        await client.PostAsJsonAsync($"/api/attempts/{attemptId}/lock-current", new { });

        // Вопрос 3: сборка термина с двумя «u» (AC-12)
        var state3 = await OpenNextAsync(client, attemptId);
        var q3 = state3.GetProperty("currentQuestion");
        Assert.Equal("LetterAssembly", q3.GetProperty("type").GetString());
        var save3 = await client.PutAsJsonAsync(
            $"/api/attempts/{attemptId}/answers/{q3.GetProperty("questionSessionId").GetGuid()}",
            new { answer = new { tileSequence = new[] { "t-o", "t-c", "t-u1", "t-l", "t-u2", "t-s" } } });
        save3.EnsureSuccessStatusCode();
        await client.PostAsJsonAsync($"/api/attempts/{attemptId}/lock-current", new { });

        // Финал: автооценивание 1 + 3 + 1 = 5 из 6 → 83% → оценка 4 (ТЗ §11)
        var finish = await client.PostAsJsonAsync($"/api/attempts/{attemptId}/finish", new { });
        finish.EnsureSuccessStatusCode();
        var finished = await BodyAsync(finish);
        Assert.Equal("Finalized", finished.GetProperty("status").GetString());
        Assert.Equal(5m, finished.GetProperty("earnedPoints").GetDecimal());
        Assert.Equal(6m, finished.GetProperty("maxPoints").GetDecimal());
        Assert.Equal(83, finished.GetProperty("roundedPercent").GetInt32());

        var result = await GetJsonAsync(client, $"/api/attempts/{attemptId}/result");
        Assert.Equal("Finalized", result.GetProperty("status").GetString());
        Assert.False(result.GetProperty("provisional").GetBoolean());
        Assert.Equal(83, result.GetProperty("roundedPercent").GetInt32());
    }

    // ---------- Инварианты времени и идемпотентности ----------

    [Fact]
    public async Task RepeatStart_ReturnsSameAttempt_NotNewOne()
    {
        // AC-07: повторный start не расходует лимит.
        var seed = await SeedAsync();
        var client = await ExchangedClientAsync(seed.StudentCode);
        var first = await StartAttemptAsync(client, seed.Assignment.Id);
        var second = await StartAttemptAsync(client, seed.Assignment.Id);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task RefreshState_DoesNotExtendDeadline()
    {
        // AC-03: refresh при активном вопросе — исходный deadline.
        var seed = await SeedAsync(perQuestionSeconds: 40);
        var client = await ExchangedClientAsync(seed.StudentCode);
        var attemptId = await StartAttemptAsync(client, seed.Assignment.Id);

        await OpenNextAsync(client, attemptId);
        var before = await GetJsonAsync(client, $"/api/attempts/{attemptId}/state");
        var deadline1 = before.GetProperty("sessions")[0].GetProperty("deadlineUtc").GetDateTimeOffset();

        factory.FakeTime.Advance(TimeSpan.FromSeconds(25));
        await client.PostAsJsonAsync($"/api/attempts/{attemptId}/open-next", new { }); // повторное «открытие»
        var after = await GetJsonAsync(client, $"/api/attempts/{attemptId}/state");
        var deadline2 = after.GetProperty("sessions")[0].GetProperty("deadlineUtc").GetDateTimeOffset();

        Assert.Equal(deadline1, deadline2);
    }

    [Fact]
    public async Task SaveAnswer_ExactlyAtDeadline_RejectedLate_EarlierAcceptedSurvives()
    {
        // AC-04: ровно в deadline — опоздание; принятая ранее ревизия остаётся.
        var seed = await SeedAsync(perQuestionSeconds: 30);
        var client = await ExchangedClientAsync(seed.StudentCode);
        var attemptId = await StartAttemptAsync(client, seed.Assignment.Id);
        var sessionId = await CurrentSessionIdAsync(client, attemptId);

        var ok = await client.PutAsJsonAsync($"/api/attempts/{attemptId}/answers/{sessionId}",
            new { answer = new { selectedOptionId = "a" } });
        ok.EnsureSuccessStatusCode();

        factory.FakeTime.Advance(TimeSpan.FromSeconds(30)); // ровно deadline
        var late = await client.PutAsJsonAsync($"/api/attempts/{attemptId}/answers/{sessionId}",
            new { answer = new { selectedOptionId = "b" } });
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        var problem = await BodyAsync(late);
        Assert.Equal("late", problem.GetProperty("code").GetString());

        var state = await GetJsonAsync(client, $"/api/attempts/{attemptId}/state");
        Assert.Equal(1, state.GetProperty("sessions")[0].GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task OfflinePastDeadline_LateAnswerRejected_SessionTimedOut()
    {
        // AC-05: связи нет дольше deadline; поздний локальный ответ не принят.
        var seed = await SeedAsync(perQuestionSeconds: 20);
        var client = await ExchangedClientAsync(seed.StudentCode);
        var attemptId = await StartAttemptAsync(client, seed.Assignment.Id);
        var sessionId = await CurrentSessionIdAsync(client, attemptId);

        factory.FakeTime.Advance(TimeSpan.FromMinutes(3)); // «вернулся в сеть»
        var response = await client.PutAsJsonAsync($"/api/attempts/{attemptId}/answers/{sessionId}",
            new { answer = new { selectedOptionId = "a" } });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var state = await GetJsonAsync(client, $"/api/attempts/{attemptId}/state");
        Assert.Equal("LockedTimedOut", state.GetProperty("sessions")[0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task LockedQuestion_RejectsDirectUpdate()
    {
        // AC-06: закрытый вопрос нельзя изменить прямым HTTP-запросом.
        var seed = await SeedAsync();
        var client = await ExchangedClientAsync(seed.StudentCode);
        var attemptId = await StartAttemptAsync(client, seed.Assignment.Id);
        var sessionId = await CurrentSessionIdAsync(client, attemptId);

        await client.PostAsJsonAsync($"/api/attempts/{attemptId}/lock-current", new { });

        var response = await client.PutAsJsonAsync($"/api/attempts/{attemptId}/answers/{sessionId}",
            new { answer = new { selectedOptionId = "a" } });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await BodyAsync(response);
        Assert.Equal("not_active", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task StudentDto_NetworkPayload_HasNoKeysOrCorrectness()
    {
        // AC-11: в DTO и сетевом payload нет ключей/объяснений/правильности.
        var seed = await SeedAsync();
        var client = await ExchangedClientAsync(seed.StudentCode);
        var attemptId = await StartAttemptAsync(client, seed.Assignment.Id);

        foreach (var expectedType in new[] { "SingleChoice", "Matching", "LetterAssembly" })
        {
            var state = await OpenNextAsync(client, attemptId);
            var question = state.GetProperty("currentQuestion");
            Assert.Equal(expectedType, question.GetProperty("type").GetString());
            var raw = question.GetRawText().ToLowerInvariant();
            foreach (var forbidden in new[]
                     {
                         "correctoptionid", "correctoptionids", "acceptedanswers", "acceptedorders",
                         "targetsequence", "acceptedvalues", "correctvalue", "scoring", "rubric",
                         "iscorrect", "correct", "explanation",
                     })
                Assert.DoesNotContain(forbidden, raw);
            await client.PostAsJsonAsync($"/api/attempts/{attemptId}/lock-current", new { });
        }

        var fullState = await GetJsonAsync(client, $"/api/attempts/{attemptId}/state");
        Assert.DoesNotContain("correct", fullState.GetRawText().ToLowerInvariant());
    }

    [Fact]
    public async Task ForeignAttempt_Is404_ForOtherStudent()
    {
        // AC-10: чужая попытка неотличима от несуществующей.
        var seed = await SeedAsync();
        var owner = await ExchangedClientAsync(seed.StudentCode);
        var attemptId = await StartAttemptAsync(owner, seed.Assignment.Id);

        var stranger = await ExchangedClientAsync(seed.OtherStudentCode);
        var state = await stranger.GetAsync($"/api/attempts/{attemptId}/state");
        var finish = await stranger.PostAsJsonAsync($"/api/attempts/{attemptId}/finish", new { });
        var ownerStillSees = await owner.GetAsync($"/api/attempts/{attemptId}/state");

        Assert.Equal(HttpStatusCode.NotFound, state.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, finish.StatusCode);
        Assert.True(ownerStillSees.IsSuccessStatusCode);
    }

    [Fact]
    public async Task TwoAttempts_ThenExhausted_ExtraGrantAllowsThird()
    {
        // AC-08 / AC-23: две обычные попытки; лимит; grant с причиной открывает третью, история не стёрта.
        var seed = await SeedAsync();
        var client = await ExchangedClientAsync(seed.StudentCode);

        var first = await StartAttemptAsync(client, seed.Assignment.Id);
        await client.PostAsJsonAsync($"/api/attempts/{first}/finish", new { });

        var second = await StartAttemptAsync(client, seed.Assignment.Id);
        Assert.NotEqual(first, second);
        await client.PostAsJsonAsync($"/api/attempts/{second}/finish", new { });

        var third = await client.PostAsJsonAsync($"/api/assignments/{seed.Assignment.Id}/attempts", new { });
        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);

        await using (var db = NewDb())
        {
            var grant = ExtraAttemptGrant.Create(seed.Assignment.Id, seed.Student.Id,
                "Сбой связи во время контрольной", seed.Assignment.CreatedByTeacherId,
                factory.FakeTime.GetUtcNow());
            db.ExtraAttemptGrants.Add(grant);
            await db.SaveChangesAsync();
        }

        var thirdOk = await client.PostAsJsonAsync($"/api/assignments/{seed.Assignment.Id}/attempts", new { });
        thirdOk.EnsureSuccessStatusCode();
        var attempt3 = await BodyAsync(thirdOk);
        Assert.Equal(3, attempt3.GetProperty("attemptNumber").GetInt32());
        await client.PostAsJsonAsync($"/api/attempts/{attempt3.GetProperty("attemptId").GetGuid()}/finish", new { });

        await using (var db = NewDb())
        {
            var attempts = await db.Attempts.AsNoTracking()
                .Where(a => a.AssignmentId == seed.Assignment.Id && a.StudentProfileId == seed.Student.Id)
                .ToListAsync();
            Assert.Equal(3, attempts.Count);
            Assert.All(attempts, a => Assert.Equal(AttemptState.Finalized, a.State));
        }
    }

    [Fact]
    public async Task Finish_IsIdempotent_SingleFinal()
    {
        // AC-07: повторная отправка уже сданной работы не создаёт новый финал.
        var seed = await SeedAsync();
        var client = await ExchangedClientAsync(seed.StudentCode);
        var attemptId = await StartAttemptAsync(client, seed.Assignment.Id);

        var first = await client.PostAsJsonAsync($"/api/attempts/{attemptId}/finish", new { });
        first.EnsureSuccessStatusCode();
        var second = await client.PostAsJsonAsync($"/api/attempts/{attemptId}/finish", new { });
        second.EnsureSuccessStatusCode();

        var r1 = await BodyAsync(first);
        var r2 = await BodyAsync(second);
        Assert.Equal(r1.GetProperty("status").GetString(), r2.GetProperty("status").GetString());
        Assert.Equal(r1.GetProperty("earnedPoints").GetDecimal(), r2.GetProperty("earnedPoints").GetDecimal());
    }

    [Fact]
    public async Task ManualReview_Question_MakesAttemptProvisional()
    {
        // AC-09: LongText не проверен — provisional, итоговой оценки нет.
        await using var db = NewDb();
        var now = factory.FakeTime.GetUtcNow();
        var subject = Subject.Create("Латынь " + Guid.NewGuid().ToString("N")[..6], null, now);
        var teacher = new TeacherUser { UserName = "mr-teacher-" + Guid.NewGuid().ToString("N")[..8], DisplayName = "П" };
        db.Subjects.Add(subject);
        db.Users.Add(teacher);
        await db.SaveChangesAsync();

        var definition = QuestionDefinitionJson.Serialize(new QuestionDefinition
        {
            Type = QuestionType.LongText,
            Prompt = "Объясните употребление аблятива",
            MaxPoints = 5m,
            RequiresManualReview = true,
            ProposedTimeLimitSeconds = 600,
        });
        var question = Question.CreateDraft(subject.Id, null, teacher.Id, now);
        db.Questions.Add(question);
        await db.SaveChangesAsync();
        var version = QuestionVersion.Create(question.Id, 1, QuestionType.LongText, definition, 1, 5m, 600, now);
        var test = Test.Create(subject.Id, teacher.Id, "Развёрнутая работа", now);
        db.QuestionVersions.Add(version);
        db.Tests.Add(test);
        await db.SaveChangesAsync();
        var testVersion = TestVersion.Create(test.Id, 1, "Развёрнутая работа", [version.Id], 5m, now);
        testVersion.Publish(now);
        db.TestVersions.Add(testVersion);
        var assignment = Assignment.Create(testVersion.Id, AssignmentMode.Assessment, FeedbackPolicy.ScoresOnly,
            now.AddHours(-1), null, 2, 600, null, true, false, teacher.Id, now);
        var student = StudentProfile.Create("Сидорова", "Мария", null, now, teacher.Id);
        db.Assignments.Add(assignment);
        db.StudentProfiles.Add(student);
        await db.SaveChangesAsync();
        var code = InvitationCode.Generate();
        db.Invitations.Add(Invitation.Create(assignment.Id, student.Id,
            Hasher.ComputeHash(InvitationCode.Normalize(code)), null, teacher.Id, now));
        await db.SaveChangesAsync();

        var client = await ExchangedClientAsync(code);
        var attemptId = await StartAttemptAsync(client, assignment.Id);
        var sessionId = await CurrentSessionIdAsync(client, attemptId);
        await client.PutAsJsonAsync($"/api/attempts/{attemptId}/answers/{sessionId}",
            new { answer = new { text = "Аблятив употребляется…" } });

        var finish = await client.PostAsJsonAsync($"/api/attempts/{attemptId}/finish", new { });
        finish.EnsureSuccessStatusCode();
        var finished = await BodyAsync(finish);
        Assert.Equal("PendingReview", finished.GetProperty("status").GetString());
        Assert.True(finished.GetProperty("provisional").GetBoolean());
        // не проверено ≠ 0 и ≠ финал: итоговые баллы ещё не подведены (ТЗ §11)
        Assert.Equal(JsonValueKind.Null, finished.GetProperty("earnedPoints").ValueKind);

        var result = await GetJsonAsync(client, $"/api/attempts/{attemptId}/result");
        Assert.True(result.GetProperty("provisional").GetBoolean());
        Assert.True(result.TryGetProperty("roundedPercent", out var percent) && percent.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task AssignmentClosedAfterExchange_StartConflict()
    {
        var seed = await SeedAsync(closesAt: factory.FakeTime.GetUtcNow().AddMinutes(10));
        var client = await ExchangedClientAsync(seed.StudentCode);
        factory.FakeTime.Advance(TimeSpan.FromMinutes(20)); // назначение закрылось после обмена
        var response = await client.PostAsJsonAsync($"/api/assignments/{seed.Assignment.Id}/attempts", new { });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
