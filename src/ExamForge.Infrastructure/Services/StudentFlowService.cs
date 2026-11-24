using System.Text.Json;
using ExamForge.Application.StudentFlow;
using ExamForge.Domain.Access;
using ExamForge.Domain.Assignments;
using ExamForge.Domain.Common;
using ExamForge.Domain.Attempts;
using ExamForge.Domain.Questions;
using ExamForge.Domain.Testing;
using ExamForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ExamForge.Infrastructure.Services;

/// <summary>
/// Студенческий поток (ТЗ §10, §17): start/open-next/save/lock/finish/result.
/// Сервер владеет временем (TimeProvider) и состоянием; все переходы — через доменные методы.
/// Идемпотентность start/finish, одна активная попытка, поздние ответы отклоняются (ADR-003).
/// </summary>
public sealed class StudentFlowService(
    ExamForgeDbContext db, TimeProvider timeProvider, ILogger<StudentFlowService> logger)
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

        if (!string.IsNullOrWhiteSpace(group))
            Guard.Required(group, "Группа", 200);
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

    public async Task<string?> CommonLinkTitleAsync(string linkCode, CancellationToken ct)
    {
        var code = InvitationCode.Normalize(linkCode);
        if (!InvitationCode.IsValidFormat(code))
            return null;

        return await (from assignment in db.Assignments.AsNoTracking()
                      join version in db.TestVersions.AsNoTracking() on assignment.TestVersionId equals version.Id
                      where assignment.AllowCommonLink && assignment.CommonLinkCode == code
                      select version.Title).SingleOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<StudentAssignmentDto>> ListAssignmentsAsync(StudentIdentity who, CancellationToken ct)
    {
        if (who.StudentProfileId is not { } profileId)
            return [];

        var rows = await (from assignment in db.Assignments.AsNoTracking()
                          join version in db.TestVersions.AsNoTracking() on assignment.TestVersionId equals version.Id
                          where who.AssignmentId != null ? assignment.Id == who.AssignmentId :
                              db.Invitations.Any(invitation => invitation.AssignmentId == assignment.Id &&
                                  invitation.StudentProfileId == profileId && invitation.Status == InvitationStatus.Active)
                          orderby assignment.OpensAtUtc descending
                          select new
                          {
                              Assignment = assignment,
                              version.Title,
                              QuestionCount = version.QuestionVersionIds.Count,
                              HasExtraAttempt = db.ExtraAttemptGrants.Any(grant => grant.AssignmentId == assignment.Id &&
                                  grant.StudentProfileId == profileId && grant.UsedByAttemptId == null),
                          }).ToListAsync(ct);

        var ids = rows.Select(row => row.Assignment.Id).ToArray();
        var allAttempts = await db.Attempts.AsNoTracking()
            .Where(attempt => attempt.StudentProfileId == profileId && ids.Contains(attempt.AssignmentId))
            .OrderBy(attempt => attempt.AttemptNumber).ToListAsync(ct);
        var attemptsByAssignment = allAttempts.ToLookup(attempt => attempt.AssignmentId);

        return [.. rows.Select(row =>
        {
            var assignment = row.Assignment;
            var attempts = attemptsByAssignment[assignment.Id];
            var active = attempts.FirstOrDefault(attempt => attempt.State is AttemptState.Created or AttemptState.InProgress);
            var history = attempts.Where(attempt => attempt.State != AttemptState.Created)
                .Select(attempt => new StudentAttemptBriefDto(attempt.Id, attempt.AttemptNumber, attempt.State.ToString(),
                    attempt.ScoreEarnedPoints, attempt.ScoreMaxPoints, attempt.Score?.RoundedPercent, attempt.CompletedAtUtc))
                .ToList();
            return new StudentAssignmentDto(assignment.Id, row.Title, assignment.Mode.ToString(), assignment.OpensAtUtc,
                assignment.ClosesAtUtc, assignment.MaxAttempts, attempts.Count(attempt => attempt.State != AttemptState.Cancelled),
                active is not null, active?.Id, history, row.HasExtraAttempt, row.QuestionCount,
                assignment.PerQuestionTimeLimitSeconds, assignment.AttemptTotalTimeLimitSeconds);
        })];
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

            var granted = await BuildNewAttemptAsync(assignment, testVersion, profileId, usedCount + 1, grant.Id, who.GroupName, ct);
            grant.MarkUsed(granted.Id);
            try
            {
                await db.SaveChangesAsync(ct); // attempt + sessions + grant атомарно (P0-07)
            }
            catch (DbUpdateException ex) when (IsUniqueActiveViolation(ex))
            {
                return await AdoptWinnerStateAsync(assignment, profileId, now, ct);
            }
            return await BuildStateAsync(granted, ct);
        }

        var created = await BuildNewAttemptAsync(assignment, testVersion, profileId, usedCount + 1, null, who.GroupName, ct);
        try
        {
            // P0-07: Attempt и QuestionSessions создаются одним SaveChanges (одна транзакция),
            // unique violation ловится только здесь — больше нет промежуточных сохранений
            // до try/catch, из-за которых посторонний 500 «протекал» наружу.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueActiveViolation(ex))
        {
            return await AdoptWinnerStateAsync(assignment, profileId, now, ct);
        }
        return await BuildStateAsync(created, ct);
    }

    /// <summary>Ожидаемая гонка двух start (PostgreSQL 23505 + ix_attempts_unique_active);
    /// остальные DbUpdateException — genuine ошибки и не «глотаются».</summary>
    private static bool IsUniqueActiveViolation(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException pg
            && pg.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation
            && string.Equals(pg.ConstraintName, "ix_attempts_unique_active", StringComparison.OrdinalIgnoreCase);

    /// <summary>Другой запрос выиграл гонку: продолжаем с его попыткой (AC-07), без 500 и без
    /// второй активной попытки. Start идемпотентен (Created → InProgress).</summary>
    private async Task<AttemptStateDto> AdoptWinnerStateAsync(
        Assignment assignment, Guid profileId, DateTimeOffset now, CancellationToken ct)
    {
        db.ChangeTracker.Clear(); // изменения проигравшего запроса отброшены
        var winner = await db.Attempts.FirstAsync(
            a => a.AssignmentId == assignment.Id && a.StudentProfileId == profileId
                 && (a.State == AttemptState.Created || a.State == AttemptState.InProgress), ct);
        winner.Start(now, Seconds(assignment.AttemptTotalTimeLimitSeconds));
        await db.SaveChangesAsync(ct);
        return await BuildStateAsync(winner, ct);
    }

    /// <summary>Формирует Attempt + QuestionSessions в памяти БЕЗ SaveChanges (P0-07):
    /// сохранение выполняет вызывающий код одним атомарным SaveChangesAsync.</summary>
    private async Task<Attempt> BuildNewAttemptAsync(
        Assignment assignment, TestVersion testVersion, Guid profileId, int number, Guid? grantId,
        string? groupSnapshot, CancellationToken ct)
    {
        var profile = await db.StudentProfiles.AsNoTracking().SingleAsync(p => p.Id == profileId, ct);
        // Варианты назначаются циклически по номеру попытки (предложение; D-08 требует подтверждения).
        var variant = await PickVariantAsync(testVersion.Id, number, ct);
        groupSnapshot ??= await db.Invitations.AsNoTracking().Where(invitation =>
            invitation.AssignmentId == assignment.Id && invitation.StudentProfileId == profileId &&
            invitation.Status == InvitationStatus.Active && invitation.GroupNameSnapshot != null)
            .OrderByDescending(invitation => invitation.CreatedAtUtc)
            .Select(invitation => invitation.GroupNameSnapshot)
            .FirstOrDefaultAsync(ct);
        var attempt = Attempt.Create(assignment.Id, number, Now, profileId, NameOf(profile), groupSnapshot,
            grantId, variant?.Id, assignment.MaxAttempts);
        attempt.Start(Now, Seconds(assignment.AttemptTotalTimeLimitSeconds));
        db.Attempts.Add(attempt);
        // Порядок вопросов попытки — из зафиксированного варианта (ТЗ §8); без вариантов — порядок версии.
        var order = 0;
        foreach (var questionVersionId in variant?.QuestionOrder ?? testVersion.QuestionVersionIds)
            db.QuestionSessions.Add(QuestionSession.Create(attempt.Id, questionVersionId, order++));
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

    public Task<AttemptStateDto> OpenNextAsync(Guid attemptId, StudentIdentity who, CancellationToken ct)
        => WithAttemptLockAsync(attemptId, who, attempt => OpenNextCoreAsync(attempt, ct), ct);

    public Task<AttemptStateDto> AdvanceAsync(
        Guid attemptId, AdvanceQuestionRequest request, StudentIdentity who, CancellationToken ct)
        => WithAttemptLockAsync(attemptId, who, async attempt =>
        {
            if (IsSubmitted(attempt))
                return await BuildStateAsync(attempt, ct);
            EnsureInProgress(attempt);
            var sessions = await LoadSessionsAsync(attempt.Id, ct);
            var latest = sessions.LastOrDefault(session => session.StartedAtUtc is not null);
            if (latest?.Id != request.QuestionSessionId)
                return await BuildStateAsync(attempt, ct);

            if (request.Answer is { } answer)
            {
                if (answer.QuestionSessionId != latest.Id)
                    throw StudentFlowException.NotFound("Вопрос не найден.");
                await RecordAnswerAsync(attempt, latest, answer, Now, ct);
            }

            latest.Lock(Now);
            if (sessions.All(session => session.State != QuestionSessionState.NotOpened))
            {
                await FinishCoreAsync(attempt, sessions, Now, ct);
                return await BuildStateAsync(attempt, ct);
            }

            return await OpenNextCoreAsync(attempt, ct);
        }, ct);

    private async Task<AttemptStateDto> OpenNextCoreAsync(Attempt attempt, CancellationToken ct)
    {
        if (IsSubmitted(attempt))
            return await BuildStateAsync(attempt, ct);
        EnsureInProgress(attempt);

        var now = Now;
        var assignment = await LoadAssignmentTimingAsync(attempt.AssignmentId, ct);
        var sessions = await LoadSessionsAsync(attempt.Id, ct);
        if (WorkDeadline(attempt, assignment.ClosesAtUtc) is { } deadline && now >= deadline)
        {
            await FinishCoreAsync(attempt, sessions, now, ct);
            return await BuildStateAsync(attempt, ct);
        }

        var active = sessions.FirstOrDefault(session => session.State == QuestionSessionState.Active);
        active?.ExpireIfDue(now);
        if (active?.State == QuestionSessionState.Active)
            return await BuildStateAsync(attempt, ct);

        var next = sessions.FirstOrDefault(session => session.State == QuestionSessionState.NotOpened)
            ?? throw StudentFlowException.Conflict("no_next_question", "Все вопросы открыты. Сдайте работу.");
        var proposedTime = await db.QuestionVersions.AsNoTracking()
            .Where(version => version.Id == next.QuestionVersionId)
            .Select(version => version.ProposedTimeLimitSeconds)
            .SingleAsync(ct);
        next.Open(now, Seconds(assignment.PerQuestionTimeLimitSeconds ?? proposedTime),
            attempt.DeadlineUtc, assignment.ClosesAtUtc);
        await db.SaveChangesAsync(ct);
        return await BuildStateAsync(attempt, ct);
    }

    public Task<SaveAnswerDto> SaveAnswerAsync(
        Guid attemptId, AttemptAnswer answer, StudentIdentity who, CancellationToken ct)
        => WithAttemptLockAsync(attemptId, who, async attempt =>
        {
            EnsureInProgress(attempt);
            var session = await db.QuestionSessions
                .SingleOrDefaultAsync(s => s.Id == answer.QuestionSessionId && s.AttemptId == attempt.Id, ct)
                ?? throw StudentFlowException.NotFound("Вопрос не найден.");
            var now = Now;
            await RecordAnswerAsync(attempt, session, answer, now, ct);
            await db.SaveChangesAsync(ct);

            return new SaveAnswerDto(true, session.AnswerRevision, now, session.DeadlineUtc, StateName(session.State), null);
        }, ct);

    private async Task RecordAnswerAsync(
        Attempt attempt, QuestionSession session, AttemptAnswer answer, DateTimeOffset now, CancellationToken ct)
    {
        if (session.State == QuestionSessionState.LockedTimedOut ||
            (session.State == QuestionSessionState.Active && !session.CanAcceptAnswer(now)))
            throw StudentFlowException.Conflict("late", "Время истекло. Последняя правка не принята сервером.");
        if (session.State != QuestionSessionState.Active)
            throw StudentFlowException.Conflict("not_active", "Вопрос закрыт, ответ сохранён в предыдущем состоянии.");

        var definitionJson = await db.QuestionVersions.AsNoTracking()
            .Where(version => version.Id == session.QuestionVersionId)
            .Select(version => version.DefinitionJson)
            .SingleAsync(ct);
        QuestionAnswerValidator.Parse(QuestionDefinitionJson.Deserialize(definitionJson), answer.Answer);

        if (answer.ExpectedRevision is { } expected && session.AnswerRevision != expected)
        {
            var saved = await db.AnswerRevisions.AsNoTracking()
                .Where(revision => revision.QuestionSessionId == session.Id && revision.Revision == session.AnswerRevision)
                .Select(revision => revision.AnswerJson)
                .SingleOrDefaultAsync(ct);
            if (saved is not null)
            {
                using var document = JsonDocument.Parse(saved);
                if (JsonElement.DeepEquals(document.RootElement, answer.Answer))
                    return; // Повтор ответа, подтверждение которого не дошло до клиента.
            }

            throw StudentFlowException.Conflict("revision_conflict",
                "У вопроса есть более новый ответ. Проверьте другую вкладку или обновите страницу.");
        }

        session.SaveAnswer(now);
        db.AnswerRevisions.Add(AnswerRevision.Create(
            session.Id, attempt.Id, session.AnswerRevision, answer.Answer.GetRawText(), now));
    }

    public Task<LockCurrentDto> LockCurrentAsync(Guid attemptId, StudentIdentity who, CancellationToken ct)
        => WithAttemptLockAsync(attemptId, who, async attempt =>
        {
            EnsureInProgress(attempt);
            var sessions = await LoadSessionsAsync(attempt.Id, ct);
            var current = sessions.LastOrDefault(session => session.StartedAtUtc is not null)
                ?? throw StudentFlowException.Conflict("no_active_question", "Сначала откройте вопрос.");
            var outcome = current.Lock(Now);
            await db.SaveChangesAsync(ct);
            return new LockCurrentDto(outcome.ToString(), current.OrderIndex, StateName(current.State));
        }, ct);

    public Task<FinishAttemptDto> FinishAsync(
        Guid attemptId, AttemptAnswer? answer, StudentIdentity who, CancellationToken ct)
        => WithAttemptLockAsync(attemptId, who, async attempt =>
        {
            if (IsSubmitted(attempt))
                return ToFinishDto(attempt);

            EnsureInProgress(attempt);
            var now = Now;
            var sessions = await LoadSessionsAsync(attempt.Id, ct);
            if (answer is not null)
            {
                var session = sessions.SingleOrDefault(item => item.Id == answer.QuestionSessionId)
                    ?? throw StudentFlowException.NotFound("Вопрос не найден.");
                await RecordAnswerAsync(attempt, session, answer, now, ct);
            }

            return await FinishCoreAsync(attempt, sessions, now, ct);
        }, ct);

    private async Task<FinishAttemptDto> FinishCoreAsync(
        Attempt attempt, IReadOnlyList<QuestionSession> sessions, DateTimeOffset now, CancellationToken ct)
    {
        // Команда перехода может принять последнюю правку. Грейдер читает её в той же транзакции.
        await db.SaveChangesAsync(ct);
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
                ? await ReadStoredAnswerAsync(revision, ct)
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
        IReadOnlyList<StudentQuestionFeedbackDto>? feedback = null;
        if (attempt.State == AttemptState.Finalized &&
            await db.Assignments.AsNoTracking().AnyAsync(assignment => assignment.Id == attempt.AssignmentId &&
                assignment.Feedback == FeedbackPolicy.PracticeFeedback, ct))
        {
            feedback = await LoadPracticeFeedbackAsync(attempt.Id, ct);
        }

        return new StudentResultDto(
            attempt.State.ToString(),
            Provisional: attempt.State != AttemptState.Finalized,
            EarnedPoints: score?.EarnedPoints,
            MaxPoints: score?.MaxPoints ?? 0m,
            RoundedPercent: score?.RoundedPercent,
            CompletedAtUtc: attempt.CompletedAtUtc,
            attempt.AttemptNumber,
            feedback);
    }

    private async Task<IReadOnlyList<StudentQuestionFeedbackDto>> LoadPracticeFeedbackAsync(Guid attemptId, CancellationToken ct)
    {
        var points = from grade in db.QuestionGrades.AsNoTracking()
                     where grade.AttemptId == attemptId
                     group grade by grade.QuestionSessionId into session
                     select new { Id = session.Key, Earned = session.Sum(row => row.EarnedPoints), Max = session.Sum(row => row.MaxPoints) };
        var items = await (from session in db.QuestionSessions.AsNoTracking()
                           join version in db.QuestionVersions.AsNoTracking() on session.QuestionVersionId equals version.Id
                           join score in points on session.Id equals score.Id
                           where session.AttemptId == attemptId
                           orderby session.OrderIndex
                           select new { session.OrderIndex, session.AnswerRevision, version.DefinitionJson, score.Earned, score.Max })
            .ToListAsync(ct);
        return [.. items.Select(item => new StudentQuestionFeedbackDto(item.OrderIndex + 1,
            QuestionDefinitionJson.Deserialize(item.DefinitionJson).Prompt, item.Earned, item.Max, item.AnswerRevision > 0))];
    }

    public Task<AttemptStateDto> GetStateAsync(Guid attemptId, StudentIdentity who, CancellationToken ct)
        => WithAttemptLockAsync(attemptId, who, async attempt =>
        {
            if (attempt.State == AttemptState.InProgress)
            {
                var now = Now;
                var assignment = await LoadAssignmentTimingAsync(attempt.AssignmentId, ct);
                var sessions = await LoadSessionsAsync(attempt.Id, ct);
                if (WorkDeadline(attempt, assignment.ClosesAtUtc) is { } deadline && now >= deadline)
                    await FinishCoreAsync(attempt, sessions, now, ct);
                else if (sessions.FirstOrDefault(session => session.State == QuestionSessionState.Active)?.ExpireIfDue(now) == true)
                    await db.SaveChangesAsync(ct);
            }
            return await BuildStateAsync(attempt, ct);
        }, ct);

    private async Task<T> WithAttemptLockAsync<T>(
        Guid attemptId, StudentIdentity who, Func<Attempt, Task<T>> operation, CancellationToken ct)
        => await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // Все переходы одной попытки получают блокировку до чтения сессий и ответов.
            var attempt = await db.Attempts
                .FromSql($"""SELECT attempts.*, xmin FROM attempts WHERE "Id" = {attemptId} FOR UPDATE""")
                .SingleOrDefaultAsync(a => a.StudentProfileId == who.StudentProfileId &&
                    (who.AssignmentId == null || a.AssignmentId == who.AssignmentId), ct)
                ?? throw StudentFlowException.NotFound();
            var result = await operation(attempt);
            await transaction.CommitAsync(ct);
            return result;
        });

    private async Task<QuestionAnswer?> ReadStoredAnswerAsync(AnswerRevision latest, CancellationToken ct)
    {
        if (TryReadStoredAnswer(latest) is { } answer)
            return answer;

        var earlier = await db.AnswerRevisions.AsNoTracking()
            .Where(revision => revision.QuestionSessionId == latest.QuestionSessionId &&
                revision.Revision < latest.Revision)
            .OrderByDescending(revision => revision.Revision)
            .ToListAsync(ct);
        return earlier.Select(TryReadStoredAnswer).FirstOrDefault(answer => answer is not null);
    }

    private QuestionAnswer? TryReadStoredAnswer(AnswerRevision revision)
    {
        try
        {
            return QuestionDefinitionJson.DeserializeAnswer(revision.AnswerJson);
        }
        catch (DomainException)
        {
            logger.LogWarning("Ignoring invalid stored answer {AnswerId} in attempt {AttemptId}",
                revision.Id, revision.AttemptId);
            return null;
        }
    }

    private static bool IsSubmitted(Attempt attempt)
        => attempt.State is AttemptState.Submitted or AttemptState.PendingReview or AttemptState.Finalized;

    private static void EnsureInProgress(Attempt attempt)
    {
        if (attempt.State != AttemptState.InProgress)
            throw StudentFlowException.Conflict("not_in_progress", "Попытка уже закрыта или ещё не началась.");
    }

    private async Task<AssignmentTiming> LoadAssignmentTimingAsync(Guid assignmentId, CancellationToken ct)
        => await (from assignment in db.Assignments.AsNoTracking()
                  join version in db.TestVersions.AsNoTracking() on assignment.TestVersionId equals version.Id
                  where assignment.Id == assignmentId
                  select new AssignmentTiming(assignment.ClosesAtUtc, assignment.PerQuestionTimeLimitSeconds, version.Title))
            .SingleAsync(ct);

    private static DateTimeOffset? WorkDeadline(Attempt attempt, DateTimeOffset? closesAtUtc)
        => closesAtUtc is { } closes && (attempt.DeadlineUtc is null || closes < attempt.DeadlineUtc)
            ? closes
            : attempt.DeadlineUtc;

    private sealed record AssignmentTiming(DateTimeOffset? ClosesAtUtc, int? PerQuestionTimeLimitSeconds, string Title);

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
            // P0-05: после F5/reconnect студент продолжает со своего последнего сохранённого
            // ответа; DTO отдаёт только собственную ревизию, ключей в ней нет (AC-11).
            string? savedAnswerJson = null;
            if (active.AnswerRevision > 0)
                savedAnswerJson = await db.AnswerRevisions.AsNoTracking()
                    .Where(r => r.QuestionSessionId == active.Id && r.Revision == active.AnswerRevision)
                    .Select(r => r.AnswerJson)
                    .FirstOrDefaultAsync(ct);
            current = await ProjectQuestionAsync(active, variant, savedAnswerJson, ct);
        }

        var assignment = await LoadAssignmentTimingAsync(attempt.AssignmentId, ct);
        return new AttemptStateDto(
            attempt.Id,
            attempt.State.ToString(),
            attempt.AttemptNumber,
            attempt.StartedAtUtc,
            WorkDeadline(attempt, assignment.ClosesAtUtc),
            sessions.Count,
            sessions.Select(s => new SessionBriefDto(s.OrderIndex, StateName(s.State), s.AnswerRevision, s.DeadlineUtc)).ToList(),
            current,
            Now,
            assignment.Title);
    }

    /// <summary>Проекция без ключей: CorrectOptionId/ключи/объяснения не сериализуются (AC-11).
    /// Порядок отображаемых списков — по варианту попытки (ТЗ §8).</summary>
    private async Task<StudentQuestionDto> ProjectQuestionAsync(
        QuestionSession session, Domain.Testing.Variant? variant, string? savedAnswerJson, CancellationToken ct)
    {
        var version = await db.QuestionVersions.AsNoTracking().SingleAsync(v => v.Id == session.QuestionVersionId, ct);
        var definition = QuestionDefinitionJson.Deserialize(version.DefinitionJson);

        return StudentQuestionProjection.Create(session.Id, version.Id, session.OrderIndex, definition,
            version.ProposedTimeLimitSeconds, variant, savedAnswerJson);
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
