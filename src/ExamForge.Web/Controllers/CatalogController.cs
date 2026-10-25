using ExamForge.Application.TeacherFlow;
using ExamForge.Infrastructure.Identity;
using ExamForge.Infrastructure.Persistence;
using ExamForge.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ExamForge.Web.Controllers;

/// <summary>Группы, предметы, словарь, преподаватели (админ), результаты-статистика и XLSX.</summary>
[ApiController]
[Authorize]
public sealed class CatalogController(
    GroupService groups,
    DictionaryService dictionary,
    AdminTeachersService adminTeachers,
    ResultsService results,
    TeacherQuestionsService questions,
    TeacherAssignmentsService assignments,
    ExamForgeDbContext db,
    TimeProvider timeProvider,
    UserManager<TeacherUser> userManager) : ControllerBase
{
    private async Task<TeacherActor> ActorAsync()
    {
        var user = await userManager.GetUserAsync(User)
            ?? throw TeacherFlowErrors.NotFound("Пользователь не найден.");
        return new TeacherActor(user.Id, await userManager.IsInRoleAsync(user, AppRoles.Administrator));
    }

    // ---------- Предметы (§4) ----------

    public sealed record CreateSubjectRequest(string Name, string? Code);

    [HttpGet("/api/subjects")]
    public async Task<IActionResult> ListSubjects(CancellationToken ct)
        => Ok(await db.Subjects.AsNoTracking()
            .OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name, s.Code })
            .ToListAsync(ct));

    [HttpPost("/api/subjects")]
    public async Task<IActionResult> CreateSubject([FromBody] CreateSubjectRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            throw TeacherFlowErrors.Conflict("bad_request", "Введите название предмета.");
        var exists = await db.Subjects.AnyAsync(s => s.Name == name, ct);
        if (exists)
            throw TeacherFlowErrors.Conflict("duplicate", "Предмет с таким названием уже есть.");
        var subject = Domain.Subjects.Subject.Create(name, request.Code, timeProvider.GetUtcNow());
        db.Subjects.Add(subject);
        await db.SaveChangesAsync(ct);
        return Ok(new { subjectId = subject.Id });
    }

    // ---------- Группы (§4) ----------

    public sealed record CreateGroupRequest(string Name);
    public sealed record AddGroupStudentRequest(string LastName, string FirstName, string? MiddleName);

    [HttpPost("/api/groups")]
    public async Task<IActionResult> CreateGroup([FromBody] CreateGroupRequest request, CancellationToken ct)
    {
        var group = await groups.CreateAsync(await ActorAsync(), request.Name, ct);
        return Ok(new { groupId = group.Id });
    }

    [HttpGet("/api/groups")]
    public async Task<IActionResult> ListGroups(CancellationToken ct)
        => Ok(await groups.ListAsync(await ActorAsync(), ct));

    [HttpGet("/api/groups/{groupId:guid}/students")]
    public async Task<IActionResult> GroupStudents(Guid groupId, CancellationToken ct)
        => Ok(await groups.MembersAsync(await ActorAsync(), groupId, ct));

    [HttpPost("/api/groups/{groupId:guid}/students")]
    public async Task<IActionResult> AddStudent(Guid groupId, [FromBody] AddGroupStudentRequest request, CancellationToken ct)
    {
        var student = await groups.AddNewStudentAsync(
            await ActorAsync(), groupId, request.LastName, request.FirstName, request.MiddleName, ct);
        return Ok(new { studentProfileId = student.Id });
    }

    [HttpPost("/api/groups/{groupId:guid}/students/{studentProfileId:guid}")]
    public async Task<IActionResult> AttachStudent(Guid groupId, Guid studentProfileId, CancellationToken ct)
    {
        await groups.AddStudentAsync(await ActorAsync(), groupId, studentProfileId, ct);
        return Ok(new { attached = true });
    }

    [HttpDelete("/api/groups/{groupId:guid}/students/{studentProfileId:guid}")]
    public async Task<IActionResult> DetachStudent(Guid groupId, Guid studentProfileId, CancellationToken ct)
    {
        await groups.RemoveStudentAsync(await ActorAsync(), groupId, studentProfileId, ct);
        return Ok(new { detached = true });
    }

    // ---------- Словарь (§6) ----------

    public sealed record CreateDictionaryRequest(Guid SubjectId, string Name);
    public sealed record ConfirmEntryRequest(string? FormKind, string? Gender, string? Declension, string? GrammaticalCase, IReadOnlyList<string> Translations);

    [HttpPost("/api/dictionaries")]
    public async Task<IActionResult> CreateDictionary([FromBody] CreateDictionaryRequest request, CancellationToken ct)
    {
        var created = await dictionary.CreateAsync(await ActorAsync(), request.SubjectId, request.Name, ct);
        return Ok(new { dictionaryId = created.Id });
    }

    [HttpGet("/api/dictionaries")]
    public async Task<IActionResult> ListDictionaries(CancellationToken ct)
        => Ok(await dictionary.ListAsync(ct));

    [HttpGet("/api/dictionaries/{dictionaryId:guid}/entries")]
    public async Task<IActionResult> Entries(Guid dictionaryId, CancellationToken ct)
        => Ok(await dictionary.EntriesAsync(dictionaryId, ct));

    [HttpPost("/api/dictionaries/{dictionaryId:guid}/import")]
    [RequestSizeLimit(30 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 30 * 1024 * 1024)]
    public async Task<IActionResult> ImportPdf(Guid dictionaryId, IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { title = "Файл PDF обязателен." });
        await using var stream = file.OpenReadStream();
        var (imported, skipped) = await dictionary.ImportPdfAsync(await ActorAsync(), dictionaryId, stream, ct);
        return Ok(new { imported, skipped });
    }

    [HttpPost("/api/dictionary-entries/{entryId:guid}/confirm")]
    public async Task<IActionResult> Confirm(Guid entryId, [FromBody] ConfirmEntryRequest request, CancellationToken ct)
        => Ok(await dictionary.ConfirmAsync(await ActorAsync(), entryId,
            request.FormKind, request.Gender, request.Declension, request.GrammaticalCase, request.Translations, ct));

    [HttpPost("/api/dictionary-entries/{entryId:guid}/reject")]
    public async Task<IActionResult> Reject(Guid entryId, CancellationToken ct)
    {
        await dictionary.RejectAsync(await ActorAsync(), entryId, ct);
        return Ok(new { rejected = true });
    }

    /// <summary>Черновик вопроса «выбор перевода» из подтверждённой записи (ТЗ §6, AC: не автопубликация).</summary>
    [HttpPost("/api/dictionary-entries/{entryId:guid}/draft-question")]
    public async Task<IActionResult> DraftQuestion(Guid entryId, CancellationToken ct)
    {
        var entry = await dictionary.LoadEntryAsync(entryId, ct);
        var others = await dictionary.EntriesAsync(entry.DictionaryId, ct);
        var distractors = others
            .Where(o => o.EntryId != entryId && o.Translations.Count > 0)
            .Select(o => o.Translations[0])
            .Distinct()
            .Take(6)
            .ToList();
        var definition = Domain.Questions.DictionaryDraftFactory.BuildTranslationChoice(
            entry, distractors, timeLimitSeconds: 40);
        var actor = await ActorAsync();
        var version = await questions.CreateDraftAsync(actor, await SubjectOfDictionaryAsync(entry.DictionaryId, ct), definition, ct);
        return Ok(new { questionId = version.QuestionId, draft = true });
    }

    private async Task<Guid> SubjectOfDictionaryAsync(Guid dictionaryId, CancellationToken ct)
        => await dictionary.LoadDictionarySubjectAsync(dictionaryId, ct);

    // ---------- Преподаватели (§3.1, только админ) ----------

    public sealed record CreateTeacherRequest(string Login, string Password, string DisplayName);

    [HttpPost("/api/admin/teachers")]
    public async Task<IActionResult> CreateTeacher([FromBody] CreateTeacherRequest request, CancellationToken ct)
    {
        var id = await adminTeachers.CreateTeacherAsync(
            await ActorAsync(), request.Login, request.Password, request.DisplayName, ct);
        return Ok(new { teacherId = id });
    }

    [HttpGet("/api/admin/teachers")]
    public async Task<IActionResult> ListTeachers(CancellationToken ct)
        => Ok(await adminTeachers.ListTeachersAsync(await ActorAsync(), ct));

    // ---------- Результаты (§12) ----------

    [HttpGet("/api/assignments/{assignmentId:guid}/stats")]
    public async Task<IActionResult> Stats(Guid assignmentId, CancellationToken ct)
        => Ok(await results.StatsAsync(await ActorAsync(), assignmentId, ct));

    [HttpGet("/api/assignments/{assignmentId:guid}/gradebook.xlsx")]
    public async Task<IActionResult> GradebookXlsx(Guid assignmentId, CancellationToken ct)
    {
        var actor = await ActorAsync();
        var details = await assignments.GetAssignmentAsync(actor, assignmentId, ct);
        var bytes = results.GradebookXlsx(actor, assignmentId, details.Assignment.TestTitle, ct);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"gradebook-{assignmentId:N}.xlsx");
    }
}
