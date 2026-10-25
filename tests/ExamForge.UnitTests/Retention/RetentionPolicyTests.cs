using ExamForge.Domain.Retention;
using Xunit;

namespace ExamForge.UnitTests.Retention;

public class RetentionPolicyTests
{
    private static readonly DateTimeOffset Completed = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RetainUntil_IsFiveCalendarYears_FromCompletion()
    {
        var until = RetentionPolicy.RetainUntil(Completed);
        Assert.Equal(new DateTimeOffset(2031, 10, 10, 12, 0, 0, TimeSpan.Zero), until);
    }

    [Fact]
    public void RetainUntil_Feb29_MapsToFeb28_InNonLeapTargetYear()
    {
        // Зафиксированное календарное поведение для 29 февраля (ТЗ §20).
        var until = RetentionPolicy.RetainUntil(new DateTimeOffset(2024, 2, 29, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2029, 2, 28, 0, 0, 0, TimeSpan.Zero), until);
    }

    [Fact]
    public void RetainUntil_NotCompleted_ReturnsNull()
    {
        Assert.Null(RetentionPolicy.RetainUntil(null));
    }

    [Fact]
    public void IsDue_ExactlyAtBoundary_IsTrue()
    {
        var until = RetentionPolicy.RetainUntil(Completed)!.Value;
        Assert.True(RetentionPolicy.IsDue(Completed, until));
        Assert.False(RetentionPolicy.IsDue(Completed, until.AddTicks(-1)));
    }

    [Fact]
    public void IsDue_NotCompleted_IsFalse()
    {
        Assert.False(RetentionPolicy.IsDue(null, Completed));
    }
}
