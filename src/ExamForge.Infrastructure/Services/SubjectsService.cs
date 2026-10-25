using ExamForge.Application.TeacherFlow;
using ExamForge.Domain.Subjects;
using ExamForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ExamForge.Infrastructure.Services;

public sealed record SubjectDto(Guid Id, string Name, string? Code);

/// <summary>Предметы (ТЗ §4): общие справочники портала; удаление — только неиспользуемых.</summary>
public sealed class SubjectsService(ExamForgeDbContext db, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<SubjectDto>> ListAsync(CancellationToken ct)
        => await db.Subjects.AsNoTracking()
            .OrderBy(s => s.Name)
            .Select(s => new SubjectDto(s.Id, s.Name, s.Code))
            .ToListAsync(ct);

    public async Task CreateAsync(TeacherActor actor, string name, string? code, CancellationToken ct)
    {
        var exists = await db.Subjects.AnyAsync(s => s.Name == name, ct);
        if (exists)
            throw TeacherFlowErrors.Conflict("subject_exists", "Предмет с таким названием уже есть.");
        db.Subjects.Add(Subject.Create(name, code, timeProvider.GetUtcNow()));
        await db.SaveChangesAsync(ct);
    }

    public async Task RenameAsync(TeacherActor actor, Guid subjectId, string name, string? code, CancellationToken ct)
    {
        var subject = await db.Subjects.SingleOrDefaultAsync(s => s.Id == subjectId, ct)
            ?? throw TeacherFlowErrors.NotFound("Предмет не найден.");
        var duplicate = await db.Subjects.AnyAsync(s => s.Id != subjectId && s.Name == name, ct);
        if (duplicate)
            throw TeacherFlowErrors.Conflict("subject_exists", "Предмет с таким названием уже есть.");
        subject.Rename(name, code);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Удаление предмета: только если по нему нет заданий, тестов, словарей и материалов.
    /// Используемый предмет не удаляется молча — преподаватель видит, что именно мешает.
    /// </summary>
    public async Task DeleteAsync(TeacherActor actor, Guid subjectId, CancellationToken ct)
    {
        var subject = await db.Subjects.SingleOrDefaultAsync(s => s.Id == subjectId, ct)
            ?? throw TeacherFlowErrors.NotFound("Предмет не найден.");
        var usage = new List<string>();
        if (await db.Questions.AnyAsync(q => q.SubjectId == subjectId, ct)) usage.Add("задания");
        if (await db.Tests.AnyAsync(t => t.SubjectId == subjectId, ct)) usage.Add("тесты");
        if (await db.Dictionaries.AnyAsync(d => d.SubjectId == subjectId, ct)) usage.Add("словари");
        if (await db.Materials.AnyAsync(m => m.SubjectId == subjectId, ct)) usage.Add("материалы");
        if (await db.Topics.AnyAsync(t => t.SubjectId == subjectId, ct)) usage.Add("темы");
        if (usage.Count > 0)
            throw TeacherFlowErrors.Conflict("subject_in_use",
                $"Предмет используется: {string.Join(", ", usage)}. Сначала удалите или перенесите их.");
        db.Subjects.Remove(subject);
        await db.SaveChangesAsync(ct);
    }
}
