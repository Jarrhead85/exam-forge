using System.Text.Json;
using ExamForge.Domain.Access;
using ExamForge.Domain.Assignments;
using ExamForge.Domain.People;
using ExamForge.Domain.Questions;
using ExamForge.Domain.Subjects;
using ExamForge.Domain.Testing;
using ExamForge.Infrastructure.Identity;
using ExamForge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ExamForge.Web;

/// <summary>
/// Одноразовый сид E2E-среды (Playwright, P0-10): dotnet run -- --seed-e2e &lt;файл-artifact&gt;.
/// Создаёт собственный предмет/тест/назначение/приглашение (не трогая данные других окружений)
/// и записывает координаты для Playwright-тестов в JSON-файл. Пароль преподавателя задаётся
/// окружением сразу после команды: E2E_TEACHER_LOGIN / E2E_TEACHER_PASSWORD.
/// </summary>
internal static class E2ESeeder
{
    public static async Task SeedAsync(IServiceProvider services, string artifactPath)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExamForgeDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<TeacherUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var hasher = scope.ServiceProvider.GetRequiredService<InvitationCodeHasher>();
        var now = DateTimeOffset.UtcNow;

        foreach (var role in new[] { AppRoles.Administrator, AppRoles.Teacher })
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole<Guid>(role));

        var login = Environment.GetEnvironmentVariable("E2E_TEACHER_LOGIN") ?? "e2e-teacher";
        var password = Environment.GetEnvironmentVariable("E2E_TEACHER_PASSWORD") ?? "E2ETeacher#2026";
        var teacher = await userManager.FindByNameAsync(login);
        if (teacher is null)
        {
            teacher = new TeacherUser { UserName = login, DisplayName = "Преподаватель E2E" };
            var created = await userManager.CreateAsync(teacher, password);
            if (!created.Succeeded)
                throw new InvalidOperationException(
                    "Не удалось создать преподавателя E2E: " + string.Join("; ", created.Errors.Select(e => e.Description)));
            await userManager.AddToRoleAsync(teacher, AppRoles.Teacher);
        }

        // Данные не «складируются бесконечно»: пересид только создаёт новое назначение;
        // чистку тестовой БД делает сам запуск окружения (recreate database).
        var marker = now.ToString("yyyyMMdd-HHmmss");
        var subject = Subject.Create($"Медицинская латынь e2e {marker}", null, now);
        db.Subjects.Add(subject);

        // Три SingleChoice-версии: студент кликает радиокнопки, «Далее», всё автооценивается.
        var definitions = new string[]
        {
            "Выберите перевод термина «oculus»", "Выберите перевод термина «auris»", "Выберите перевод термина «nasus»",
        }
        .Select(prompt => QuestionDefinitionJson.Serialize(new QuestionDefinition
        {
            Type = QuestionType.SingleChoice,
            Prompt = prompt,
            // первый вариант — правильный: E2E кликает первую радиокнопку и ожидает 3/3.
            Options = [new("a", "глаз"), new("b", "ухо"), new("c", "нос")],
            CorrectOptionId = "a",
            MaxPoints = 1m,
            ProposedTimeLimitSeconds = 120,
        }))
        .ToList();

        var question = Question.CreateDraft(subject.Id, null, teacher.Id, now);
        db.Questions.Add(question);
        await db.SaveChangesAsync();
        question.Publish(now, QuestionDefinitionJson.Deserialize(definitions[0]));

        var versionIds = new List<Guid>();
        for (var i = 0; i < definitions.Count; i++)
        {
            var version = QuestionVersion.Create(question.Id, i + 1, QuestionType.SingleChoice,
                definitions[i], 1, 1m, 120, now);
            version.MarkPublished(now);
            db.QuestionVersions.Add(version);
            versionIds.Add(version.Id);
        }

        var test = Test.Create(subject.Id, teacher.Id, "E2E контрольная", now);
        db.Tests.Add(test);
        await db.SaveChangesAsync();

        var testVersion = TestVersion.Create(test.Id, 1, "E2E контрольная", versionIds, 3m, now);
        testVersion.Publish(now);
        db.TestVersions.Add(testVersion);
        await db.SaveChangesAsync();

        var assignment = Assignment.Create(
            testVersion.Id, AssignmentMode.Assessment, FeedbackPolicy.ScoresOnly,
            now.AddHours(-1), null, 2, 120, null,
            noBacktracking: true, allowCommonLink: false, teacher.Id, now);
        var commonLinkCode = InvitationCode.Normalize(InvitationCode.Generate());
        assignment.EnableCommonLink(commonLinkCode, now);
        db.Assignments.Add(assignment);

        var codes = new List<string>();
        var accountLogin = "student_" + assignment.Id.ToString("N")[..12];
        var accountPassword = InvitationCode.Generate().Replace("-", "", StringComparison.Ordinal);
        foreach (var name in new[] { "Мария", "Алексей", "Дарья" })
        {
            var student = StudentProfile.Create("Тестов", name, null, now, teacher.Id);
            db.StudentProfiles.Add(student);
            if (name == "Дарья")
            {
                var passwordHasher = new PasswordHasher<StudentAccount>();
                db.StudentAccounts.Add(StudentAccount.Create(student.Id, accountLogin,
                    passwordHasher.HashPassword(null!, accountPassword), teacher.Id, now));
            }
            var code = InvitationCode.Generate();
            db.Invitations.Add(Invitation.Create(assignment.Id, student.Id,
                hasher.ComputeHash(InvitationCode.Normalize(code)), null, teacher.Id, now));
            codes.Add(code);
        }
        await db.SaveChangesAsync();

        var artifact = new
        {
            teacherLogin = login,
            teacherPassword = password,
            invitationCode = codes[0],
            revisitCode = codes[1],
            offlineCode = codes[2],
            accountLogin,
            accountPassword,
            commonLinkCode,
            assignmentId = assignment.Id,
            assignmentTitle = test.Title,
            testTitle = test.Title,
            subjectName = subject.Name,
        };
        var dir = Path.GetDirectoryName(Path.GetFullPath(artifactPath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(artifactPath, JsonSerializer.Serialize(artifact, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"E2E-данные готовы (назначение {assignment.Id}); artifact → {Path.GetFullPath(artifactPath)}");
    }
}
