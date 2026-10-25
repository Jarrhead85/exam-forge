using ExamForge.Domain.Common;

namespace ExamForge.Domain.Questions;

/// <summary>
/// Проверка готовности к публикации (ТЗ §7.1): непустая формулировка, валидный ключ,
/// сумма критериев соответствует максимуму, time limit корректен.
/// </summary>
public static class QuestionValidator
{
    public static IReadOnlyList<string> ValidateForPublish(QuestionDefinition d)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(d.Prompt))
            errors.Add("Формулировка вопроса не может быть пустой.");
        if (d.MaxPoints <= 0)
            errors.Add("MaxPoints должен быть больше нуля: тест с нулём баллов не публикуется (ТЗ §11).");
        if (d.ProposedTimeLimitSeconds is < 1 or > 3600)
            errors.Add("Лимит времени вопроса должен быть в диапазоне 1–3600 секунд.");

        switch (d.Type)
        {
            case QuestionType.SingleChoice:
                if (d.Options.Count < 2)
                    errors.Add("Одиночный выбор требует минимум двух вариантов.");
                if (d.CorrectOptionId is null || d.Options.All(o => o.OptionId != d.CorrectOptionId))
                    errors.Add("Ключ одиночного выбора отсутствует или не входит в варианты.");
                break;

            case QuestionType.MultipleChoice:
                if (d.Options.Count < 2)
                    errors.Add("Множественный выбор требует минимум двух вариантов.");
                if (d.CorrectOptionIds.Count == 0)
                    errors.Add("Множественный выбор требует хотя бы одного верного варианта.");
                var optionIds = d.Options.Select(o => o.OptionId).ToHashSet();
                if (!d.CorrectOptionIds.All(optionIds.Contains))
                    errors.Add("Ключи множественного выбора не входят в варианты.");
                break;

            case QuestionType.ShortText:
                if (d.AcceptedAnswers.Count == 0 && !d.SendUnknownShortAnswersToReview)
                    errors.Add("Короткий ответ требует допустимых значений или режима ручной проверки.");
                break;

            case QuestionType.Structured:
                if (d.StructuredFields.Count == 0)
                    errors.Add("Структурированный ответ требует хотя бы одно поле.");
                if (d.StructuredFields.Sum(f => f.Points) != d.MaxPoints)
                    errors.Add("Сумма баллов полей должна равняться максимуму вопроса.");
                break;

            case QuestionType.Matching:
                if (d.MatchingPairs.Count < 2)
                    errors.Add("Сопоставление требует минимум две пары.");
                // Равные веса пар: decimal сохраняет сумму весов равной максимуму (проверяется ниже глобально).
                if (d.MatchingPairs.Select(p => p.LeftId).ToHashSet().Count != d.MatchingPairs.Count
                    || d.MatchingPairs.Select(p => p.RightId).ToHashSet().Count != d.MatchingPairs.Count)
                    errors.Add("Идентификаторы пар должны быть уникальны.");
                break;

            case QuestionType.LetterAssembly:
                if (string.IsNullOrEmpty(d.TargetSequence))
                    errors.Add("Сборка термина требует итоговой последовательности.");
                if (d.LetterTiles.Count == 0)
                    errors.Add("Сборка термина требует плиток букв.");
                var assembled = new string(d.LetterTiles.Select(t => t.Letter).OrderBy(c => c).ToArray());
                var target = new string(d.TargetSequence!.OrderBy(c => c).ToArray());
                if (assembled != target)
                    errors.Add("Набор плиток не соответствует итоговой последовательности (буквы/количество).");
                if (d.LetterTiles.Select(t => t.TileId).ToHashSet().Count != d.LetterTiles.Count)
                    errors.Add("Повторяющиеся буквы должны иметь отдельные tileId (AC-12).");
                break;

            case QuestionType.Ordering:
                if (d.AcceptedOrders.Count == 0)
                    errors.Add("Порядок требует хотя бы одной допустимой последовательности.");
                var itemIds = d.OrderingItems.Select(i => i.OptionId).ToHashSet();
                foreach (var order in d.AcceptedOrders)
                {
                    if (order.Count != d.OrderingItems.Count || !order.All(itemIds.Contains))
                    {
                        errors.Add("Допустимая последовательность должна включать все элементы ровно один раз.");
                        break;
                    }
                }
                break;

            case QuestionType.GapFill:
                if (d.GapBlanks.Count == 0)
                    errors.Add("Заполнение пропусков требует хотя бы одного пропуска.");
                if (d.GapBlanks.Sum(b => b.Points) != d.MaxPoints)
                    errors.Add("Сумма баллов пропусков должна равняться максимуму вопроса.");
                if (d.GapBlanks.Any(b => b.AcceptedValues.Length == 0))
                    errors.Add("Каждый пропуск требует допустимых значений.");
                break;

            case QuestionType.LongText:
                if (!d.RequiresManualReview)
                    errors.Add("Развёрнутый ответ требует ручной проверки по рубрике.");
                break;

            default:
                errors.Add($"Неизвестный тип вопроса {d.Type}.");
                break;
        }

        // Сумма критериев рубрики обязана соответствовать максимуму (ТЗ §7.1, §11).
        var rubricSum = d.BuildRubric().Sum(c => c.MaxPoints);
        if (rubricSum != d.MaxPoints)
            errors.Add($"Сумма критериев рубрики ({rubricSum}) не равна максимуму ({d.MaxPoints}).");

        return errors;
    }
}
