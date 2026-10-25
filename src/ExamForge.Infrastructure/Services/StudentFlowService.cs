using ExamForge.Application.StudentFlow;
using ExamForge.Domain.Access;
using ExamForge.Domain.Assignments;
using ExamForge.Domain.Attempts;
using ExamForge.Domain.Questions;
using ExamForge.Domain.Testing;
using ExamForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ExamForge.Infrastructure.Services;

/// <summary>
/// Студенческий поток (ТЗ §10, §17): start/open-next/save/lock/finish/result.
/// Сервер владеет временем (TimeProvider) и состоянием; все переходы — через доменные методы.
/// Идемпотентность start/finish, одна активная попытка, поздние ответы отклоняются (ADR-003).
/// </summary>
public sealed class StudentFlowService(ExamForgeDbContext db, TimeProvider timeProvider)
{
    private DateTimeOffset Now => timeProvider.GetUtcNow();

    // ---------- Обмен кода ----------

    public async Task<(Invitation Invitation, Assignment Assignment)> ValidateExchangeAsync(
        InvitationCodeHasher hasher, string rawCode, CancellationToken ct)
    {
        var normalized = Domain.Access.InvitationCode.Normalize(rawCode);
        if (!Domain.Access.InvitationCode.IsValidFormat(normalized))
            throw new StudentFlowException(400, "bad_code", "Код состоит из 12 символов (без 0/O/1/I). Проверьте ввод.");

        var invitation = await db.Invitations.FirstOrDefaultAsync(i => i.CodeHash == hasher.ComputeHash(normalized), ct);
        if (invitation is null)
            throw new StudentFlowException(401, "invalid_code", "Неверный код приглашения.");

        var now = Now;
        var reason = invitation.CheckExchangeable(now);
        if (reason is not null)
        {
            invitation.RegisterFailedAttempt(now);
            await db.SaveChangesAsync(ct);
            throw new StudentFlowException(401, "code_unavailable", reason);
        }

        var assignment = await db.Assignments.FirstOrDefaultAsync(a => a.Id == invitation.AssignmentId, ct);
        if (assignment is null || !assignment.IsOpenAt(now))
            throw new StudentFlowException(409, "assignment_closed", "Назначение сейчас недоступно.");

        invitation.RegisterSuccessfulExchange(now);
        await db.SaveChangesAsync(ct);
        return (invitation, assignment);
    }

    // ---------- Общая ссылка (ТЗ §5.1) ----------

    /// <summary>
    /// Вход по общей ссылке: ФИО+группа не сопоставляются с существующими профилями и не дают чужую
    /// историю (AC-18). Каждый вход создаёт новый неидентифицированный профиль — строгой защиты
    /// повторного входа нет, что явно допускается ТЗ §5.1.
    /// </summary>
    public async Task<(Domain.People.StudentProfile Profile, Assignment Assignment)> EnterByCommonLinkAsync(
        string linkCode, string lastName, string firstName, string? middleName, string? group, CancellationToken ct)
    {
        var code = Domain.Access.InvitationCode.Normalize(linkCode ?? string.Empty);
        var assignment = await db.Assignments.AsNoTracking()
            .SingleOrDefaultAsync(a => a.CommonLinkCode == code && a.AllowCommonLink, ct);
        if (assignment is null)
            throw new StudentFlowException(401, "invalid_link", "Ссылка недействительна или отключена.");
        if (!assignment.IsOpenAt(Now))
            throw new StudentFlowException(409, "assignment_closed", "Назначение сейчас недоступно.");

        var profile = Domain.People.StudentProfile.Create(lastName, firstName, middleName, Now, null, identified: false);
        db.StudentProfiles.Add(profile);
        await db.SaveChangesAsync(ct);
        return (profile, assignment);
    }

    // ---------- Постоянный аккаунт (ТЗ §5.3) ----------

    public async Task<Domain.People.StudentAccount?> ValidateStudentLoginAsync(
        string login, string password, CancellationToken ct)
    {
        var account = await db.StudentAccounts
            .SingleOrDefaultAsync(a => a.Login == login, ct);
        if (account is null || account.IsDisabled)
            return null;
        var now = Now;
        if (account.LockedUntilUtc is { } lockedUntil && now < lockedUntil)
            return null;

        var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<Domain.People.StudentAccount>();
        if (hasher.VerifyHashedPassword(account, account.PasswordHash, password)
            == Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed)
        {
            account.RegisterFailedAttempt(now);
            await db.SaveChangesAsync(ct);
            return null;
        }
        account.RegisterSuccessfulLogin(now);
        await db.SaveChangesAsync(ct);
        return account;
    }

