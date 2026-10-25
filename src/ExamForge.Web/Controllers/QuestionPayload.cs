using ExamForge.Domain.Questions;

namespace ExamForge.Web.Controllers;

/// <summary>
/// Payload вопроса из UI-редактора (все 9 типов §7.2) → доменное QuestionDefinition.
/// Сервер доверяет только явным полям; валидация полной корректности — при публикации (§7.1).
/// </summary>
public sealed record QuestionPayload(
    Guid SubjectId,
    string Type,
    string Prompt,
    string? Instruction,
    decimal MaxPoints,
    int? TimeLimitSeconds,
    IReadOnlyList<QuestionPayload.OptionPayload>? Options,
    string? CorrectOptionId,
    IReadOnlyList<string>? CorrectOptionIds,
    string? Scoring,
    IReadOnlyList<string>? AcceptedAnswers,
    bool? IgnoreCase,
    bool? IgnoreDiacritics,
    bool? SendToReview,
    IReadOnlyList<QuestionPayload.FieldPayload>? Fields,
    IReadOnlyList<QuestionPayload.PairPayload>? Pairs,
    string? TargetWord,
    IReadOnlyList<string>? OrderItems,
    string? GapText,
    IReadOnlyList<QuestionPayload.BlankPayload>? Blanks)
{
    public sealed record OptionPayload(string Id, string Text);
    public sealed record FieldPayload(string Id, string Label, string Correct, decimal Points);
    public sealed record PairPayload(string LeftId, string Left, string RightId, string Right);
    public sealed record BlankPayload(string Id, string Accepted, decimal Points);

    public QuestionDefinition ToDefinition()
    {
        var type = Enum.Parse<QuestionType>(Type, ignoreCase: true);
        return new QuestionDefinition
        {
            Type = type,
            Prompt = Prompt,
            Instruction = Instruction,
            MaxPoints = MaxPoints,
            ProposedTimeLimitSeconds = TimeLimitSeconds,
            Options = Options?.Select(o => new OptionDef(o.Id, o.Text)).ToList() ?? [],
            CorrectOptionId = CorrectOptionId,
            CorrectOptionIds = CorrectOptionIds ?? [],
            MultipleChoiceScoring = Scoring is "PartialPerCorrect"
                ? MultipleChoiceScoring.PartialPerCorrect
                : MultipleChoiceScoring.AllOrNothing,
            AcceptedAnswers = AcceptedAnswers?.Select(a => new AcceptedAnswer(a)).ToList() ?? [],
            Normalization = new ShortTextNormalization
            {
                IgnoreCase = IgnoreCase ?? true,
                IgnoreDiacritics = IgnoreDiacritics ?? true,
            },
            SendUnknownShortAnswersToReview = SendToReview ?? false,
            StructuredFields = Fields?.Select(f => new StructuredFieldDef(f.Id, f.Label, f.Correct, f.Points)).ToList() ?? [],
            MatchingPairs = Pairs?.Select(p => new MatchingPairDef(p.LeftId, p.Left, p.RightId, p.Right)).ToList() ?? [],
            LetterTiles = TargetWord is { Length: > 0 }
                ? [.. DictionaryDraftFactory.BuildTiles(TargetWord)]
                : [],
            TargetSequence = TargetWord,
            OrderingItems = OrderItems?.Select((item, i) => new OptionDef($"o{i}", item)).ToList() ?? [],
            AcceptedOrders = OrderItems is { Count: > 0 } ? [Enumerable.Range(0, OrderItems.Count).Select(i => $"o{i}").ToList()] : [],
            GapTextWithPlaceholders = GapText,
            GapBlanks = Blanks?.Select(b => new GapBlankDef(b.Id,
                b.Accepted.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), b.Points)).ToList() ?? [],
            RequiresManualReview = type == QuestionType.LongText,
        };
    }
}
