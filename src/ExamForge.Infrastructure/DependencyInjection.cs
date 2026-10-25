using ExamForge.Infrastructure.Identity;
using ExamForge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ExamForge.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddExamForgeInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        // Dictionary/объекты в jsonb требуют явного включения dynamic JSON (Npgsql 8+).
        var dataSource = new NpgsqlDataSourceBuilder(connectionString)
            .EnableDynamicJson()
            .Build();
        services.AddDbContext<ExamForgeDbContext>(options =>
            options.UseNpgsql(dataSource, npgsql =>
                npgsql.EnableRetryOnFailure(maxRetryCount: 3)));

        services
            .AddIdentityCore<TeacherUser>(options =>
            {
                // Преподавателей мало — строгая политика оправдана; rate limit входа — Lockout (ТЗ §19).
                options.Password.RequiredLength = 10;
                options.Password.RequireDigit = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredUniqueChars = 4;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.User.RequireUniqueEmail = false; // email не обязательный внутренний логин (ТЗ §4)
                options.SignIn.RequireConfirmedEmail = false;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<ExamForgeDbContext>()
            .AddSignInManager<SignInManager<TeacherUser>>();

        return services;
    }
}
