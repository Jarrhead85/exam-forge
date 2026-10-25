using ExamForge.Domain.Assignments;
using ExamForge.Domain.Attempts;
using ExamForge.Domain.Dictionary;
using ExamForge.Domain.Files;
using ExamForge.Domain.People;
using ExamForge.Domain.Questions;
using ExamForge.Domain.Subjects;
using ExamForge.Domain.Testing;
using ExamForge.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamForge.Infrastructure.Persistence;

public sealed class ExamForgeDbContext(DbContextOptions<ExamForgeDbContext> options)
    : IdentityDbContext<TeacherUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<StudentProfile> StudentProfiles => Set<StudentProfile>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMembership> GroupMemberships => Set<GroupMembership>();
    public DbSet<TeacherGroupGrant> TeacherGroupGrants => Set<TeacherGroupGrant>();
    public DbSet<Subject> Subjects => Set<Subject>();
    public DbSet<Topic> Topics => Set<Topic>();
    public DbSet<FileAsset> FileAssets => Set<FileAsset>();
    public DbSet<Attempt> Attempts => Set<Attempt>();
    public DbSet<QuestionSession> QuestionSessions => Set<QuestionSession>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<QuestionVersion> QuestionVersions => Set<QuestionVersion>();
    public DbSet<Dictionary> Dictionaries => Set<Dictionary>();
    public DbSet<DictionaryEntry> DictionaryEntries => Set<DictionaryEntry>();
    public DbSet<ImportJob> ImportJobs => Set<ImportJob>();
    public DbSet<Test> Tests => Set<Test>();
    public DbSet<TestVersion> TestVersions => Set<TestVersion>();
    public DbSet<Variant> Variants => Set<Variant>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<ExtraAttemptGrant> ExtraAttemptGrants => Set<ExtraAttemptGrant>();
    public DbSet<AnswerRevision> AnswerRevisions => Set<AnswerRevision>();
    public DbSet<Domain.People.StudentAccount> StudentAccounts => Set<Domain.People.StudentAccount>();
    public DbSet<Domain.Grading.QuestionGrade> QuestionGrades => Set<Domain.Grading.QuestionGrade>();
    public DbSet<Domain.Grading.GradeRevision> GradeRevisions => Set<Domain.Grading.GradeRevision>();
    public DbSet<Domain.Assignments.PaperWork> PaperWorks => Set<Domain.Assignments.PaperWork>();
    public DbSet<Domain.Assignments.PaperScore> PaperScores => Set<Domain.Assignments.PaperScore>();
    public DbSet<Domain.Materials.Material> Materials => Set<Domain.Materials.Material>();
    public DbSet<Domain.Materials.MaterialGrant> MaterialGrants => Set<Domain.Materials.MaterialGrant>();
    public DbSet<Domain.Auditing.AuditEvent> AuditEvents => Set<Domain.Auditing.AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ExamForgeDbContext).Assembly);
    }
}

public sealed class AttemptConfiguration : IEntityTypeConfiguration<Attempt>
{
    public void Configure(EntityTypeBuilder<Attempt> builder)
    {
        builder.ToTable("attempts");
        builder.HasKey(a => a.Id);
        // Optimistic concurrency через системный xmin: гонки start/finish решает БД (ADR-003).
        builder.Property<uint>("xmin").IsRowVersion();

        // Единственная активная (несданная) попытка на студента в назначении (ТЗ §5.2, AC-07).
        builder.HasIndex(a => new { a.AssignmentId, a.StudentProfileId })
            .IsUnique()
            .HasFilter("\"State\" IN (1, 2)") // Created, InProgress
            .HasDatabaseName("ix_attempts_unique_active");

        builder.Property(a => a.State).HasConversion<int>();
        builder.Property(a => a.StudentNameSnapshot).HasMaxLength(300);
        builder.Property(a => a.StudentGroupSnapshot).HasMaxLength(200);
        builder.Property(a => a.ScoreEarnedPoints).HasPrecision(10, 3);
        builder.Property(a => a.ScoreMaxPoints).HasPrecision(10, 3);
        builder.Ignore(a => a.Score); // вычисляется из сохранённых баллов
        builder.HasIndex(a => new { a.AssignmentId, a.AttemptNumber });
        builder.HasIndex(a => a.VariantId);
        builder.HasOne<Domain.Testing.Variant>().WithMany().HasForeignKey(a => a.VariantId);
    }
}