    // ---------- Попытки ----------

    public async Task<AttemptStateDto> StartAttemptAsync(Guid assignmentId, StudentIdentity who, CancellationToken ct)
    {
        var (assignment, testVersion) = await LoadAssignmentWithVersionAsync(assignmentId, who, ct);
        var now = Now;
        if (!assignment.IsOpenAt(now))
            throw StudentFlowException.Conflict("assignment_closed", "Назначение сейчас недоступно.");

        var profileId = who.StudentProfileId
            ?? throw StudentFlowException.Conflict("identity_required", "Для старта требуется индивидуальный код приглашения.");

        var existingActive = await db.Attempts.FirstOrDefaultAsync(
            a => a.AssignmentId == assignmentId && a.StudentProfileId == profileId
                 && (a.State == AttemptState.Created || a.State == AttemptState.InProgress),
            ct);
        if (existingActive is not null)
        {
            // Идемпотентность (AC-07): повторный start возвращает существующую попытку.
            if (existingActive.State == AttemptState.Created)
            {
                existingActive.Start(now, Seconds(assignment.AttemptTotalTimeLimitSeconds));
                await db.SaveChangesAsync(ct);
            }
            await EnsureSessionsAsync(existingActive, testVersion,
                await PickVariantAsync(testVersion.Id, existingActive.AttemptNumber, ct), ct);
            return await BuildStateAsync(existingActive, ct);
        }

        var usedCount = await db.Attempts.CountAsync(
            a => a.AssignmentId == assignmentId && a.StudentProfileId == profileId && a.State != AttemptState.Cancelled, ct);
        if (usedCount >= assignment.MaxAttempts)
        {
            var grant = await db.ExtraAttemptGrants.FirstOrDefaultAsync(
                g => g.AssignmentId == assignmentId && g.StudentProfileId == profileId && g.UsedByAttemptId == null, ct);
            if (grant is null)
                throw StudentFlowException.Conflict("attempts_exhausted",
                    "Лимит попыток исчерпан. Дополнительную попытку выдаёт преподаватель.");

            var attempt = await CreateAttemptAsync(assignment, testVersion, profileId, usedCount + 1, grant.Id, who.GroupName, ct);
            grant.MarkUsed(attempt.Id);
            await db.SaveChangesAsync(ct);
            return await BuildStateAsync(attempt, ct);
        }

        var created = await CreateAttemptAsync(assignment, testVersion, profileId, usedCount + 1, null, who.GroupName, ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Гонка двух start: уникальный индекс активной попытки — возвращаем существующую.
            db.Entry(created).State = EntityState.Detached;
            var winner = await db.Attempts.AsNoTracking().FirstAsync(
                a => a.AssignmentId == assignmentId && a.StudentProfileId == profileId
                     && (a.State == AttemptState.Created || a.State == AttemptState.InProgress), ct);
            var tracked = await db.Attempts.FirstAsync(a => a.Id == winner.Id, ct);
            return await BuildStateAsync(tracked, ct);
        }
        return await BuildStateAsync(created, ct);
    }

    private async Task<Attempt> CreateAttemptAsync(
        Assignment assignment, TestVersion testVersion, Guid profileId, int number, Guid? grantId,
        string? groupSnapshot, CancellationToken ct)
    {
        var profile = await db.StudentProfiles.AsNoTracking().SingleAsync(p => p.Id == profileId, ct);
        // Варианты назначаются циклически по номеру попытки (предложение; D-08 требует подтверждения).
        var variant = await PickVariantAsync(testVersion.Id, number, ct);
        var attempt = Attempt.Create(
            assignment.Id, number, Now, profileId, NameOf(profile), groupSnapshot, grantId, variant?.Id);
        attempt.Start(Now, Seconds(assignment.AttemptTotalTimeLimitSeconds));
        db.Attempts.Add(attempt);
        await db.SaveChangesAsync(ct);
        await EnsureSessionsAsync(attempt, testVersion, variant, ct);
        return attempt;
    }

    private static string NameOf(Domain.People.StudentProfile p)
        => p.MiddleName is null ? $"{p.LastName} {p.FirstName}" : $"{p.LastName} {p.FirstName} {p.MiddleName}";

