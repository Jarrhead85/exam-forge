using ExamForge.Domain.Common;
using ExamForge.Domain.Grading;

namespace ExamForge.Domain.Questions;

public readonly record struct CriterionGrade(string CriterionId, decimal EarnedPoints, decimal MaxPoints, string Reason);

public sealed record GradingOutcome
{
    public required decimal EarnedPoints { get; init; }
    public required decimal MaxPoints { get; init; }
    public required IReadOnlyList<CriterionGrade> Criteria { get; init; }
    /// <summary>Ответ требует ручной проверки; не проверено ≠ 0 (ТЗ §11).</summary>
    public bool NeedsManualReview { get; init; }

    public decimal RawPercent => MaxPoints > 0 ? EarnedPoints / MaxPoints * 100m : 0m;
}

/// <summary>
/// Автоматическое начисление баллов по типу вопроса. Все начисления в [0; MaxPoints];
/// отрицательных баллов и скрытых штрафов нет (ТЗ §11).
/// </summary>
public static class AnswerGrader
{
    public static GradingOutcome Grade(QuestionDefinition definition, QuestionAnswer? answer)
    {
        if (definition.MaxPoints <= 0)
            throw new DomainException("MaxPoints должен быть больше нуля.");

        return definition.Type switch
        {
            QuestionType.SingleChoice => GradeSingleChoice(definition, answer),
            QuestionType.MultipleChoice => GradeMultipleChoice(definition, answer),
            QuestionType.ShortText => GradeShortText(definition, answer),
            QuestionType.Structured => GradeStructured(definition, answer),
            QuestionType.Matching => GradeMatching(definition, answer),
            QuestionType.LetterAssembly => GradeLetterAssembly(definition, answer),
            QuestionType.Ordering => GradeOrdering(definition, answer),
            QuestionType.GapFill => GradeGapFill(definition, answer),
            QuestionType.LongText => new GradingOutcome
            {
                EarnedPoints = 0m,
                MaxPoints = definition.MaxPoints,
                Criteria = [new("answer", 0m, definition.MaxPoints, "Требуется ручная проверка по рубрике")],
                NeedsManualReview = true,
            },
            _ => throw new DomainException($"Неизвестный тип вопроса {definition.Type}."),
        };
    }

    private static GradingOutcome Outcome(QuestionDefinition d, IReadOnlyList<CriterionGrade> criteria, bool needsReview = false)
    {
        var earned = criteria.Sum(c => c.EarnedPoints);
        if (earned < 0m || earned > d.MaxPoints)
            throw new DomainException("Начисленные баллы вышли за пределы [0; MaxPoints].");
        return new GradingOutcome
        {
            EarnedPoints = earned,
            MaxPoints = d.MaxPoints,
            Criteria = criteria,
            NeedsManualReview = needsReview,
        };
    }

    private static GradingOutcome GradeSingleChoice(QuestionDefinition d, QuestionAnswer? a)
    {
        var correct = a?.SelectedOptionId is { } selected && selected == d.CorrectOptionId;
        return Outcome(d, [new("answer", correct ? d.MaxPoints : 0m, d.MaxPoints,
            correct ? "Выбран верный вариант" : "Выбран неверный вариант или ответ отсутствует")]);
    }

    private static GradingOutcome GradeMultipleChoice(QuestionDefinition d, QuestionAnswer? a)
    {
        var selected = (a?.SelectedOptionIds ?? []).ToHashSet();
        var correctSet = d.CorrectOptionIds.ToHashSet();

        if (d.MultipleChoiceScoring == MultipleChoiceScoring.AllOrNothing)
        {
            var exact = selected.SetEquals(correctSet);
            return Outcome(d, [new("answer", exact ? d.MaxPoints : 0m, d.MaxPoints,
                exact ? "Набор отметок совпадает с ключом" : "Набор отметок не совпадает с ключом")]);
        }

        // PartialPerCorrect: баллы за каждый корректно отмеченный верный вариант; ложные отметки не вычитают.
        var selectedCorrect = selected.Count(correctSet.Contains);
        var totalCorrect = correctSet.Count;
        var earned = totalCorrect > 0 ? decimal.Round(d.MaxPoints * selectedCorrect / totalCorrect, 3) : 0m;
        return Outcome(d, [new("answer", earned, d.MaxPoints,
            $"Верных отмечено {selectedCorrect} из {totalCorrect}")]);
    }

    private static GradingOutcome GradeShortText(QuestionDefinition d, QuestionAnswer? a)
    {
        if (a?.Text is null or { Length: 0 })
        {
            return d.SendUnknownShortAnswersToReview
                ? Outcome(d, [new("answer", 0m, d.MaxPoints, "Пустой ответ отправлен на ручную проверку")], needsReview: true)
                : Outcome(d, [new("answer", 0m, d.MaxPoints, "Ответ отсутствует")]);
        }

        var normalized = ShortAnswerNormalizer.Normalize(a.Text, d.Normalization);
        var accepted = d.AcceptedAnswers
            .Select(x => ShortAnswerNormalizer.Normalize(x.Value, d.Normalization))
            .ToHashSet();

        if (accepted.Contains(normalized))
            return Outcome(d, [new("answer", d.MaxPoints, d.MaxPoints, "Ответ совпал с допустимым значением")]);

        if (d.SendUnknownShortAnswersToReview)
            return Outcome(d, [new("answer", 0m, d.MaxPoints, "Неизвестный ответ отправлен на ручную проверку")], needsReview: true);

        return Outcome(d, [new("answer", 0m, d.MaxPoints, "Ответ не совпал с допустимыми значениями")]);
    }

