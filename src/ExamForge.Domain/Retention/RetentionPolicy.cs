namespace ExamForge.Domain.Retention;

/// <summary>
/// Пять календарных лет от завершения попытки для результатов и ответов (ТЗ §20).
/// Срок — пожелание заказчика, не заявленная законная обязанность; действие по expiry — D-04.
/// </summary>
public static class RetentionPolicy
{
    public const int Years = 5;

    /// <summary>
    /// Календарные годы: DateTimeOffset.AddYears. Для 29 февраля високосного года
    /// даёт 28 февраля некратного года — стандартное календарное поведение, зафиксировано тестом.
    /// </summary>
    public static DateTimeOffset? RetainUntil(DateTimeOffset? completedAtUtc)
        => completedAtUtc is { } completed ? completed.AddYears(Years) : null;

    /// <summary>Наступил ли срок. Само удаление/обезличивание выполняет утверждённая политика (D-04).</summary>
    public static bool IsDue(DateTimeOffset? completedAtUtc, DateTimeOffset nowUtc)
        => RetainUntil(completedAtUtc) is { } until && nowUtc >= until;
}
