using System.Text;
using System.Text.RegularExpressions;
using ExamForge.Application.StudentFlow;
using ExamForge.Application.TeacherFlow;
using ExamForge.Domain.Dictionary;
using ExamForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using UglyToad.PdfPig;

namespace ExamForge.Infrastructure.Services;

public sealed record DictionaryDto(Guid DictionaryId, string Name, int Entries, DateTimeOffset CreatedAtUtc);
public sealed record DictionaryEntryDto(
    Guid EntryId,
    string OriginalText,
    string Lemma,
    IReadOnlyList<string> Translations,
    string Status,
    bool NeedsReview,
    string? SourceRef);

/// <summary>
/// Словарь (ТЗ §6): создание, список записей, подтверждение/отклонение,
/// импорт текстового PDF → черновики на проверку (без автопубликации — AC-13).
/// </summary>
public sealed class DictionaryService(ExamForgeDbContext db, TimeProvider timeProvider)
{
    private DateTimeOffset Now => timeProvider.GetUtcNow();

    public async Task<Dictionary> CreateAsync(TeacherActor actor, Guid subjectId, string name, CancellationToken ct)
    {
        var dictionary = Dictionary.Create(subjectId, name, Now);
        db.Dictionaries.Add(dictionary);
        await db.SaveChangesAsync(ct);
        return dictionary;
    }

