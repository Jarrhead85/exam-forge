using ExamForge.Application.StudentFlow;
using ExamForge.Application.TeacherFlow;
using ExamForge.Domain.Access;
using ExamForge.Domain.Assignments;
using ExamForge.Domain.Attempts;
using ExamForge.Domain.People;
using ExamForge.Domain.Testing;
using ExamForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ExamForge.Infrastructure.Services;

/// <summary>
/// Действия преподавателя по назначениям (ТЗ §3.2, §9): создание, приглашения, extra grants,
/// обзор попыток. Проверка владения объектом на каждый вызов; администратору доступ отдельно.
/// </summary>
public sealed class TeacherAssignmentsService(
    ExamForgeDbContext db,
    TimeProvider timeProvider,
    InvitationCodeHasher hasher)
{
    private DateTimeOffset Now => timeProvider.GetUtcNow();

    public async Task<Assignment> CreateAssignmentAsync(TeacherActor actor, CreateAssignmentRequest request, CancellationToken ct)
    {
        var version = await db.TestVersions.AsNoTracking()
            .SingleOrDefaultAsync(v => v.Id == request.TestVersionId, ct)
            ?? throw TeacherFlowErrors.NotFound("Версия теста не найдена.");

        var mode = ParseEnum<AssignmentMode>(request.Mode, "Режим назначения");
        var assignment = Assignment.Create(
            version.Id, mode,
            feedback: mode == AssignmentMode.Practice ? FeedbackPolicy.PracticeFeedback : FeedbackPolicy.ScoresOnly,
            opensAtUtc: request.OpensAtUtc.ToUniversalTime(),
            closesAtUtc: request.ClosesAtUtc?.ToUniversalTime(),
            maxAttempts: request.MaxAttempts,
            perQuestionTimeLimitSeconds: request.PerQuestionTimeLimitSeconds,
            attemptTotalTimeLimitSeconds: request.AttemptTotalTimeLimitSeconds,
            noBacktracking: request.NoBacktracking,
            allowCommonLink: false, // включается явно отдельным решением (ТЗ §5.1)
            teacherId: actor.TeacherId,
            nowUtc: Now);
        db.Assignments.Add(assignment);
        await db.SaveChangesAsync(ct);
        return assignment;
    }

    public async Task<IReadOnlyList<TeacherAssignmentDto>> ListAssignmentsAsync(TeacherActor actor, CancellationToken ct)
    {
        var query = db.Assignments.AsNoTracking().AsQueryable();
        if (!actor.IsAdministrator)
            query = query.Where(a => a.CreatedByTeacherId == actor.TeacherId);

        var rows = await query
            .Join(db.TestVersions.AsNoTracking(), a => a.TestVersionId, v => v.Id, (a, v) => new { a, v.Title })
            .OrderByDescending(r => r.a.CreatedAtUtc)
            .ToListAsync(ct);

        var assignmentIds = rows.Select(r => r.a.Id).ToList();
        var invitedCounts = await db.Invitations.AsNoTracking()
            .Where(i => assignmentIds.Contains(i.AssignmentId))
            .GroupBy(i => i.AssignmentId)
            .Select(g => new { AssignmentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AssignmentId, x => x.Count, ct);
        var startedCounts = await db.Attempts.AsNoTracking()
            .Where(a => assignmentIds.Contains(a.AssignmentId))
            .GroupBy(a => a.AssignmentId)
            .Select(g => new { AssignmentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AssignmentId, x => x.Count, ct);

        var result = new List<TeacherAssignmentDto>(rows.Count);
        foreach (var row in rows)
        {
            result.Add(new TeacherAssignmentDto(
                row.a.Id, row.Title, row.a.Mode.ToString(), row.a.OpensAtUtc, row.a.ClosesAtUtc,
                row.a.MaxAttempts, invitedCounts.GetValueOrDefault(row.a.Id), startedCounts.GetValueOrDefault(row.a.Id),
                row.a.IsOpenAt(Now)));
        }
        return result;
    }

    public async Task<TeacherAssignmentDetailsDto> GetAssignmentAsync(TeacherActor actor, Guid assignmentId, CancellationToken ct)
    {
        var assignment = await LoadOwnedAsync(actor, assignmentId, ct);
        var title = await db.TestVersions.AsNoTracking()
            .Where(v => v.Id == assignment.TestVersionId)
            .Select(v => v.Title)
            .SingleAsync(ct);

        var students = await db.StudentProfiles.AsNoTracking()
            .Where(p => db.Invitations.Any(i => i.AssignmentId == assignmentId && i.StudentProfileId == p.Id))
            .ToDictionaryAsync(p => p.Id, ct);
        var invitations = await db.Invitations.AsNoTracking()
            .Where(i => i.AssignmentId == assignmentId)
            .OrderByDescending(i => i.CreatedAtUtc)
            .ToListAsync(ct);

        var attempts = await db.Attempts.AsNoTracking()
            .Where(a => a.AssignmentId == assignmentId)
            .OrderBy(a => a.StudentNameSnapshot).ThenBy(a => a.AttemptNumber)
            .ToListAsync(ct);

        var summary = new TeacherAssignmentDto(
            assignmentId, title, assignment.Mode.ToString(), assignment.OpensAtUtc, assignment.ClosesAtUtc,
            assignment.MaxAttempts, invitations.Count, attempts.Count, assignment.IsOpenAt(Now));

        var invitationDtos = invitations.Select(i => new InvitationDto(
            i.Id, i.StudentProfileId!.Value,
            students.TryGetValue(i.StudentProfileId!.Value, out var p) ? p.LastName + " " + p.FirstName : "?",
            null, i.CreatedAtUtc, i.Status.ToString(), i.ReplacedByInvitationId, null)).ToList();

        // Лучшая — только среди Finalized eligible (ТЗ §10.4); tie-break — ранняя завершённая (D-08).
        var best = AttemptRules.SelectBest(attempts);

        var attemptDtos = attempts.Select(a => new TeacherAttemptDto(
            a.Id, a.StudentNameSnapshot, a.StudentGroupSnapshot, a.AttemptNumber, a.State.ToString(),
            a.StartedAtUtc, a.CompletedAtUtc,
            a.Score?.EarnedPoints, a.Score?.MaxPoints, a.Score?.RoundedPercent,
            best?.Id == a.Id)).ToList();

        return new TeacherAssignmentDetailsDto(summary, invitationDtos, attemptDtos);
    }

    /// <summary>Создать профиль студента и приглашение одним действием (ТЗ §4: список ФИО ведёт преподаватель).</summary>
    public async Task<InvitationDto> AddStudentWithInvitationAsync(
        TeacherActor actor, Guid assignmentId, AddStudentRequest request, CancellationToken ct)
    {
        var assignment = await LoadOwnedAsync(actor, assignmentId, ct);
        var now = Now;
        var student = StudentProfile.Create(request.LastName, request.FirstName, request.MiddleName, now, actor.TeacherId);
        db.StudentProfiles.Add(student);
        await db.SaveChangesAsync(ct);
        return await CreateInvitationCoreAsync(assignment, student, now, ct);
    }

    public async Task<InvitationDto> CreateInvitationForStudentAsync(
        TeacherActor actor, Guid assignmentId, Guid studentProfileId, CancellationToken ct)
    {
        var assignment = await LoadOwnedAsync(actor, assignmentId, ct);
        var student = await db.StudentProfiles.FindAsync([studentProfileId], ct)
            ?? throw TeacherFlowErrors.NotFound("Студент не найден.");
        return await CreateInvitationCoreAsync(assignment, student, Now, ct);
    }

    private async Task<InvitationDto> CreateInvitationCoreAsync(
        Assignment assignment, StudentProfile student, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var code = InvitationCode.Generate();
        var invitation = Invitation.Create(
            assignment.Id, student.Id, hasher.ComputeHash(InvitationCode.Normalize(code)), null, assignment.CreatedByTeacherId, nowUtc);
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(ct);
        return ToDto(invitation, student, code);
    }

    public async Task RevokeInvitationAsync(TeacherActor actor, Guid invitationId, CancellationToken ct)
    {
        var invitation = await db.Invitations
            .SingleOrDefaultAsync(i => i.Id == invitationId, ct)
            ?? throw TeacherFlowErrors.NotFound("Приглашение не найдено.");
        await LoadOwnedAsync(actor, invitation.AssignmentId, ct);
        invitation.Revoke(Now);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Замена кода (ТЗ §5.2): старый отзывается, новый выдаётся, связь сохраняется.</summary>
    public async Task<(Invitation NewInvitation, StudentProfile Student, string OneTimeCode)> ReissueInvitationAsync(
        TeacherActor actor, Guid invitationId, CancellationToken ct)
    {
        var old = await db.Invitations.AsNoTracking()
            .SingleOrDefaultAsync(i => i.Id == invitationId, ct)
            ?? throw TeacherFlowErrors.NotFound("Приглашение не найдено.");
        await LoadOwnedAsync(actor, old.AssignmentId, ct);
        var student = await db.StudentProfiles.FindAsync([old.StudentProfileId!.Value], ct)
            ?? throw TeacherFlowErrors.NotFound("Студент не найден.");

        var code = InvitationCode.Generate();
        var replacement = Invitation.Create(
            old.AssignmentId, old.StudentProfileId, hasher.ComputeHash(InvitationCode.Normalize(code)),
            old.ExpiresAtUtc, actor.TeacherId, Now);
        db.Invitations.Add(replacement);
        await db.SaveChangesAsync(ct);

        var oldTracked = await db.Invitations.SingleAsync(i => i.Id == invitationId, ct);
        oldTracked.ReplaceWith(replacement, Now);
        await db.SaveChangesAsync(ct);
        return (replacement, student, code);
    }

    public async Task<ExtraAttemptGrant> CreateExtraGrantAsync(
        TeacherActor actor, Guid assignmentId, ExtraGrantRequest request, CancellationToken ct)
    {
        var assignment = await LoadOwnedAsync(actor, assignmentId, ct);
        var grant = ExtraAttemptGrant.Create(assignmentId, request.StudentProfileId, request.Reason, actor.TeacherId, Now);
        db.ExtraAttemptGrants.Add(grant);
        await db.SaveChangesAsync(ct);
        return grant;
    }

    /// <summary>
    /// Удаление назначения (manual delete): запрещено при любых попытках — история хранится 5 лет (ТЗ §21).
    /// Неиспользованные приглашения и extra-гранты удаляются вместе с назначением.
    /// </summary>
    public async Task DeleteAssignmentAsync(TeacherActor actor, Guid assignmentId, CancellationToken ct)
    {
        var assignment = await LoadOwnedAsync(actor, assignmentId, ct);
        var attemptCount = await db.Attempts.AsNoTracking()
            .Where(a => a.AssignmentId == assignmentId).CountAsync(ct);
        if (attemptCount > 0)
            throw TeacherFlowErrors.Conflict("assignment_has_attempts",
                $"По назначению есть попытки ({attemptCount}) — история не удаляется (ТЗ §21).");
        var invitations = await db.Invitations.Where(i => i.AssignmentId == assignmentId).ToListAsync(ct);
        var grants = await db.ExtraAttemptGrants.Where(g => g.AssignmentId == assignmentId).ToListAsync(ct);
        db.Invitations.RemoveRange(invitations);
        db.ExtraAttemptGrants.RemoveRange(grants);
        db.Assignments.Remove(assignment);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Текущий код общей ссылки (без изменения состояния).</summary>
    public async Task<string?> GetCommonLinkCodeAsync(TeacherActor actor, Guid assignmentId, CancellationToken ct)
    {
        var assignment = await LoadOwnedAsync(actor, assignmentId, ct);
        return assignment.AllowCommonLink ? assignment.CommonLinkCode : null;
    }

    /// <summary>
    /// Включить общую ссылку (ТЗ §5.1). Возвращает код-URL-часть; строгой защиты повторного входа нет —
    /// об этом предупреждает UI. Отключение не удаляет историю.
    /// </summary>
    public async Task<string> EnableCommonLinkAsync(TeacherActor actor, Guid assignmentId, CancellationToken ct)
    {
        var assignment = await LoadOwnedAsync(actor, assignmentId, ct);
        var code = Domain.Access.InvitationCode.Normalize(InvitationCode.Generate());
        assignment.EnableCommonLink(code, Now);
        await db.SaveChangesAsync(ct);
        return code;
    }

    public async Task DisableCommonLinkAsync(TeacherActor actor, Guid assignmentId, CancellationToken ct)
    {
        var assignment = await LoadOwnedAsync(actor, assignmentId, ct);
        assignment.DisableCommonLink(Now);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Создать постоянный аккаунт студента (ТЗ §5.3): системный логин + одноразовый пароль,
    /// который показывается ровно один раз (как код приглашения, ADR-002).
    /// </summary>
    public async Task<(Domain.People.StudentAccount Account, string OneTimePassword)> CreateStudentAccountAsync(
        TeacherActor actor, Guid assignmentId, Guid studentProfileId, CancellationToken ct)
    {
        await LoadOwnedAsync(actor, assignmentId, ct);
        var student = await db.StudentProfiles.FindAsync([studentProfileId], ct)
            ?? throw TeacherFlowErrors.NotFound("Студент не найден.");

        var login = "stud_" + Domain.Access.InvitationCode.Generate()[..^5].Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        var oneTimePassword = Domain.Access.InvitationCode.Generate().Replace("-", "", StringComparison.Ordinal);
        var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<Domain.People.StudentAccount>();
        var account = Domain.People.StudentAccount.Create(
            student.Id, login, hasher.HashPassword(null!, oneTimePassword), actor.TeacherId, Now);
        db.StudentAccounts.Add(account);
        await db.SaveChangesAsync(ct);
        return (account, oneTimePassword);
    }

    /// <summary>Безопасный сброс пароля преподавателем (ТЗ §4): новый одноразовый пароль.</summary>
    public async Task<string> ResetStudentAccountPasswordAsync(
        TeacherActor actor, Guid assignmentId, Guid studentAccountId, CancellationToken ct)
    {
        await LoadOwnedAsync(actor, assignmentId, ct);
        var account = await db.StudentAccounts.FindAsync([studentAccountId], ct)
            ?? throw TeacherFlowErrors.NotFound("Аккаунт не найден.");
        var oneTimePassword = Domain.Access.InvitationCode.Generate().Replace("-", "", StringComparison.Ordinal);
        var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<Domain.People.StudentAccount>();
        account.ChangePassword(hasher.HashPassword(null!, oneTimePassword), Now);
        await db.SaveChangesAsync(ct);
        return oneTimePassword;
    }

    // ---------- Вспомогательное ----------

    private async Task<Assignment> LoadOwnedAsync(TeacherActor actor, Guid assignmentId, CancellationToken ct)
    {
        var assignment = await db.Assignments.SingleOrDefaultAsync(a => a.Id == assignmentId, ct);
        if (assignment is null || (!actor.IsAdministrator && assignment.CreatedByTeacherId != actor.TeacherId))
            throw TeacherFlowErrors.NotFound("Назначение не найдено."); // не раскрываем чужое (ТЗ §3.3)
        return assignment;
    }

    private static InvitationDto ToDto(Invitation invitation, StudentProfile student, string? oneTimeCode) => new(
        invitation.Id, student.Id,
        student.MiddleName is null ? $"{student.LastName} {student.FirstName}" : $"{student.LastName} {student.FirstName} {student.MiddleName}",
        null, invitation.CreatedAtUtc, invitation.Status.ToString(), invitation.ReplacedByInvitationId, oneTimeCode);

    private static T ParseEnum<T>(string value, string name) where T : struct, Enum
        => Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw TeacherFlowErrors.Conflict("bad_request", $"{name}: недопустимое значение «{value}».");
}

public static class TeacherFlowErrors
{
    public static StudentFlowException NotFound(string message) => new(404, "not_found", message);

    public static StudentFlowException Conflict(string code, string message) => new(409, code, message);
}
