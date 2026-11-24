using ExamForge.Web.Client;

namespace ExamForge.UnitTests.StudentFlow;

/// <summary>P0-04: переход разрешён только при подтверждённом сохранении.</summary>
public sealed class ExamSaveGateTests
{
    [Theory]
    [InlineData(SaveAnswerResult.Saved)]
    [InlineData(SaveAnswerResult.Offline)]
    [InlineData(SaveAnswerResult.RejectedLate)]
    public void CanProceed_OnlyWhenSaved(SaveAnswerResult result)
    {
        Assert.Equal(result == SaveAnswerResult.Saved, ExamSaveGate.CanProceed(result));
    }

    [Fact]
    public void Offline_HasUserReadableMessage()
    {
        var message = ExamSaveGate.GetMessage(SaveAnswerResult.Offline);
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Contains("сервер", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectedLate_HasUserReadableMessage()
    {
        var message = ExamSaveGate.GetMessage(SaveAnswerResult.RejectedLate);
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Contains("Время", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Saved_HasNoErrorMessage()
    {
        Assert.Null(ExamSaveGate.GetMessage(SaveAnswerResult.Saved));
    }
}
