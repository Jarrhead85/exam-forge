using System.Text.Json;

namespace ExamForge.Web.Client;

/// <summary>
/// Разбор собственной сохранённой ревизии ответа (P0-05): после F5/reconnect редактор
/// открывается с сохранённым ответом, а не пустым. Формат — то, что шлёт BuildAnswer
/// (camelCase от JsonSerializerDefaults.Web). Ключей/оценивания в данных нет; повреждённый
/// JSON и неизвестные значения не уводят редактор в некорректное состояние (поля пустые).
/// </summary>
public static class SavedAnswerJsonParser
{
    public sealed record Restored(
        string? Selected,
        IReadOnlyList<string> SelectedIds,
        string Text,
        IReadOnlyDictionary<string, string> Pairs,
        IReadOnlyList<string> TileSequence,
        IReadOnlyList<string> Order,
        IReadOnlyDictionary<string, string> Values);

    public static Restored? Parse(string type, string? savedAnswerJson)
    {
        if (string.IsNullOrEmpty(savedAnswerJson))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(savedAnswerJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            Restored? answer = type switch
            {
                "SingleChoice" => ParseSingleChoice(root),
                "MultipleChoice" => ParseMultipleChoice(root),
                "ShortText" or "LongText" => ParseText(root),
                "Matching" => ParseMatching(root),
                "LetterAssembly" => ParseLetterAssembly(root),
                "Ordering" => ParseOrdering(root),
                "GapFill" => ParseValueMap(root, "blankValues"),
                "Structured" => ParseValueMap(root, "fieldValues"),
                _ => null,
            };
            return answer;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Restored? ParseSingleChoice(JsonElement root)
        => new(
            root.TryGetProperty("selectedOptionId", out var s) ? s.GetString() : null,
            [], "", new Dictionary<string, string>(), [], [], new Dictionary<string, string>());

    private static Restored ParseMultipleChoice(JsonElement root)
    {
        var ids = new List<string>();
        if (root.TryGetProperty("selectedOptionIds", out var arr) && arr.ValueKind == JsonValueKind.Array)
            ids.AddRange(arr.EnumerateArray().Select(v => v.GetString() ?? ""));
        return new Restored(null, ids, "", new Dictionary<string, string>(), [], [], new Dictionary<string, string>());
    }

    private static Restored ParseText(JsonElement root)
        => new(null, [], root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
            new Dictionary<string, string>(), [], [], new Dictionary<string, string>());

    private static Restored ParseMatching(JsonElement root)
    {
        var pairs = new Dictionary<string, string>();
        if (root.TryGetProperty("pairSelections", out var obj) && obj.ValueKind == JsonValueKind.Object)
            foreach (var pair in obj.EnumerateObject())
                pairs[pair.Name] = pair.Value.GetString() ?? "";
        return new Restored(null, [], "", pairs, [], [], new Dictionary<string, string>());
    }

    private static Restored ParseLetterAssembly(JsonElement root)
    {
        var sequence = new List<string>();
        if (root.TryGetProperty("tileSequence", out var arr) && arr.ValueKind == JsonValueKind.Array)
            sequence.AddRange(arr.EnumerateArray().Select(v => v.GetString() ?? ""));
        return new Restored(null, [], "", new Dictionary<string, string>(), sequence, [], new Dictionary<string, string>());
    }

    private static Restored ParseOrdering(JsonElement root)
    {
        var order = new List<string>();
        if (root.TryGetProperty("order", out var arr) && arr.ValueKind == JsonValueKind.Array)
            order.AddRange(arr.EnumerateArray().Select(v => v.GetString() ?? ""));
        return new Restored(null, [], "", new Dictionary<string, string>(), [], order, new Dictionary<string, string>());
    }

    private static Restored ParseValueMap(JsonElement root, string property)
    {
        var values = new Dictionary<string, string>();
        if (root.TryGetProperty(property, out var obj) && obj.ValueKind == JsonValueKind.Object)
            foreach (var v in obj.EnumerateObject())
                values[v.Name] = v.Value.ToString();
        return new Restored(null, [], "", new Dictionary<string, string>(), [], [], values);
    }
}