public sealed class QuestionSessionConfiguration : IEntityTypeConfiguration<QuestionSession>
{
    public void Configure(EntityTypeBuilder<QuestionSession> builder)
    {
        builder.ToTable("question_sessions");
        builder.HasKey(q => q.Id);
        // Атомарность открытия/сохранения/закрытия при параллельных запросах (ADR-003 п.3–6).
        builder.Property<uint>("xmin").IsRowVersion();
        builder.Property(q => q.State).HasConversion<int>();
        builder.HasIndex(q => new { q.AttemptId, q.OrderIndex }).IsUnique();
        builder.HasIndex(q => q.AttemptId);
    }
}

public sealed class StudentProfileConfiguration : IEntityTypeConfiguration<StudentProfile>
{
    public void Configure(EntityTypeBuilder<StudentProfile> builder)
    {
        builder.ToTable("student_profiles");
        builder.HasKey(p => p.Id);
        // ФИО не уникальный ключ: однофамильцы не объединяются (ТЗ §4). Индекс только для поиска.
        builder.HasIndex(p => new { p.LastName, p.FirstName, p.MiddleName });
        builder.Property(p => p.LastName).HasMaxLength(100);
        builder.Property(p => p.FirstName).HasMaxLength(100);
        builder.Property(p => p.MiddleName).HasMaxLength(100);
        builder.Ignore(p => p.Name);
    }
}

public sealed class GroupConfiguration : IEntityTypeConfiguration<Group>
{
    public void Configure(EntityTypeBuilder<Group> builder)
    {
        builder.ToTable("groups");
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Name).HasMaxLength(200);
        builder.HasIndex(g => g.Name);
    }
}

public sealed class GroupMembershipConfiguration : IEntityTypeConfiguration<GroupMembership>
{
    public void Configure(EntityTypeBuilder<GroupMembership> builder)
    {
        builder.ToTable("group_memberships");
        builder.HasKey(m => new { m.GroupId, m.StudentProfileId });
        builder.HasOne<StudentProfile>().WithMany().HasForeignKey(m => m.StudentProfileId);
        builder.HasOne<Group>().WithMany().HasForeignKey(m => m.GroupId);
    }
}

public sealed class TeacherGroupGrantConfiguration : IEntityTypeConfiguration<TeacherGroupGrant>
{
    public void Configure(EntityTypeBuilder<TeacherGroupGrant> builder)
    {
        builder.ToTable("teacher_group_grants");
        builder.HasKey(g => new { g.TeacherId, g.GroupId });
        builder.HasOne<TeacherUser>().WithMany().HasForeignKey(g => g.TeacherId);
        builder.HasOne<Group>().WithMany().HasForeignKey(g => g.GroupId);
    }
}

public sealed class SubjectConfiguration : IEntityTypeConfiguration<Subject>
{
    public void Configure(EntityTypeBuilder<Subject> builder)
    {
        builder.ToTable("subjects");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Name).HasMaxLength(200);
        builder.Property(s => s.Code).HasMaxLength(32);
    }
}

public sealed class TopicConfiguration : IEntityTypeConfiguration<Topic>
{
    public void Configure(EntityTypeBuilder<Topic> builder)
    {
        builder.ToTable("topics");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name).HasMaxLength(200);
        builder.HasOne<Subject>().WithMany().HasForeignKey(t => t.SubjectId);
        builder.HasOne<Topic>().WithMany().HasForeignKey(t => t.ParentTopicId);
        builder.HasIndex(t => t.SubjectId);
    }
}

public sealed class FileAssetConfiguration : IEntityTypeConfiguration<FileAsset>
{
    public void Configure(EntityTypeBuilder<FileAsset> builder)
    {
        builder.ToTable("file_assets");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.StorageKey).HasMaxLength(500);
        builder.HasIndex(f => f.StorageKey).IsUnique();
        builder.Property(f => f.Sha256Hex).HasMaxLength(64).IsFixedLength();
        builder.Property(f => f.MimeType).HasMaxLength(100);
        builder.Property(f => f.OriginalFileName).HasMaxLength(255);
        builder.Property(f => f.AccessClass).HasConversion<int>();
    }
}

