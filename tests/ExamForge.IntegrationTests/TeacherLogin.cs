using System.Net.Http.Json;
using System.Text.RegularExpressions;
using ExamForge.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ExamForge.IntegrationTests;

/// <summary>
/// Общий вход преподавателя для интеграционных тестов. Статический SSR-пост формы логина
/// изредка не обрабатывается под нагрузкой тест-хоста — повторяем до подтверждённой
/// аутентификации защищённым GET (флак-фикс; продукт не при чём).
/// </summary>
public static class TeacherLogin
{
    public const string Password = "LatynskieGoda1";

    public static async Task<HttpClient> LoginExistingAsync(
        ExamForgeWebFactory factory, string login, int maxAttempts = 3)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var diagnostics = new List<string>();
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var loginPage = await client.GetStringAsync("/account/login");
            var token = Regex
                .Match(loginPage, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
            var loginResponse = await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_handler"] = "teacher-login",
                ["__RequestVerificationToken"] = token,
                ["Input.Login"] = login,
                ["Input.Password"] = Password,
            }));
            var setCookie = loginResponse.Headers.TryGetValues("Set-Cookie", out var sc)
                ? string.Join(" | ", sc)
                : "none";

            var probe = await client.GetAsync("/api/questions");
            if (probe.IsSuccessStatusCode)
            {
                // teacher-API защищён antiforgery-middleware: после входа прикрепляем токен ко всем запросам
                var af = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
                    "/api/access/antiforgery-teacher", System.Text.Json.JsonSerializerOptions.Web);
                client.DefaultRequestHeaders.Add("X-XSRF-TOKEN",
                    af.GetProperty("requestToken").GetString()!);
                return client;
            }
            var probeBody = await probe.Content.ReadAsStringAsync();
            diagnostics.Add($"#{attempt}: probe={(int)probe.StatusCode}, body={probeBody[..Math.Min(400, probeBody.Length)]}");
        }
        throw new InvalidOperationException(
            $"Не удалось войти преподавателем ({login}) после {maxAttempts} попыток. {string.Join("; ", diagnostics)}");
    }

    /// <summary>Создаёт преподавателя (роль Teacher или Administrator) и логинится.</summary>
    public static async Task<(HttpClient Client, Guid TeacherId)> CreateAndLoginAsync(
        ExamForgeWebFactory factory, bool administrator = false, string? displayName = null)
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<TeacherUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.RoleManager<Microsoft.AspNetCore.Identity.IdentityRole<Guid>>>();
        foreach (var role in new[] { Infrastructure.Identity.AppRoles.Administrator, Infrastructure.Identity.AppRoles.Teacher })
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new Microsoft.AspNetCore.Identity.IdentityRole<Guid>(role));

        var login = "t-" + Guid.NewGuid().ToString("N")[..12];
        var teacher = new TeacherUser { UserName = login, DisplayName = displayName ?? "Тестовый Преподаватель" };
        var created = await userManager.CreateAsync(teacher, Password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        await userManager.AddToRoleAsync(teacher, administrator
            ? Infrastructure.Identity.AppRoles.Administrator
            : Infrastructure.Identity.AppRoles.Teacher);

        var client = await LoginExistingAsync(factory, login);
        return (client, teacher.Id);
    }
}
