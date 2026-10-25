using ExamForge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Xunit;

namespace ExamForge.IntegrationTests;

/// <summary>
/// Поднимает приложение с тестовой базой examforge_test на локальном PostgreSQL (ADR-004).
/// База пересоздаётся на каждый запуск набора; миграции применяются реально.
/// Время детерминировано: FakeTimeProvider (ТЗ §24 — без flaky sleep).
/// </summary>
public sealed class ExamForgeWebFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string TestDatabase = "examforge_test";
    private const string ServerConnectionString = "Host=localhost;Port=5432;Username=postgres;Database=postgres";
    public const string TestConnectionString = "Host=localhost;Port=5432;Username=postgres;Database=examforge_test";

    /// <summary>Известный тестам HMAC-ключ кодов приглашений.</summary>
    public const string TestHmacKey = "00112233445566778899AABBCCDDEEFF";

    /// <summary>
    /// Стартует от реального UtcNow (не фиксированной даты): auth-cookie выдаётся на «fake now + 8 ч»,
    /// а DataProtection валидирует срок по реальным часам — фиксированная дата в прошлом
    /// приводила к «истёкшим» cookie после пересечения реального времени с экспирацией.
    /// </summary>
    public FakeTimeProvider FakeTime { get; } = new(TruncateToSecond(DateTimeOffset.UtcNow));

    /// <summary>Целая секунда: JSON-округление тиков не ломает сравнение дедлайнов.</summary>
    private static DateTimeOffset TruncateToSecond(DateTimeOffset value)
        => new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, value.Offset);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging =>
            logging.AddProvider(new FileLoggerProvider("C:/Users/J/pg/test-web.log")));
        builder.UseSetting("ConnectionStrings:Default", TestConnectionString);
        builder.UseSetting("ExamForge:InvitationHmacKey", TestHmacKey);
        // Интеграционные тесты делают много обменов подряд: лимит перебора не должен срабатывать.
        builder.UseSetting("ExamForge:RateLimits:ExchangePerMinute", "1000");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(FakeTime);
        });
    }

    public async Task InitializeAsync()
    {
        await using (var connection = new NpgsqlConnection(ServerConnectionString))
        {
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {TestDatabase} WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {TestDatabase} ENCODING 'UTF8'", connection);
            await create.ExecuteNonQueryAsync();
        }

        using var scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ExamForgeDbContext>();
        await dbContext.Database.MigrateAsync();
    }

    public new async Task DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {TestDatabase} WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
        await base.DisposeAsync();
    }
}

[CollectionDefinition("database")]
public sealed class DatabaseCollection : ICollectionFixture<ExamForgeWebFactory>;

/// <summary>Пишет логи тест-хоста в файл: 500-ки в интеграционных тестах иначе непрозрачны.</summary>
public sealed class FileLoggerProvider(string path) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(path);
    public void Dispose() { }

    private sealed class FileLogger(string path) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Warning)
                return;
            var message = $"{DateTime.Now:HH:mm:ss.fff} {logLevel} {formatter(state, exception)}\n{exception}\n";
            File.AppendAllText(path, message);
        }
    }
}
