using System.Text.Json;
using ExamForge.Domain.Common;

namespace ExamForge.Domain.Questions;

public static class QuestionAnswerValidator
{
    public static QuestionAnswer Parse(QuestionDefinition definition, JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object || json.GetRawText().Length > 20_000)
            throw InvalidAnswer();

        var field = definition.Type switch
        {
            QuestionType.SingleChoice => "selectedOptionId",
            QuestionType.MultipleChoice => "selectedOptionIds",
            QuestionType.ShortText or QuestionType.LongText => "text",
            QuestionType.Matching => "pairSelections",
            QuestionType.LetterAssembly => "tileSequence",
            QuestionType.Ordering => "order",
            QuestionType.GapFill => "blankValues",
            QuestionType.Structured => "fieldValues",
            _ => throw InvalidAnswer(),
        };

        var hasField = false;
        foreach (var property in json.EnumerateObject())
        {
            if (hasField || !field.Equals(property.Name, StringComparison.OrdinalIgnoreCase))
                throw InvalidAnswer();
            hasField = true;
        }

        QuestionAnswer answer;
        try
        {
            answer = QuestionDefinitionJson.DeserializeAnswer(json.GetRawText());
        }
        catch (DomainException)
        {
            throw InvalidAnswer();
        }

        var valid = definition.Type switch
        {
            QuestionType.SingleChoice => answer.SelectedOptionId is null ||
                definition.Options.Any(option => option.OptionId == answer.SelectedOptionId),
            QuestionType.MultipleChoice => ValidSelection(answer.SelectedOptionIds, definition.Options.Select(option => option.OptionId)),
            QuestionType.ShortText or QuestionType.LongText => true,
            QuestionType.Matching => answer.PairSelections is null || answer.PairSelections.All(choice =>
                definition.MatchingPairs.Any(pair => pair.LeftId == choice.Key) &&
                definition.MatchingPairs.Any(pair => pair.RightId == choice.Value)),
            QuestionType.LetterAssembly => ValidSelection(answer.TileSequence, definition.LetterTiles.Select(tile => tile.TileId)),
            QuestionType.Ordering => ValidSelection(answer.Order, definition.OrderingItems.Select(item => item.OptionId)),
            QuestionType.GapFill => ValidValues(answer.BlankValues, definition.GapBlanks.Select(blank => blank.BlankId)),
            QuestionType.Structured => ValidValues(answer.FieldValues, definition.StructuredFields.Select(item => item.FieldId)),
            _ => false,
        };

        return valid ? answer : throw InvalidAnswer();
    }

    private static bool ValidSelection(IReadOnlyCollection<string>? selected, IEnumerable<string> allowed)
    {
        if (selected is null)
            return true;
        var ids = allowed.ToHashSet(StringComparer.Ordinal);
        return selected.Count == selected.Distinct(StringComparer.Ordinal).Count() && selected.All(ids.Contains);
    }

    private static bool ValidValues(IReadOnlyDictionary<string, string>? values, IEnumerable<string> allowed)
    {
        if (values is null)
            return true;
        var ids = allowed.ToHashSet(StringComparer.Ordinal);
        return values.All(entry => entry.Value is not null && ids.Contains(entry.Key));
    }

    private static DomainException InvalidAnswer()
        => new("Ответ не соответствует формату задания. Проверьте введённые данные и повторите сохранение.");
}