    public async Task RenameAsync(TeacherActor actor, Guid dictionaryId, string name, CancellationToken ct)
    {
        var dictionary = await db.Dictionaries.SingleOrDefaultAsync(d => d.Id == dictionaryId, ct)
            ?? throw TeacherFlowErrors.NotFound("Словарь не найден.");
        dictionary.Rename(name);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Удаление словаря вместе с записями (manual delete, ТЗ §21). Вопросы, созданные из записей,
    /// не зависят от словаря: их содержание — снимок, FK нет. Возвращает число удалённых записей.
    /// </summary>
    public async Task<int> DeleteAsync(TeacherActor actor, Guid dictionaryId, CancellationToken ct)
    {
        var dictionary = await db.Dictionaries.SingleOrDefaultAsync(d => d.Id == dictionaryId, ct)
            ?? throw TeacherFlowErrors.NotFound("Словарь не найден.");
        var entries = await db.DictionaryEntries.Where(e => e.DictionaryId == dictionaryId).ToListAsync(ct);
        db.DictionaryEntries.RemoveRange(entries);
        db.Dictionaries.Remove(dictionary);
        await db.SaveChangesAsync(ct);
        return entries.Count;
    }

    /// <summary>Удаление отдельной записи: решение преподавателя (черновик/отклонённая/подтверждённая).</summary>
    public async Task DeleteEntryAsync(TeacherActor actor, Guid entryId, CancellationToken ct)
    {
        var entry = await db.DictionaryEntries.SingleOrDefaultAsync(e => e.Id == entryId, ct)
            ?? throw TeacherFlowErrors.NotFound("Запись не найдена.");
        db.DictionaryEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Ручной ввод записи преподавателем (ТЗ §6): источник — сам преподаватель, поэтому запись
    /// сразу подтверждается с явно указанными признаками (null-признаки остаются на повторное подтверждение).
    /// </summary>
    public async Task<DictionaryEntry> AddEntryManuallyAsync(
        TeacherActor actor, Guid dictionaryId,
        string lemma, string translation, string? formKind, string? gender, string? declension,
        CancellationToken ct)
    {
        var dictionary = await db.Dictionaries.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == dictionaryId, ct)
            ?? throw TeacherFlowErrors.NotFound("Словарь не найден.");
        lemma = lemma.Trim();
        translation = translation.Trim();
        if (lemma.Length == 0 || translation.Length == 0)
            throw TeacherFlowErrors.Conflict("bad_request", "Лемма и перевод обязательны.");

        GrammaticalFeatures? features = formKind is null && gender is null && declension is null
            ? null
            : new GrammaticalFeatures { FormKind = formKind, Gender = gender, Declension = declension };
        var entry = DictionaryEntry.Import(
            dictionaryId, $"{lemma} — {translation}", lemma, [translation], features, [],
            "ручной ввод", Now);
        if (features is null || features.IsKnown)
            entry.Confirm(features, [translation], Now);
        db.DictionaryEntries.Add(entry);
        await db.SaveChangesAsync(ct);
        return entry;
    }

    public async Task<IReadOnlyList<DictionaryDto>> ListAsync(CancellationToken ct)
    {
        var dictionaries = await db.Dictionaries.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct);
        var ids = dictionaries.Select(d => d.Id).ToList();
        var counts = await db.DictionaryEntries.AsNoTracking()
            .Where(e => ids.Contains(e.DictionaryId) && e.Status != DictionaryEntryStatus.Rejected)
            .GroupBy(e => e.DictionaryId)
            .Select(g => new { DictionaryId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.DictionaryId, x => x.Count, ct);
        return [.. dictionaries.Select(d => new DictionaryDto(d.Id, d.Name, counts.GetValueOrDefault(d.Id), d.CreatedAtUtc))];
    }

    public async Task<IReadOnlyList<DictionaryEntryDto>> EntriesAsync(Guid dictionaryId, CancellationToken ct)
    {
        var entries = await db.DictionaryEntries.AsNoTracking()
            .Where(e => e.DictionaryId == dictionaryId)
            .OrderBy(e => e.Lemma)
            .ToListAsync(ct);
        return [.. entries.Select(ToDto)];
    }

    /// <summary>
    /// Импорт PDF: извлекаем текст (PdfPig), каждую непустую строку — черновик записи.
    /// Признаки не выдумываются: NeedsReview=true, подтверждение преподавателем (ТЗ §6, AC-13).
    /// </summary>
    public async Task<(int Imported, int Skipped)> ImportPdfAsync(
        TeacherActor actor, Guid dictionaryId, Stream pdfContent, CancellationToken ct)
    {
        var dictionary = await db.Dictionaries.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == dictionaryId, ct)
            ?? throw TeacherFlowErrors.NotFound("Словарь не найден.");

        // Поток из браузерной загрузки (IBrowserFile.OpenReadStream) поддерживает только
        // асинхронное чтение, а PdfPig читает синхронно — буферизуем файл целиком
        // (лимит размера уже проверяет вызывающая сторона).
        using var buffered = new MemoryStream();
        await pdfContent.CopyToAsync(buffered, ct);
        buffered.Position = 0;

        var text = new StringBuilder();
        using (var document = PdfDocument.Open(buffered))
        {
            foreach (var page in document.GetPages())
            {
                // Группируем слова по строкам (Y-координата): page.Text склеивает строки без переносов.
                var lines = page.GetWords()
                    .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 4, MidpointRounding.AwayFromZero))
                    .Select(g => string.Join(" ", g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)))
                    .ToList();
                foreach (var line in lines)
                    text.AppendLine(line);
            }
        }

        var imported = 0;
        var skipped = 0;
        foreach (var rawLine in text.ToString().Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length < 3)
            {
                skipped++;
                continue;
            }
            var (lemma, translation) = SplitEntry(line);
            if (lemma.Length == 0 || translation.Length == 0)
            {
                skipped++;
                continue;
            }
            var source = $"стр. импорта {DateTimeOffset.UtcNow:yyyy-MM-dd}";
            db.DictionaryEntries.Add(DictionaryEntry.Import(
                dictionaryId, line, lemma, [translation],
                features: null, topics: [], sourceRef: source, Now));
            imported++;
        }
        await db.SaveChangesAsync(ct);
        return (imported, skipped);
    }

    /// <summary>«lemma, form g — перевод» / «lemma — перевод» → (лемма, перевод). Признаки не парсим.</summary>
    internal static (string Lemma, string Translation) SplitEntry(string line)
    {
        var dashIndex = line.IndexOfAny(['—', '–', '-']);
        if (dashIndex <= 0 || dashIndex >= line.Length - 1)
            return ("", "");
        var lemma = line[..dashIndex].Trim();
        var translation = line[(dashIndex + 1)..].Trim().TrimStart('-', ' ').TrimEnd('.', ';', ',');
        // «oculus, i m» → лемма до первой запятой
        var comma = lemma.IndexOf(',');
        if (comma > 0)
            lemma = lemma[..comma].Trim();
        lemma = Regex.Replace(lemma, @"\s+", " ");
        if (lemma.Length is 0 or > 200 || translation.Length is 0 or > 500)
            return ("", "");
        return (lemma, translation);
    }

    public async Task<DictionaryEntryDto> ConfirmAsync(
        TeacherActor actor, Guid entryId, string? formKind, string? gender, string? declension, string? grammaticalCase,
        IReadOnlyList<string> translations, CancellationToken ct)
    {
        var entry = await db.DictionaryEntries.SingleOrDefaultAsync(e => e.Id == entryId, ct)
            ?? throw TeacherFlowErrors.NotFound("Запись не найдена.");
        var features = formKind is null && gender is null && declension is null && grammaticalCase is null
            ? null
            : new GrammaticalFeatures { FormKind = formKind, Gender = gender, Declension = declension, GrammaticalCase = grammaticalCase };
        entry.Confirm(features, translations, Now);
        await db.SaveChangesAsync(ct);
        return ToDto(entry);
    }

    public async Task RejectAsync(TeacherActor actor, Guid entryId, CancellationToken ct)
    {
        var entry = await db.DictionaryEntries.SingleOrDefaultAsync(e => e.Id == entryId, ct)
            ?? throw TeacherFlowErrors.NotFound("Запись не найдена.");
        entry.Reject(Now);
        await db.SaveChangesAsync(ct);
    }

    public async Task<DictionaryEntry> LoadEntryAsync(Guid entryId, CancellationToken ct)
        => await db.DictionaryEntries.AsNoTracking().SingleOrDefaultAsync(e => e.Id == entryId, ct)
           ?? throw TeacherFlowErrors.NotFound("Запись не найдена.");

    public async Task<Guid> LoadDictionarySubjectAsync(Guid dictionaryId, CancellationToken ct)
        => await db.Dictionaries.AsNoTracking()
            .Where(d => d.Id == dictionaryId)
            .Select(d => d.SubjectId)
            .SingleAsync(ct);

    /// <summary>Черновик вопроса из подтверждённой записи + дистракторы из соседних записей (ТЗ §6).</summary>
    public async Task<Guid> CreateDraftQuestionAsync(TeacherActor actor, Guid entryId, CancellationToken ct)
    {
        var entry = await LoadEntryAsync(entryId, ct);
        var others = await EntriesAsync(entry.DictionaryId, ct);
        var distractors = others
            .Where(o => o.EntryId != entryId && o.Translations.Count > 0)
            .Select(o => o.Translations[0])
            .Distinct()
            .Take(6)
            .ToList();
        var definition = Domain.Questions.DictionaryDraftFactory.BuildTranslationChoice(entry, distractors, 40);
        var subjectId = await LoadDictionarySubjectAsync(entry.DictionaryId, ct);
        var version = await new TeacherQuestionsService(db, timeProvider)
            .CreateDraftAsync(actor, subjectId, definition, ct);
        return version.QuestionId;
    }

    private static DictionaryEntryDto ToDto(DictionaryEntry e) => new(
        e.Id, e.OriginalText, e.Lemma, e.Translations, e.Status.ToString(), e.NeedsReview, e.SourceRef);
}
