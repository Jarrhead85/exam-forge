using ExamForge.Application.TeacherFlow;
using ExamForge.Domain.Common;
using ExamForge.Domain.People;
using ExamForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;

namespace ExamForge.Infrastructure.Services;

public sealed record RosterPreviewRow(long Line, PersonName? Name, bool PossibleMatch, string? Error);

public sealed class GroupRosterImportService(
    IDbContextFactory<ExamForgeDbContext> contextFactory, GroupService groups, TimeProvider timeProvider)
{
    public const int MaximumRows = 500;

    public async Task<IReadOnlyList<RosterPreviewRow>> PreviewAsync(
        TeacherActor actor, Guid groupId, string csv, CancellationToken ct)
    {
        using var reader = new StringReader(csv);
        var firstLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(firstLine))
            throw new DomainException("Файл пуст. Добавьте строку с заголовками и состав группы.");

        using var parser = new TextFieldParser(new StringReader(csv))
        {
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = true,
        };
        parser.SetDelimiters(firstLine.Contains(';') ? ";" : ",");
        var header = ReadFields(parser);
        if (header is not { Length: 2 or 3 } ||
            !header[0].TrimStart('\uFEFF').Equals("Фамилия", StringComparison.OrdinalIgnoreCase) ||
            !header[1].Equals("Имя", StringComparison.OrdinalIgnoreCase) ||
            (header.Length == 3 && !header[2].Equals("Отчество", StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("Ожидаются заголовки «Фамилия;Имя;Отчество». Столбец отчества необязателен.");

        var members = await groups.MembersAsync(actor, groupId, ct);
        var names = members.Select(member => NameKey(member.LastName, member.FirstName, member.MiddleName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<RosterPreviewRow>();
        while (!parser.EndOfData)
        {
            ct.ThrowIfCancellationRequested();
            if (result.Count >= MaximumRows)
                throw new DomainException($"Разделите файл: за один раз можно проверить до {MaximumRows} строк.");

            var line = parser.LineNumber;
            try
            {
                var row = ReadFields(parser);
                if (row is null || row.Length != header.Length)
                    throw new DomainException("Число полей не совпадает с заголовком.");

                var person = PersonName.Create(row[0], row[1], row.Length == 3 ? row[2] : null);
                var exists = !names.Add(NameKey(person.LastName, person.FirstName, person.MiddleName));
                result.Add(new RosterPreviewRow(line, person, exists, null));
            }
            catch (DomainException ex)
            {
                result.Add(new RosterPreviewRow(line, null, false, ex.Message));
            }
        }
        return result;
    }

    public async Task<int> ImportAsync(
        TeacherActor actor, Guid groupId, IReadOnlyList<PersonName> rows, CancellationToken ct)
    {
        if (rows.Count is < 1 or > MaximumRows)
            throw new DomainException($"Выберите от 1 до {MaximumRows} строк.");
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var allowed = await db.Groups.AsNoTracking().AnyAsync(group => group.Id == groupId &&
            (actor.IsAdministrator || db.TeacherGroupGrants.Any(grant =>
                grant.GroupId == groupId && grant.TeacherId == actor.TeacherId)), ct);
        if (!allowed)
            throw TeacherFlowErrors.NotFound("Группа не найдена.");

        var now = timeProvider.GetUtcNow();
        foreach (var row in rows)
        {
            var student = StudentProfile.Create(row.LastName, row.FirstName, row.MiddleName, now, actor.TeacherId);
            db.StudentProfiles.Add(student);
            db.GroupMemberships.Add(GroupMembership.Create(groupId, student.Id, now));
        }
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    private static string NameKey(string last, string first, string? middle)
        => string.Join('\u001f', last, first, middle);

    private static string[]? ReadFields(TextFieldParser parser)
    {
        try
        {
            return parser.ReadFields();
        }
        catch (MalformedLineException)
        {
            throw new DomainException("Строка CSV повреждена. Проверьте разделители и кавычки.");
        }
    }
}
