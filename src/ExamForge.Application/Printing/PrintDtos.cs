namespace ExamForge.Application.Printing;

/// <summary>Общая часть печатного листа (ТЗ §13): A4, шапка с кодами версии/варианта.</summary>
public sealed record PrintSheetDto(
    string TestTitle,
    Guid TestVersionId,
    int VariantNumber,
    string Kind, // "student" | "teacher-key"
    IReadOnlyList<PrintQuestionDto> Questions)
{
    public decimal TotalPoints => Questions.Sum(q => q.MaxPoints);
}

/// <summary>
/// Вопрос печатного представления. Поля ключа (Key*) присутствуют ТОЛЬКО в teacher-версии;
/// студентский DTO строится отдельным типом без ключей (ТЗ §13, UI_KIT §5.9).
/// </summary>
public sealed record PrintQuestionDto(
    int Number,
    string Type,
    string Prompt,
    string? Instruction,
    decimal MaxPoints,
    IReadOnlyList<PrintOptionDto> Options,
    IReadOnlyList<PrintOptionDto> MatchingLeft,
    IReadOnlyList<PrintOptionDto> MatchingRight,
    IReadOnlyList<string> TileLetters,
    int Slots,
    IReadOnlyList<PrintOptionDto> OrderingItems,
    IReadOnlyList<string> BlankIds,
    IReadOnlyList<PrintFieldDto> Fields,
    // --- только TeacherKey ---
    string? KeyCorrectMarkers = null,
    string? KeyPairs = null,
    string? KeySequence = null,
    string? KeyOrders = null,
    string? KeyAcceptedValues = null,
    string? KeyNote = null);

public sealed record PrintOptionDto(string Marker, string Text);

public sealed record PrintFieldDto(string Label, string AnswerLine);

/// <summary>Студентский лист: БЕЗ ключей и правильных ответов (ТЗ §13).</summary>
public sealed record StudentPrintSheetDto(
    string TestTitle,
    Guid TestVersionId,
    int VariantNumber,
    IReadOnlyList<StudentPrintQuestionDto> Questions)
{
    public decimal TotalPoints => Questions.Sum(q => q.MaxPoints);
}

public sealed record StudentPrintQuestionDto(
    int Number,
    string Type,
    string Prompt,
    string? Instruction,
    decimal MaxPoints,
    IReadOnlyList<PrintOptionDto> Options,
    IReadOnlyList<PrintOptionDto> MatchingLeft,
    IReadOnlyList<PrintOptionDto> MatchingRight,
    IReadOnlyList<string> TileLetters,
    int Slots,
    IReadOnlyList<PrintOptionDto> OrderingItems,
    IReadOnlyList<string> BlankIds,
    IReadOnlyList<PrintFieldDto> Fields);
