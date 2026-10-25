using ExamForge.Domain.Dictionary;
using ExamForge.Domain.Questions;
using Xunit;

namespace ExamForge.UnitTests.Questions;

public class MedicalTemplatesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static DictionaryEntry ConfirmedEntry(string lemma, params string[] translations)
    {
        var entry = DictionaryEntry.Import(Guid.NewGuid(), $"{lemma} — {translations[0]}", lemma, translations, null, [], null, Now);
        entry.Confirm(null, translations, Now);
        return entry;
    }

    [Fact]
    public void Templates_CoverAllInteractionTypes_FromSpec()
    {
        // ТЗ §7.3: десять шаблонов, включая рецептуру (LongText, ручная проверка).
        Assert.Equal(10, MedicalTemplates.All.Count);
        Assert.Contains(MedicalTemplates.All, t => t.Type == QuestionType.LongText && t.Code == "prescription");
        Assert.Contains(MedicalTemplates.All, t => t.Type == QuestionType.LetterAssembly && t.Code == "term-assembly");
        Assert.Contains(MedicalTemplates.All, t => t.Type == QuestionType.Matching && t.Code == "matching");
    }

    [Fact]
    public void BuildTranslationChoice_DraftFromConfirmedEntry()
    {
        var entry = ConfirmedEntry("oculus", "глаз");
        var definition = DictionaryDraftFactory.BuildTranslationChoice(entry, ["ухо", "нос", "рот", "зуб"]);

        Assert.Equal(QuestionType.SingleChoice, definition.Type);
        Assert.Contains("oculus", definition.Prompt);
        Assert.Equal(4, definition.Options.Count);
        Assert.Equal("c", definition.CorrectOptionId);
        // черновик уже валиден — преподавателю остаётся подтвердить ключ
        Assert.Empty(QuestionValidator.ValidateForPublish(definition));
    }

    [Fact]
    public void BuildTranslationChoice_NeverUsesSynonymAsDistractor()
    {
        // ТЗ §6: среди «неверных» нет синонимичных правильных ответов.
        var entry = ConfirmedEntry("oculus", "глаз");
        var definition = DictionaryDraftFactory.BuildTranslationChoice(entry, ["глаз", "ухо", "нос", "зуб"]);

        var distractorTexts = definition.Options.Where(o => o.OptionId != "c").Select(o => o.Text);
        Assert.All(distractorTexts, t => Assert.NotEqual("глаз", t));
        Assert.Equal(3, definition.Options.Count - 1); // ровно три отвлекающих
    }

    [Fact]
    public void BuildTranslationChoice_RequiresConfirmedEntry()
    {
        var draft = DictionaryEntry.Import(Guid.NewGuid(), "x — y", "x", ["y"], null, [], null, Now);
        Assert.Throws<ExamForge.Domain.Common.DomainException>(
            () => DictionaryDraftFactory.BuildTranslationChoice(draft, ["a", "b", "c"]));
    }

    [Fact]
    public void BuildTranslationChoice_TooFewDistractors_Throws()
    {
        var entry = ConfirmedEntry("auris", "ухо");
        Assert.Throws<ExamForge.Domain.Common.DomainException>(
            () => DictionaryDraftFactory.BuildTranslationChoice(entry, ["глаз"]));
    }

    [Fact]
    public void BuildTiles_DuplicateLettersGetUniqueIds()
    {
        // AC-12: две u в «oculus» — отдельные плитки.
        var tiles = DictionaryDraftFactory.BuildTiles("oculus");
        Assert.Equal(6, tiles.Count);
        Assert.Equal(6, tiles.Select(t => t.TileId).Distinct().Count());
        Assert.Equal("oculus", new string([.. tiles.Select(t => t.Letter)]));
        // порядок букв сохраняется, id стабильны
        Assert.Equal("o1", tiles[0].TileId);
        Assert.Equal("u1", tiles[2].TileId);
        Assert.Equal("u2", tiles[4].TileId);
    }
}
