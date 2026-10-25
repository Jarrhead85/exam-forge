using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Threading.RateLimiting;
using ExamForge.Domain.Access;
using ExamForge.Domain.Questions;
using ExamForge.Infrastructure;
using ExamForge.Infrastructure.Identity;
using ExamForge.Infrastructure.Persistence;
using ExamForge.Infrastructure.Services;
using ExamForge.Web.Authentication;
using ExamForge.Web.Components;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Кириллица из @-выражений не должна превращаться в HTML-entities (проверяется тестами печати).
builder.Services.AddSingleton(HtmlEncoder.Create(UnicodeRanges.All));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();

builder.Services.AddExamForgeInfrastructure(
    builder.Configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("ConnectionStrings:Default не задан."));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ =>
{
    // HMAC-ключ проверки кодов приглашений (ADR-002); в production задаётся конфигурацией/секретом.
    var keyHex = builder.Configuration["ExamForge:InvitationHmacKey"];
    var key = string.IsNullOrWhiteSpace(keyHex)
        ? Convert.FromHexString("5F1D2A7C94E80B3FA6C5D2E147B0899ADF23C6E501B7A4D8F9E0C3B2A1D4F7E6")
        : Convert.FromHexString(keyHex);
    return new InvitationCodeHasher(key);
});
builder.Services.AddScoped<StudentFlowService>();
builder.Services.AddScoped<TeacherAssignmentsService>();
builder.Services.AddScoped<TestVersionService>();
builder.Services.AddScoped<ManualReviewService>();
builder.Services.AddScoped<PaperWorkService>();
builder.Services.AddScoped<MaterialsService>();
builder.Services.AddScoped<TeacherQuestionsService>();
builder.Services.AddScoped<GroupService>();
builder.Services.AddScoped<DictionaryService>();
builder.Services.AddScoped<SubjectsService>();
builder.Services.AddScoped<AdminTeachersService>();
builder.Services.AddScoped<ResultsService>();
builder.Services.AddScoped<ExamForge.Application.Files.IFileStorage, ExamForge.Infrastructure.Files.LocalDiskFileStorage>();
builder.Services.AddScoped<ExamForge.Infrastructure.Printing.PrintViewBuilder>();

// Cookie-схема преподавателя/администратора: Secure, HttpOnly, Lax (ADR-002).
// Production — строго Always; переопределяется на SameAsRequest только для пилота по IP без домена
// (конфиг ExamForge:Cookies:SecurePolicy — см. deploy/docker-compose.yml).
var cookieSecure = builder.Configuration["ExamForge:Cookies:SecurePolicy"]?.ToLowerInvariant() switch
{
    "always" => CookieSecurePolicy.Always,
    "sameasrequest" => CookieSecurePolicy.SameAsRequest,
    _ => builder.Environment.IsProduction() ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest,
};

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies(options =>
    {
        // AddIdentityCookies гарантированно создаёт ApplicationCookie.
        options.ApplicationCookie!.Configure(cookie =>
        {
            cookie.Cookie.Name = "ef.teacher";
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Lax;
            cookie.Cookie.SecurePolicy = cookieSecure;
            cookie.ExpireTimeSpan = TimeSpan.FromHours(8);
            cookie.SlidingExpiration = true;
            cookie.LoginPath = "/account/login";
            cookie.AccessDeniedPath = "/account/denied";
        });
    });

// Студенческая сессия обмена кодом (ADR-002): Strict, не sliding, без UI-редиректов (401 для API).
builder.Services.AddAuthentication()
    .AddCookie(StudentAuth.Scheme, options =>
    {
        options.Cookie.Name = "ef.student";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = cookieSecure;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = false;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
    });

// Antiforgery для студенческого WASM: cookie + заголовок X-XSRF-TOKEN (ADR-002 п.5).
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-XSRF-TOKEN";
    options.Cookie.Name = "ef.antiforgery";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = cookieSecure;
});

// Ограничение перебора кодов приглашения: фиксированное окно по IP (ТЗ §5.2, §19).
var exchangePerMinute = builder.Configuration.GetValue("ExamForge:RateLimits:ExchangePerMinute", 15);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("access-exchange", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = exchangePerMinute,
                QueueLimit = 0,
            }));
});

// ControllersWithViews: регистрирует ValidateAntiforgeryTokenAuthorizationFilter для API-атрибутов.
builder.Services.AddControllersWithViews();

// Сервисы WASM-клиента регистрируются и на хосте для пререндера student-страниц (паттерн шаблона Blazor).
// Вызовы API выполняются только в интерактивном режиме (см. RendererInfo.IsInteractive на страницах).
builder.Services.AddScoped(_ => new HttpClient());
builder.Services.AddScoped<ExamForge.Web.Client.Services.ExamApiClient>();
builder.Services.AddHostedService<ExamForge.Web.StartupWarmupService>();

var app = builder.Build();
var antiforgery = app.Services.GetRequiredService<IAntiforgery>();

