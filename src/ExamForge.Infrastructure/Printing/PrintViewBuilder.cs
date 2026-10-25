using ExamForge.Application.Printing;
using ExamForge.Application.StudentFlow;
using ExamForge.Domain.Questions;
using ExamForge.Domain.Testing;
using Microsoft.EntityFrameworkCore;
using ExamForge.Infrastructure.Persistence;

namespace ExamForge.Infrastructure.Printing;

/// <summary>
/// Построение печатных представлений из зафиксированного варианта (ТЗ §13).
/// Ключ строго соответствует порядку варианта: маркер правильного ответа вычисляется
/// по позиции optionId в фактическом порядке варианта (AC-16).
/// </summary>
public sealed class PrintViewBuilder(ExamForgeDbContext db)
{
    private static readonly string[] Markers =
    [
        "А", "Б", "В", "Г", "Д", "Е", "Ж", "З", "И", "К", "Л", "М", "Н", "О", "П", "Р", "С", "Т", "У", "Ф", "Х", "Ц", "Ч", "Ш", "Э", "Ю", "Я",
    ];

    public async Task<(Variant Variant, TestVersion TestVersion, Dictionary<Guid, QuestionDefinition> Definitions)> LoadAsync(Guid variantId, CancellationToken ct)
    {
        var variant = await db.Variants.AsNoTracking().SingleOrDefaultAsync(v => v.Id == variantId, ct)
            ?? throw new StudentFlowException(404, "not_found", "Вариант не найден.");
        var testVersion = await db.TestVersions.AsNoTracking().SingleAsync(v => v.Id == variant.TestVersionId, ct);
        var definitions = await db.QuestionVersions.AsNoTracking()
            .Where(v => testVersion.QuestionVersionIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, v => QuestionDefinitionJson.Deserialize(v.DefinitionJson), ct);
        return (variant, testVersion, definitions);
    }

    public StudentPrintSheetDto BuildStudentSheet(
        Variant variant, TestVersion testVersion, Dictionary<Guid, QuestionDefinition> definitions)
    {
        var questions = new List<StudentPrintQuestionDto>();
        foreach (var (questionVersionId, index) in variant.QuestionOrder.Select((id, i) => (id, i)))
        {
            var definition = definitions[questionVersionId];
            questions.Add(new StudentPrintQuestionDto(
                index + 1, definition.Type.ToString(), definition.Prompt, definition.Instruction,
                definition.MaxPoints,
                Options(variant, questionVersionId, definition),
                Left(variant, questionVersionId, definition),
                Right(variant, questionVersionId, definition),
                Tiles(variant, questionVersionId, definition),
                definition.LetterTiles.Count,
                Items(variant, questionVersionId, definition),
                [.. definition.GapBlanks.Select(b => b.BlankId)],
                [.. definition.StructuredFields.Select(f => new PrintFieldDto(f.Label, ""))]));
        }
        return new StudentPrintSheetDto(testVersion.Title, testVersion.Id, variant.VariantNumber, questions);
    }

