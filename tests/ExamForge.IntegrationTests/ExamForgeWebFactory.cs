using ExamForge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Xunit;

namespace ExamForge.IntegrationTests;

/// <summary>Отдельная база PostgreSQL на каждый прогон, с реальными миграциями и управляемыми часами.</summary>
public sealed class ExamForgeWebFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _databaseName = $"examforge_test_{Guid.NewGuid():N}";
    private bool _createdDatabase;

    private string ConnectionString(string database)
        => new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("EXAMFORGE_TEST_DB") ??
            "Host=localhost;Port=5432;Username=postgres")
        {
            Database = database,
        }.ConnectionString;

    /// <summary>Известный тестам HMAC-ключ кодов приглашений: 64 hex-символа = 32 байта.</summary>
    public const string TestHmacKey = "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF";

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
        builder.UseSetting("ConnectionStrings:Default", ConnectionString(_databaseName));
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
        await using (var connection = new NpgsqlConnection(ConnectionString("postgres")))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {_databaseName} ENCODING 'UTF8'", connection);
            await create.ExecuteNonQueryAsync();
            _createdDatabase = true;
        }

        using var scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ExamForgeDbContext>();
        await dbContext.Database.MigrateAsync();
    }

    public new async Task DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            if (_createdDatabase)
            {
                await using var connection = new NpgsqlConnection(ConnectionString("postgres"));
                await connection.OpenAsync();
                await using var drop = new NpgsqlCommand($"DROP DATABASE {_databaseName} WITH (FORCE)", connection);
                await drop.ExecuteNonQueryAsync();
            }
        }
    }
}

[CollectionDefinition("database")]
public sealed class DatabaseCollection : ICollectionFixture<ExamForgeWebFactory>;
