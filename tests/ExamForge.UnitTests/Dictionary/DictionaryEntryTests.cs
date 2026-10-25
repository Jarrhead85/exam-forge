using ExamForge.Domain.Common;
using ExamForge.Domain.Dictionary;
using Xunit;

namespace ExamForge.UnitTests.Dictionary;

public class DictionaryEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Import_WithKnownFeatures_DoesNotNeedReview()
    {
        var entry = DictionaryEntry.Import(
            Guid.NewGuid(), "aqua, ae f — вода", "aqua", ["вода"],
            new GrammaticalFeatures { FormKind = "liquid", Gender = "f" }, [], "стр. 12", Now);
        Assert.Equal(DictionaryEntryStatus.ImportedDraft, entry.Status);
        Assert.False(entry.NeedsReview);
    }

    [Fact]
    public void Import_WithoutFeatures_IsMarkedForReview()
    {
        // Неизвестные признаки не выдумываются (ТЗ §6).
        var entry = DictionaryEntry.Import(
            Guid.NewGuid(), "?, ? — ?", "???x", ["?"], null, [], null, Now);
        Assert.True(entry.NeedsReview);
    }

    [Fact]
    public void Import_WithUnknownFeatureValue_IsMarkedForReview()
    {
        var entry = DictionaryEntry.Import(
            Guid.NewGuid(), "x — y", "x", ["y"],
            new GrammaticalFeatures { Declension = "VII" }, [], null, Now); // VII нет в словаре склонений
        Assert.True(entry.NeedsReview);
    }

    [Fact]
    public void Confirm_WithUnknownFeature_Throws()
    {
        var entry = DictionaryEntry.Import(Guid.NewGuid(), "x — y", "x", ["y"], null, [], null, Now);
        Assert.Throws<DomainException>(() =>
            entry.Confirm(new GrammaticalFeatures { Declension = "IX" }, ["y"], Now));
    }

    [Fact]
    public void Confirm_WithKnownFeatures_Succeeds_AndBumpsVersion()
    {
        var entry = DictionaryEntry.Import(
            Guid.NewGuid(), "aqua, ae f — вода", "aqua", ["вода"], null, [], "стр. 12", Now);
        entry.Confirm(new GrammaticalFeatures { FormKind = "liquid", Gender = "f" }, ["вода"], Now.AddMinutes(1));

        Assert.Equal(DictionaryEntryStatus.Confirmed, entry.Status);
        Assert.False(entry.NeedsReview);
        Assert.Equal(2, entry.Version);
    }

    [Fact]
    public void Confirm_AlreadyConfirmed_Throws()
    {
        var entry = DictionaryEntry.Import(Guid.NewGuid(), "x — y", "x", ["y"], null, [], null, Now);
        entry.Confirm(null, ["y"], Now);
        Assert.Throws<DomainException>(() => entry.Confirm(null, ["y"], Now));
    }
}

public class ImportJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Workflow_QueuedToPreviewReadyToConfirmed()
    {
        var job = ImportJob.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now);
        job.MarkPreviewReady(Now.AddMinutes(2));
        job.MarkConfirmed(Now.AddMinutes(5));
        Assert.Equal(ImportJobStatus.Confirmed, job.Status);
    }

    [Fact]
    public void Confirm_WithoutPreview_Throws()
    {
        // AC-13: неоднозначный импорт не публикуется автоматически.
        var job = ImportJob.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now);
        Assert.Throws<DomainException>(() => job.MarkConfirmed(Now));
    }

    [Fact]
    public void Failed_RecordsError()
    {
        var job = ImportJob.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now);
        job.MarkFailed("PDF не содержит текстового слоя (OCR вне первой версии)", Now);
        Assert.Equal(ImportJobStatus.Failed, job.Status);
        Assert.NotNull(job.Error);
    }
}
