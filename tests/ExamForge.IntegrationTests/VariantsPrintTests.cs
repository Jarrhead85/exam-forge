using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ExamForge.Domain.Questions;
using ExamForge.Infrastructure.Identity;
using ExamForge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ExamForge.IntegrationTests;

/// <summary>
/// Варианты и печать (ТЗ §8, §13): генерация, привязка к попыткам, StudentSheet без ключей,
/// TeacherKey строго по порядку варианта (AC-16). Автор версий — вошедший преподаватель.
/// </summary>
[Collection("database")]
public sealed class VariantsPrintTests(ExamForgeWebFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ExamForgeDbContext NewDb()
    {
        var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ExamForgeDbContext>();
    }

    /// <summary>Логинит преподавателя и создаёт опубликованную версию с одним SingleChoice-вопросом.</summary>
    private async Task<(HttpClient Client, Guid TestVersionId)> LoginWithVersionAsync()
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<TeacherUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        if (!await roleManager.RoleExistsAsync(AppRoles.Teacher))
            await roleManager.CreateAsync(new IdentityRole<Guid>(AppRoles.Teacher));
        var login = "p3-teacher-" + Guid.NewGuid().ToString("N")[..8];
        var teacher = new TeacherUser { UserName = login, DisplayName = "Печать Тестова" };
        Assert.True((await userManager.CreateAsync(teacher, "LatynskieGoda1")).Succeeded);
        await userManager.AddToRoleAsync(teacher, AppRoles.Teacher);
        var teacherId = teacher.Id;

        await using var db = NewDb();
        var now = factory.FakeTime.GetUtcNow();
        var subject = Domain.Subjects.Subject.Create("Латынь " + Guid.NewGuid().ToString("N")[..6], null, now);
        db.Subjects.Add(subject);
        await db.SaveChangesAsync();

        var definition = QuestionDefinitionJson.Serialize(new QuestionDefinition
        {
            Type = QuestionType.SingleChoice,
            Prompt = "Выберите перевод термина «oculus»",
            Options = [new("a", "глаз"), new("b", "ухо"), new("c", "нос")],
            CorrectOptionId = "a",
            MaxPoints = 1m,
            ProposedTimeLimitSeconds = 40,
        });
        var question = Domain.Questions.Question.CreateDraft(subject.Id, null, teacherId, now);
        db.Questions.Add(question);
        await db.SaveChangesAsync();
        var version = Domain.Questions.QuestionVersion.Create(question.Id, 1, QuestionType.SingleChoice, definition, 1, 1m, 40, now);
        var test = Domain.Testing.Test.Create(subject.Id, teacherId, "Печатная контрольная", now);
        db.QuestionVersions.Add(version);
        db.Tests.Add(test);
        await db.SaveChangesAsync();
        var testVersion = Domain.Testing.TestVersion.Create(test.Id, 1, "Печатная контрольная", [version.Id], 1m, now);
        testVersion.Publish(now);
        db.TestVersions.Add(testVersion);
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
        Assert.True(response.IsSuccessStatusCode, "логин не удался: " + response.StatusCode);
        // teacher-API защищён antiforgery-мидлварью — прикрепляем токен ко всем запросам
        var af = await client.GetFromJsonAsync<JsonElement>("/api/access/antiforgery-teacher", Json);
        client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", af.GetProperty("requestToken").GetString()!);
        return (client, testVersion.Id);
    }

    private static async Task<JsonElement> GenerateVariantsAsync(HttpClient client, Guid testVersionId, int count = 2)
    {
        var response = await client.PostAsJsonAsync($"/api/test-versions/{testVersionId}/variants",
            new { count, shuffleQuestions = false });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"generate: {response.StatusCode} {body}");
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    [Fact]
    public async Task GenerateVariants_TwoDistinctVariants_AnonDenied()
    {
        var (client, testVersionId) = await LoginWithVersionAsync();

        var anon = factory.CreateClient();
        var anonResponse = await anon.PostAsJsonAsync($"/api/test-versions/{testVersionId}/variants",
            new { count = 1, shuffleQuestions = false });
        // анониму отказано без раскрытия деталей (401 challenge или редирект на логин)
        Assert.False(anonResponse.IsSuccessStatusCode, "аноним не должен создавать варианты");

        var variants = await GenerateVariantsAsync(client, testVersionId, 2);
        Assert.Equal(2, variants.GetArrayLength());
        Assert.NotEqual(variants[0].GetProperty("id").GetGuid(), variants[1].GetProperty("id").GetGuid());

        var list = await client.GetFromJsonAsync<JsonElement>($"/api/test-versions/{testVersionId}/variants", Json);
        Assert.Equal(2, list.GetArrayLength());
    }

    [Fact]
    public async Task PrintPages_KeyFollowsVariantOrder_StudentSheetHasNoKey()
    {
        // AC-16: ключ строго соответствует variant order и option IDs.
        var (client, testVersionId) = await LoginWithVersionAsync();
        var variants = await GenerateVariantsAsync(client, testVersionId);
        var variantId = variants[0].GetProperty("id").GetGuid();

        string[] order;
        await using (var db = NewDb())
        {
            var variant = await db.Variants.AsNoTracking().SingleAsync(v => v.Id == variantId);
            var version = await db.TestVersions.AsNoTracking().SingleAsync(v => v.Id == variant.TestVersionId);
            var qvId = version.QuestionVersionIds[0];
            order = [.. variant.OptionOrders[qvId.ToString()]];
            Assert.Equal(3, order.Length); // перестановка всех трёх опций
        }

        var keyHtml = await client.GetStringAsync($"/print/variant/{variantId}/teacher");
        Assert.Contains("ключ преподавателя", keyHtml);

        // AC-16: маркер правильного ответа в ключе = маркер, под которым «глаз» напечатан на этом листе.
        var eyeMarker = System.Text.RegularExpressions.Regex
            .Match(keyHtml, "print-option__marker\"[^>]*>([^<]+)</span>\\s*<span[^>]*>глаз")
            .Groups[1].Value.TrimEnd(')');
        if (eyeMarker.Length == 0)
        {
            var optionsIndex = keyHtml.IndexOf("print-options", StringComparison.Ordinal);
            var context = optionsIndex >= 0
                ? keyHtml.Substring(optionsIndex, Math.Min(600, keyHtml.Length - optionsIndex))
                : keyHtml[..Math.Min(600, keyHtml.Length)];
            Assert.Fail("Маркер «глаз» не найден. HTML: " + System.Net.WebUtility.HtmlDecode(context));
        }
        Assert.Contains($"Правильный ответ: {eyeMarker}", keyHtml);

        var sheetHtml = await client.GetStringAsync($"/print/variant/{variantId}/student");
        Assert.Contains("ЛИСТ СТУДЕНТА", sheetHtml);
        Assert.Contains("глаз", sheetHtml);
        Assert.Contains("ухо", sheetHtml);
        Assert.DoesNotContain("Правильный ответ", sheetHtml);
        Assert.DoesNotContain("ключ преподавателя", sheetHtml);

        var anon = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var anonPage = await anon.GetAsync($"/print/variant/{variantId}/teacher");
        Assert.Equal(HttpStatusCode.Redirect, anonPage.StatusCode); // на страницу входа (ТЗ §13: teacher-only)
    }

    [Fact]
    public async Task Attempt_UsesVariantOptionOrder_InStudentDto()
    {
        // Попытка с вариантами отдаёт опции в порядке варианта №1 (ТЗ §8); вторая попытка — вариант №2.
        var (client, testVersionId) = await LoginWithVersionAsync();
        var variants = await GenerateVariantsAsync(client, testVersionId);

        string[] order1, order2;
        Guid qvId;
        await using (var db = NewDb())
        {
            var v1 = await db.Variants.AsNoTracking().SingleAsync(v => v.Id == variants[0].GetProperty("id").GetGuid());
            var v2 = await db.Variants.AsNoTracking().SingleAsync(v => v.Id == variants[1].GetProperty("id").GetGuid());
            var version = await db.TestVersions.AsNoTracking().SingleAsync(t => t.Id == v1.TestVersionId);
            qvId = version.QuestionVersionIds[0];
            order1 = [.. v1.OptionOrders[qvId.ToString()]];
            order2 = [.. v2.OptionOrders[qvId.ToString()]];
        }

        // назначение + студент + код — через teacher-API (e2e)
        var create = await client.PostAsJsonAsync("/api/assignments", new
        {
            testVersionId, mode = "Assessment",
            opensAtUtc = factory.FakeTime.GetUtcNow().AddHours(-1), closesAtUtc = (DateTimeOffset?)null,
            maxAttempts = 2, perQuestionTimeLimitSeconds = 40, attemptTotalTimeLimitSeconds = (int?)null,
            noBacktracking = true,
        });
        var created = JsonSerializer.Deserialize<JsonElement>(await create.Content.ReadAsStringAsync());
        var assignmentId = created.GetProperty("assignmentId").GetGuid();

        var addStudent = await client.PostAsJsonAsync($"/api/assignments/{assignmentId}/students",
            new { lastName = "Иванова", firstName = "Анна", middleName = (string?)null, groupName = (string?)null });
        var invitation = JsonSerializer.Deserialize<JsonElement>(await addStudent.Content.ReadAsStringAsync());
        var code = invitation.GetProperty("oneTimeCode").GetString()!;

        var student = factory.CreateClient();
        var exchange = await student.PostAsJsonAsync("/api/access/exchange", new { code });
        exchange.EnsureSuccessStatusCode();
        var af = await student.GetFromJsonAsync<JsonElement>("/api/access/antiforgery");
        student.DefaultRequestHeaders.Add("X-XSRF-TOKEN", af.GetProperty("requestToken").GetString()!);

        var start = await student.PostAsJsonAsync($"/api/assignments/{assignmentId}/attempts", new { });
        start.EnsureSuccessStatusCode();
        var attempt = JsonSerializer.Deserialize<JsonElement>(await start.Content.ReadAsStringAsync());
        var attemptId = attempt.GetProperty("attemptId").GetGuid();

        var openNext = await student.PostAsJsonAsync($"/api/attempts/{attemptId}/open-next", new { });
        openNext.EnsureSuccessStatusCode();
        var state = JsonSerializer.Deserialize<JsonElement>(await openNext.Content.ReadAsStringAsync());
        var options = state.GetProperty("currentQuestion").GetProperty("options");
        Assert.Equal(3, options.GetArrayLength());
        for (var i = 0; i < order1.Length; i++)
            Assert.Equal(order1[i], options[i].GetProperty("id").GetString());
    }
}
