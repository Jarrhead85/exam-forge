using System.Security.Cryptography;
using ExamForge.Domain.Common;

namespace ExamForge.Domain.Questions;

public enum ShuffleResult
{
    NotShuffled = 1,
    Shuffled = 2,
}

/// <summary>
/// Перемешивание вариантов ответа по stable ID c seed (ТЗ §8): ключ (CorrectOptionId)
/// не меняется — проверяется идентификатор, а не позиция. Порядок детерминирован seed.
/// </summary>
public static class OptionShuffler
{
    public static IReadOnlyList<T> Shuffle<T>(IReadOnlyList<T> source, string seed)
    {
        Guard.Required(seed, "Seed перемешивания", 200);
        if (source.Count < 2)
            return source;

        var indices = Enumerable.Range(0, source.Count).ToArray();
        var random = CreateSeededRandom(seed);
        for (var i = indices.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }
        return [.. indices.Select(i => source[i])];
    }

    private static Random CreateSeededRandom(string seed)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        return new Random(BitConverter.ToInt32(hash, 0));
    }
}

/// <summary>Строка отбора Blueprint (ТЗ §2, §8): тип, тема, количество, баллы.</summary>
public sealed record BlueprintLine(
    QuestionType? QuestionType,
    Guid? TopicId,
    string? Tag,
    int Count,
    decimal PointsEach);

/// <summary>Кандидат банка для подбора.</summary>
public sealed record BankCandidate(
    Guid QuestionVersionId,
    QuestionType Type,
    Guid? TopicId,
    IReadOnlyCollection<string> Tags,
    decimal MaxPoints);

public sealed record BlueprintPickResult
{
    public required IReadOnlyList<Guid> Picked { get; init; }
    /// <summary>Явные предупреждения о недостаточном покрытии банка; без молчаливых дублей (AC-14).</summary>
    public required IReadOnlyList<string> Warnings { get; init; }
    public bool HasWarnings => Warnings.Count > 0;
}

/// <summary>Подбор по Blueprint без дублирования вопросов внутри варианта (ТЗ §8).</summary>
public static class BankPicker
{
    public static BlueprintPickResult Pick(IReadOnlyList<BankCandidate> bank, IReadOnlyList<BlueprintLine> blueprint, string seed)
    {
        var warnings = new List<string>();
        var picked = new List<Guid>();
        var used = new HashSet<Guid>();
        var random = OptionShuffler.Shuffle(bank.Select((b, i) => (b, i)).ToList(), seed)
            .Select(t => t.b).ToList();

        foreach (var line in blueprint)
        {
            var matching = random
                .Where(c => !used.Contains(c.QuestionVersionId))
                .Where(c => line.QuestionType is null || c.Type == line.QuestionType)
                .Where(c => line.TopicId is null || c.TopicId == line.TopicId)
                .Where(c => line.Tag is null || c.Tags.Contains(line.Tag))
                .ToList();

            if (matching.Count < line.Count)
                warnings.Add(
                    $"Запрошено {line.Count} заданий (тип {line.QuestionType?.ToString() ?? "любой"}, тема {line.TopicId}), банк даёт {matching.Count}. Вариант неполный.");

            foreach (var candidate in matching.Take(line.Count))
            {
                picked.Add(candidate.QuestionVersionId);
                used.Add(candidate.QuestionVersionId);
            }
        }

        return new BlueprintPickResult { Picked = picked, Warnings = warnings };
    }
}