    private async Task<Domain.Testing.Variant?> PickVariantAsync(Guid testVersionId, int attemptNumber, CancellationToken ct)
        => await db.Variants.AsNoTracking()
            .Where(v => v.TestVersionId == testVersionId)
            .OrderBy(v => v.VariantNumber)
            .ToListAsync(ct) is { Count: > 0 } variants
                ? variants[(attemptNumber - 1) % variants.Count]
                : null;

    private async Task EnsureSessionsAsync(Attempt attempt, TestVersion testVersion, Domain.Testing.Variant? variant, CancellationToken ct)
    {
        var existing = await db.QuestionSessions.CountAsync(s => s.AttemptId == attempt.Id, ct);
        if (existing > 0)
            return;
        // Порядок вопросов попытки — из зафиксированного варианта (ТЗ §8); без вариантов — порядок версии.
        var questionOrder = variant?.QuestionOrder ?? testVersion.QuestionVersionIds;
        var order = 0;
        foreach (var questionVersionId in questionOrder)
            db.QuestionSessions.Add(QuestionSession.Create(attempt.Id, questionVersionId, order++));
        await db.SaveChangesAsync(ct);
    }

    public async Task<AttemptStateDto> OpenNextAsync(Guid attemptId, StudentIdentity who, CancellationToken ct)
    {
        var attempt = await LoadOwnedAttemptAsync(attemptId, who, ct);
        var now = Now;
        if (attempt.State != AttemptState.InProgress)
            throw StudentFlowException.Conflict("not_in_progress", "Попытка не активна.");

        var sessions = await LoadSessionsAsync(attempt.Id, ct);
        var active = sessions.FirstOrDefault(s => s.State == QuestionSessionState.Active);
        if (active is not null)
        {
            active.ExpireIfDue(now);
            if (active.State == QuestionSessionState.Active)
            {
                await db.SaveChangesAsync(ct); // возможен переход в TimedOut выше
                return await BuildStateAsync(attempt, ct);
            }
            await db.SaveChangesAsync(ct);
        }

        // Скрытая вкладка не запускает таймеры будущих вопросов: открываем ровно один следующий (ТЗ §10.2).
        var next = sessions.Where(s => s.State == QuestionSessionState.NotOpened).OrderBy(s => s.OrderIndex).FirstOrDefault();
        if (next is null)
            throw StudentFlowException.Conflict("no_next_question", "Доступных вопросов нет: завершите попытку.");

        var (assignment, testVersion) = await LoadAssignmentWithVersionAsync(attempt.AssignmentId, who, ct);
        var questionVersion = await db.QuestionVersions.AsNoTracking()
            .SingleAsync(v => v.Id == next.QuestionVersionId, ct);

        var perQuestion = assignment.PerQuestionTimeLimitSeconds ?? questionVersion.ProposedTimeLimitSeconds;
        next.Open(now, Seconds(perQuestion), attempt.DeadlineUtc, assignment.ClosesAtUtc);
        await db.SaveChangesAsync(ct);
        return await BuildStateAsync(attempt, ct);
    }

    public async Task<SaveAnswerDto> SaveAnswerAsync(
        Guid attemptId, Guid questionSessionId, string answerJson, StudentIdentity who, CancellationToken ct)
    {
        var attempt = await LoadOwnedAttemptAsync(attemptId, who, ct);
        var now = Now;
        var session = await db.QuestionSessions
            .SingleOrDefaultAsync(s => s.Id == questionSessionId && s.AttemptId == attempt.Id, ct)
            ?? throw StudentFlowException.NotFound("Вопрос не найден.");

        var decision = session.SaveAnswer(now);
        if (decision != AnswerDecision.Accepted)
        {
            await db.SaveChangesAsync(ct); // фиксируем ленивое истечение срока
            var reason = decision == AnswerDecision.RejectedLate
                ? "Время истекло. Ответ не принят; ранее сохранённый вариант сохранён."   // ТЗ §10.1
                : "Вопрос закрыт; изменить ответ нельзя (возврат запрещён).";              // AC-06
            throw new StudentFlowException(409, decision == AnswerDecision.RejectedLate ? "late" : "not_active", reason)
            {
                // детали состояния включаются в ProblemDetails расширением
            };
        }

        db.AnswerRevisions.Add(AnswerRevision.Create(session.Id, attempt.Id, session.AnswerRevision, answerJson, now));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Гонка ревизий (два устройства): последним признаётся зафиксированный сервером порядок.
            throw StudentFlowException.Conflict("revision_conflict", "Конфликт сохранения: обновите состояние и повторите.");
        }

