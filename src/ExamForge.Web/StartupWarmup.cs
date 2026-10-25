using ExamForge.Application.TeacherFlow;
using ExamForge.Infrastructure.Identity;
using ExamForge.Infrastructure.Persistence;
using ExamForge.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ExamForge.Web;

/// <summary>
/// Прогрев после старта: выполняет основные запросы кабинета, чтобы компиляция запросов EF
/// (секунды на слабом VPS при первом исполнении) легла на старт приложения, а не на первого
/// пользователя после деплоя. Формы запросов — реальные сервисные методы, кэш EF собирается
/// именно под них.
/// </summary>
public sealed class StartupWarmupService(IServiceProvider services, ILogger<StartupWarmupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ExamForgeDbContext>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<TeacherUser>>();
            var user = await db.Users.AsNoTracking().OrderBy(u => u.UserName).FirstOrDefaultAsync(stoppingToken);
            if (user is null)
            {
                logger.LogInformation("Warmup: пользователей нет, прогрев сервисов пропущен.");
                return;
            }
            var actor = new TeacherActor(user.Id, true);
            var failures = new List<string>();
            var admins = new AdminTeachersService(db, userManager,
                scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>());
            await WarmAsync("teachers", () => admins.ListTeachersAsync(actor, stoppingToken), failures);
            await WarmAsync("subjects", () => scope.ServiceProvider.GetRequiredService<SubjectsService>().ListAsync(stoppingToken), failures);
            await WarmAsync("groups", () => scope.ServiceProvider.GetRequiredService<GroupService>().ListAsync(actor, stoppingToken), failures);
            await WarmAsync("dictionaries", () => scope.ServiceProvider.GetRequiredService<DictionaryService>().ListAsync(stoppingToken), failures);
            await WarmAsync("questions", () => scope.ServiceProvider.GetRequiredService<TeacherQuestionsService>().ListAsync(actor, stoppingToken), failures);
            await WarmAsync("tests", () => TeacherTestsService.ListAsync(db, actor, stoppingToken), failures);
            await WarmAsync("assignments", () => scope.ServiceProvider.GetRequiredService<TeacherAssignmentsService>().ListAssignmentsAsync(actor, stoppingToken), failures);
            logger.LogInformation("Warmup завершён за {ElapsedMs} мс; ошибки: {Failures}",
                sw.ElapsedMilliseconds, failures.Count == 0 ? "нет" : string.Join("; ", failures));
        }
        catch (OperationCanceledException)
        {
            // остановка приложения во время прогрева — норма
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Прогрев не удался (на работу не влияет).");
        }
    }

    private static async Task WarmAsync<T>(string name, Func<Task<T>> call, List<string> failures)
    {
        try
        {
            await call();
        }
        catch (Exception ex)
        {
            failures.Add($"{name}: {ex.Message}");
        }
    }
}
