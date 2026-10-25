using ExamForge.Domain.Attempts;
using ExamForge.Domain.Common;
using Xunit;

namespace ExamForge.UnitTests.Attempts;

public class QuestionSessionTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 10, 0, 0, TimeSpan.Zero);

    private static QuestionSession NewSession() =>
        QuestionSession.Create(Guid.NewGuid(), Guid.NewGuid(), orderIndex: 1);

    [Fact]
    public void Open_FixesStartedAtAndDeadline_Once()
    {
        var session = NewSession();
        var result = session.Open(Start, perQuestionLimit: TimeSpan.FromSeconds(45), null, null);
        Assert.Equal(OpenQuestionResult.Opened, result);
        Assert.Equal(QuestionSessionState.Active, session.State);
        Assert.Equal(Start, session.StartedAtUtc);
        Assert.Equal(Start.AddSeconds(45), session.DeadlineUtc);
    }

    [Fact]
    public void Reopen_OnRefresh_DoesNotExtendDeadline()
    {
        // AC-03: refresh при активном вопросе — исходный deadline, не новый таймер.
        var session = NewSession();
        session.Open(Start, TimeSpan.FromSeconds(30), null, null);
        var firstDeadline = session.DeadlineUtc;

        var later = Start.AddSeconds(20);
        var result = session.Open(later, TimeSpan.FromSeconds(30), null, null);

        Assert.Equal(OpenQuestionResult.AlreadyActive, result);
        Assert.Equal(firstDeadline, session.DeadlineUtc);
        Assert.Equal(Start, session.StartedAtUtc);
    }

    [Fact]
    public void Open_TakesMinimum_OfQuestionAttemptAndAssignmentLimits()
    {
        // ТЗ §9: если заданы оба лимита, deadline = min(срок вопроса, срок попытки, срок назначения).
        var session = NewSession();
        var attemptDeadline = Start.AddSeconds(20);
        var assignmentCloses = Start.AddSeconds(90);
        session.Open(Start, TimeSpan.FromSeconds(45), attemptDeadline, assignmentCloses);
        Assert.Equal(attemptDeadline, session.DeadlineUtc);
    }

    [Theory]
    [InlineData(0, AnswerDecision.RejectedLate)]   // ровно в deadline — уже опоздал (ТЗ §10.1)
    [InlineData(1, AnswerDecision.RejectedLate)]   // после deadline
    [InlineData(-1, AnswerDecision.Accepted)]      // до deadline
    public void SaveAnswer_Boundary_IsStrict(int offsetSeconds, AnswerDecision expected)
    {
        var session = NewSession();
        session.Open(Start, TimeSpan.FromSeconds(45), null, null);
        var deadline = session.DeadlineUtc!.Value;

        var decision = session.SaveAnswer(deadline.AddSeconds(offsetSeconds));
        Assert.Equal(expected, decision);
    }

    [Fact]
    public void AcceptedRevision_SurvivesLaterLateSubmission()
    {
        // AC-04: ранее принятый ответ остаётся; поздняя запись отклонена.
        var session = NewSession();
        session.Open(Start, TimeSpan.FromSeconds(45), null, null);
        Assert.Equal(AnswerDecision.Accepted, session.SaveAnswer(Start.AddSeconds(10)));
        Assert.Equal(1, session.AnswerRevision);

        Assert.Equal(AnswerDecision.RejectedLate, session.SaveAnswer(session.DeadlineUtc!.Value.AddSeconds(5)));
        Assert.Equal(1, session.AnswerRevision);
    }

    [Fact]
    public void LockedQuestion_RejectsDirectUpdate()
    {
        // AC-06: закрытый прошлый вопрос нельзя изменить прямым запросом.
        var session = NewSession();
        session.Open(Start, TimeSpan.FromSeconds(45), null, null);
        session.Lock(Start.AddSeconds(5));

        Assert.Equal(AnswerDecision.RejectedNotActive, session.SaveAnswer(Start.AddSeconds(6)));
    }

    [Fact]
    public void ExpireIfDue_AtOrAfterDeadline_LocksAsTimedOut()
    {
        var session = NewSession();
        session.Open(Start, TimeSpan.FromSeconds(45), null, null);
        var deadline = session.DeadlineUtc!.Value;

        session.ExpireIfDue(deadline);
        Assert.Equal(QuestionSessionState.LockedTimedOut, session.State);
    }

    [Fact]
    public void OfflinePastDeadline_LateLocalAnswer_RejectedOnRecovery()
    {
        // AC-05: нет связи дольше deadline — время истекло; локальный поздний ответ не принят.
        var session = NewSession();
        session.Open(Start, TimeSpan.FromSeconds(45), null, null);
        var deadline = session.DeadlineUtc!.Value;

        // студент вернулся в сеть сильно позже; сервер проверяет срок при запросе
        var decision = session.SaveAnswer(deadline.AddMinutes(3));
        Assert.Equal(AnswerDecision.RejectedLate, decision);
        Assert.Equal(QuestionSessionState.LockedTimedOut, session.State);
    }

    [Fact]
    public void Lock_BeforeDeadline_IsSubmitted_AtOrAfter_IsTimedOut()
    {
        var early = NewSession();
        early.Open(Start, TimeSpan.FromSeconds(45), null, null);
        Assert.Equal(LockOutcome.LockedSubmitted, early.Lock(Start.AddSeconds(10)));
        Assert.Equal(QuestionSessionState.LockedSubmitted, early.State);

        var late = NewSession();
        late.Open(Start, TimeSpan.FromSeconds(45), null, null);
        Assert.Equal(LockOutcome.LockedTimedOut, late.Lock(late.DeadlineUtc!.Value));
    }

    [Fact]
    public void Lock_IsIdempotent()
    {
        var session = NewSession();
        session.Open(Start, TimeSpan.FromSeconds(45), null, null);
        session.Lock(Start.AddSeconds(10));
        Assert.Equal(LockOutcome.AlreadyLocked, session.Lock(Start.AddSeconds(11)));
    }

    [Fact]
    public void Lock_UnopenedQuestion_Throws()
    {
        var session = NewSession();
        Assert.Throws<DomainException>(() => session.Lock(Start));
    }

    [Fact]
    public void Open_WithoutAnyLimit_Throws()
    {
        var session = NewSession();
        Assert.Throws<DomainException>(() => session.Open(Start, null, null, null));
    }
}
