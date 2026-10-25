using ExamForge.Domain.Common;
using ExamForge.Domain.Dictionary;

namespace ExamForge.Domain.Questions;

/// <summary>
/// Предметные шаблоны медицинской латыни (ТЗ §7.3): тип универсальный, шаблон задаёт
/// структуру, метаданные и рубрику. Примеры — учебные fixtures, подтверждает преподаватель.
/// </summary>
public static class MedicalTemplates
{
    public sealed record TemplatePreset(string Code, string Title, QuestionType Type, string Prompt, string? Instruction);

    public static readonly IReadOnlyList<TemplatePreset> All =
    [
        new("translation-choice", "Выбор перевода", QuestionType.SingleChoice,
            "Выберите перевод термина", "Отметьте один верный вариант"),
        new("odd-word", "Лишнее слово", QuestionType.SingleChoice,
            "Найдите семантически/грамматически лишнее слово", "Три слова связаны, одно — нет"),
        new("term-assembly", "Сборка термина", QuestionType.LetterAssembly,
            "Соберите термин из букв", "Нажимайте буквы по порядку; клик по клетке возвращает букву"),
        new("ru-lat", "Русско-латинский перевод", QuestionType.ShortText,
            "Переведите на латинский язык", null),
        new("lat-ru", "Латинско-русский перевод", QuestionType.ShortText,
            "Переведите на русский язык", null),
        new("dictionary-form", "Словарная форма", QuestionType.Structured,
            "Запишите словарную форму термина", "Укажите форму и род"),
        new("matching", "Сопоставление", QuestionType.Matching,
            "Соотнесите термины и переводы", "Кликните термин слева, затем перевод справа"),
        new("letters-gap", "Заполнение букв/сочетаний", QuestionType.GapFill,
            "Заполните пропуски в терминах", null),
        new("word-order", "Порядок слов", QuestionType.Ordering,
            "Расставьте слова в правильном порядке", "Используйте кнопки вверх/вниз"),
        new("prescription", "Учебная рецептурная конструкция", QuestionType.LongText,
            "Напишите учебную рецептурную конструкцию по заданной структуре",
            "Проверяет язык и структуру; не является назначением лечения"),
    ];
}

/// <summary>
/// Генерация черновиков заданий из словаря (ТЗ §6): ключи и отвлекающие варианты подтверждает
/// преподаватель; среди «неверных» нет синонимичных правильных ответов.
/// </summary>
public static class DictionaryDraftFactory
{
    /// <summary>Черновик «выбор перевода»: правильный перевод леммы + отвлекающие переводы других записей.</summary>
    public static QuestionDefinition BuildTranslationChoice(
        DictionaryEntry entry,
        IReadOnlyList<string> distractorTranslations,
        int timeLimitSeconds = 40)
    {
        if (entry.Status != DictionaryEntryStatus.Confirmed)
            throw new DomainException("Черновики создаются из подтверждённых записей словаря (ТЗ §6).");
        var correct = entry.Translations.FirstOrDefault() ?? entry.Lemma;
        var distractors = distractorTranslations
            .Where(d => !string.Equals(d.Trim(), correct.Trim(), StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .ToList();
        if (distractors.Count < 2)
            throw new DomainException("Недостаточно отвлекающих вариантов в банке словаря.");

        return new QuestionDefinition
        {
            Type = QuestionType.SingleChoice,
            Prompt = $"Выберите перевод термина «{entry.Lemma}»",
            Instruction = "Отметьте один верный вариант",
            Options = [new("c", correct), .. distractors.Select((d, i) => new OptionDef($"d{i}", d))],
            CorrectOptionId = "c",
            MaxPoints = 1m,
            ProposedTimeLimitSeconds = timeLimitSeconds,
        };
    }

    /// <summary>Плитки для сборки термина: повторяющиеся буквы получают уникальные tileId (AC-12).</summary>
    public static IReadOnlyList<LetterTileDef> BuildTiles(string word)
    {
        Guard.Required(word, "Термин", 100);
        var counts = new Dictionary<char, int>();
        var tiles = new List<LetterTileDef>(word.Length);
        foreach (var letter in word)
        {
            counts.TryGetValue(letter, out var n);
            counts[letter] = n + 1;
            tiles.Add(new LetterTileDef($"{char.ToLowerInvariant(letter)}{n + 1}", letter));
        }
        return tiles;
    }
}
