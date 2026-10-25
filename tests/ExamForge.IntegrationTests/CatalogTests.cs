using System.Net;
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
/// Хвосты без заглушек: группы (§4), словарь с PDF-импортом (§6), админ-преподаватели (§3.1),
/// статистика и XLSX (§12). PDF — минимальный валидный файл с текстовым слоем (PdfPig читает).
/// </span>
[Collection("database")]
public sealed class CatalogTests(ExamForgeWebFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ExamForgeDbContext NewDb()
    {
        var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ExamForgeDbContext>();
    }

    private async Task<HttpClient> LoginTeacherAsync(bool admin = false)
        => (await TeacherLogin.CreateAndLoginAsync(factory, administrator: admin)).Client;

    private static byte[] MinimalPdfWithText(params string[] lines)
    {
        // Минимальный PDF 1.4 — каждый строковый блок отдельно: PdfPig отдаёт их построчно.
        var textOps = string.Join("\n", lines.Select((l, i) =>
            $"BT /F1 12 Tf 72 {720 - 20 * i} Td ({l}) Tj ET"));
        var content = textOps;
        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
        };
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        foreach (var o in objects)
        {
            offsets.Add(Encoding.ASCII.GetBytes(pdf.ToString()).Length);
            pdf.Append(o);
        }
        var xrefStart = Encoding.ASCII.GetBytes(pdf.ToString()).Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xrefStart}\n%%EOF");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    // ---------- Группы (§4) ----------

    [Fact]
    public async Task Groups_CreateAddMember_List()
    {
        var teacher = await LoginTeacherAsync();
        var probe = await teacher.GetAsync("/api/groups");
        var probeBody = await probe.Content.ReadAsStringAsync();
        Assert.True(probe.IsSuccessStatusCode, $"probe GET {probe.StatusCode}: {probeBody}");
        var created = await teacher.PostAsJsonAsync("/api/groups", new { name = "ЛД-201" });
        var createBody = await created.Content.ReadAsStringAsync();
        Assert.True(created.IsSuccessStatusCode, $"create {created.StatusCode}: {createBody}");
        var groupId = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("groupId").GetGuid();

        var add = await teacher.PostAsJsonAsync($"/api/groups/{groupId}/students",
            new { lastName = "Иванова", firstName = "Анна", middleName = "Сергеевна" });
        add.EnsureSuccessStatusCode();
        var profileId = (await add.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("studentProfileId").GetGuid();

        var members = await teacher.GetFromJsonAsync<JsonElement[]>($"/api/groups/{groupId}/students", Json);
        Assert.Single(members!);
        Assert.Contains("Иванова", members![0].GetProperty("name").GetString());
        Assert.Equal(profileId, members[0].GetProperty("studentProfileId").GetGuid());

        // ФИО не уникальный ключ: второй такой же — отдельный профиль
        var add2 = await teacher.PostAsJsonAsync($"/api/groups/{groupId}/students",
            new { lastName = "Иванова", firstName = "Анна", middleName = "Сергеевна" });
        add2.EnsureSuccessStatusCode();
        var profileId2 = (await add2.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("studentProfileId").GetGuid();
        Assert.NotEqual(profileId, profileId2);

        // чужой преподаватель не видит группу
        var stranger = await LoginTeacherAsync();
        var foreign = await stranger.GetAsync($"/api/groups/{groupId}/students");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    // ---------- Словарь с PDF-импортом (§6, AC-13) ----------

    [Fact]
    public async Task Dictionary_ImportPdf_CreatesDrafts_ConfirmAndDraftQuestion()
    {
        await using var db = NewDb();
        var subject = Domain.Subjects.Subject.Create("Латынь " + Guid.NewGuid().ToString("N")[..6], null, DateTime.UtcNow);
        db.Subjects.Add(subject);
        await db.SaveChangesAsync();
        var subjectId = subject.Id;

        var teacher = await LoginTeacherAsync();
        var create = await teacher.PostAsJsonAsync("/api/dictionaries", new { subjectId, name = "Импорт-словарь" });
        create.EnsureSuccessStatusCode();
        var dictionaryId = (await create.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("dictionaryId").GetGuid();

        // импорт PDF с двумя записями: вторая — источник отвлекающих вариантов
        var pdf = MinimalPdfWithText("oculus, i m - glaz", "auris, ae f - uho", "nasus, i m - nos");
        var form = new MultipartFormDataContent
        {
            { new StreamContent(new MemoryStream(pdf)), "file", "dict.pdf" },
        };
        // multipart POST проходит antiforgery-мидлварь .NET 10 — нужен X-XSRF-TOKEN
        var af = await teacher.GetFromJsonAsync<JsonElement>("/api/access/antiforgery-teacher", Json);
        var importRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/dictionaries/{dictionaryId}/import")
        {
            Content = form,
        };
        importRequest.Headers.Add("X-XSRF-TOKEN", af.GetProperty("requestToken").GetString());
        var import = await teacher.SendAsync(importRequest);
        Assert.True(import.IsSuccessStatusCode, await import.Content.ReadAsStringAsync());
        var result = await import.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(3, result.GetProperty("imported").GetInt32());

        // запись — черновик с NeedsReview (AC-13: без автопубликации)
        var entries = await teacher.GetFromJsonAsync<JsonElement[]>($"/api/dictionaries/{dictionaryId}/entries", Json);
        Assert.NotNull(entries);
        Assert.Equal(3, entries!.Length);
        var oculus = entries.Single(e => e.GetProperty("lemma").GetString() == "oculus");
        Assert.Equal("ImportedDraft", oculus.GetProperty("status").GetString());
        Assert.True(oculus.GetProperty("needsReview").GetBoolean());
        Assert.Equal("glaz", oculus.GetProperty("translations")[0].GetString());
        var entryId = oculus.GetProperty("entryId").GetGuid();

        // подтверждение с признаками
        var confirm = await teacher.PostAsJsonAsync($"/api/dictionary-entries/{entryId}/confirm", new
        {
            formKind = (string?)null, gender = "n", declension = "II", grammaticalCase = (string?)null,
            translations = new[] { "glaz" },
        });

        // вторая запись — источник отвлекающих вариантов
        var uhoEntry = entries.Single(e => e.GetProperty("lemma").GetString() == "auris");
        var confirm2 = await teacher.PostAsJsonAsync($"/api/dictionary-entries/{uhoEntry.GetProperty("entryId").GetGuid()}/confirm", new
        {
            formKind = (string?)null, gender = "f", declension = "I", grammaticalCase = (string?)null,
            translations = new[] { "uho" },
        });
        confirm2.EnsureSuccessStatusCode();
        confirm.EnsureSuccessStatusCode();
        var confirmed = await confirm.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("Confirmed", confirmed.GetProperty("status").GetString());

        // черновик вопроса из подтверждённой записи — не опубликован
        var draft = await teacher.PostAsJsonAsync($"/api/dictionary-entries/{entryId}/draft-question", new { });
        var draftBody = await draft.Content.ReadAsStringAsync();
        Assert.True(draft.IsSuccessStatusCode, $"draft-question {draft.StatusCode}: {draftBody[..Math.Min(300, draftBody.Length)]}");
        var questionId = (await draft.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("questionId").GetGuid();
        await using (var db2 = NewDb())
        {
            var question = await db2.Questions.AsNoTracking().SingleAsync(q => q.Id == questionId);
            Assert.Equal(Domain.Questions.QuestionStatus.Draft, question.Status);
        }
    }

    // ---------- Админ-преподаватели (§3.1) ----------

    [Fact]
    public async Task Admin_CreatesTeacher_TeacherCannot()
    {
        var admin = await LoginTeacherAsync(admin: true);
        var login = "new-teacher-" + Guid.NewGuid().ToString("N")[..8];
        var create = await admin.PostAsJsonAsync("/api/admin/teachers", new
        {
            login, password = "StrongPass99", displayName = "Новый Преподаватель",
        });
        create.EnsureSuccessStatusCode();

        var list = await admin.GetFromJsonAsync<JsonElement[]>("/api/admin/teachers", Json);
        Assert.Contains(list!, t => t.GetProperty("login").GetString() == login);

        var teacher = await LoginTeacherAsync();
        var forbidden = await teacher.PostAsJsonAsync("/api/admin/teachers", new
        {
            login = "hack-" + Guid.NewGuid().ToString("N")[..6], password = "StrongPass99", displayName = "X",
        });
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
    }

    // ---------- Статистика и XLSX (§12) ----------

    [Fact]
    public async Task StatsAndXlsx_ExplicitDenominators()
    {
        var admin = await LoginTeacherAsync(admin: true);
        // демо-данные уже могут быть в dev, но тестовая база чистая: создаём через API быстро
        await using var db = NewDb();
        var subject = Domain.Subjects.Subject.Create("Латынь " + Guid.NewGuid().ToString("N")[..6], null, DateTime.UtcNow);
        var author = new TeacherUser { UserName = "cat-seed-" + Guid.NewGuid().ToString("N")[..8], DisplayName = "С" };
        db.Subjects.Add(subject);
        db.Users.Add(author);
        await db.SaveChangesAsync();
        var definition = Domain.Questions.QuestionDefinitionJson.Serialize(new()
        {
            Type = Domain.Questions.QuestionType.SingleChoice, Prompt = "p",
            Options = [new("a", "1"), new("b", "2")], CorrectOptionId = "a",
            MaxPoints = 1m, ProposedTimeLimitSeconds = 40,
        });
        var question = Domain.Questions.Question.CreateDraft(subject.Id, null, author.Id, DateTime.UtcNow);
        db.Questions.Add(question);
        await db.SaveChangesAsync();
        var version = Domain.Questions.QuestionVersion.Create(question.Id, 1,
            Domain.Questions.QuestionType.SingleChoice, definition, 1, 1m, 40, DateTime.UtcNow);
        var test = Domain.Testing.Test.Create(subject.Id, author.Id, "Стат-тест", DateTime.UtcNow);
        db.QuestionVersions.Add(version);
        db.Tests.Add(test);
        await db.SaveChangesAsync();
        var testVersion = Domain.Testing.TestVersion.Create(test.Id, 1, "Стат-тест", [version.Id], 1m, DateTime.UtcNow);
        testVersion.Publish(DateTime.UtcNow);
        db.TestVersions.Add(testVersion);
        var assignment = Domain.Assignments.Assignment.Create(testVersion.Id,
            Domain.Assignments.AssignmentMode.Assessment, Domain.Assignments.FeedbackPolicy.ScoresOnly,
            DateTime.UtcNow.AddHours(-1), null, 2, 40, null, true, false, author.Id, DateTime.UtcNow);
        db.Assignments.Add(assignment);
        await db.SaveChangesAsync();
        // назначение владеет seed-автором; для API-доступа делаем админа (он видит всё)

        var stats = await admin.GetFromJsonAsync<JsonElement>($"/api/assignments/{assignment.Id}/stats", Json);
        Assert.Equal(0, stats.GetProperty("attemptsTotal").GetInt32()); // знаменатель: пока 0 попыток
        Assert.Equal(0, stats.GetProperty("studentsInvited").GetInt32());

        var xlsx = await admin.GetAsync($"/api/assignments/{assignment.Id}/gradebook.xlsx");
        Assert.True(xlsx.IsSuccessStatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            xlsx.Content.Headers.ContentType!.MediaType);
        var bytes = await xlsx.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 1000);
        // XLSX — это ZIP: сигнатура PK
        Assert.Equal(0x50, bytes[0]);
        Assert.Equal(0x4B, bytes[1]);
    }
}
