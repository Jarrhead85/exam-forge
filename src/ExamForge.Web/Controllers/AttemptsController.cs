using ExamForge.Application.StudentFlow;
using ExamForge.Web.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExamForge.Web.Controllers;

/// <summary>
/// Студенческие endpoint'ы попытки (ТЗ §17). Ошибки — ProblemDetails без stacktrace;
/// чужой объект неотличим от отсутствующего (404).
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = StudentAuth.Scheme)]
public sealed class AttemptsController(Infrastructure.Services.StudentFlowService flow) : ControllerBase
{
    [HttpPost("/api/assignments/{assignmentId:guid}/attempts")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartAttempt(Guid assignmentId, CancellationToken ct)
    {
        var identity = User.StudentIdentity() ?? throw StudentFlowException.NotFound();
        return Ok(await flow.StartAttemptAsync(assignmentId, identity, ct));
    }

    [HttpGet("/api/attempts/{attemptId:guid}/state")]
    public async Task<IActionResult> GetState(Guid attemptId, CancellationToken ct)
    {
        var identity = User.StudentIdentity() ?? throw StudentFlowException.NotFound();
        return Ok(await flow.GetStateAsync(attemptId, identity, ct));
    }

    [HttpPost("/api/attempts/{attemptId:guid}/open-next")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OpenNext(Guid attemptId, CancellationToken ct)
    {
        var identity = User.StudentIdentity() ?? throw StudentFlowException.NotFound();
        return Ok(await flow.OpenNextAsync(attemptId, identity, ct));
    }

    [HttpPut("/api/attempts/{attemptId:guid}/answers/{questionSessionId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveAnswer(Guid attemptId, Guid questionSessionId, [FromBody] SaveAnswerRequest body, CancellationToken ct)
    {
        var identity = User.StudentIdentity() ?? throw StudentFlowException.NotFound();
        return Ok(await flow.SaveAnswerAsync(attemptId, questionSessionId, body.Answer.GetRawText(), identity, ct));
    }

    [HttpPost("/api/attempts/{attemptId:guid}/lock-current")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LockCurrent(Guid attemptId, CancellationToken ct)
    {
        var identity = User.StudentIdentity() ?? throw StudentFlowException.NotFound();
        return Ok(await flow.LockCurrentAsync(attemptId, identity, ct));
    }

    [HttpPost("/api/attempts/{attemptId:guid}/finish")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Finish(Guid attemptId, CancellationToken ct)
    {
        var identity = User.StudentIdentity() ?? throw StudentFlowException.NotFound();
        return Ok(await flow.FinishAsync(attemptId, identity, ct));
    }

    [HttpGet("/api/attempts/{attemptId:guid}/result")]
    public async Task<IActionResult> GetResult(Guid attemptId, CancellationToken ct)
    {
        var identity = User.StudentIdentity() ?? throw StudentFlowException.NotFound();
        return Ok(await flow.GetResultAsync(attemptId, identity, ct));
    }

    /// <summary>Ответ передаётся как JSON-документ любого типа; сервер валидирует при оценивании.</summary>
    public sealed record SaveAnswerRequest(System.Text.Json.JsonElement Answer);
}
