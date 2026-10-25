using ExamForge.Domain.Attempts;
using ExamForge.Domain.People;
using ExamForge.Infrastructure.Identity;
using ExamForge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ExamForge.IntegrationTests;

[Collection("database")]
public sealed class PersistenceTests(ExamForgeWebFactory factory)
{
    private ExamForgeDbContext NewDb()
    {
        var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ExamForgeDbContext>();
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Migration_Creates_ExpectedTables()
    {
        await using var db = NewDb();
        var tables = await db.Database
            .SqlQuery<string>($@"SELECT table_name AS ""Value"" FROM information_schema.tables WHERE table_schema = 'public'")
            .ToListAsync();
        Assert.Contains("attempts", tables);
        Assert.Contains("question_sessions", tables);
        Assert.Contains("student_profiles", tables);
        Assert.Contains("file_assets", tables);
        Assert.Contains("groups", tables);
        Assert.Contains("group_memberships", tables);
        Assert.Contains("teacher_group_grants", tables);
        Assert.Contains("subjects", tables);
        Assert.Contains("topics", tables);
        Assert.Contains("AspNetUsers", tables);
    }

    [Fact]
    public async Task UniqueActiveAttemptIndex_PreventsSecondActive_AndAllowsAfterSubmit()
    {
        var assignmentId = Guid.NewGuid();
        var studentId = Guid.NewGuid();

        await using var db = NewDb();
        var first = Attempt.Create(assignmentId, 1, Now, studentId, "Иванова Анна Сергеевна", "ЛД-201");
        db.Attempts.Add(first);
        await db.SaveChangesAsync();

        // вторая активная попытка того же студента в том же назначении отклоняется БД (AC-07)
        var second = Attempt.Create(assignmentId, 2, Now.AddMinutes(1), studentId, "Иванова Анна Сергеевна", "ЛД-201");
        db.Attempts.Add(second);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        db.Entry(second).State = EntityState.Detached;

        // после сдачи первой попытки следующая разрешена
        var reloaded = await db.Attempts.SingleAsync(a => a.Id == first.Id);
        reloaded.Start(Now);
        reloaded.Submit(Now.AddMinutes(30), false);
        await db.SaveChangesAsync();

        var next = Attempt.Create(assignmentId, 2, Now.AddHours(1), studentId, "Иванова Анна Сергеевна", "ЛД-201");
        db.Attempts.Add(next);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GroupMembership_CompositeKey_PreventsDuplicates()
    {
        Guid groupId; Guid studentId;
        await using (var db = NewDb())
        {
            var group = Group.Create("ЛД-201", Now);
            var student = StudentProfile.Create("Петров", "Пётр", null, Now, null);
            db.Groups.Add(group);
            db.StudentProfiles.Add(student);
            await db.SaveChangesAsync();
            groupId = group.Id; studentId = student.Id;

            db.GroupMemberships.Add(GroupMembership.Create(groupId, studentId, Now));
            await db.SaveChangesAsync();
        }

        // Отдельный контекст: нарушение проверяет сама БД (составной PK), а не change tracker.
        await using (var db2 = NewDb())
        {
            db2.GroupMemberships.Add(GroupMembership.Create(groupId, studentId, Now.AddMinutes(1)));
            await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task FinalizedAttempt_GradingIsPersistedAndRecomputed()
    {
        await using var db = NewDb();
        var attempt = Attempt.Create(Guid.NewGuid(), 1, Now, Guid.NewGuid(), "Сидорова Мария Ивановна", "ЛД-202");
        attempt.Start(Now);
        attempt.Submit(Now.AddMinutes(30), false);
        attempt.Finalize(8.57m, 10m, Now.AddMinutes(31));
        db.Attempts.Add(attempt);
        await db.SaveChangesAsync();

        var reloaded = await db.Attempts.AsNoTracking().SingleAsync(a => a.Id == attempt.Id);
        Assert.Equal(8.57m, reloaded.ScoreEarnedPoints);
        Assert.Equal(10m, reloaded.ScoreMaxPoints);
        Assert.Equal(86, reloaded.Score!.Value.RoundedPercent);
        Assert.Equal(5, reloaded.Score!.Value.Grade);
        Assert.True(reloaded.IsEligibleForBest);
    }

    [Fact]
    public async Task TeacherUser_PasswordPolicy_RejectsWeakPasswords()
    {
        var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<TeacherUser>>();

        var weak = await userManager.CreateAsync(new TeacherUser { UserName = "t1", DisplayName = "Тест" }, "abc");
        Assert.False(weak.Succeeded);

        var strong = await userManager.CreateAsync(new TeacherUser { UserName = "teacher1", DisplayName = "Преподаватель Один" }, "LatynskieGoda1");
        Assert.True(strong.Succeeded);

        var created = await userManager.FindByNameAsync("teacher1");
        Assert.NotNull(created);
        Assert.Equal("Преподаватель Один", created.DisplayName);
    }

    [Fact]
    public async Task StudentProfiles_SameNames_BothStored_NoUniqueOnName()
    {
        // ТЗ §4: ФИО не уникальный ключ, однофамильцы не объединяются.
        await using var db = NewDb();
        var a = StudentProfile.Create("Иванов", "Иван", "Иванович", Now, null);
        var b = StudentProfile.Create("Иванов", "Иван", "Иванович", Now, null);
        db.StudentProfiles.AddRange(a, b);
        await db.SaveChangesAsync();
        Assert.NotEqual(a.Id, b.Id);
    }
}
