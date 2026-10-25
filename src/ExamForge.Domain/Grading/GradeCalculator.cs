namespace ExamForge.Domain.Grading;

/// <summary>
/// Итог попытки: точные баллы, raw и округлённый процент, оценка 2–5.
/// Храним earned/max для exact rational (ТЗ §11); UI показывает RoundedPercent.
/// </summary>
public readonly record struct GradeResult(decimal EarnedPoints, decimal MaxPoints, decimal RawPercent, int RoundedPercent, int Grade)
{
    public override string ToString() => $"{EarnedPoints:0.###}/{MaxPoints:0.###} = {RoundedPercent}% (оценка {Grade})";
}

public static class GradeCalculator
{
    /// <summary>
    /// Единственное округление — после расчёта итога, AwayFromZero (не банковское).
    /// Оценка по округлённому целому: ≥86 → 5; ≥70 → 4; ≥55 → 3; иначе 2 (ТЗ §11).
    /// </summary>
    public static GradeResult Compute(decimal earnedPoints, decimal maxPoints)
    {
        if (maxPoints <= 0m)
            throw new Common.DomainException("MaxPoints должен быть больше нуля: тест с нулём баллов не публикуется (ТЗ §11).");
        if (earnedPoints < 0m || earnedPoints > maxPoints)
            throw new Common.DomainException("Начисленные баллы должны быть в [0; MaxPoints] (ТЗ §11).");

        var rawPercent = earnedPoints / maxPoints * 100m;
        var roundedPercent = (int)Math.Round(rawPercent, 0, MidpointRounding.AwayFromZero);
        var grade = roundedPercent >= 86 ? 5
                  : roundedPercent >= 70 ? 4
                  : roundedPercent >= 55 ? 3
                  : 2;
        return new GradeResult(earnedPoints, maxPoints, rawPercent, roundedPercent, grade);
    }
}