public sealed class QuestionConfiguration : IEntityTypeConfiguration<Question>
{
    public void Configure(EntityTypeBuilder<Question> builder)
    {
        builder.ToTable("questions");
        builder.HasKey(q => q.Id);
        builder.Property(q => q.Status).HasConversion<int>();
        builder.HasIndex(q => q.SubjectId);
        builder.HasIndex(q => q.AuthorTeacherId);
        builder.HasOne<Subject>().WithMany().HasForeignKey(q => q.SubjectId);
        builder.HasOne<Topic>().WithMany().HasForeignKey(q => q.TopicId);
        builder.HasOne<TeacherUser>().WithMany().HasForeignKey(q => q.AuthorTeacherId);
    }
}

public sealed class QuestionVersionConfiguration : IEntityTypeConfiguration<QuestionVersion>
{
    public void Configure(EntityTypeBuilder<QuestionVersion> builder)
    {
        builder.ToTable("question_versions");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Type).HasConversion<int>();
        // Определение версии хранится JSONB со schemaVersion (ТЗ §15); published-редакция неизменяема.
        builder.Property(v => v.DefinitionJson).HasColumnType("jsonb");
        builder.Property(v => v.MaxPoints).HasPrecision(10, 3);
        builder.HasIndex(v => new { v.QuestionId, v.VersionNumber }).IsUnique();
        builder.HasIndex(v => v.PublishedAtUtc);
        builder.HasOne<Question>().WithMany().HasForeignKey(v => v.QuestionId);
    }
}

public sealed class DictionaryConfiguration : IEntityTypeConfiguration<Dictionary>
{
    public void Configure(EntityTypeBuilder<Dictionary> builder)
    {
        builder.ToTable("dictionaries");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Name).HasMaxLength(200);
        builder.HasOne<Subject>().WithMany().HasForeignKey(d => d.SubjectId);
    }
}

public sealed class DictionaryEntryConfiguration : IEntityTypeConfiguration<DictionaryEntry>
{
    public void Configure(EntityTypeBuilder<DictionaryEntry> builder)
    {
        builder.ToTable("dictionary_entries");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.OriginalText).HasMaxLength(1000);
        builder.Property(e => e.Lemma).HasMaxLength(200);
        builder.Property(e => e.SourceRef).HasMaxLength(200);
        builder.Property(e => e.Status).HasConversion<int>();
        builder.Property(e => e.Translations).HasColumnType("jsonb");
        builder.Property(e => e.Topics).HasColumnType("jsonb");
        builder.OwnsOne(e => e.Features, features =>
        {
            features.Property(f => f.FormKind).HasMaxLength(20);
            features.Property(f => f.Gender).HasMaxLength(10);
            features.Property(f => f.Declension).HasMaxLength(10);
            features.Property(f => f.GrammaticalCase).HasMaxLength(20);
        });
        builder.HasIndex(e => new { e.DictionaryId, e.Lemma });
        builder.HasIndex(e => e.Status);
        builder.HasOne<Dictionary>().WithMany().HasForeignKey(e => e.DictionaryId);
    }
}

public sealed class ImportJobConfiguration : IEntityTypeConfiguration<ImportJob>
{
    public void Configure(EntityTypeBuilder<ImportJob> builder)
    {
        builder.ToTable("import_jobs");
        builder.HasKey(j => j.Id);
        builder.Property(j => j.Status).HasConversion<int>();
        builder.Property(j => j.Error).HasMaxLength(1000);
        builder.HasIndex(j => j.Status);
        builder.HasOne<Dictionary>().WithMany().HasForeignKey(j => j.DictionaryId);
        builder.HasOne<FileAsset>().WithMany().HasForeignKey(j => j.FileAssetId);
    }
}

public sealed class TestConfiguration : IEntityTypeConfiguration<Test>
{
    public void Configure(EntityTypeBuilder<Test> builder)
    {
        builder.ToTable("tests");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Title).HasMaxLength(300);
        builder.HasOne<Subject>().WithMany().HasForeignKey(t => t.SubjectId);
        builder.HasOne<TeacherUser>().WithMany().HasForeignKey(t => t.AuthorTeacherId);
    }
}

