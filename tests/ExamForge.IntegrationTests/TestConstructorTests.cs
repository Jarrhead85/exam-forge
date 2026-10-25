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
/// Конструктор заданий и тестов (ТЗ §7–8): CRUD вопросов, публикация с валидацией §7.1,
/// версии теста только из опубликованных заданий, Blueprint-предпросмотр с предупреждениями (AC-14).
/// </summary>
[Collection("database")]
public sealed class TestConstructorTests(ExamForgeWebFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement?>(Json)).GetValueOrDefault();

    private ExamForgeDbContext NewDb()
    {
        var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ExamForgeDbContext>();
    }

    private async Task<(HttpClient Client, Guid SubjectId, Guid TeacherId)> LoginWithSubjectAsync()
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<TeacherUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        if (!await roleManager.RoleExistsAsync(AppRoles.Teacher))
            await roleManager.CreateAsync(new IdentityRole<Guid>(AppRoles.Teacher));
        var login = "ctor-teacher-" + Guid.NewGuid().ToString("N")[..8];
        var teacher = new TeacherUser { UserName = login, DisplayName = "Конструктор Тестов" };
        Assert.True((await userManager.CreateAsync(teacher, "LatynskieGoda1")).Succeeded);
        await userManager.AddToRoleAsync(teacher, AppRoles.Teacher);

        await using var db = NewDb();
        var subject = Domain.Subjects.Subject.Create("Латынь " + Guid.NewGuid().ToString("N")[..6], null, DateTime.UtcNow);
        db.Subjects.Add(subject);
        await db.SaveChangesAsync();

        var client = factory.CreateClient();
        var loginPage = await client.GetStringAsync("/account/login");
        var token = System.Text.RegularExpressions.Regex
            .Match(loginPage, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var response = await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "teacher-login",
            ["__RequestVerificationToken"] = token,
            ["Input.Login"] = login,
            ["Input.Password"] = "LatynskieGoda1",
        }));
        Assert.True(response.IsSuccessStatusCode);
        // teacher-API защищён antiforgery-мидлварью — прикрепляем токен ко всем запросам
        var af = await client.GetFromJsonAsync<JsonElement>("/api/access/antiforgery-teacher", Json);
        client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", af.GetProperty("requestToken").GetString()!);
        return (client, subject.Id, teacher.Id);
    }

    private static object ChoicePayload(Guid subjectId, string prompt = "Перевод «oculus»", string correct = "глаз")
        => new
        {
            subjectId,
            type = "SingleChoice",
            prompt,
            instruction = (string?)null,
            maxPoints = 1m,
            timeLimitSeconds = 40,
            options = new[] { new { id = "o0", text = correct }, new { id = "o1", text = "ухо" }, new { id = "o2", text = "нос" } },
            correctOptionId = "o0",
            correctOptionIds = Array.Empty<string>(),
            scoring = "AllOrNothing",
            acceptedAnswers = Array.Empty<string>(),
            ignoreCase = true,
            ignoreDiacritics = true,
            sendToReview = false,
            fields = Array.Empty<object>(),
            pairs = Array.Empty<object>(),
            targetWord = (string?)null,
            orderItems = Array.Empty<string>(),
            gapText = (string?)null,
            blanks = Array.Empty<object>(),
        };

    private static object LetterPayload(Guid subjectId, string word = "oculus")
        => new
        {
            subjectId,
            type = "LetterAssembly",
            prompt = $"Соберите термин «{word}»",
            instruction = (string?)null,
            maxPoints = 1m,
            timeLimitSeconds = 40,
            options = Array.Empty<object>(),
            correctOptionId = (string?)null,
            correctOptionIds = Array.Empty<string>(),
            scoring = "AllOrNothing",
            acceptedAnswers = Array.Empty<string>(),
            ignoreCase = true,
            ignoreDiacritics = true,
            sendToReview = false,
            fields = Array.Empty<object>(),
            pairs = Array.Empty<object>(),
            targetWord = word,
            orderItems = Array.Empty<string>(),
            gapText = (string?)null,
            blanks = Array.Empty<object>(),
        };

    [Fact]
    public async Task QuestionLifecycle_DraftPublish_NewEdition()
    {
        var (client, subjectId, _) = await LoginWithSubjectAsync();

        // черновик
        var create = await client.PostAsJsonAsync("/api/questions", ChoicePayload(subjectId));
        create.EnsureSuccessStatusCode();
        var question = await BodyAsync(create);
        var questionId = question.GetProperty("questionId").GetGuid();

        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/questions", Json);
        Assert.Single(list!);
        Assert.Equal("Draft", list![0].GetProperty("status").GetString());

        // публикация (валидация §7.1)
        var publish = await client.PostAsync($"/api/questions/{questionId}/publish", null);
        publish.EnsureSuccessStatusCode();
        var after = await client.GetFromJsonAsync<JsonElement[]>("/api/questions", Json);
        Assert.Equal("Published", after![0].GetProperty("status").GetString());

        // правка опубликованного → новая редакция-черновик (AC-15)
        var edit = await client.PutAsJsonAsync($"/api/questions/{questionId}",
            ((JsonElement)JsonSerializer.SerializeToElement(ChoicePayload(subjectId, "Обновлённый вопрос"), Json))
            .Deserialize<JsonElement>(Json));
        Assert.True(edit.IsSuccessStatusCode, await edit.Content.ReadAsStringAsync());
        var edited = await BodyAsync(edit);
        Assert.Equal(2, edited.GetProperty("versionNumber").GetInt32());
        var statuses = await client.GetFromJsonAsync<JsonElement[]>("/api/questions", Json);
        Assert.Equal("Draft", statuses![0].GetProperty("status").GetString()); // новая редакция ждёт публикации
    }

    [Fact]
    public async Task PublishInvalidQuestion_RejectedWithExplanation()
    {
        var (client, subjectId, _) = await LoginWithSubjectAsync();
        // невалидный: только один вариант
        var payload = ChoicePayload(subjectId);
        payload = JsonSerializer.SerializeToElement(payload, Json).Deserialize<object>();
        var element = JsonSerializer.SerializeToElement(payload, Json);
        // уберём два варианта
        var bad = new
        {
            subjectId, type = "SingleChoice", prompt = "x", instruction = (string?)null,
            maxPoints = 1m, timeLimitSeconds = 40,
            options = new[] { new { id = "o0", text = "единственный" } },
            correctOptionId = "o0", correctOptionIds = Array.Empty<string>(), scoring = "AllOrNothing",
            acceptedAnswers = Array.Empty<string>(), ignoreCase = true, ignoreDiacritics = true,
            sendToReview = false, fields = Array.Empty<object>(), pairs = Array.Empty<object>(),
            targetWord = (string?)null, orderItems = Array.Empty<string>(), gapText = (string?)null,
            blanks = Array.Empty<object>(),
        };
        var create = await client.PostAsJsonAsync("/api/questions", bad);
        create.EnsureSuccessStatusCode();
        var questionId = (await BodyAsync(create)).GetProperty("questionId").GetGuid();

        var publish = await client.PostAsync($"/api/questions/{questionId}/publish", null);
        Assert.Equal(HttpStatusCode.Conflict, publish.StatusCode);
        var problem = await publish.Content.ReadAsStringAsync();
        Assert.Contains("минимум двух вариантов", problem);
    }

    [Fact]
    public async Task TestVersion_OnlyFromPublishedQuestions()
    {
        var (client, subjectId, _) = await LoginWithSubjectAsync();

        var q1 = (await BodyAsync(await client.PostAsJsonAsync("/api/questions", ChoicePayload(subjectId)))).GetProperty("questionId").GetGuid();
        var q2draft = (await BodyAsync(await client.PostAsJsonAsync("/api/questions", LetterPayload(subjectId)))).GetProperty("questionId").GetGuid();
        await client.PostAsync($"/api/questions/{q1}/publish", null); // только q1 опубликован

        var test = await client.PostAsJsonAsync("/api/tests", new { title = "Словарная №1", subjectId });
        test.EnsureSuccessStatusCode();
        var testId = (await BodyAsync(test)).GetProperty("testId").GetGuid();

        // получить id версии опубликованного вопроса
        await using var db = NewDb();
        var publishedVersionId = await db.QuestionVersions.AsNoTracking()
            .Where(v => v.QuestionId == q1 && v.PublishedAtUtc != null)
            .Select(v => v.Id)
            .SingleAsync();
        var draftVersionId = await db.QuestionVersions.AsNoTracking()
            .Where(v => v.QuestionId == q2draft)
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => v.Id)
            .FirstAsync();

        // черновик в версии — отказ
        var withDraft = await client.PostAsJsonAsync("/api/test-versions",
            new { testId, title = "v1", questionVersionIds = new[] { publishedVersionId, draftVersionId } });
        Assert.Equal(HttpStatusCode.Conflict, withDraft.StatusCode);

        // только опубликованные — успех + публикация версии
        var version = await client.PostAsJsonAsync("/api/test-versions",
            new { testId, title = "v1", questionVersionIds = new[] { publishedVersionId } });
        version.EnsureSuccessStatusCode();
        var versionId = (await BodyAsync(version)).GetProperty("versionId").GetGuid();

        var publishVersion = await client.PostAsync($"/api/test-versions/{versionId}/publish", null);
        publishVersion.EnsureSuccessStatusCode();

        var tests = await client.GetFromJsonAsync<JsonElement[]>("/api/tests", Json);
        Assert.Single(tests!);
        Assert.True(tests![0].GetProperty("versions")[0].GetProperty("isPublished").GetBoolean());
    }

    [Fact]
    public async Task BlueprintPreview_WarnsOnInsufficientBank_NoDuplicates()
    {
        // AC-14: недостаток банка — явное предупреждение, без молчаливых повторов.
        var (client, subjectId, _) = await LoginWithSubjectAsync();
        for (var i = 0; i < 2; i++)
        {
            var q = await client.PostAsJsonAsync("/api/questions", ChoicePayload(subjectId, $"Вопрос {i + 1}"));
            var questionId = (await BodyAsync(q)).GetProperty("questionId").GetGuid();
            await client.PostAsync($"/api/questions/{questionId}/publish", null);
        }

        var preview = await client.PostAsJsonAsync("/api/tests/blueprint-preview", new
        {
            lines = new[] { new { questionType = "SingleChoice", count = 5 } },
            seed = "ctor-seed",
        });
        preview.EnsureSuccessStatusCode();
        var result = await BodyAsync(preview);

        Assert.Equal(2, result.GetProperty("picked").GetArrayLength()); // только имеющиеся
        var ids = result.GetProperty("picked").EnumerateArray().Select(r => r.GetProperty("questionVersionId").GetGuid());
        Assert.Equal(2, ids.Distinct().Count());
        Assert.True(result.GetProperty("warnings").GetArrayLength() > 0);
        Assert.Contains("даёт 2", result.GetProperty("warnings")[0].GetString());
    }
}
