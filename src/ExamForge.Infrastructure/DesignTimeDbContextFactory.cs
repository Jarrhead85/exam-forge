using ExamForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ExamForge.Infrastructure;

/// <summary>
/// Фабрика для dotnet-ef (создание миграций без запуска Web).
/// Строка подключения: EXAMFORGE_CONNECTION_STRING или локальная dev-база по умолчанию.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ExamForgeDbContext>
{
    public ExamForgeDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("EXAMFORGE_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=examforge_dev;Username=postgres";

        var options = new DbContextOptionsBuilder<ExamForgeDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new ExamForgeDbContext(options);
    }
}