public sealed class TestVersionConfiguration : IEntityTypeConfiguration<TestVersion>
{
    public void Configure(EntityTypeBuilder<TestVersion> builder)
    {
        builder.ToTable("test_versions");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Title).HasMaxLength(300);
        builder.Property(v => v.QuestionVersionIds).HasColumnType("jsonb");
        builder.Property(v => v.MaxPoints).HasPrecision(10, 3);
        builder.HasIndex(v => new { v.TestId, v.VersionNumber }).IsUnique();
        builder.HasIndex(v => v.PublishedAtUtc);
        builder.HasOne<Test>().WithMany().HasForeignKey(v => v.TestId);
    }
}

public sealed class VariantConfiguration : IEntityTypeConfiguration<Variant>
{
    public void Configure(EntityTypeBuilder<Variant> builder)
    {
        builder.ToTable("variants");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Seed).HasMaxLength(200);
        // Seed и фактический порядок сохраняются (ТЗ §8).
        builder.Property(v => v.QuestionOrder).HasColumnType("jsonb");
        builder.Property(v => v.OptionOrders).HasColumnType("jsonb");
        builder.HasIndex(v => new { v.TestVersionId, v.VariantNumber }).IsUnique();
        builder.HasOne<TestVersion>().WithMany().HasForeignKey(v => v.TestVersionId);
    }
}

public sealed class AssignmentConfiguration : IEntityTypeConfiguration<Assignment>
{
    public void Configure(EntityTypeBuilder<Assignment> builder)
    {
        builder.ToTable("assignments");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Mode).HasConversion<int>();
        builder.Property(a => a.Feedback).HasConversion<int>();
        builder.Property(a => a.CommonLinkCode).HasMaxLength(64);
        builder.HasIndex(a => a.CommonLinkCode).IsUnique();
        builder.HasIndex(a => a.TestVersionId);
        builder.HasIndex(a => a.OpensAtUtc);
        builder.HasOne<TestVersion>().WithMany().HasForeignKey(a => a.TestVersionId);
        builder.HasOne<TeacherUser>().WithMany().HasForeignKey(a => a.CreatedByTeacherId);
    }
}

public sealed class StudentAccountConfiguration : IEntityTypeConfiguration<Domain.People.StudentAccount>
{
    public void Configure(EntityTypeBuilder<Domain.People.StudentAccount> builder)
    {
        builder.ToTable("student_accounts");
        builder.HasKey(a => a.Id);
        // Студенческие логины отделены от учительских (ТЗ §5.3): отдельная таблица, отдельная схема входа.
        builder.HasIndex(a => a.Login).IsUnique();
        builder.Property(a => a.Login).HasMaxLength(64);
        builder.Property(a => a.PasswordHash).HasMaxLength(500);
        builder.HasOne<StudentProfile>().WithMany().HasForeignKey(a => a.StudentProfileId);
        builder.HasOne<TeacherUser>().WithMany().HasForeignKey(a => a.CreatedByTeacherId);
    }
}

public sealed class InvitationConfiguration : IEntityTypeConfiguration<Invitation>
{
    public void Configure(EntityTypeBuilder<Invitation> builder)
    {
        builder.ToTable("invitations");
        builder.HasKey(i => i.Id);
        // Хеш вместо кода; уникальный индекс — быстрый поиск при обмене (ADR-002).
        builder.HasIndex(i => i.CodeHash).IsUnique();
        builder.Property(i => i.CodeHash).HasMaxLength(64).IsFixedLength();
        builder.Property(i => i.Status).HasConversion<int>();
        builder.HasIndex(i => new { i.AssignmentId, i.StudentProfileId });
        builder.HasOne<Assignment>().WithMany().HasForeignKey(i => i.AssignmentId);
        builder.HasOne<StudentProfile>().WithMany().HasForeignKey(i => i.StudentProfileId);
        builder.HasOne<TeacherUser>().WithMany().HasForeignKey(i => i.CreatedByTeacherId);
    }
}

public sealed class ExtraAttemptGrantConfiguration : IEntityTypeConfiguration<ExtraAttemptGrant>
{
    public void Configure(EntityTypeBuilder<ExtraAttemptGrant> builder)
    {
        builder.ToTable("extra_attempt_grants");
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Reason).HasMaxLength(1000);
        builder.HasIndex(g => new { g.AssignmentId, g.StudentProfileId });
        builder.HasOne<Assignment>().WithMany().HasForeignKey(g => g.AssignmentId);
        builder.HasOne<StudentProfile>().WithMany().HasForeignKey(g => g.StudentProfileId);
        builder.HasOne<TeacherUser>().WithMany().HasForeignKey(g => g.GrantedByTeacherId);
        builder.HasOne<Attempt>().WithMany().HasForeignKey(g => g.UsedByAttemptId);
    }
}

