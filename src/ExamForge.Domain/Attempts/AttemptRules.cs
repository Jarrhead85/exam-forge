namespace ExamForge.Domain.Attempts;

/// <summary>
/// Правила лучшей попытки (ТЗ §10.4): только полностью проверенные Finalized eligible попытки;
/// отменённая/истёкшая/на проверке — не участвует. При равном результате — ранняя завершённая
/// (tie-break — предложение, требует подтверждения заказчика, D-08).
/// </summary>
public static class AttemptRules
{
    public static Attempt? SelectBest(IEnumerable<Attempt> attempts)
    {
        Attempt? best = null;
        foreach (var attempt in attempts)
        {
            if (!attempt.IsEligibleForBest || attempt.Score is not { } score)
                continue;
            if (best is null
                || score.RoundedPercent > best.Score!.Value.RoundedPercent
                || (score.RoundedPercent == best.Score!.Value.RoundedPercent
                    && attempt.CompletedAtUtc < best.CompletedAtUtc))
            {
                best = attempt;
            }
        }
        return best;
    }
}