    public PrintSheetDto BuildTeacherKey(
        Variant variant, TestVersion testVersion, Dictionary<Guid, QuestionDefinition> definitions)
    {
        var questions = new List<PrintQuestionDto>();
        foreach (var (questionVersionId, index) in variant.QuestionOrder.Select((id, i) => (id, i)))
        {
            var definition = definitions[questionVersionId];
            var options = Options(variant, questionVersionId, definition);

            string? keyMarkers = null, keyPairs = null, keySequence = null, keyOrders = null, keyValues = null, keyNote = null;
            switch (definition.Type)
            {
                case QuestionType.SingleChoice:
                {
                    var markers = MarkerOf(variant, questionVersionId, definition,
                        [.. definition.Options.Select(o => o.OptionId)], definition.CorrectOptionId!);
                    keyMarkers = $"Правильный ответ: {markers}";
                    break;
                }
                case QuestionType.MultipleChoice:
                {
                    var markers = definition.CorrectOptionIds
                        .Select(id => MarkerOf(variant, questionVersionId, definition,
                            [.. definition.Options.Select(o => o.OptionId)], id))
                        .OrderBy(m => m);
                    keyMarkers = $"Правильные ответы: {string.Join(", ", markers)} · стратегия: {definition.MultipleChoiceScoring}";
                    break;
                }
                case QuestionType.Matching:
                {
                    // Пары по порядку варианта левой колонки; вес каждой пары (ТЗ §7.2).
                    var perPair = definition.MaxPoints / definition.MatchingPairs.Count;
                    keyPairs = string.Join("; ", definition.MatchingPairs
                        .Select(p => $"{p.LeftText} → {p.RightText}"));
                    keyNote = $"По 1 баллу за пару ({perPair:0.###} × {definition.MatchingPairs.Count})";
                    break;
                }
                case QuestionType.LetterAssembly:
                    keySequence = $"Ответ: {definition.TargetSequence}";
                    break;
                case QuestionType.Ordering:
                {
                    // Маркеры элементов — по порядку определения (как напечатаны на листе).
                    var idToMarker = definition.OrderingItems
                        .Select((item, i) => (item.OptionId, Marker: Marker(i)))
                        .ToDictionary(x => x.OptionId, x => x.Marker);
                    keyOrders = string.Join(" / ", definition.AcceptedOrders
                        .Select(o => string.Join("–", o.Select(id => idToMarker.TryGetValue(id, out var m) ? m : "?"))));
                    if (definition.OrderingScoring == OrderingScoring.PerPosition)
                        keyNote = "Оценивание по позициям";
                    break;
                }
                case QuestionType.ShortText:
                    keyValues = $"Допустимо: {string.Join("; ", definition.AcceptedAnswers.Select(a => a.Value))}"
                        + (definition.SendUnknownShortAnswersToReview ? " · незнакомые ответы — на ручную проверку" : "");
                    break;
                case QuestionType.GapFill:
                    keyValues = string.Join("; ", definition.GapBlanks
                        .Select(b => $"{b.BlankId}: {string.Join("/", b.AcceptedValues)} ({b.Points:0.###} б.)"));
                    break;
                case QuestionType.Structured:
                    keyValues = string.Join("; ", definition.StructuredFields
                        .Select(f => $"{f.Label}: {f.CorrectValue} ({f.Points:0.###} б.)"));
                    break;
                case QuestionType.LongText:
                    keyNote = "Ручная проверка по рубрике";
                    break;
            }

            questions.Add(new PrintQuestionDto(
                index + 1, definition.Type.ToString(), definition.Prompt, definition.Instruction,
                definition.MaxPoints,
                options,
                Left(variant, questionVersionId, definition),
                Right(variant, questionVersionId, definition),
                Tiles(variant, questionVersionId, definition),
                definition.LetterTiles.Count,
                Items(variant, questionVersionId, definition),
                [.. definition.GapBlanks.Select(b => b.BlankId)],
                [.. definition.StructuredFields.Select(f => new PrintFieldDto(f.Label, ""))],
                keyMarkers, keyPairs, keySequence, keyOrders, keyValues, keyNote));
        }
        return new PrintSheetDto(testVersion.Title, testVersion.Id, variant.VariantNumber, "teacher-key", questions);
    }

    // ---------- порядок по варианту ----------

    private static IReadOnlyList<PrintOptionDto> Options(Variant variant, Guid qvId, QuestionDefinition d)
    {
        var ordered = variant.OrderFor(qvId, [.. d.Options.Select(o => o.OptionId)]);
        return [.. ordered.Select((id, i) => new PrintOptionDto(Marker(i), d.Options.First(o => o.OptionId == id).Text))];
    }

    private static IReadOnlyList<PrintOptionDto> Right(Variant variant, Guid qvId, QuestionDefinition d)
    {
        if (d.Type != QuestionType.Matching)
            return [];
        var ordered = variant.OrderFor(qvId, [.. d.MatchingPairs.Select(p => p.RightId)]);
        return [.. ordered.Select((id, i) => new PrintOptionDto(Marker(i), d.MatchingPairs.First(p => p.RightId == id).RightText))];
    }

    private static IReadOnlyList<PrintOptionDto> Left(Variant variant, Guid qvId, QuestionDefinition d)
    {
        if (d.Type != QuestionType.Matching)
            return [];
        // Левая колонка сохраняет порядок определения: пары «термин → перевод» читаются сверху вниз.
        return [.. d.MatchingPairs.Select((p, i) => new PrintOptionDto($"{i + 1}", p.LeftText))];
    }

    private static IReadOnlyList<string> Tiles(Variant variant, Guid qvId, QuestionDefinition d)
    {
        if (d.Type != QuestionType.LetterAssembly)
            return [];
        var ordered = variant.OrderFor(qvId, [.. d.LetterTiles.Select(t => t.TileId)]);
        return [.. ordered.Select(id => d.LetterTiles.First(t => t.TileId == id).Letter.ToString())];
    }

    private static IReadOnlyList<PrintOptionDto> Items(Variant variant, Guid qvId, QuestionDefinition d)
    {
        if (d.Type != QuestionType.Ordering)
            return [];
        return [.. d.OrderingItems.Select((item, i) => new PrintOptionDto(Marker(i), item.Text))];
    }

    private static string Marker(int index)
        => index < Markers.Length ? Markers[index] : (index + 1).ToString();

    private static string MarkerOf(Variant variant, Guid qvId, QuestionDefinition d, List<string> ids, string targetId)
    {
        var ordered = variant.OrderFor(qvId, ids).ToList();
        return Marker(ordered.IndexOf(targetId));
    }
}
