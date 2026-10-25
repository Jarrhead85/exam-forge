using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ExamForge.Application.StudentFlow;

namespace ExamForge.Web.Client.Services;

/// <summary>
/// Клиент студенческого API: antiforgery-токен подтягивается после установления сессии
/// и прикладывается ко всем изменяющим запросам (ADR-002 п.5). Коды и ответы не логируются.
/// </summary>
public sealed class ExamApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string? _antiforgeryToken;

    public async Task<string?> ExchangeAsync(string code)
    {
        var response = await http.PostAsJsonAsync("/api/access/exchange", new { code });
        return await ReadAuthErrorAsync(response, "Неверный код приглашения.");
    }

    /// <summary>Вход по общей ссылке (ТЗ §5.1): ФИО+группа; без доступа к чужой истории.</summary>
    public async Task<string?> CommonLinkAsync(string code, string lastName, string firstName, string? middleName, string? group)
    {
        var response = await http.PostAsJsonAsync("/api/access/common-link",
            new { code, lastName, firstName, middleName, group });
        return await ReadAuthErrorAsync(response, "Ссылка недействительна.");
    }

    /// <summary>Вход постоянного аккаунта (ТЗ §5.3).</summary>
    public async Task<string?> LoginAsync(string login, string password)
    {
        var response = await http.PostAsJsonAsync("/api/access/login", new { login, password });
        return await ReadAuthErrorAsync(response, "Неверный логин или пароль.");
    }

    private async Task<string?> ReadAuthErrorAsync(HttpResponseMessage response, string fallback)
    {
        if (response.IsSuccessStatusCode)
            return null;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
            return await ReadProblemAsync(response) ?? fallback;
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            return "Слишком много попыток входа. Подождите минуту.";
        return "Сервис временно недоступен. Попробуйте позже.";
    }

    /// <summary>null — нет сессии (нужен ввод кода).</summary>
    public async Task<StudentAssignmentDto[]?> TryGetAssignmentsAsync()
    {
        var response = await http.GetAsync("/api/student/assignments");
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<StudentAssignmentDto[]>(Json);
    }

    public async Task<AttemptStateDto> StartAttemptAsync(Guid assignmentId)
        => await SendAsync<AttemptStateDto>(HttpMethod.Post, $"/api/assignments/{assignmentId}/attempts");

    public async Task<AttemptStateDto> GetStateAsync(Guid attemptId)
        => await SendAsync<AttemptStateDto>(HttpMethod.Get, $"/api/attempts/{attemptId}/state");

    public async Task<AttemptStateDto> OpenNextAsync(Guid attemptId)
        => await SendAsync<AttemptStateDto>(HttpMethod.Post, $"/api/attempts/{attemptId}/open-next");

    public async Task<SaveAnswerDto> SaveAnswerAsync(Guid attemptId, Guid questionSessionId, object answer)
        => await SendAsync<SaveAnswerDto>(HttpMethod.Put, $"/api/attempts/{attemptId}/answers/{questionSessionId}",
            new { answer });

    public async Task LockCurrentAsync(Guid attemptId)
        => await SendAsync<LockCurrentDto>(HttpMethod.Post, $"/api/attempts/{attemptId}/lock-current");

    public async Task<FinishAttemptDto> FinishAsync(Guid attemptId)
        => await SendAsync<FinishAttemptDto>(HttpMethod.Post, $"/api/attempts/{attemptId}/finish");

    public async Task<StudentResultDto> GetResultAsync(Guid attemptId)
        => await SendAsync<StudentResultDto>(HttpMethod.Get, $"/api/attempts/{attemptId}/result");

    /// <summary>Лекции, доступные студенту (ТЗ §14). null — нет сессии.</summary>
    public async Task<List<StudentMaterialDto>?> TryGetMaterialsAsync()
    {
        var response = await http.GetAsync("/api/student/materials");
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<StudentMaterialDto>>(Json);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (method != HttpMethod.Get)
        {
            await EnsureAntiforgeryTokenAsync();
            if (_antiforgeryToken is not null)
                request.Headers.Add("X-XSRF-TOKEN", _antiforgeryToken);
        }
        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);

        using var response = await http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new ExamSessionExpiredException();
        if (!response.IsSuccessStatusCode)
        {
            var problem = await ReadProblemAsync(response);
            throw new ExamApiException((int)response.StatusCode, problem ?? $"Ошибка {response.StatusCode}");
        }
        if (typeof(T) == typeof(object))
            return (T)(object)true!;
        return await response.Content.ReadFromJsonAsync<T>(Json) ?? throw new ExamApiException(0, "Пустой ответ сервера.");
    }

    private async Task EnsureAntiforgeryTokenAsync()
    {
        if (_antiforgeryToken is not null)
            return;
        var response = await http.GetAsync("/api/access/antiforgery");
        if (!response.IsSuccessStatusCode)
            throw new ExamSessionExpiredException();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        _antiforgeryToken = payload.GetProperty("requestToken").GetString();
    }

    private static async Task<string?> ReadProblemAsync(HttpResponseMessage response)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
            return problem.TryGetProperty("title", out var title) ? title.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class ExamApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public bool IsRejectedLate => StatusCode == 409 && Message.Contains("истекл", StringComparison.OrdinalIgnoreCase);
}

public sealed class ExamSessionExpiredException() : Exception("Студенческая сессия истекла.");
