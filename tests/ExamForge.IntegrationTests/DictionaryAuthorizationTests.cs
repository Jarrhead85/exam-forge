using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ExamForge.Application.TeacherFlow;
using ExamForge.Application.StudentFlow;
using ExamForge.Domain.Dictionary;
using ExamForge.Domain.Subjects;
using ExamForge.Infrastructure.Persistence;
using ExamForge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ExamForge.IntegrationTests;

/// <summary>
/// P0-08: authorization словаря — чужое/недоступное неотличимо от несуществующего (404).
/// Модель: Private (только владелец/админ), SharedRead (использование без правки),
/// SharedEdit (правка только по явному DictionaryGrant), legacy (OwnerTeacherId = Guid.Empty
/// — прежнее общедоступное поведение для существующих данных).
/// </summary>
[Collection("database")]
public sealed class DictionaryAuthorizationTests(ExamForgeWebFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private ExamForgeDbContext NewDb()
    {
        var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ExamForgeDbContext>();
    }

    /// <summary>Словарь с двумя записями (одна подтверждена) фиксированного владельца и режима.</summary>
    private async Task<(Guid DictionaryId, Guid EntryId)> SeedDictionaryAsync(
        Guid ownerTeacherId, Dictionary.AccessMode mode, bool legacy = false)
    {
        await using var db = NewDb();
        var subject = Subject.Create("Латынь-" + Guid.NewGuid().ToString("N")[..6], null, Now);
        db.Subjects.Add(subject);
        var dictionary = legacy
            ? Dictionary.Create(subject.Id, "Legacy-словарь " + Guid.NewGuid().ToString("N")[..6], Now)
            : Dictionary.Create(subject.Id, "Словарь " + Guid.NewGuid().ToString("N")[..6], Now, ownerTeacherId);
        dictionary.SetAccessMode(mode);
        db.Dictionaries.Add(dictionary);
        var feature = new GrammaticalFeatures { FormKind = "solid", Gender = "m", Declension = "II" };
        var entry = DictionaryEntry.Import(dictionary.Id, "oculus, i m — glaz", "oculus",
            ["glaz"], feature, [], "ручной ввод", Now);
        entry.Confirm(feature, ["glaz"], Now);
        db.DictionaryEntries.Add(entry);
        var draft = DictionaryEntry.Import(dictionary.Id, "auris, ae f — uho", "auris",
            ["uho"], null, [], "стр. 2", Now);
        db.DictionaryEntries.Add(draft);
        await db.SaveChangesAsync();
        return (dictionary.Id, entry.Id);
    }

    private async Task<HttpClient> StrangerAsync()
        => (await TeacherLogin.CreateAndLoginAsync(factory)).Client;

    // ---------- Private: чужой словарь неотличим от несуществующего ----------

    [Fact]
    public async Task ForeignTeacher_PrivateEntries_Is404()
    {
        var (_, ownerId) = await TeacherLogin.CreateAndLoginAsync(factory);
        var (dictionaryId, _) = await SeedDictionaryAsync(ownerId, Dictionary.AccessMode.Private);
        var stranger = await StrangerAsync();
        var response = await stranger.GetAsync($"/api/dictionaries/{dictionaryId}/entries");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ForeignTeacher_PrivateList_IsHidden_OwnerSees()
    {
        var (owner, ownerId) = await TeacherLogin.CreateAndLoginAsync(factory);
        var (dictionaryId, _) = await SeedDictionaryAsync(ownerId, Dictionary.AccessMode.Private);
        var stranger = await StrangerAsync();

        var hidden = await stranger.GetFromJsonAsync<JsonElement[]>("/api/dictionaries", Json);
        Assert.DoesNotContain(hidden!, d => d.GetProperty("dictionaryId").GetGuid() == dictionaryId);

        var visible = await owner.GetFromJsonAsync<JsonElement[]>("/api/dictionaries", Json);
        Assert.Contains(visible!, d => d.GetProperty("dictionaryId").GetGuid() == dictionaryId);
    }

    [Fact]
    public async Task ForeignTeacher_PrivateConfirm_Is404()
    {
        var (_, ownerId) = await TeacherLogin.CreateAndLoginAsync(factory);
        var (_, entryId) = await SeedDictionaryAsync(ownerId, Dictionary.AccessMode.Private);
        var stranger = await StrangerAsync();

        var response = await stranger.PostAsJsonAsync($"/api/dictionary-entries/{entryId}/confirm", new
        {
            formKind = (string?)null, gender = (string?)null, declension = (string?)null,
            grammaticalCase = (string?)null, translations = new[] { "глаз" },
        });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ForeignTeacher_PrivateReject_Is404()
    {
        var (_, ownerId) = await TeacherLogin.CreateAndLoginAsync(factory);
        var (_, entryId) = await SeedDictionaryAsync(ownerId, Dictionary.AccessMode.Private);
        var stranger = await StrangerAsync();

        var response = await stranger.PostAsJsonAsync($"/api/dictionary-entries/{entryId}/reject", new { });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ForeignTeacher_PrivateImport_Is404()
    {
        var (_, ownerId) = await TeacherLogin.CreateAndLoginAsync(factory);
        var (dictionaryId, _) = await SeedDictionaryAsync(ownerId, Dictionary.AccessMode.Private);
        var stranger = await StrangerAsync();

        // multipart (как реальный импорт) + antiforgery: доступ проверяет сервис → 404 до чтения PDF.
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(new byte[] { 0x25, 0x50, 0x44, 0x46 }), "file", "fault.pdf" },
        };
        var af = await stranger.GetFromJsonAsync<JsonElement>("/api/access/antiforgery-teacher", Json);
        var importRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/dictionaries/{dictionaryId}/import")
        {
            Content = form,
        };
        importRequest.Headers.Add("X-XSRF-TOKEN", af.GetProperty("requestToken").GetString());

        var response = await stranger.SendAsync(importRequest);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ForeignTeacher_PrivateDraftQuestion_Is404()
    {
        var (_, ownerId) = await TeacherLogin.CreateAndLoginAsync(factory);
        var (_, entryId) = await SeedDictionaryAsync(ownerId, Dictionary.AccessMode.Private);
        var stranger = await StrangerAsync();

        var response = await stranger.PostAsJsonAsync($"/api/dictionary-entries/{entryId}/draft-question", new { });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- SharedRead: чтение разрешено, правка нет ----------

    [Fact]
    public async Task SharedRead_Stranger_CanRead_CannotEdit()
    {
        var (_, ownerId) = await TeacherLogin.CreateAndLoginAsync(factory);
        var (dictionaryId, entryId) = await SeedDictionaryAsync(ownerId, Dictionary.AccessMode.SharedRead);
        var stranger = await StrangerAsync();

        var entries = await stranger.GetAsync($"/api/dictionaries/{dictionaryId}/entries");
        Assert.Equal(HttpStatusCode.OK, entries.StatusCode);

        var confirm = await stranger.PostAsJsonAsync($"/api/dictionary-entries/{entryId}/confirm", new
        {
            formKind = (string?)null, gender = (string?)null, declension = (string?)null,
            grammaticalCase = (string?)null, translations = new[] { "перевод-чужого" },
        });
        Assert.Equal(HttpStatusCode.NotFound, confirm.StatusCode);

        var include = await stranger.GetFromJsonAsync<JsonElement[]>("/api/dictionaries", Json);
        Assert.Contains(include!, d => d.GetProperty("dictionaryId").GetGuid() == dictionaryId);
    }

    // ---------- SharedEdit: право править только по явному DictionaryGrant ----------

    [Fact]
    public async Task SharedEdit_WithGrant_CanEdit_WithoutGrant_ReadOnly()
    {
        var (grantedClient, grantedId) = await TeacherLogin.CreateAndLoginAsync(factory);
        var (plainClient, plainId) = await TeacherLogin.CreateAndLoginAsync(factory);
        var (_, ownerId) = await TeacherLogin.CreateAndLoginAsync(factory);
        var (dictionaryId, entryId) = await SeedDictionaryAsync(ownerId, Dictionary.AccessMode.SharedEdit);

        await using (var db = NewDb())
        {
            db.DictionaryGrants.Add(DictionaryGrant.Create(dictionaryId, grantedId, ownerId, Now));
            await db.SaveChangesAsync();
        }

        // Грант = право правки.
        var ok = await grantedClient.PostAsJsonAsync($"/api/dictionary-entries/{entryId}/confirm", new
        {
            formKind = (string?)null, gender = "m", declension = "II",
            grammaticalCase = (string?)null, translations = new[] { "глаз" },
        });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // Без гранта — только чтение.
        var read = await plainClient.GetAsync($"/api/dictionaries/{dictionaryId}/entries");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var reject = await plainClient.PostAsJsonAsync($"/api/dictionary-entries/{entryId}/reject", new { });
        Assert.Equal(HttpStatusCode.NotFound, reject.StatusCode);
    }

    // ---------- Legacy: прежние общие словари сохраняют поведение ----------

    [Fact]
    public async Task LegacyDictionary_Stranger_CanReadAndConfirm()
    {
        var (dictionaryId, entryId) = await SeedDictionaryAsync(Guid.NewGuid(), Dictionary.AccessMode.Private, legacy: true);
        var stranger = await StrangerAsync();

        var entries = await stranger.GetAsync($"/api/dictionaries/{dictionaryId}/entries");
        Assert.Equal(HttpStatusCode.OK, entries.StatusCode);

        var confirm = await stranger.PostAsJsonAsync($"/api/dictionary-entries/{entryId}/confirm", new
        {
            formKind = (string?)null, gender = "m", declension = "II",
            grammaticalCase = (string?)null, translations = new[] { "глаз" },
        });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
    }

    // ---------- Администратор ----------

    [Fact]
    public async Task Administrator_ConfirmsForeignPrivateEntry()
    {
        var (_, ownerId) = await TeacherLogin.CreateAndLoginAsync(factory);
        var (dictionaryId, entryId) = await SeedDictionaryAsync(ownerId, Dictionary.AccessMode.Private);
        var (admin, _) = await TeacherLogin.CreateAndLoginAsync(factory, administrator: true);

        var entries = await admin.GetAsync($"/api/dictionaries/{dictionaryId}/entries");
        Assert.Equal(HttpStatusCode.OK, entries.StatusCode);
        var confirm = await admin.PostAsJsonAsync($"/api/dictionary-entries/{entryId}/confirm", new
        {
            formKind = (string?)null, gender = "m", declension = "II",
            grammaticalCase = (string?)null, translations = new[] { "глаз" },
        });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
    }

    // ---------- Service-level: переименование/удаление (без HTTP-endpoint) ----------

    [Fact]
    public async Task ForeignTeacher_RenameAndDelete_Are404()
    {
        var (_, ownerId) = await TeacherLogin.CreateAndLoginAsync(factory);
        var (dictionaryId, _) = await SeedDictionaryAsync(ownerId, Dictionary.AccessMode.Private);
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<DictionaryService>();
        var strangerActor = new TeacherActor(Guid.NewGuid(), false);

        await Assert.ThrowsAsync<StudentFlowException>(() =>
            service.RenameAsync(strangerActor, dictionaryId, "Чужое имя", CancellationToken.None));
        await Assert.ThrowsAsync<StudentFlowException>(() =>
            service.DeleteAsync(strangerActor, dictionaryId, CancellationToken.None));

        // Состояние не изменилось.
        await using var db = NewDb();
        var unchanged = await db.Dictionaries.AsNoTracking().SingleAsync(d => d.Id == dictionaryId);
        Assert.NotEqual("Чужое имя", unchanged.Name);
    }

    [Fact]
    public async Task Owner_RenameAndDelete_Succeed()
    {
        var (_, ownerId) = await TeacherLogin.CreateAndLoginAsync(factory);
        var (dictionaryId, _) = await SeedDictionaryAsync(ownerId, Dictionary.AccessMode.Private);
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<DictionaryService>();
        var ownerActor = new TeacherActor(ownerId, false);

        await service.RenameAsync(ownerActor, dictionaryId, "Новое имя владельца", CancellationToken.None);
        await using (var db = NewDb())
        {
            var renamed = await db.Dictionaries.AsNoTracking().SingleAsync(d => d.Id == dictionaryId);
            Assert.Equal("Новое имя владельца", renamed.Name);
        }

        var deletedEntries = await service.DeleteAsync(ownerActor, dictionaryId, CancellationToken.None);
        Assert.Equal(2, deletedEntries);
        await using var check = NewDb();
        Assert.False(await check.Dictionaries.AsNoTracking().AnyAsync(d => d.Id == dictionaryId));
        Assert.False(await check.DictionaryEntries.AsNoTracking().AnyAsync(e => e.DictionaryId == dictionaryId));
    }
}