        return new SaveAnswerDto(true, session.AnswerRevision, now, session.DeadlineUtc, StateName(session.State), null);
    }

    public async Task<LockCurrentDto> LockCurrentAsync(Guid attemptId, StudentIdentity who, CancellationToken ct)
    {
        var attempt = await LoadOwnedAttemptAsync(attemptId, who, ct);
        var now = Now;
        var sessions = await LoadSessionsAsync(attempt.Id, ct);

        var current = sessions
            .Where(s => s.State == QuestionSessionState.Active || s.StartedAtUtc != null)
            .OrderByDescending(s => s.OrderIndex)
            .FirstOrDefault(s => s.State == QuestionSessionState.Active)
            ?? throw StudentFlowException.Conflict("no_active_question", "Активного вопроса нет: откройте следующий.");

        var outcome = current.Lock(now);
        await db.SaveChangesAsync(ct);
        return new LockCurrentDto(outcome.ToString(), current.OrderIndex, StateName(current.State));
    }

    public async Task<FinishAttemptDto> FinishAsync(Guid attemptId, StudentIdentity who, CancellationToken ct)
    {
        var attempt = await LoadOwnedAttemptAsync(attemptId, who, ct);
        var now = Now;

        if (attempt.State is AttemptState.Submitted or AttemptState.PendingReview or AttemptState.Finalized)
            return ToFinishDto(attempt); // идемпотентность (AC-07)

        if (attempt.State != AttemptState.InProgress)
            throw StudentFlowException.Conflict("not_in_progress", "Попытку нельзя завершить в текущем состоянии.");

        var sessions = await LoadSessionsAsync(attempt.Id, ct);
        foreach (var session in sessions)
        {
            session.ExpireIfDue(now);
            if (session.State == QuestionSessionState.Active)
                session.Lock(now);
        }

        var versions = await db.QuestionVersions.AsNoTracking()
            .Where(v => sessions.Select(s => s.QuestionVersionId).Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, ct);
        var lastRevisions = await db.AnswerRevisions
            .Where(r => r.AttemptId == attempt.Id)
            .GroupBy(r => r.QuestionSessionId)
            .Select(g => g.OrderByDescending(r => r.Revision).First())
            .ToDictionaryAsync(r => r.QuestionSessionId, ct);

        decimal earned = 0m, max = 0m;
        var needsReview = false;
        var alreadyGraded = await db.QuestionGrades.AnyAsync(g => g.AttemptId == attempt.Id, ct);
        foreach (var session in sessions)
        {
            if (!versions.TryGetValue(session.QuestionVersionId, out var version))
                throw new StudentFlowException(500, "version_missing", "Версия вопроса не найдена; обратитесь к преподавателю.");
            var definition = QuestionDefinitionJson.Deserialize(version.DefinitionJson);
            var answer = lastRevisions.TryGetValue(session.Id, out var revision)
                ? QuestionDefinitionJson.DeserializeAnswer(revision.AnswerJson)
                : null;
            var outcome = AnswerGrader.Grade(definition, answer);
            earned += outcome.EarnedPoints;
            max += outcome.MaxPoints;
            needsReview |= outcome.NeedsManualReview;

            // Критерии фиксируются по-вопросно (ТЗ §11): авто — подтверждённые баллы;
            // ручной вопрос — PendingManual, «не проверено ≠ 0 и ≠ финал».
            if (!alreadyGraded)
            {
                var rubricLabels = definition.BuildRubric().ToDictionary(c => c.CriterionId, c => c.Label);
                foreach (var criterion in outcome.Criteria)
                {
                    var manual = outcome.NeedsManualReview;
                    var label = rubricLabels.TryGetValue(criterion.CriterionId, out var rubricLabel) ? rubricLabel : criterion.CriterionId;
                    db.QuestionGrades.Add(Domain.Grading.QuestionGrade.Create(
                        attempt.Id, session.Id, session.QuestionVersionId,
                        criterion.CriterionId, label, criterion.MaxPoints,
                        manual ? 0m : criterion.EarnedPoints,
                        manual ? "Ожидает ручной проверки" : criterion.Reason,
                        manual ? Domain.Grading.VerificationState.PendingManual : Domain.Grading.VerificationState.AutoGraded,
                        now));
                }
            }
        }

        attempt.Submit(now, hasPendingManualReview: needsReview);
        if (!needsReview)
            attempt.Finalize(earned, max, now);
        await db.SaveChangesAsync(ct);
        return ToFinishDto(attempt);
    }

    public async Task<StudentResultDto> GetResultAsync(Guid attemptId, StudentIdentity who, CancellationToken ct)
    {
        var attempt = await LoadOwnedAttemptAsync(attemptId, who, ct);
        if (attempt.State is AttemptState.Created or AttemptState.InProgress)
            throw StudentFlowException.Conflict("not_finished", "Попытка ещё не завершена.");

        var score = attempt.Score;
        return new StudentResultDto(
            attempt.State.ToString(),
            Provisional: attempt.State != AttemptState.Finalized,
            EarnedPoints: score?.EarnedPoints,
            MaxPoints: score?.MaxPoints ?? 0m,
            RoundedPercent: score?.RoundedPercent,
            CompletedAtUtc: attempt.CompletedAtUtc,
            attempt.AttemptNumber);
    }

    public async Task<AttemptStateDto> GetStateAsync(Guid attemptId, StudentIdentity who, CancellationToken ct)
    {
        var attempt = await LoadOwnedAttemptAsync(attemptId, who, ct);
        var now = Now;
        var active = await db.QuestionSessions.FirstOrDefaultAsync(
            s => s.AttemptId == attempt.Id && s.State == QuestionSessionState.Active, ct);
        if (active is not null)
        {
            active.ExpireIfDue(now);
            if (active.State != QuestionSessionState.Active)
                await db.SaveChangesAsync(ct);
        }
        return await BuildStateAsync(attempt, ct);
    }

    // ---------- Вспомогательное ----------

    private async Task<(Assignment Assignment, TestVersion TestVersion)> LoadAssignmentWithVersionAsync(
        Guid assignmentId, StudentIdentity who, CancellationToken ct)
    {
        if (who.AssignmentId is { } limited)
        {
            // Сессия кода/ссылки ограничена своим назначением (ТЗ §5.2).
            if (limited != assignmentId)
                throw StudentFlowException.NotFound();
        }
        else if (who.StudentProfileId is { } profileId)
        {
            // Сессия аккаунта: назначение доступно, если профиль приглашён (ТЗ §5.3).
            var invited = await db.Invitations.AnyAsync(
                i => i.AssignmentId == assignmentId && i.StudentProfileId == profileId
                     && i.Status == Domain.Assignments.InvitationStatus.Active, ct);
            if (!invited)
                throw StudentFlowException.NotFound();
        }
        else
        {
            throw StudentFlowException.NotFound();
        }

        var assignment = await db.Assignments.AsNoTracking()
            .SingleAsync(a => a.Id == assignmentId, ct);
        var testVersion = await db.TestVersions.AsNoTracking()
            .SingleAsync(v => v.Id == assignment.TestVersionId, ct);
        return (assignment, testVersion);
    }

    private async Task<Attempt> LoadOwnedAttemptAsync(Guid attemptId, StudentIdentity who, CancellationToken ct)
    {
        // IDOR (ТЗ §3.3, AC-10): чужая попытка неотличима от несуществующей — 404.
        var attempt = await db.Attempts
            .SingleOrDefaultAsync(a => a.Id == attemptId
                && a.StudentProfileId == who.StudentProfileId
                && (who.AssignmentId == null || a.AssignmentId == who.AssignmentId), ct);
        return attempt ?? throw StudentFlowException.NotFound();
    }

    private async Task<List<QuestionSession>> LoadSessionsAsync(Guid attemptId, CancellationToken ct)
        => await db.QuestionSessions.Where(s => s.AttemptId == attemptId).OrderBy(s => s.OrderIndex).ToListAsync(ct);

    private async Task<AttemptStateDto> BuildStateAsync(Attempt attempt, CancellationToken ct)
    {
        var sessions = await LoadSessionsAsync(attempt.Id, ct);
        StudentQuestionDto? current = null;
        var active = sessions.FirstOrDefault(s => s.State == QuestionSessionState.Active);
        if (active is not null)
        {
            var variant = attempt.VariantId is null
                ? null
                : await db.Variants.AsNoTracking().SingleOrDefaultAsync(v => v.Id == attempt.VariantId, ct);
            current = await ProjectQuestionAsync(active, variant, ct);
        }

        return new AttemptStateDto(
            attempt.Id,
            attempt.State.ToString(),
            attempt.AttemptNumber,
            attempt.StartedAtUtc,
            attempt.DeadlineUtc,
            sessions.Count,
            sessions.Select(s => new SessionBriefDto(s.OrderIndex, StateName(s.State), s.AnswerRevision, s.DeadlineUtc)).ToList(),
            current);
    }

    /// <summary>Проекция без ключей: CorrectOptionId/ключи/объяснения не сериализуются (AC-11).
    /// Порядок отображаемых списков — по варианту попытки (ТЗ §8).</summary>
    private async Task<StudentQuestionDto> ProjectQuestionAsync(QuestionSession session, Domain.Testing.Variant? variant, CancellationToken ct)
    {
        var version = await db.QuestionVersions.AsNoTracking().SingleAsync(v => v.Id == session.QuestionVersionId, ct);
        var definition = QuestionDefinitionJson.Deserialize(version.DefinitionJson);

        IReadOnlyList<StudentOptionDto>? options = null;
        IReadOnlyList<StudentTileDto>? tiles = null;
        IReadOnlyList<StudentOptionDto>? left = null, right = null, items = null, fields = null;
        IReadOnlyList<string>? blanks = null;

        switch (definition.Type)
        {
            case QuestionType.SingleChoice:
            case QuestionType.MultipleChoice:
                options = OrderOptions(variant, version.Id,
                    [.. definition.Options.Select(o => new StudentOptionDto(o.OptionId, o.Text))], o => o.Id);
                break;
            case QuestionType.Matching:
                left = [.. definition.MatchingPairs.Select(p => new StudentOptionDto(p.LeftId, p.LeftText))];
                right = OrderOptions(variant, version.Id,
                    [.. definition.MatchingPairs.Select(p => new StudentOptionDto(p.RightId, p.RightText))], o => o.Id);
                break;
            case QuestionType.LetterAssembly:
                tiles = OrderTiles(variant, version.Id,
                    [.. definition.LetterTiles.Select(t => new StudentTileDto(t.TileId, t.Letter))]);
                break;
            case QuestionType.Ordering:
                items = [.. definition.OrderingItems.Select(o => new StudentOptionDto(o.OptionId, o.Text))];
                break;
            case QuestionType.Structured:
                fields = [.. definition.StructuredFields.Select(f => new StudentOptionDto(f.FieldId, f.Label))];
                break;
            case QuestionType.GapFill:
                blanks = [.. definition.GapBlanks.Select(b => b.BlankId)];
                break;
        }

        return new StudentQuestionDto(
            session.Id, version.Id, session.OrderIndex, definition.Type.ToString(), definition.Prompt, definition.Instruction,
            version.ProposedTimeLimitSeconds, options, tiles, left, right, items, fields, blanks);
    }

    private static IReadOnlyList<StudentOptionDto> OrderOptions(
        Domain.Testing.Variant? variant, Guid questionVersionId,
        IReadOnlyList<StudentOptionDto> source, Func<StudentOptionDto, string> idOf)
        => variant is null ? source : Reordered(variant, questionVersionId, source, idOf);

    private static IReadOnlyList<StudentTileDto> OrderTiles(
        Domain.Testing.Variant? variant, Guid questionVersionId, IReadOnlyList<StudentTileDto> source)
        => variant is null ? source : Reordered(variant, questionVersionId, source, t => t.TileId);

    private static IReadOnlyList<T> Reordered<T>(
        Domain.Testing.Variant variant, Guid questionVersionId, IReadOnlyList<T> source, Func<T, string> idOf)
    {
        var byId = source.ToDictionary(idOf);
        var ordered = variant.OrderFor(questionVersionId, [.. byId.Keys]);
        return ordered.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    private static TimeSpan? Seconds(int? seconds) => seconds is { } s ? TimeSpan.FromSeconds(s) : null;

    private static string StateName(QuestionSessionState state) => state.ToString();

    private static FinishAttemptDto ToFinishDto(Attempt attempt) => new(
        attempt.State.ToString(),
        Provisional: attempt.State != AttemptState.Finalized,
        attempt.Score?.EarnedPoints,
        attempt.Score?.MaxPoints,
        attempt.Score?.RoundedPercent);
}
