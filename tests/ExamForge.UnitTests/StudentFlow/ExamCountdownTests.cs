using ExamForge.Web.Client;

namespace ExamForge.UnitTests.StudentFlow;

/// <summary>
/// P0-03: остаток пересчитывается из DeadlineUtc, не накапливается — таймер идёт
/// «секунда в секунду», не ускоряется; после сна/свёрнутой вкладки остаток корректен.
/// </summary>
public sealed class ExamCountdownTests
{
    [Fact]
    public void CountdownDecreasesOneSecondPerSecond()
    {
        var start = new DateTimeOffset(2026, 10, 25, 14, 0, 0, TimeSpan.Zero);
        var deadline = start.AddSeconds(60);

        // Имитация тиков ровно каждую секунду: 60 → 59 → 58 → … (не 59 → 57 → 54).
        for (var i = 0; i <= 60; i++)
        {
            var remaining = ExamCountdown.Remaining(deadline, start.AddSeconds(i));
            Assert.Equal(TimeSpan.FromSeconds(60 - i), remaining);
        }
    }

    [Fact]
    public void SequenceOfTicksIsStrictlyMonotonic()
    {
        var start = DateTimeOffset.UtcNow;
        var deadline = start.AddMinutes(45);
        TimeSpan? previous = null;
        for (var i = 0; i < 30; i++)
        {
            var remaining = ExamCountdown.Remaining(deadline, start.AddSeconds(0.9 * i));
            Assert.NotNull(remaining);
            if (previous is { } prev)
                Assert.InRange(remaining!.Value.TotalSeconds, prev.TotalSeconds - 1.5, prev.TotalSeconds);
            previous = remaining;
        }
    }

    [Fact]
    public void SleepJumpShowsCorrectRemaining()
    {
        // Устройство заснуло на 30 секунд: остаток пересчитывается верно, а не по «замерзшему» значению.
        var start = new DateTimeOffset(2026, 10, 25, 14, 0, 0, TimeSpan.Zero);
        var deadline = start.AddMinutes(5);
        Assert.Equal(TimeSpan.FromMinutes(4.5), ExamCountdown.Remaining(deadline, start.AddSeconds(30)));
    }

    [Fact]
    public void ExpiredClampsToZero()
    {
        var now = new DateTimeOffset(2026, 10, 25, 14, 0, 0, TimeSpan.Zero);
        var deadline = now.AddSeconds(-1);
        Assert.Equal(TimeSpan.Zero, ExamCountdown.Remaining(deadline, now));
    }

    [Fact]
    public void NoDeadlineMeansNoTimer()
    {
        Assert.Null(ExamCountdown.Remaining(null, DateTimeOffset.UtcNow));
    }
}
