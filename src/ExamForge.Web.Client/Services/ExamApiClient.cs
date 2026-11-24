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

    public async Task<AttemptStateDto> GetStateAsync(Guid attemptId)
        => await SendAsync<AttemptStateDto>(HttpMethod.Get, $"/api/attempts/{attemptId}/state");

    public async Task<AttemptStateDto> OpenNextAsync(Guid attemptId)
        => await SendAsync<AttemptStateDto>(HttpMethod.Post, $"/api/attempts/{attemptId}/open-next");

    public async Task<SaveAnswerDto> SaveAnswerAsync(Guid attemptId, AttemptAnswer answer)
        => await SendAsync<SaveAnswerDto>(HttpMethod.Put,
            $"/api/attempts/{attemptId}/answers/{answer.QuestionSessionId}",
            new { answer.Answer, answer.ExpectedRevision });

    public async Task<AttemptStateDto> AdvanceAsync(Guid attemptId, AdvanceQuestionRequest request)
        => await SendAsync<AttemptStateDto>(HttpMethod.Post, $"/api/attempts/{attemptId}/advance", request);

    public async Task<FinishAttemptDto> FinishAsync(Guid attemptId, AttemptAnswer? answer)
        => await SendAsync<FinishAttemptDto>(HttpMethod.Post, $"/api/attempts/{attemptId}/finish", answer);

    public async Task<StudentResultDto> GetResultAsync(Guid attemptId)
        => await SendAsync<StudentResultDto>(HttpMethod.Get, $"/api/attempts/{attemptId}/result");

    /// <summary>Лекции, доступные студенту (ТЗ §14). null — нет сессии.</summary>
    public async Task<List<StudentMaterialDto>?> TryGetMaterialsAsync()
    {
        using var response = await http.GetAsync("/api/student/materials");
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
            throw new ExamApiException((int)response.StatusCode,
                problem.Title ?? "Не удалось выполнить действие. Попробуйте ещё раз.", problem.Code);
        }
        return await response.Content.ReadFromJsonAsync<T>(Json) ?? throw new ExamApiException(0, "Пустой ответ сервера.");
    }

    private async Task EnsureAntiforgeryTokenAsync()
    {
        if (_antiforgeryToken is not null)
            return;
        using var response = await http.GetAsync("/api/access/antiforgery");
        if (!response.IsSuccessStatusCode)
            throw new ExamSessionExpiredException();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        _antiforgeryToken = payload.GetProperty("requestToken").GetString();
    }

    private static async Task<(string? Title, string? Code)> ReadProblemAsync(HttpResponseMessage response)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
            if (problem.ValueKind == JsonValueKind.Object)
                return (ReadString(problem, "title"), ReadString(problem, "code"));
            return default;
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string? ReadString(JsonElement element, string key)
        => element.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
}

public sealed class ExamApiException(int statusCode, string message, string? code = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
    public bool IsRejectedLate => Code == "late";
}

public sealed class ExamSessionExpiredException() : Exception("Студенческая сессия истекла.");
