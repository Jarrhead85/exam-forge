using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ExamForge.IntegrationTests;

/// <summary>
/// P0-09: production не может стартовать без явного ExamForge:InvitationHmacKey
/// (встроенный fallback допустим только вне production), ключ короче 32 байт также
/// отклоняется. Проверка конфигурации выполняется fail-fast при старте приложения.
/// </summary>
public sealed class HmacKeyConfigTests
{
    [Fact]
    public async Task Production_WithoutKey_DoesNotStart()
    {
        await AssertStartupFailsWithConfigError(_ => { }, hmacKey: null);
    }

    [Fact]
    public async Task Production_WithShortKey_DoesNotStart()
    {
        // 16 байт (32 hex) — меньше требуемых 32 байт.
        await AssertStartupFailsWithConfigError(_ => { }, hmacKey: "00112233445566778899AABBCCDDEEFF");
    }

    [Fact]
    public void NonProduction_WithoutKey_Starts()
    {
        // Вне production явный dev-маркированный fallback допустим.
        using var factory = new StartupFactory(
            configure: _ => { },
            hmacKey: null,
            environment: "Development");
        var client = factory.CreateClient();
        Assert.NotNull(client);
        client.Dispose();
    }

    private static Task AssertStartupFailsWithConfigError(
        Action<IWebHostBuilder> configure, string? hmacKey)
    {
        using var factory = new StartupFactory(configure, hmacKey, "Production");
        var exception = Record.Exception(() => factory.CreateClient());
        Assert.NotNull(exception);
        var messages = new List<string>();
        for (var e = exception; e is not null; e = e.InnerException)
            messages.Add(e.Message);
        Assert.Contains(messages, m => m.Contains("InvitationHmacKey"));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Отдельная фабрика: Environment задан явно (Production/Development по сценарию),
    /// никакой базы не трогает — HMAC-проверка выполнится раньше hosted-сервисов.
    /// </summary>
    private sealed class StartupFactory(
        Action<IWebHostBuilder> configure, string? hmacKey, string environment)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            if (hmacKey is not null)
                builder.UseSetting("ExamForge:InvitationHmacKey", hmacKey);
            configure(builder);
        }
    }
}
