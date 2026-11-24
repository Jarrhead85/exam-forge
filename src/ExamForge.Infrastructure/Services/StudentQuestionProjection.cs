using ExamForge.Application.StudentFlow;
using ExamForge.Domain.Questions;
using ExamForge.Domain.Testing;

namespace ExamForge.Infrastructure.Services;

public static class StudentQuestionProjection
{
    public static StudentQuestionDto Create(
        Guid sessionId, Guid versionId, int orderIndex, QuestionDefinition definition,
        int? proposedTimeLimitSeconds, Variant? variant, string? savedAnswerJson = null)
    {
        IReadOnlyList<StudentOptionDto>? options = null;
        IReadOnlyList<StudentTileDto>? tiles = null;
        IReadOnlyList<StudentOptionDto>? left = null, right = null, items = null, fields = null;
        IReadOnlyList<string>? blanks = null;

        switch (definition.Type)
        {
            case QuestionType.SingleChoice:
            case QuestionType.MultipleChoice:
                options = OrderOptions(variant, versionId,
                    [.. definition.Options.Select(o => new StudentOptionDto(o.OptionId, o.Text))], o => o.Id);
                break;
            case QuestionType.Matching:
                left = [.. definition.MatchingPairs.Select(p => new StudentOptionDto(p.LeftId, p.LeftText))];
                right = OrderOptions(variant, versionId,
                    [.. definition.MatchingPairs.Select(p => new StudentOptionDto(p.RightId, p.RightText))], o => o.Id);
                break;
            case QuestionType.LetterAssembly:
                tiles = OrderTiles(variant, versionId,
                    [.. definition.LetterTiles.Select(t => new StudentTileDto(t.TileId, t.Letter))]);
                break;
            case QuestionType.Ordering:
                items = [.. definition.OrderingItems.Select(o => new StudentOptionDto(o.OptionId, o.Text))];
                break;
            case QuestionType.Structured:
                fields = [.. definition.StructuredFields.Select(f => new StudentOptionDto(f.FieldId, f.Label))];
                break;
            case QuestionType.GapFill:
                blanks = [.. definition.GapBlanks.Select(b => b.BlankId)];
                break;
        }

        return new StudentQuestionDto(
            sessionId, versionId, orderIndex, definition.Type.ToString(), definition.Prompt, definition.Instruction,
            proposedTimeLimitSeconds, options, tiles, left, right, items, fields, blanks, savedAnswerJson);
    }

    private static IReadOnlyList<StudentOptionDto> OrderOptions(
        Domain.Testing.Variant? variant, Guid questionVersionId,
        IReadOnlyList<StudentOptionDto> source, Func<StudentOptionDto, string> idOf)
        => variant is null ? source : Reordered(variant, questionVersionId, source, idOf);

    private static IReadOnlyList<StudentTileDto> OrderTiles(
        Domain.Testing.Variant? variant, Guid questionVersionId, IReadOnlyList<StudentTileDto> source)
        => variant is null ? source : Reordered(variant, questionVersionId, source, t => t.TileId);

    private static IReadOnlyList<T> Reordered<T>(
        Domain.Testing.Variant variant, Guid questionVersionId, IReadOnlyList<T> source, Func<T, string> idOf)
    {
        var byId = source.ToDictionary(idOf);
        var ordered = variant.OrderFor(questionVersionId, [.. byId.Keys]);
        return ordered.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }
}