public sealed class AnswerRevisionConfiguration : IEntityTypeConfiguration<AnswerRevision>
{
    public void Configure(EntityTypeBuilder<AnswerRevision> builder)
    {
        builder.ToTable("answer_revisions");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.AnswerJson).HasColumnType("jsonb");
        builder.HasIndex(r => new { r.QuestionSessionId, r.Revision }).IsUnique();
        builder.HasIndex(r => r.AttemptId);
        builder.HasOne<QuestionSession>().WithMany().HasForeignKey(r => r.QuestionSessionId);
        builder.HasOne<Attempt>().WithMany().HasForeignKey(r => r.AttemptId);
    }
}

public sealed class QuestionGradeConfiguration : IEntityTypeConfiguration<Domain.Grading.QuestionGrade>
{
    public void Configure(EntityTypeBuilder<Domain.Grading.QuestionGrade> builder)
    {
        builder.ToTable("question_grades");
        builder.HasKey(g => g.Id);
        builder.Property(g => g.State).HasConversion<int>();
        builder.Property(g => g.MaxPoints).HasPrecision(10, 3);
        builder.Property(g => g.EarnedPoints).HasPrecision(10, 3);
        builder.Property(g => g.CriterionId).HasMaxLength(100);
        builder.Property(g => g.Label).HasMaxLength(300);
        builder.Property(g => g.Reason).HasMaxLength(1000);
        builder.Property(g => g.TeacherComment).HasMaxLength(1000);
        builder.HasIndex(g => new { g.QuestionSessionId, g.CriterionId }).IsUnique();
        builder.HasIndex(g => g.AttemptId);
        builder.HasIndex(g => g.State);
        builder.HasOne<Attempt>().WithMany().HasForeignKey(g => g.AttemptId);
        builder.HasOne<QuestionSession>().WithMany().HasForeignKey(g => g.QuestionSessionId);
    }
}

public sealed class GradeRevisionConfiguration : IEntityTypeConfiguration<Domain.Grading.GradeRevision>
{
    public void Configure(EntityTypeBuilder<Domain.Grading.GradeRevision> builder)
    {
        builder.ToTable("grade_revisions");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.CriterionId).HasMaxLength(100);
        builder.Property(r => r.Reason).HasMaxLength(1000);
        builder.Property(r => r.OldEarnedPoints).HasPrecision(10, 3);
        builder.Property(r => r.NewEarnedPoints).HasPrecision(10, 3);
        builder.Property(r => r.OldAttemptEarned).HasPrecision(10, 3);
        builder.Property(r => r.NewAttemptEarned).HasPrecision(10, 3);
        builder.HasIndex(r => r.AttemptId);
        builder.HasOne<Attempt>().WithMany().HasForeignKey(r => r.AttemptId);
        builder.HasOne<TeacherUser>().WithMany().HasForeignKey(r => r.TeacherId);
    }
}

public sealed class PaperWorkConfiguration : IEntityTypeConfiguration<Domain.Assignments.PaperWork>
{
    public void Configure(EntityTypeBuilder<Domain.Assignments.PaperWork> builder)
    {
        builder.ToTable("paper_works");
        builder.HasKey(w => w.Id);
        builder.Property(w => w.StudentNameSnapshot).HasMaxLength(300);
        builder.Property(w => w.StudentGroupSnapshot).HasMaxLength(200);
        builder.Property(w => w.Comment).HasMaxLength(2000);
        builder.Property(w => w.TotalEarned).HasPrecision(10, 3);
        builder.Property(w => w.TotalMax).HasPrecision(10, 3);
        builder.HasMany(w => w.Scores).WithOne().HasForeignKey(s => s.PaperWorkId);
        builder.HasOne<Assignment>().WithMany().HasForeignKey(w => w.AssignmentId);
        builder.HasOne<TeacherUser>().WithMany().HasForeignKey(w => w.EnteredByTeacherId);
        builder.HasIndex(w => w.AssignmentId);
    }
}

