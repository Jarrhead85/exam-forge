using ExamForge.Application.StudentFlow;
using ExamForge.Application.TeacherFlow;
using ExamForge.Domain.People;
using ExamForge.Infrastructure.Identity;
using ExamForge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ExamForge.Infrastructure.Services;

public sealed record GroupDto(Guid GroupId, string Name, int StudentCount, DateTimeOffset CreatedAtUtc);
public sealed record GroupStudentDto(
    Guid StudentProfileId, string Name, string LastName, string FirstName, string? MiddleName, DateTimeOffset JoinedAtUtc);
public sealed record TeacherListItemDto(Guid Id, string Login, string DisplayName, bool IsActive);

/// <summary>Группы (ТЗ §4): создание, состав, членство; доступ преподавателей — TeacherGroupGrant.</summary>
public sealed class GroupService(ExamForgeDbContext db, TimeProvider timeProvider)
{
    private DateTimeOffset Now => timeProvider.GetUtcNow();

    public async Task<Group> CreateAsync(TeacherActor actor, string name, CancellationToken ct)
    {
        var group = Group.Create(name, Now);
        db.Groups.Add(group);
        db.TeacherGroupGrants.Add(TeacherGroupGrant.Create(actor.TeacherId, group.Id, Now));
        await db.SaveChangesAsync(ct);
        return group;
    }

