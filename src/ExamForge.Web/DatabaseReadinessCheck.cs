using ExamForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ExamForge.Web;

public sealed class DatabaseReadinessCheck(IDbContextFactory<ExamForgeDbContext> contextFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Database.CanConnectAsync(cancellationToken))
            return HealthCheckResult.Unhealthy("Нет подключения к базе данных.");

        var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken);
        return pending.Any()
            ? HealthCheckResult.Unhealthy("В базе есть неприменённые миграции.")
            : HealthCheckResult.Healthy();
    }
}
