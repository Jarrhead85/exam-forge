namespace ExamForge.Web.Client;

/// <summary>
/// Отображение остатка времени на вопрос (ТЗ §10.1): срок владеет сервер (DeadlineUtc),
/// клиент только визуализирует. Остаток — не аккумулятор: каждый момент пересчитывается
/// из дедлайна, поэтому таймер не ускоряется (P0-03), а после сворачивания вкладки
/// или сна устройства показывается корректный остаток.
/// </summary>
public static class ExamCountdown
{
    /// <param name="deadlineUtc">Дедлайн активной сессии вопроса (UTC); null — таймера нет.</param>
    /// <param name="now">Текущий момент UTC (обычно DateTimeOffset.UtcNow).</param>
    /// <returns>Остаток, зажатый снизу нулём.</returns>
    public static TimeSpan? Remaining(DateTimeOffset? deadlineUtc, DateTimeOffset now)
        => deadlineUtc is { } deadline
            ? deadline - now <= TimeSpan.Zero ? TimeSpan.Zero : deadline - now
            : null;
}