// Утилиты одноразового запуска (деплой/разработка): применение миграций и сиды.
if (args.Length >= 1)
{
    if (args[0] == "--migrate")
    {
        using var migrateScope = app.Services.CreateScope();
        var migrateDb = migrateScope.ServiceProvider.GetRequiredService<ExamForgeDbContext>();
        await migrateDb.Database.MigrateAsync();
        Console.WriteLine("Миграции применены.");
        return;
    }
    if (args.Length == 4 && args[0] == "--seed-admin")
    {
        await AdminSeeder.SeedAsync(app.Services, args[1], args[2], args[3]);
        return;
    }
    if (args.Length == 1 && args[0] == "--seed-demo")
    {
        await DemoSeeder.SeedAsync(app.Services);
        return;
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// Security-заголовки: nosniff, запрет фрейминга, минимальная CSP без внешних источников.
// wasm-unsafe-eval нужен студенческому WebAssembly-клиенту (компиляция .NET-рантайма в браузере);
// ws/wss по хосту запроса — SignalR-circuit страниц кабинета (схема ws отличается от http,
// поэтому 'self' её не покрывает и соединение блокировалось браузером).
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    var wsScheme = context.Request.IsHttps ? "wss" : "ws";
    context.Response.Headers["Content-Security-Policy"] =
        $"default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self' {wsScheme}://{context.Request.Host.Value}; frame-ancestors 'none'; base-uri 'self'";
    await next(context);
});

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// Диагностика производительности: каждый HTTP-запрос дольше 500 мс отмечается warn'ом
// с путём и временем — медленные места видны в логах без воспроизведения у пользователя.
app.Use(async (context, next) =>
{
    var started = System.Diagnostics.Stopwatch.GetTimestamp();
    await next(context);
    var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
    if (elapsed.TotalMilliseconds > 500)
        app.Logger.LogWarning("Slow request {Method} {Path} took {Ms:F0} ms",
            context.Request.Method, context.Request.Path, elapsed.TotalMilliseconds);
});

// Ошибки студенческого потока и доменные инварианты → ProblemDetails без stacktrace (ТЗ §17).
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (UriFormatException ex)
    {
        // Редкие падения RemoteNavigationManager.Initialize: фиксируем контекст запроса,
        // чтобы найти источник (заголовок Host / путь) по логам.
        app.Logger.LogError(ex, "UriFormatException host='{Host}' path={Path} query={Query} proto={Proto}",
            context.Request.Host.Value, context.Request.Path, context.Request.QueryString, context.Request.Protocol);
        throw;
    }
    catch (ExamForge.Application.StudentFlow.StudentFlowException ex)
    {
        context.Response.StatusCode = ex.StatusCode;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = ex.StatusCode,
            Title = ex.Message,
            Extensions = { ["code"] = ex.ErrorCode },
        });
    }
    catch (ExamForge.Domain.Common.DomainException ex)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = ex.Message,
            Extensions = { ["code"] = "domain_violation" },
        });
    }
    catch (ExamForge.Application.Files.FileStorageException ex)
    {
        // отклонённые загрузки (формат/сигнатура/лимит) — 400 без деталей хранилища (ТЗ §14, §19)
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = ex.Message,
            Extensions = { ["code"] = "file_rejected" },
        });
    }
});

// Пилот по IP без домена работает по HTTP: редирект на HTTPS отключается конфигом
// (ExamForge:HttpsRedirection=false); по умолчанию в production включён.
if (app.Configuration.GetValue("ExamForge:HttpsRedirection", app.Environment.IsProduction()))
    app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// CSRF-защита teacher-API: любой POST/PUT/DELETE с учительской cookie обязан нести валидный
// antiforgery-токен (заголовок X-XSRF-TOKEN). Студенческий API защищён [ValidateAntiForgeryToken]
// на контроллерах и не зависит от этого middleware (ADR-002 п.5, §19).
app.Use(async (context, next) =>
{
    var isMutating = context.Request.Method is "POST" or "PUT" or "DELETE";
    var hasTeacherCookie = context.Request.Cookies.ContainsKey("ef.teacher");
    var isApiPath = context.Request.Path.StartsWithSegments("/api");
    if (isMutating && hasTeacherCookie && isApiPath)
    {
        await antiforgery.ValidateRequestAsync(context);
    }
    await next(context);
});

app.MapStaticAssets();
// Браузеры запрашивают /favicon.ico — отдаём редирект на существующий png вместо
// 404→re-execute /not-found (лишний SSR-рендер на каждый визит).
app.MapGet("/favicon.ico", () => Results.Redirect("/favicon.png", permanent: true));
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(ExamForge.Web.Client._Imports).Assembly);

app.MapControllers();

app.Run();

