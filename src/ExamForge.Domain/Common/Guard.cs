namespace ExamForge.Domain.Common;

public static class Guard
{
    public static string Required(string? value, string paramName, int maxLength = 256)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException($"«{paramName}» обязательно для заполнения.");
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            throw new DomainException($"«{paramName}» длиннее {maxLength} символов.");
        return trimmed;
    }

    public static decimal Positive(decimal value, string paramName)
    {
        if (value <= 0m)
            throw new DomainException($"«{paramName}» должно быть больше нуля.");
        return value;
    }

    public static void InRange(decimal value, decimal minInclusive, decimal maxInclusive, string paramName)
    {
        if (value < minInclusive || value > maxInclusive)
            throw new DomainException($"«{paramName}» должно быть в диапазоне [{minInclusive}; {maxInclusive}].");
    }
}