    public async Task<IReadOnlyList<GroupDto>> ListAsync(TeacherActor actor, CancellationToken ct)
    {
        var groupIds = await db.TeacherGroupGrants.AsNoTracking()
            .Where(g => g.TeacherId == actor.TeacherId)
            .Select(g => g.GroupId)
            .ToListAsync(ct);
        var groups = await db.Groups.AsNoTracking()
            .Where(g => actor.IsAdministrator ? true : groupIds.Contains(g.Id))
            .OrderBy(g => g.Name)
            .ToListAsync(ct);
        var ids = groups.Select(g => g.Id).ToList();
        var counts = await db.GroupMemberships.AsNoTracking()
            .Where(m => ids.Contains(m.GroupId))
            .GroupBy(m => m.GroupId)
            .Select(g => new { GroupId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.GroupId, x => x.Count, ct);
        return [.. groups.Select(g => new GroupDto(g.Id, g.Name, counts.GetValueOrDefault(g.Id), g.CreatedAtUtc))];
    }

    public async Task<IReadOnlyList<GroupStudentDto>> MembersAsync(TeacherActor actor, Guid groupId, CancellationToken ct)
    {
        await LoadOwnedAsync(actor, groupId, ct);
        var rows = await db.GroupMemberships.AsNoTracking()
            .Where(m => m.GroupId == groupId)
            .Join(db.StudentProfiles.AsNoTracking(),
                m => m.StudentProfileId, p => p.Id,
                (m, p) => new { m.JoinedAtUtc, p.Id, p.LastName, p.FirstName, p.MiddleName })
            .ToListAsync(ct);
        return [.. rows
            .Select(r => new GroupStudentDto(r.Id,
                r.MiddleName != null ? $"{r.LastName} {r.FirstName} {r.MiddleName}" : $"{r.LastName} {r.FirstName}",
                r.LastName, r.FirstName, r.MiddleName, r.JoinedAtUtc))
            .OrderBy(s => s.Name)];
    }

    public async Task AddStudentAsync(TeacherActor actor, Guid groupId, Guid studentProfileId, CancellationToken ct)
    {
        await LoadOwnedAsync(actor, groupId, ct);
        var exists = await db.GroupMemberships.AnyAsync(m => m.GroupId == groupId && m.StudentProfileId == studentProfileId, ct);
        if (exists)
            throw TeacherFlowErrors.Conflict("already_member", "Студент уже в группе.");
        db.GroupMemberships.Add(GroupMembership.Create(groupId, studentProfileId, Now));
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveStudentAsync(TeacherActor actor, Guid groupId, Guid studentProfileId, CancellationToken ct)
    {
        await LoadOwnedAsync(actor, groupId, ct);
        var membership = await db.GroupMemberships
            .SingleOrDefaultAsync(m => m.GroupId == groupId && m.StudentProfileId == studentProfileId, ct)
            ?? throw TeacherFlowErrors.NotFound("Членство не найдено.");
        db.GroupMemberships.Remove(membership);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Создать профиль студента прямо из группы (ТЗ §4: список ведёт преподаватель).</summary>
    public async Task<StudentProfile> AddNewStudentAsync(
        TeacherActor actor, Guid groupId, string lastName, string firstName, string? middleName, CancellationToken ct)
    {
        await LoadOwnedAsync(actor, groupId, ct);
        var student = StudentProfile.Create(lastName, firstName, middleName, Now, actor.TeacherId);
        db.StudentProfiles.Add(student);
        db.GroupMemberships.Add(GroupMembership.Create(groupId, student.Id, Now));
        await db.SaveChangesAsync(ct);
        return student;
    }

    public async Task<IReadOnlyList<(Guid StudentProfileId, string Name)>> StudentsForTeacherAsync(TeacherActor actor, CancellationToken ct)
    {
        var groupIds = await db.TeacherGroupGrants.AsNoTracking()
            .Where(g => g.TeacherId == actor.TeacherId)
            .Select(g => g.GroupId)
            .ToListAsync(ct);
        var profiles = await db.GroupMemberships.AsNoTracking()
            .Where(m => groupIds.Contains(m.GroupId))
            .Join(db.StudentProfiles.AsNoTracking(), m => m.StudentProfileId, p => p.Id, (m, p) => p)
            .Distinct()
            .OrderBy(p => p.LastName).ThenBy(p => p.FirstName)
            .ToListAsync(ct);
        return [.. profiles.Select(p => (p.Id, p.MiddleName is null ? $"{p.LastName} {p.FirstName}" : $"{p.LastName} {p.FirstName} {p.MiddleName}"))];
    }

    public async Task RenameAsync(TeacherActor actor, Guid groupId, string name, CancellationToken ct)
    {
        var group = await LoadOwnedAsync(actor, groupId, ct);
        group.Rename(name);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Удаление группы (manual delete): только пустую — состав удаляется отдельно и осознанно.
    /// Grants доступа к материалам и права преподавателей группы удаляются вместе с ней.
    /// </summary>
    public async Task DeleteAsync(TeacherActor actor, Guid groupId, CancellationToken ct)
    {
        await LoadOwnedAsync(actor, groupId, ct);
        var memberCount = await db.GroupMemberships.CountAsync(m => m.GroupId == groupId, ct);
        if (memberCount > 0)
            throw TeacherFlowErrors.Conflict("group_not_empty",
                $"В группе {memberCount} студент(ов). Сначала уберите их из состава.");
        var grants = await db.TeacherGroupGrants.Where(g => g.GroupId == groupId).ToListAsync(ct);
        var materialGrants = await db.MaterialGrants.Where(g => g.GroupId == groupId).ToListAsync(ct);
        db.TeacherGroupGrants.RemoveRange(grants);
        db.MaterialGrants.RemoveRange(materialGrants);
        db.Groups.Remove(await db.Groups.SingleAsync(g => g.Id == groupId, ct));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Исправление ФИО профиля (ТЗ §4: снимки попыток не переписываются).</summary>
    public async Task RenameStudentAsync(
        TeacherActor actor, Guid studentProfileId, string lastName, string firstName, string? middleName, CancellationToken ct)
    {
        await LoadOwnedStudentAsync(actor, studentProfileId, ct);
        var profile = await db.StudentProfiles.SingleAsync(p => p.Id == studentProfileId, ct);
        profile.Rename(lastName, firstName, middleName);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Удаление профиля — только если нет истории: попыток, приглашений, extra-грантов,
    /// постоянных аккаунтов и выданных материалов (5 лет хранения результатов, ТЗ §21).
    /// Профиль без истории — ошибка ввода, удаляется вместе с членствами.
    /// </summary>
    public async Task DeleteStudentAsync(TeacherActor actor, Guid studentProfileId, CancellationToken ct)
    {
        await LoadOwnedStudentAsync(actor, studentProfileId, ct);
        var hasAttempts = await db.Attempts.AnyAsync(a => a.StudentProfileId == studentProfileId, ct);
        if (hasAttempts)
            throw TeacherFlowErrors.Conflict("student_has_attempts", "У студента есть попытки — история не удаляется (ТЗ §21).");
        var hasInvites = await db.Invitations.AnyAsync(i => i.StudentProfileId == studentProfileId, ct);
        if (hasInvites)
            throw TeacherFlowErrors.Conflict("student_has_invites", "У студента есть приглашения — сначала удалите их в назначениях.");
        var hasAccount = await db.StudentAccounts.AnyAsync(a => a.StudentProfileId == studentProfileId, ct);
        if (hasAccount)
            throw TeacherFlowErrors.Conflict("student_has_account", "У студента есть постоянный аккаунт — сначала удалите его.");
        var hasMaterialGrants = await db.MaterialGrants.AnyAsync(g => g.StudentProfileId == studentProfileId, ct);
        if (hasMaterialGrants)
            throw TeacherFlowErrors.Conflict("student_has_materials", "Студенту выданы материалы — сначала отзовите доступ.");

        var memberships = await db.GroupMemberships.Where(m => m.StudentProfileId == studentProfileId).ToListAsync(ct);
        db.GroupMemberships.RemoveRange(memberships);
        db.StudentProfiles.Remove(await db.StudentProfiles.SingleAsync(p => p.Id == studentProfileId, ct));
        await db.SaveChangesAsync(ct);
    }

    public async Task<Domain.People.StudentAccount?> AccountOfStudentAsync(TeacherActor actor, Guid studentProfileId, CancellationToken ct)
    {
        await LoadOwnedStudentAsync(actor, studentProfileId, ct);
        return await db.StudentAccounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.StudentProfileId == studentProfileId, ct);
    }

    /// <summary>Сброс пароля постоянного аккаунта (ТЗ §4): новый одноразовый пароль, показывается один раз.</summary>
    public async Task<string> ResetStudentAccountPasswordAsync(TeacherActor actor, Guid studentAccountId, CancellationToken ct)
    {
        var account = await LoadOwnedAccountAsync(actor, studentAccountId, ct);
        var oneTimePassword = Domain.Access.InvitationCode.Generate().Replace("-", "", StringComparison.Ordinal);
        var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<Domain.People.StudentAccount>();
        account.ChangePassword(hasher.HashPassword(null!, oneTimePassword), Now);
        await db.SaveChangesAsync(ct);
        return oneTimePassword;
    }

    public async Task SetStudentAccountActiveAsync(TeacherActor actor, Guid studentAccountId, bool active, CancellationToken ct)
    {
        var account = await LoadOwnedAccountAsync(actor, studentAccountId, ct);
        if (active) account.Enable(Now);
        else account.Disable(Now);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Удаление постоянного аккаунта: вход по логину/паролю пропадает, история попыток не затрагивается.</summary>
    public async Task DeleteStudentAccountAsync(TeacherActor actor, Guid studentAccountId, CancellationToken ct)
    {
        var account = await LoadOwnedAccountAsync(actor, studentAccountId, ct);
        db.StudentAccounts.Remove(account);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Доступ к профилю: преподаватель с правом на любую группу профиля или администратор.</summary>
    private async Task LoadOwnedStudentAsync(TeacherActor actor, Guid studentProfileId, CancellationToken ct)
    {
        if (actor.IsAdministrator)
            return;
        var granted = await (from m in db.GroupMemberships.AsNoTracking()
                             join g in db.TeacherGroupGrants.AsNoTracking() on m.GroupId equals g.GroupId
                             where m.StudentProfileId == studentProfileId && g.TeacherId == actor.TeacherId
                             select m.GroupId).AnyAsync(ct);
        if (!granted)
            throw TeacherFlowErrors.NotFound("Студент не найден.");
    }

    private async Task<Domain.People.StudentAccount> LoadOwnedAccountAsync(TeacherActor actor, Guid studentAccountId, CancellationToken ct)
    {
        var account = await db.StudentAccounts.SingleOrDefaultAsync(a => a.Id == studentAccountId, ct)
            ?? throw TeacherFlowErrors.NotFound("Аккаунт не найден.");
        await LoadOwnedStudentAsync(actor, account.StudentProfileId, ct);
        return account;
    }

    private async Task<Group> LoadOwnedAsync(TeacherActor actor, Guid groupId, CancellationToken ct)
    {
        var group = await db.Groups.SingleOrDefaultAsync(g => g.Id == groupId, ct)
            ?? throw TeacherFlowErrors.NotFound("Группа не найдена.");
        if (!actor.IsAdministrator)
        {
            var granted = await db.TeacherGroupGrants.AnyAsync(g => g.TeacherId == actor.TeacherId && g.GroupId == groupId, ct);
            if (!granted)
                throw TeacherFlowErrors.NotFound("Группа не найдена.");
        }
        return group;
    }
}

/// <summary>Административные действия (ТЗ §3.1): управление учётными записями преподавателей. Только администратор.</summary>
public sealed class AdminTeachersService(
    ExamForgeDbContext db,
    UserManager<TeacherUser> userManager,
    RoleManager<IdentityRole<Guid>> roleManager)
{
    public async Task<Guid> CreateTeacherAsync(TeacherActor actor, string login, string password, string displayName, CancellationToken ct)
    {
        if (!actor.IsAdministrator)
            throw TeacherFlowErrors.NotFound("Недостаточно прав.");
        var existing = await userManager.FindByNameAsync(login);
        if (existing is not null)
            throw TeacherFlowErrors.Conflict("login_taken", "Логин уже занят.");
        var teacher = new TeacherUser { UserName = login, DisplayName = displayName };
        var result = await userManager.CreateAsync(teacher, password);
        if (!result.Succeeded)
            throw TeacherFlowErrors.Conflict("weak_password", string.Join("; ", result.Errors.Select(e => e.Description)));
        if (!await roleManager.RoleExistsAsync(AppRoles.Teacher))
            await roleManager.CreateAsync(new IdentityRole<Guid>(AppRoles.Teacher));
        await userManager.AddToRoleAsync(teacher, AppRoles.Teacher);
        return teacher.Id;
    }

    public async Task<IReadOnlyList<TeacherListItemDto>> ListTeachersAsync(TeacherActor actor, CancellationToken ct)
    {
        if (!actor.IsAdministrator)
            throw TeacherFlowErrors.NotFound("Недостаточно прав.");
        var users = await db.Users.AsNoTracking()
            .OrderBy(u => u.UserName)
            .Select(u => new { u.Id, u.UserName, u.DisplayName, u.LockoutEnd })
            .ToListAsync(ct);
        return [.. users.Select(u => new TeacherListItemDto(
            u.Id, u.UserName!, u.DisplayName, u.LockoutEnd is null || u.LockoutEnd <= DateTimeOffset.UtcNow))];
    }

    /// <summary>Активность учётной записи: деактивация через lockout до 2099 года (вход запрещён,
    /// authored-контент и история сохраняются; hard delete по FK невозможен без потери авторства).</summary>
    private static bool IsActive(TeacherUser u)
        => u.LockoutEnd is null || u.LockoutEnd <= DateTimeOffset.UtcNow;

    public async Task RenameTeacherAsync(TeacherActor actor, Guid teacherId, string displayName, CancellationToken ct)
    {
        if (!actor.IsAdministrator)
            throw TeacherFlowErrors.NotFound("Недостаточно прав.");
        var teacher = await userManager.FindByIdAsync(teacherId.ToString())
            ?? throw TeacherFlowErrors.NotFound("Преподаватель не найден.");
        teacher.DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? throw TeacherFlowErrors.Conflict("bad_request", "ФИО не может быть пустым.")
            : displayName.Trim();
        await userManager.UpdateAsync(teacher);
    }

    public async Task SetTeacherActiveAsync(TeacherActor actor, Guid teacherId, bool active, CancellationToken ct)
    {
        if (!actor.IsAdministrator)
            throw TeacherFlowErrors.NotFound("Недостаточно прав.");
        if (teacherId == actor.TeacherId && !active)
            throw TeacherFlowErrors.Conflict("self_deactivate", "Нельзя деактивировать собственную учётную запись.");
        var teacher = await userManager.FindByIdAsync(teacherId.ToString())
            ?? throw TeacherFlowErrors.NotFound("Преподаватель не найден.");
        if (!active && await userManager.IsInRoleAsync(teacher, AppRoles.Administrator))
        {
            // Последнего активного администратора деактивировать нельзя — иначе портал без управления.
            var adminIds = await (from ur in db.UserRoles
                                  join r in db.Roles on ur.RoleId equals r.Id
                                  where r.Name == AppRoles.Administrator
                                  select ur.UserId).ToListAsync(ct);
            var admins = db.Users.Where(u => adminIds.Contains(u.Id)).AsEnumerable();
            var activeAdmins = admins.Where(IsActive).Select(u => u.Id).ToList();
            if (activeAdmins.Count <= 1 && activeAdmins.Contains(teacherId))
                throw TeacherFlowErrors.Conflict("last_admin", "Это последний активный администратор — деактивация запрещена.");
        }
        if (!active)
        {
            teacher.LockoutEnabled = true;
            teacher.LockoutEnd = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);
        }
        else
        {
            teacher.LockoutEnd = null;
        }
        await userManager.UpdateAsync(teacher);
    }

    /// <summary>Сброс пароля администратором: новый пароль задаётся явно и передаётся лично.</summary>
    public async Task ResetTeacherPasswordAsync(TeacherActor actor, Guid teacherId, string newPassword, CancellationToken ct)
    {
        if (!actor.IsAdministrator)
            throw TeacherFlowErrors.NotFound("Недостаточно прав.");
        var teacher = await userManager.FindByIdAsync(teacherId.ToString())
            ?? throw TeacherFlowErrors.NotFound("Преподаватель не найден.");
        // Проверка политики пароля + прямая установка хеша: reset-токены не настроены
        // (email в системе нет — ТЗ §4), поэтому классический ResetPasswordAsync неприменим.
        foreach (var validator in userManager.PasswordValidators)
        {
            var valid = await validator.ValidateAsync(userManager, teacher, newPassword);
            if (!valid.Succeeded)
                throw TeacherFlowErrors.Conflict("weak_password", string.Join("; ", valid.Errors.Select(e => e.Description)));
        }
        teacher.PasswordHash = userManager.PasswordHasher.HashPassword(teacher, newPassword);
        var result = await userManager.UpdateAsync(teacher);
        if (!result.Succeeded)
            throw TeacherFlowErrors.Conflict("bad_request", string.Join("; ", result.Errors.Select(e => e.Description)));
        // Деактивированный аккаунт остаётся деактивированным; сброс пароля не включает вход.
    }
}
