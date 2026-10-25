using System.Globalization;
using System.Text;

namespace ExamForge.Domain.Questions;

/// <summary>Нормализация короткого ответа строго по настройкам задания (ТЗ §11).</summary>
public static class ShortAnswerNormalizer
{
    public static string Normalize(string input, ShortTextNormalization settings)
    {
        var result = input ?? string.Empty;
        result = result.Normalize(NormalizationForm.FormC);

        if (settings.IgnoreDiacritics)
        {
            var sb = new StringBuilder(result.Length);
            foreach (var c in result.Normalize(NormalizationForm.FormD))
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            result = sb.ToString().Normalize(NormalizationForm.FormC);
        }

        if (settings.IgnorePunctuation)
        {
            var sb = new StringBuilder(result.Length);
            foreach (var c in result)
                if (!char.IsPunctuation(c))
                    sb.Append(c);
            result = sb.ToString();
        }

        if (settings.IgnoreCase)
            result = result.ToUpperInvariant();

        if (settings.CollapseWhitespace)
            result = string.Join(' ', result.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (settings.TrimWhitespace)
            result = result.Trim();

        return result;
    }
}