    private static GradingOutcome GradeStructured(QuestionDefinition d, QuestionAnswer? a)
    {
        var values = a?.FieldValues ?? new Dictionary<string, string>();
        var criteria = new List<CriterionGrade>(d.StructuredFields.Count);
        foreach (var field in d.StructuredFields)
        {
            var given = values.TryGetValue(field.FieldId, out var v) ? v : null;
            var ok = given is not null
                && ShortAnswerNormalizer.Normalize(given, d.Normalization)
                   == ShortAnswerNormalizer.Normalize(field.CorrectValue, d.Normalization);
            criteria.Add(new(field.FieldId, ok ? field.Points : 0m, field.Points,
                ok ? $"Поле «{field.Label}» верно" : $"Поле «{field.Label}» неверно"));
        }
        return Outcome(d, criteria);
    }

    private static GradingOutcome GradeMatching(QuestionDefinition d, QuestionAnswer? a)
    {
        // Балл за каждую правильную пару; максимум равен сумме пар; полное совпадение не требуется (ТЗ §7.2, AC-02).
        var selections = a?.PairSelections ?? new Dictionary<string, string>();
        var perPair = d.MaxPoints / d.MatchingPairs.Count;
        var criteria = new List<CriterionGrade>(d.MatchingPairs.Count);
        foreach (var pair in d.MatchingPairs)
        {
            var chosen = selections.TryGetValue(pair.LeftId, out var right) ? right : null;
            var ok = chosen == pair.RightId;
            criteria.Add(new($"pair:{pair.LeftId}", ok ? perPair : 0m, perPair,
                ok ? $"Пара «{pair.LeftText}» верна" : $"Пара «{pair.LeftText}» неверна"));
        }
        return Outcome(d, criteria);
    }

    private static GradingOutcome GradeLetterAssembly(QuestionDefinition d, QuestionAnswer? a)
    {
        // Итоговая последовательность из tileId; повторяющиеся буквы — отдельные плитки (AC-12).
        var sequence = a?.TileSequence ?? [];
        var tileById = d.LetterTiles.ToDictionary(t => t.TileId, t => t.Letter);
        var assembled = new string([.. sequence.Where(tileById.ContainsKey).Select(t => tileById[t])]);
        var ok = assembled.Length == sequence.Count && assembled == d.TargetSequence;
        return Outcome(d, [new("answer", ok ? d.MaxPoints : 0m, d.MaxPoints,
            ok ? "Последовательность собрана верно" : "Последовательность не совпала")]);
    }

    private static GradingOutcome GradeOrdering(QuestionDefinition d, QuestionAnswer? a)
    {
        var given = a?.Order ?? [];
        var canonical = d.AcceptedOrders[0];

        if (d.OrderingScoring == OrderingScoring.Exact)
        {
            var ok = d.AcceptedOrders.Any(accepted => given.SequenceEqual(accepted));
            return Outcome(d, [new("answer", ok ? d.MaxPoints : 0m, d.MaxPoints,
                ok ? "Последовательность совпала с допустимой" : "Последовательность неверна")]);
        }

        // PerPosition: балл за каждую верную позицию относительно канонической последовательности.
        var perPosition = d.MaxPoints / canonical.Count;
        var correctPositions = 0;
        for (var i = 0; i < Math.Min(given.Count, canonical.Count); i++)
            if (given[i] == canonical[i])
                correctPositions++;
        var earned = correctPositions * perPosition;
        return Outcome(d, [new("answer", earned, d.MaxPoints,
            $"Верных позиций {correctPositions} из {canonical.Count}")]);
    }

    private static GradingOutcome GradeGapFill(QuestionDefinition d, QuestionAnswer? a)
    {
        var values = a?.BlankValues ?? new Dictionary<string, string>();
        var criteria = new List<CriterionGrade>(d.GapBlanks.Count);
        foreach (var blank in d.GapBlanks)
        {
            var given = values.TryGetValue(blank.BlankId, out var v) ? v : null;
            var normalizedGiven = given is null ? null : ShortAnswerNormalizer.Normalize(given, d.Normalization);
            var ok = normalizedGiven is not null && blank.AcceptedValues
                .Select(x => ShortAnswerNormalizer.Normalize(x, d.Normalization))
                .Contains(normalizedGiven);
            criteria.Add(new(blank.BlankId, ok ? blank.Points : 0m, blank.Points,
                ok ? $"Пропуск {blank.BlankId} верен" : $"Пропуск {blank.BlankId} неверен"));
        }
        return Outcome(d, criteria);
    }
}
