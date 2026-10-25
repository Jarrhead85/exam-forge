using ExamForge.Domain.Common;

namespace ExamForge.Domain.Assignments;

/// <summary>Балл за одно задание бумажной работы (ТЗ §13: по заданиям/критериям).</summary>
public sealed class PaperScore
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid PaperWorkId { get; private set; }
    /// <summary>Номер задания на печатном листе (порядок варианта).</summary>
    public int OrderIndex { get; private set; }
    public string Label { get; private set; } = string.Empty;
    public decimal Earned { get; private set; }
    public decimal Max { get; private set; }
    public string? Comment { get; private set; }

    private PaperScore() { }

    internal static PaperScore Create(Guid paperWorkId, int orderIndex, string label, decimal earned, decimal max, string? comment)
    {
        if (max <= 0m)
            throw new DomainException("Максимум баллов задания должен быть положительным.");
        if (earned < 0m || earned > max)
            throw new DomainException($"Баллы задания {orderIndex + 1} должны быть в [0; {max}].");
        return new()
        {
            PaperWorkId = paperWorkId,
            OrderIndex = orderIndex,
            Label = Guard.Required(label, "Задание", 500),
            Earned = earned,
            Max = max,
            Comment = string.IsNullOrWhiteSpace(comment) ? null : Guard.Required(comment, "Комментарий", 1000),
        };
    }
}

/// <summary>
/// Бумажная работа (ТЗ §13): баллы вводит преподаватель, Origin=Paper — вручную введённые
/// баллы не выдаются за автоматическое оценивание. Сканы приватны; срок их хранения — D-05.
/// </summary>
public sealed class PaperWork
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid AssignmentId { get; private set; }
    public Guid? StudentProfileId { get; private set; }
    public string StudentNameSnapshot { get; private set; } = string.Empty;
    public string? StudentGroupSnapshot { get; private set; }

    public Guid EnteredByTeacherId { get; private set; }
    public string? Comment { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public decimal TotalEarned { get; private set; }
    public decimal TotalMax { get; private set; }

    private readonly List<PaperScore> _scores = [];
    public IReadOnlyList<PaperScore> Scores => _scores;

    private PaperWork() { }

    public static PaperWork Create(
        Guid assignmentId,
        string studentName, string? studentGroup,
        Guid teacherId,
        IReadOnlyList<(int OrderIndex, string Label, decimal Earned, decimal Max, string? Comment)> scores,
        string? comment,
        Guid? studentProfileId,
        DateTimeOffset nowUtc)
    {
        if (scores.Count == 0)
            throw new DomainException("Бумажная работа требует хотя бы одного задания с баллами.");
        var work = new PaperWork
        {
            AssignmentId = assignmentId,
            StudentProfileId = studentProfileId,
            StudentNameSnapshot = Guard.Required(studentName, "ФИО студента", 300),
            StudentGroupSnapshot = string.IsNullOrWhiteSpace(studentGroup) ? null : studentGroup.Trim(),
            EnteredByTeacherId = teacherId,
            Comment = string.IsNullOrWhiteSpace(comment) ? null : Guard.Required(comment, "Комментарий", 2000),
            CreatedAtUtc = nowUtc,
        };
        foreach (var (orderIndex, label, earned, max, scoreComment) in scores)
            work._scores.Add(PaperScore.Create(work.Id, orderIndex, label, earned, max, scoreComment));
        work.TotalEarned = work._scores.Sum(s => s.Earned);
        work.TotalMax = work._scores.Sum(s => s.Max);
        if (work.TotalMax <= 0m)
            throw new DomainException("Сумма максимумов должна быть положительной.");
        return work;
    }
}
