using ExamForge.Domain.Attempts;
using ExamForge.Domain.Common;
using Xunit;

namespace ExamForge.UnitTests.Attempts;

public class AttemptTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

    private static Attempt NewAttempt(int number = 1) =>
        Attempt.Create(Guid.NewGuid(), number, Now, Guid.NewGuid(), "Иванова Анна Сергеевна", "ЛД-201", null);

    [Fact]
    public void Create_ThirdAttempt_WithoutGrant_Throws()
    {
        Assert.Throws<DomainException>(() => NewAttempt(3));
    }

    [Fact]
    public void Start_IsIdempotent_AndKeepsOriginalStartTime()
    {
        // AC-07: повторный start — одна попытка, лимит не расходуется дважды.
        var attempt = NewAttempt();
        attempt.Start(Now);
        attempt.Start(Now.AddMinutes(5));
        Assert.Equal(AttemptState.InProgress, attempt.State);
        Assert.Equal(Now, attempt.StartedAtUtc);
    }

    [Fact]
    public void Submit_IsIdempotent()
    {
        var attempt = NewAttempt();
        attempt.Start(Now);
        attempt.Submit(Now.AddMinutes(10), hasPendingManualReview: false);
        attempt.Submit(Now.AddMinutes(11), hasPendingManualReview: false);
        Assert.Equal(AttemptState.Submitted, attempt.State);
        Assert.Equal(Now.AddMinutes(10), attempt.SubmittedAtUtc);
    }

    [Fact]
    public void Submit_WithManualReview_EntersPendingReview()
    {
        var attempt = NewAttempt();
        attempt.Start(Now);
        attempt.Submit(Now.AddMinutes(10), hasPendingManualReview: true);
        Assert.Equal(AttemptState.PendingReview, attempt.State);
    }

    [Fact]
    public void Finalize_ComputesScore_AndSetsCompletion()
    {
        var attempt = NewAttempt();
        attempt.Start(Now);
        attempt.Submit(Now.AddMinutes(10), false);
        var score = attempt.Finalize(8.57m, 10m, Now.AddMinutes(11));
        Assert.Equal(86, score.RoundedPercent);
        Assert.Equal(5, score.Grade);
        Assert.Equal(AttemptState.Finalized, attempt.State);
        Assert.Equal(Now.AddMinutes(11), attempt.CompletedAtUtc);
        Assert.True(attempt.IsEligibleForBest);
    }

    [Fact]
    public void Finalize_NotSubmitted_Throws()
    {
        var attempt = NewAttempt();
        attempt.Start(Now);
        Assert.Throws<DomainException>(() => attempt.Finalize(1m, 10m, Now));
    }

    [Fact]
    public void PendingReview_IsNotEligibleForBest()
    {
        // AC-09: LongText не проверен — provisional, нет итоговой grade/best.
        var attempt = NewAttempt();
        attempt.Start(Now);
        attempt.Submit(Now.AddMinutes(10), true);
        Assert.False(attempt.IsEligibleForBest);
        Assert.Null(attempt.Score);
    }

    [Fact]
    public void CancelledAttempt_StaysInHistory_ButNotEligible()
    {
        var attempt = NewAttempt();
        attempt.Start(Now);
        attempt.Cancel(Now.AddMinutes(1));
        Assert.Equal(AttemptState.Cancelled, attempt.State);
        Assert.False(attempt.IsEligibleForBest);
    }

    [Fact]
    public void SelectBest_OnlyFinalizedAttempts_Count()
    {
        // AC-08: 2 попытки + extra grant — все сохранены; лучший только finalized.
        var best = NewAttempt(1);
        best.Start(Now); best.Submit(Now.AddMinutes(10), false); best.Finalize(90m, 100m, Now.AddMinutes(11));

        var worse = NewAttempt(2);
        worse.Start(Now); worse.Submit(Now.AddMinutes(10), false); worse.Finalize(70m, 100m, Now.AddMinutes(11));

        var pending = NewAttempt(2);
        pending.Start(Now); pending.Submit(Now.AddMinutes(10), true); // на ручной проверке

        var winner = AttemptRules.SelectBest(new[] { best, worse, pending });
        Assert.Same(best, winner);
    }

    [Fact]
    public void SelectBest_Tie_PrefersEarlierCompleted()
    {
        // Tie-break D-08 (предложение, требует подтверждения): равный результат — ранняя завершённая.
        var early = NewAttempt(1);
        early.Start(Now); early.Submit(Now.AddMinutes(10), false); early.Finalize(80m, 100m, Now.AddMinutes(11));

        var late = NewAttempt(2);
        late.Start(Now.AddHours(1)); late.Submit(Now.AddHours(2), false); late.Finalize(80m, 100m, Now.AddHours(2).AddMinutes(1));

        var winner = AttemptRules.SelectBest(new[] { late, early });
        Assert.Same(early, winner);
    }

    [Fact]
    public void SelectBest_Empty_ReturnsNull()
    {
        Assert.Null(AttemptRules.SelectBest(Array.Empty<Attempt>()));
    }
}
