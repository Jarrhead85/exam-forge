using System.Security.Cryptography;

namespace ExamForge.Domain.Access;

/// <summary>
/// Криптостойкий непоследовательный код приглашения (ТЗ §5.2).
/// 12 символов алфавита без похожих глифов (0/O, 1/I) = 60 бит энтропии.
/// </summary>
public static class InvitationCode
{
    public const int Length = 12;
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Generate()
    {
        Span<char> chars = stackalloc char[Length];
        Span<byte> random = stackalloc byte[Length];
        RandomNumberGenerator.Fill(random);
        for (var i = 0; i < Length; i++)
            chars[i] = Alphabet[random[i] % Alphabet.Length];
        return Format(chars);
    }

    /// <summary>Формат отображения XXXX-XXXX-XXXX. Дефисы не часть секрета.</summary>
    public static string Format(ReadOnlySpan<char> normalized)
        => normalized.Length != Length
            ? throw new ArgumentException($"Код состоит из {Length} символов.", nameof(normalized))
            : $"{new string(normalized[..4])}-{new string(normalized.Slice(4, 4))}-{new string(normalized.Slice(8, 4))}";

    /// <summary>Приведение ввода к каноническому виду: верхний регистр, без дефисов/пробелов.</summary>
    public static string Normalize(string input)
        => input.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal)
                .Trim().ToUpperInvariant();

    public static bool IsValidFormat(string normalized)
    {
        if (normalized.Length != Length)
            return false;
        foreach (var c in normalized)
            if (!Alphabet.Contains(c))
                return false;
        return true;
    }
}

/// <summary>
/// Хранение кода: только HMAC-SHA256 проверочное значение (ADR-002).
/// Код высокоэнтропийный — перебор ограничивается rate limit, не стоимостью хеша.
/// </summary>
public sealed class InvitationCodeHasher
{
    private readonly byte[] _hmacKey;

    public InvitationCodeHasher(byte[] hmacKey)
    {
        if (hmacKey.Length < 16)
            throw new ArgumentException("Ключ HMAC должен быть не короче 16 байт.", nameof(hmacKey));
        _hmacKey = hmacKey;
    }

    public string ComputeHash(string normalizedCode)
    {
        using var hmac = new HMACSHA256(_hmacKey);
        var hash = hmac.ComputeHash(System.Text.Encoding.ASCII.GetBytes(normalizedCode));
        return Convert.ToHexString(hash);
    }
}
