using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ExamForge.Infrastructure.Identity;
using ExamForge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ExamForge.IntegrationTests;

/// <summary>
/// Бумажные работы (ТЗ §13, AC-17) и лекции с grants (ТЗ §14).
/// Файлы — валидные сигнатуры PDF/JPEG; хранилище локальное (.storage).
/// </summary>
[Collection("database")]
public sealed class PaperMaterialsTests(ExamForgeWebFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static byte[] PdfBytes(string marker = "lecture") =>
        [.. Encoding.ASCII.GetBytes("%PDF-1.4\n%" + marker + "\nfake body"u8.ToArray()), 1, 2, 3];

    private static byte[] JpegBytes() => [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5];

    private ExamForgeDbContext NewDb()
    {
        var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ExamForgeDbContext>();
    }

    private async Task<HttpClient> LoginTeacherAsync()
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<TeacherUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        if (!await roleManager.RoleExistsAsync(AppRoles.Teacher))
            await roleManager.CreateAsync(new IdentityRole<Guid>(AppRoles.Teacher));
        var login = "p5t-teacher-" + Guid.NewGuid().ToString("N")[..8];
        var teacher = new TeacherUser { UserName = login, DisplayName = "Бумага Лекционная" };
        Assert.True((await userManager.CreateAsync(teacher, "LatynskieGoda1")).Succeeded);
        await userManager.AddToRoleAsync(teacher, AppRoles.Teacher);
        return await LoginClientAsync(factory, login);
    }

    internal static async Task<HttpClient> LoginClientAsync(ExamForgeWebFactory factory, string login)
    {
        // Shared login helper: static SSR form post is flaky under test-host load.
        var client = await TeacherLogin.LoginExistingAsync(factory, login);
        return client;
    }

    private async Task<Guid> CreateAssignmentAsync(HttpClient teacher)
    {
        await using var db = NewDb();
        var now = factory.FakeTime.GetUtcNow();
        var subject = Domain.Subjects.Subject.Create("Латынь " + Guid.NewGuid().ToString("N")[..6], null, now);
        var author = new TeacherUser { UserName = "p5t-seed-" + Guid.NewGuid().ToString("N")[..8], DisplayName = "С" };
        db.Subjects.Add(subject);
        db.Users.Add(author);
        await db.SaveChangesAsync();
        var definition = ExamForge.Domain.Questions.QuestionDefinitionJson.Serialize(new()
        {
            Type = ExamForge.Domain.Questions.QuestionType.SingleChoice,
            Prompt = "Перевод «caput»",
            Options = [new("a", "голова"), new("b", "рука")],
            CorrectOptionId = "a", MaxPoints = 1m, ProposedTimeLimitSeconds = 40,
        });
        var question = Domain.Questions.Question.CreateDraft(subject.Id, null, author.Id, now);
        db.Questions.Add(question);
        await db.SaveChangesAsync();
        var version = Domain.Questions.QuestionVersion.Create(question.Id, 1,
            ExamForge.Domain.Questions.QuestionType.SingleChoice, definition, 1, 1m, 40, now);
        var test = Domain.Testing.Test.Create(subject.Id, author.Id, "Бумажная контрольная", now);
        db.QuestionVersions.Add(version);
        db.Tests.Add(test);
        await db.SaveChangesAsync();
        var testVersion = Domain.Testing.TestVersion.Create(test.Id, 1, "Бумажная контрольная", [version.Id], 1m, now);
        testVersion.Publish(now);
        db.TestVersions.Add(testVersion);
        await db.SaveChangesAsync();

        var create = await teacher.PostAsJsonAsync("/api/assignments", new
        {
            testVersionId = testVersion.Id, mode = "Assessment",
            opensAtUtc = now.AddHours(-1), closesAtUtc = (DateTimeOffset?)null,
            maxAttempts = 2, perQuestionTimeLimitSeconds = 40, attemptTotalTimeLimitSeconds = (int?)null,
            noBacktracking = true,
        });
        create.EnsureSuccessStatusCode();
        var created = JsonSerializer.Deserialize<JsonElement>(await create.Content.ReadAsStringAsync());
        return created.GetProperty("assignmentId").GetGuid();
    }

    private static MultipartFormDataContent MultipartPdf(Guid subjectId, string fileName = "lecture.pdf")
    {
        var content = new MultipartFormDataContent
        {
            { new StreamContent(new MemoryStream(PdfBytes())), "file", fileName },
            { new StringContent("Лекция: термины"), "Title" },
            { new StringContent(subjectId.ToString()), "SubjectId" },
        };
        return content;
    }

    // ---------- Бумажные работы (AC-17) ----------

    [Fact]
    public async Task PaperWork_WithScan_ManualOrigin_PrivateAttachment_Audit()
    {
        // AC-17: бумажные баллы + фото → manual origin, приватное вложение, аудит.
        var teacher = await LoginTeacherAsync();
        var assignmentId = await CreateAssignmentAsync(teacher);

        var create = await teacher.PostAsJsonAsync($"/api/assignments/{assignmentId}/paper-works", new
        {
            studentName = "Орлова Ольга Олеговна",
            studentGroup = "ЛД-401",
            comment = "Письменная работа с распечатанного варианта",
            scores = new object[]
            {
                new { orderIndex = 0, label = "Задание 1", earned = 1m, max = 1m, comment = (string?)null },
                new { orderIndex = 1, label = "Задание 2", earned = 3.5m, max = 4m, comment = (string?)null },
            },
            studentProfileId = (Guid?)null,
        });
        create.EnsureSuccessStatusCode();
        var work = JsonSerializer.Deserialize<JsonElement>(await create.Content.ReadAsStringAsync());
        var workId = work.GetProperty("paperWorkId").GetGuid();
        Assert.Equal(4.5m, work.GetProperty("totalEarned").GetDecimal());
        Assert.Equal(5m, work.GetProperty("totalMax").GetDecimal());

        // скан (валидный JPEG) — приватное вложение
        var scansContent = new MultipartFormDataContent
        {
            { new StreamContent(new MemoryStream(JpegBytes())), "files", "scan1.jpg" },
        };
        var attach = await teacher.PostAsync($"/api/paper-works/{workId}/scans", scansContent);
        var attachBody = await attach.Content.ReadAsStringAsync();
        Assert.True(attach.IsSuccessStatusCode, $"attach {attach.StatusCode}: {attachBody}");
        var assets = JsonSerializer.Deserialize<JsonElement>(await attach.Content.ReadAsStringAsync());
        var fileId = assets[0].GetProperty("id").GetGuid();

        // владелец скачивает
        var download = await teacher.GetAsync($"/api/paper-works/{workId}/scans/{fileId}");
        Assert.True(download.IsSuccessStatusCode);
        Assert.Equal("image/jpeg", download.Content.Headers.ContentType!.MediaType);

        // чужой преподаватель и аноним — 404 (приватность, не раскрытие)
        var stranger = await LoginTeacherAsync();
        Assert.Equal(HttpStatusCode.NotFound,
            (await stranger.GetAsync($"/api/paper-works/{workId}/scans/{fileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClient().GetAsync($"/api/paper-works/{workId}/scans/{fileId}")).StatusCode);

        // Origin=Paper: сущность отдельная от онлайн-попыток; аудит зафиксирован без содержимого
        await using (var db = NewDb())
        {
            var paper = await db.PaperWorks.AsNoTracking().Include(w => w.Scores).SingleAsync(w => w.Id == workId);
            Assert.Equal(2, paper.Scores.Count);
            var audit = await db.AuditEvents.AsNoTracking()
                .Where(e => e.TargetType == "PaperWork" && e.TargetId == workId)
                .ToListAsync();
            Assert.Contains(audit, e => e.Kind == "paper-work-entered");
            Assert.Contains(audit, e => e.Kind == "paper-scans-attached");
        }

        // ведомость содержит строку Origin=Paper
        var csv = await teacher.GetStringAsync($"/api/assignments/{assignmentId}/gradebook.csv");
        Assert.Contains("бумажная", csv);
        Assert.Contains("Орлова Ольга Олеговна", csv);
    }

    [Fact]
    public async Task PaperWork_RejectsBadScores_AndBadFileSignature()
    {
        var teacher = await LoginTeacherAsync();
        var assignmentId = await CreateAssignmentAsync(teacher);

        // баллы вне диапазона
        var badScores = await teacher.PostAsJsonAsync($"/api/assignments/{assignmentId}/paper-works", new
        {
            studentName = "Тест", studentGroup = (string?)null, comment = (string?)null,
            scores = new[] { new { orderIndex = 0, label = "З1", earned = 6m, max = 5m, comment = (string?)null } },
            studentProfileId = (Guid?)null,
        });
        Assert.Equal(HttpStatusCode.BadRequest, badScores.StatusCode);

        // «PDF» без сигнатуры %PDF отклоняется
        var work = await teacher.PostAsJsonAsync($"/api/assignments/{assignmentId}/paper-works", new
        {
            studentName = "Тест", studentGroup = (string?)null, comment = (string?)null,
            scores = new[] { new { orderIndex = 0, label = "З1", earned = 1m, max = 1m, comment = (string?)null } },
            studentProfileId = (Guid?)null,
        });
        work.EnsureSuccessStatusCode();
        var workId = JsonSerializer.Deserialize<JsonElement>(await work.Content.ReadAsStringAsync())
            .GetProperty("paperWorkId").GetGuid();

        var fake = new MultipartFormDataContent
        {
            { new StreamContent(new MemoryStream("not a pdf at all"u8.ToArray())), "files", "fake.pdf" },
        };
        var attach = await teacher.PostAsync($"/api/paper-works/{workId}/scans", fake);
        Assert.Equal(HttpStatusCode.BadRequest, attach.StatusCode);
    }

    // ---------- Лекции (§14) ----------

    [Fact]
    public async Task Material_Lifecycle_UploadPublishGrant_DownloadByStudentOnly()
    {
        var teacher = await LoginTeacherAsync();
        var assignmentId = await CreateAssignmentAsync(teacher);

        // подготовим студента через назначение
        var addStudent = await teacher.PostAsJsonAsync($"/api/assignments/{assignmentId}/students",
            new { lastName = "Зайцев", firstName = "Захар", middleName = (string?)null, groupName = (string?)null });
        var invitation = JsonSerializer.Deserialize<JsonElement>(await addStudent.Content.ReadAsStringAsync());
        var profileId = invitation.GetProperty("studentProfileId").GetGuid();
        var code = invitation.GetProperty("oneTimeCode").GetString()!;

        await using var db = NewDb();
        var subjectId = await db.Subjects.AsNoTracking()
            .OrderByDescending(s => s.CreatedAtUtc)
            .Select(s => s.Id)
            .FirstAsync();

        // загрузка (PDF-сигнатура)
        var upload = await teacher.PostAsync("/api/materials", MultipartPdf(subjectId));
        Assert.True(upload.IsSuccessStatusCode, await upload.Content.ReadAsStringAsync());
        var material = JsonSerializer.Deserialize<JsonElement>(await upload.Content.ReadAsStringAsync());
        var materialId = material.GetProperty("materialId").GetGuid();

        // студент ещё не видит: черновик + нет grant
        var student = factory.CreateClient();
        Assert.True((await student.PostAsJsonAsync("/api/access/exchange", new { code })).IsSuccessStatusCode);
        var empty = await student.GetFromJsonAsync<JsonElement[]>("/api/student/materials", Json);
        Assert.Empty(empty!);

        // публикация + grant студенту
        Assert.True((await teacher.PostAsync($"/api/materials/{materialId}/publish", null)).IsSuccessStatusCode);
        Assert.True((await teacher.PostAsJsonAsync($"/api/materials/{materialId}/grants",
            new { groupId = (Guid?)null, studentProfileId = profileId })).IsSuccessStatusCode);

        // студент видит и скачивает через защищённый endpoint
        var list = await student.GetFromJsonAsync<JsonElement[]>("/api/student/materials", Json);
        Assert.NotNull(list);
        Assert.Single(list);
        Assert.Equal("Лекция: термины", list[0].GetProperty("title").GetString());
        var download = await student.GetAsync($"/api/materials/{materialId}/download");
        Assert.True(download.IsSuccessStatusCode);
        Assert.Equal("application/pdf", download.Content.Headers.ContentType!.MediaType);

        // студент без grant — 404; аноним — 401
        var strangerStudent = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await strangerStudent.GetAsync($"/api/materials/{materialId}/download")).StatusCode);
        var other = await CreateOtherStudentAsync(teacher, assignmentId);
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.GetAsync($"/api/materials/{materialId}/download")).StatusCode);

        // архив скрывает материал у студента с grant
        Assert.True((await teacher.PostAsync($"/api/materials/{materialId}/archive", null)).IsSuccessStatusCode);
        var afterArchive = await student.GetFromJsonAsync<JsonElement[]>("/api/student/materials", Json);
        Assert.Empty(afterArchive!);
    }

    private async Task<HttpClient> CreateOtherStudentAsync(HttpClient teacher, Guid assignmentId)
    {
        var add = await teacher.PostAsJsonAsync($"/api/assignments/{assignmentId}/students",
            new { lastName = "Чужой", firstName = "Студент", middleName = (string?)null, groupName = (string?)null });
        var invitation = JsonSerializer.Deserialize<JsonElement>(await add.Content.ReadAsStringAsync());
        var client = factory.CreateClient();
        Assert.True((await client.PostAsJsonAsync("/api/access/exchange",
            new { code = invitation.GetProperty("oneTimeCode").GetString()! })).IsSuccessStatusCode);
        return client;
    }
}
