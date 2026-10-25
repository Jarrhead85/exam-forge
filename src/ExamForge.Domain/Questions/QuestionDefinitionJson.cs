using System.Text.Json;
using System.Text.Json.Serialization;
using ExamForge.Domain.Common;

namespace ExamForge.Domain.Questions;

/// <summary>
/// Сериализация определения вопроса в JSONB со строковыми enum и schemaVersion (ТЗ §15).
/// </summary>
public static class QuestionDefinitionJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(QuestionDefinition definition) => JsonSerializer.Serialize(definition, Options);

    public static QuestionDefinition Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<QuestionDefinition>(json, Options)
                ?? throw new DomainException("Определение вопроса пустое.");
        }
        catch (JsonException ex)
        {
            throw new DomainException($"Определение вопроса не читается: {ex.Message}");
        }
    }

    public static QuestionAnswer DeserializeAnswer(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<QuestionAnswer>(json, Options) ?? new QuestionAnswer();
        }
        catch (JsonException ex)
        {
            throw new DomainException($"Ответ не читается: {ex.Message}");
        }
    }
}
