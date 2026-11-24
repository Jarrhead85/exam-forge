namespace ExamForge.Web.Client;

/// <summary>Итог явного сохранения ответа перед переходом (P0-04).</summary>
public enum SaveAnswerResult
{
    /// <summary>Сервер подтвердил ревизию.</summary>
    Saved,
    /// <summary>Сервер отклонил: время на вопрос истекло.</summary>
    RejectedLate,
    /// <summary>Ответ не подтверждён сервером (ошибка связи / сбой) — переход невозможен.</summary>
    Offline,
}

/// <summary>
/// Правило перехода Next/Finish: переход разрешён только при подтверждённом сохранении.
/// Ошибка сохранения не закрывает вопрос и не завершает попытку (P0-04).
/// </summary>
public static class ExamSaveGate
{
    public static bool CanProceed(SaveAnswerResult result) => result == SaveAnswerResult.Saved;

    public static string? GetMessage(SaveAnswerResult result) => result switch
    {
        SaveAnswerResult.Offline
            => "Ответ не подтверждён сервером: проверьте соединение и повторите попытку «Далее».",
        SaveAnswerResult.RejectedLate
            => "Время на вопрос истекло — последняя правка не принята. Завершите работу по сохранённым ответам.",
        _ => null,
    };
}
