using ExamForge.Domain.Common;
using ExamForge.Domain.Grading;
using Xunit;

namespace ExamForge.UnitTests.Grading;

public class GradeCalculatorTests
{
    [Theory]
    // AC-01: AwayFromZero, не банковское округление; оценка по округлённому целому.
    [InlineData(85.7, 100, 86, 5)]
    [InlineData(85.5, 100, 86, 5)]
    [InlineData(85.4, 100, 85, 4)]
    [InlineData(69.5, 100, 70, 4)]
    [InlineData(54.5, 100, 55, 3)]
    [InlineData(86.0, 100, 86, 5)]
    [InlineData(85.999, 100, 86, 5)]
    [InlineData(55.0, 100, 55, 3)]
    [InlineData(54.499, 100, 54, 2)]
    [InlineData(0.0, 100, 0, 2)]
    [InlineData(100.0, 100, 100, 5)]
    [InlineData(70.0, 100, 70, 4)]
    [InlineData(69.499, 100, 69, 3)]
    public void Compute_RoundsOnceAwayFromZero_AndGradesByRoundedValue(
        decimal earned, decimal max, int expectedPercent, int expectedGrade)
    {
        var result = GradeCalculator.Compute(earned, max);
        Assert.Equal(expectedPercent, result.RoundedPercent);
        Assert.Equal(expectedGrade, result.Grade);
    }

    [Fact]
    public void Compute_PreservesExactRawPercent()
    {
        var result = GradeCalculator.Compute(3m, 4m);
        Assert.Equal(75.0m, result.RawPercent);
        Assert.Equal(75, result.RoundedPercent);
        Assert.Equal(4, result.Grade);
    }

    [Fact]
    public void Compute_FractionalPoints_WorkWithoutIntermediateRounding()
    {
        // 1.5+1.5 из 4 = 75% без округления отдельных критериев
        var result = GradeCalculator.Compute(3.0m, 4.0m);
        Assert.Equal(75, result.RoundedPercent);
    }

    [Fact]
    public void Compute_ZeroMax_Throws()
    {
        Assert.Throws<DomainException>(() => GradeCalculator.Compute(0m, 0m));
    }

    [Fact]
    public void Compute_NegativeEarned_Throws()
    {
        Assert.Throws<DomainException>(() => GradeCalculator.Compute(-0.1m, 10m));
    }

    [Fact]
    public void Compute_EarnedAboveMax_Throws()
    {
        Assert.Throws<DomainException>(() => GradeCalculator.Compute(10.1m, 10m));
    }

    [Fact]
    public void Compute_MatchingPartialCredit_ThreeOfFourPairs()
    {
        // AC-02: 3 верные пары из 4 по 1 баллу = 3/4, без требования полного совпадения.
        var result = GradeCalculator.Compute(3m, 4m);
        Assert.Equal(3m, result.EarnedPoints);
        Assert.Equal(4m, result.MaxPoints);
        Assert.Equal(75, result.RoundedPercent);
    }
}
