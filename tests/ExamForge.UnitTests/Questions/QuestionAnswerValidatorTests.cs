using System.Text.Json;
using ExamForge.Domain.Common;
using ExamForge.Domain.Questions;
using FluentAssertions;
using Xunit;

namespace ExamForge.UnitTests.Questions;

public sealed class QuestionAnswerValidatorTests
{
    private readonly QuestionDefinition _question = new()
    {
        Type = QuestionType.SingleChoice,
        Prompt = "Выберите перевод.",
        Options = [new("a", "глаз"), new("b", "ухо")],
        CorrectOptionId = "a",
        MaxPoints = 1,
    };

    [Fact]
    public void Parse_AcceptsIncorrectButValidChoice()
    {
        using var json = JsonDocument.Parse("""{"selectedOptionId":"b"}""");
        var answer = QuestionAnswerValidator.Parse(_question, json.RootElement);
        answer.SelectedOptionId.Should().Be("b");
    }

    [Theory]
    [InlineData("""{"selectedOptionId":42}""")]
    [InlineData("""{"selectedOptionId":"unknown"}""")]
    [InlineData("""{"text":"wrong shape"}""")]
    [InlineData("""{"selectedOptionId":"a","SelectedOptionId":"b"}""")]
    [InlineData("""["a"]""")]
    public void Parse_RejectsAnswersThatCannotBeStored(string raw)
    {
        using var json = JsonDocument.Parse(raw);
        var parse = () => QuestionAnswerValidator.Parse(_question, json.RootElement);
        parse.Should().Throw<DomainException>();
    }
}
