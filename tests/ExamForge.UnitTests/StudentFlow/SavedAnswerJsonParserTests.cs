using ExamForge.Web.Client;

namespace ExamForge.UnitTests.StudentFlow;

/// <summary>
/// P0-05: собственный сохранённый ответ студента восстанавливается во всех типах
/// вопросов; повреждённый/чужой формат не приводит к некорректному состоянию.
/// </summary>
public sealed class SavedAnswerJsonParserTests
{
    [Fact]
    public void SingleChoice_RestoresSelectedOption()
    {
        var answer = SavedAnswerJsonParser.Parse("SingleChoice", """{"selectedOptionId":"b"}""");
        Assert.Equal("b", answer!.Selected);
    }

    [Fact]
    public void MultipleChoice_RestoresSelectedIds()
    {
        var answer = SavedAnswerJsonParser.Parse("MultipleChoice", """{"selectedOptionIds":["a","c"]}""");
        Assert.Equal(["a", "c"], answer!.SelectedIds);
    }

    [Fact]
    public void ShortText_RestoresText()
    {
        var answer = SavedAnswerJsonParser.Parse("ShortText", """{"text":"вода"}""");
        Assert.Equal("вода", answer!.Text);
    }

    [Fact]
    public void LongText_RestoresText()
    {
        var answer = SavedAnswerJsonParser.Parse("LongText", """{"text":"Аблятив…"}""");
        Assert.Equal("Аблятив…", answer!.Text);
    }

    [Fact]
    public void Matching_RestoresPairs()
    {
        var answer = SavedAnswerJsonParser.Parse("Matching",
            """{"pairSelections":{"L0":"R0","L1":"R1"}}""");
        Assert.Equal("R0", answer!.Pairs["L0"]);
        Assert.Equal("R1", answer.Pairs["L1"]);
    }

    [Fact]
    public void LetterAssembly_RestoresTileSequence()
    {
        var answer = SavedAnswerJsonParser.Parse("LetterAssembly",
            """{"tileSequence":["t-o","t-c","t-u1"]}""");
        Assert.Equal(["t-o", "t-c", "t-u1"], answer!.TileSequence);
    }

    [Fact]
    public void Ordering_RestoresOrder()
    {
        var answer = SavedAnswerJsonParser.Parse("Ordering",
            """{"order":["n2","n1","n3"]}""");
        Assert.Equal(["n2", "n1", "n3"], answer!.Order);
    }

    [Fact]
    public void GapFill_RestoresBlankValues()
    {
        var answer = SavedAnswerJsonParser.Parse("GapFill",
            """{"blankValues":{"b1":"aqua","b2":"terra"}}""");
        Assert.Equal("aqua", answer!.Values["b1"]);
        Assert.Equal("terra", answer.Values["b2"]);
    }

    [Fact]
    public void Structured_RestoresFieldValues()
    {
        var answer = SavedAnswerJsonParser.Parse("Structured",
            """{"fieldValues":{"f-case":"ablative"}}""");
        Assert.Equal("ablative", answer!.Values["f-case"]);
    }

    [Fact]
    public void CorruptedJson_ReturnsNothing()
    {
        Assert.Null(SavedAnswerJsonParser.Parse("ShortText", """{"text": voda"""));
    }

    [Fact]
    public void Empty_NoRevision_ReturnsNothing()
    {
        Assert.Null(SavedAnswerJsonParser.Parse("SingleChoice", null));
        Assert.Null(SavedAnswerJsonParser.Parse("SingleChoice", ""));
    }

    [Fact]
    public void NonObjectJson_ReturnsNothing()
    {
        Assert.Null(SavedAnswerJsonParser.Parse("SingleChoice", """["a"]"""));
    }
}