internal static class AdminSeeder
{
    public static async Task SeedAsync(IServiceProvider services, string login, string password, string displayName)
    {
        using var scope = services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<TeacherUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        if (!await roleManager.RoleExistsAsync(AppRoles.Administrator))
            await roleManager.CreateAsync(new IdentityRole<Guid>(AppRoles.Administrator));
        if (!await roleManager.RoleExistsAsync(AppRoles.Teacher))
            await roleManager.CreateAsync(new IdentityRole<Guid>(AppRoles.Teacher));

        var existing = await userManager.FindByNameAsync(login);
        if (existing is not null)
        {
            Console.WriteLine($"Пользователь {login} уже существует.");
            return;
        }
        var admin = new TeacherUser { UserName = login, DisplayName = displayName };
        var result = await userManager.CreateAsync(admin, password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
                Console.WriteLine($"Ошибка: {error.Description}");
            return;
        }
        await userManager.AddToRoleAsync(admin, AppRoles.Administrator);
        Console.WriteLine($"Создан администратор {login}.");
    }
}

internal static class DemoSeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExamForgeDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<TeacherUser>>();
        var teacher = await userManager.FindByNameAsync("admin1")
            ?? throw new InvalidOperationException("Сначала создайте администратора: dotnet run -- --seed-admin admin1 <password> <ФИО>");
        var now = DateTimeOffset.UtcNow;

        if (await db.Subjects.AnyAsync())
        {
            Console.WriteLine("Демо-данные уже есть (subjects > 0), пропускаю.");
            return;
        }

        var subject = ExamForge.Domain.Subjects.Subject.Create("Медицинская латынь", "latmed", now);
        db.Subjects.Add(subject);
        await db.SaveChangesAsync();

        var definitions = new (QuestionType Type, string Json, decimal Max)[]
        {
            (QuestionType.SingleChoice, QuestionDefinitionJson.Serialize(new QuestionDefinition
            {
                Type = QuestionType.SingleChoice,
                Prompt = "Выберите перевод термина «oculus»",
                Options = [new("a", "глаз"), new("b", "ухо"), new("c", "нос"), new("d", "рот")],
                CorrectOptionId = "a",
                MaxPoints = 1m, ProposedTimeLimitSeconds = 40,
            }), 1m),
            (QuestionType.Matching, QuestionDefinitionJson.Serialize(new QuestionDefinition
            {
                Type = QuestionType.Matching,
                Prompt = "Соотнесите латинские термины и переводы",
                MatchingPairs =
                [
                    new("L0", "auris", "R0", "ухо"),
                    new("L1", "nasus", "R1", "нос"),
                    new("L2", "dens", "R2", "зуб"),
                    new("L3", "caput", "R3", "голова"),
                ],
                MaxPoints = 4m, ProposedTimeLimitSeconds = 60,
            }), 4m),
            (QuestionType.LetterAssembly, QuestionDefinitionJson.Serialize(new QuestionDefinition
            {
                Type = QuestionType.LetterAssembly,
                Prompt = "Соберите термин «oculus» из букв",
                TargetSequence = "oculus",
                LetterTiles = [.. DictionaryDraftFactory.BuildTiles("oculus")],
                MaxPoints = 1m, ProposedTimeLimitSeconds = 45,
            }), 1m),
            (QuestionType.ShortText, QuestionDefinitionJson.Serialize(new QuestionDefinition
            {
                Type = QuestionType.ShortText,
                Prompt = "Переведите на русский: «aqua»",
                AcceptedAnswers = [new AcceptedAnswer("вода")],
                MaxPoints = 1m, ProposedTimeLimitSeconds = 30,
            }), 1m),
        };

        var versionIds = new List<Guid>();
        var number = 0;
        foreach (var (type, json, max) in definitions)
        {
            number++;
            var question = ExamForge.Domain.Questions.Question.CreateDraft(subject.Id, null, teacher.Id, now);
            db.Questions.Add(question);
            await db.SaveChangesAsync();
            var definition = QuestionDefinitionJson.Deserialize(json);
            question.Publish(now, definition);
            var version = ExamForge.Domain.Questions.QuestionVersion.Create(question.Id, 1, type, json, 1, max, definition.ProposedTimeLimitSeconds, now);
            version.MarkPublished(now);
            db.QuestionVersions.Add(version);
            await db.SaveChangesAsync();
            versionIds.Add(version.Id);
        }

        var test = ExamForge.Domain.Testing.Test.Create(subject.Id, teacher.Id, "Словарная контрольная №1 (демо)", now);
        db.Tests.Add(test);
        await db.SaveChangesAsync();
        var testVersion = ExamForge.Domain.Testing.TestVersion.Create(test.Id, 1, "Словарная контрольная №1 (демо)", versionIds, 7m, now);
        testVersion.Publish(now);
        db.TestVersions.Add(testVersion);
        await db.SaveChangesAsync();

        var variants = ExamForge.Domain.Testing.VariantGenerator.Generate(
            testVersion,
            versionIds.ToDictionary(id => id, id => QuestionDefinitionJson.Deserialize(
                db.QuestionVersions.AsNoTracking().First(v => v.Id == id).DefinitionJson)),
            2, $"demo:{now.Ticks}", shuffleQuestions: false, now);
        db.Variants.AddRange(variants);
        await db.SaveChangesAsync();

        Console.WriteLine($"Демо: предмет, {versionIds.Count} опубликованных вопроса, тест+версия, {variants.Count} варианта.");
    }
}