public sealed class PaperScoreConfiguration : IEntityTypeConfiguration<Domain.Assignments.PaperScore>
{
    public void Configure(EntityTypeBuilder<Domain.Assignments.PaperScore> builder)
    {
        builder.ToTable("paper_scores");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Label).HasMaxLength(500);
        builder.Property(s => s.Comment).HasMaxLength(1000);
        builder.Property(s => s.Earned).HasPrecision(10, 3);
        builder.Property(s => s.Max).HasPrecision(10, 3);
        builder.HasIndex(s => s.PaperWorkId);
    }
}

public sealed class PaperScanConfiguration : IEntityTypeConfiguration<PaperScan>
{
    public void Configure(EntityTypeBuilder<PaperScan> builder)
    {
        builder.ToTable("paper_scans");
        builder.HasKey(s => s.Id);
        builder.HasIndex(s => s.PaperWorkId);
        builder.HasOne<Domain.Assignments.PaperWork>().WithMany().HasForeignKey(s => s.PaperWorkId);
        builder.HasOne<FileAsset>().WithMany().HasForeignKey(s => s.FileAssetId);
    }
}

/// <summary>Приватное вложение-скан бумажной работы (ТЗ §13); без публичных URL.</summary>
public sealed class PaperScan
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid PaperWorkId { get; private set; }
    public Guid FileAssetId { get; private set; }
    public DateTimeOffset AttachedAtUtc { get; private set; }

    private PaperScan() { }

    public static PaperScan Create(Guid paperWorkId, Guid fileAssetId, DateTimeOffset nowUtc)
        => new() { PaperWorkId = paperWorkId, FileAssetId = fileAssetId, AttachedAtUtc = nowUtc };
}

public sealed class MaterialConfiguration : IEntityTypeConfiguration<Domain.Materials.Material>
{
    public void Configure(EntityTypeBuilder<Domain.Materials.Material> builder)
    {
        builder.ToTable("materials");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Title).HasMaxLength(300);
        builder.Property(m => m.Description).HasMaxLength(2000);
        builder.Property(m => m.SourceNote).HasMaxLength(1000);
        builder.Property(m => m.Kind).HasConversion<int>();
        builder.Property(m => m.Status).HasConversion<int>();
        builder.HasOne<Domain.Subjects.Subject>().WithMany().HasForeignKey(m => m.SubjectId);
        builder.HasOne<Domain.Subjects.Topic>().WithMany().HasForeignKey(m => m.TopicId);
        builder.HasOne<FileAsset>().WithMany().HasForeignKey(m => m.FileAssetId);
        builder.HasOne<TeacherUser>().WithMany().HasForeignKey(m => m.AuthorTeacherId);
        builder.HasIndex(m => m.Status);
        builder.HasIndex(m => m.SubjectId);
    }
}

public sealed class MaterialGrantConfiguration : IEntityTypeConfiguration<Domain.Materials.MaterialGrant>
{
    public void Configure(EntityTypeBuilder<Domain.Materials.MaterialGrant> builder)
    {
        builder.ToTable("material_grants");
        builder.HasKey(g => g.Id);
        builder.HasIndex(g => new { g.MaterialId, g.StudentProfileId }).IsUnique()
            .HasFilter("\"StudentProfileId\" IS NOT NULL");
        builder.HasIndex(g => new { g.MaterialId, g.GroupId }).IsUnique()
            .HasFilter("\"GroupId\" IS NOT NULL");
        builder.HasOne<Domain.Materials.Material>().WithMany().HasForeignKey(g => g.MaterialId);
        builder.HasOne<Domain.People.Group>().WithMany().HasForeignKey(g => g.GroupId);
        builder.HasOne<Domain.People.StudentProfile>().WithMany().HasForeignKey(g => g.StudentProfileId);
        builder.HasOne<TeacherUser>().WithMany().HasForeignKey(g => g.GrantedByTeacherId);
    }
}

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<Domain.Auditing.AuditEvent>
{
    public void Configure(EntityTypeBuilder<Domain.Auditing.AuditEvent> builder)
    {
        builder.ToTable("audit_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Kind).HasMaxLength(100);
        builder.Property(e => e.TargetType).HasMaxLength(100);
        builder.Property(e => e.Details).HasMaxLength(2000);
        builder.HasIndex(e => new { e.TargetType, e.TargetId });
        builder.HasIndex(e => e.CreatedAtUtc);
    }
}
