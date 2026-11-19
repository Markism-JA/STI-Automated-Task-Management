using System.Text.Json.Serialization;

namespace ScholarFlow.Core.Quiz;

public enum QuizQuestionKind
{
    MultipleChoice,
    TrueFalse,
    Identification,
    Situational,
    FillInTheBlank,
    Ordering
}

public enum CognitiveLevel
{
    Recall,
    Comprehension,
    Application,
    Analysis
}

public enum QuizPresetMode
{
    ExamSimulator,
    QuickReview,
    WeakTopicDrill,
    Custom
}

public enum QuestionAllocationMode
{
    Percentages,
    ExactCounts
}

/// <summary>
/// Normalized top-left [0..1] bounding box on a PDF page so highlights scale cleanly at any zoom level.
/// </summary>
public sealed record NormalizedHighlightRect(
    double X,
    double Y,
    double Width,
    double Height);

/// <summary>
/// JSON DTO representing the exact statement and page inside the Master/Handout PDF that grounds a question.
/// </summary>
public sealed class EvidenceAnchorDto
{
    [JsonPropertyName("pageNumber")]
    public int PageNumber { get; set; } = 1;

    [JsonPropertyName("sectionTitle")]
    public string SectionTitle { get; set; } = string.Empty;

    [JsonPropertyName("verbatimQuote")]
    public string VerbatimQuote { get; set; } = string.Empty;
}

/// <summary>
/// JSON DTO for a single AI-generated or imported question.
/// </summary>
public sealed class GeneratedQuestionDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "MultipleChoice";

    [JsonPropertyName("cognitiveLevel")]
    public string CognitiveLevel { get; set; } = "Comprehension";

    [JsonPropertyName("difficulty")]
    public int Difficulty { get; set; } = 3;

    [JsonPropertyName("topicTag")]
    public string TopicTag { get; set; } = "general";

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;

    [JsonPropertyName("options")]
    public List<string> Options { get; set; } = [];

    [JsonPropertyName("correctAnswer")]
    public string CorrectAnswer { get; set; } = string.Empty;

    [JsonPropertyName("acceptedAliases")]
    public List<string> AcceptedAliases { get; set; } = [];

    [JsonPropertyName("explanation")]
    public string Explanation { get; set; } = string.Empty;

    [JsonPropertyName("evidence")]
    public EvidenceAnchorDto Evidence { get; set; } = new();
}

/// <summary>
/// Root JSON DTO returned by the AI model or pasted via Manual JSON Import.
/// </summary>
public sealed class QuizGenerationResponseDto
{
    [JsonPropertyName("quizTitle")]
    public string QuizTitle { get; set; } = string.Empty;

    [JsonPropertyName("courseTerm")]
    public string CourseTerm { get; set; } = string.Empty;

    [JsonPropertyName("keyConceptsCovered")]
    public List<string> KeyConceptsCovered { get; set; } = [];

    [JsonPropertyName("questions")]
    public List<GeneratedQuestionDto> Questions { get; set; } = [];
}

/// <summary>
/// Persistent question record stored in SQLite (scholarflow.db) with grounded PDF highlight coordinates and SRS state.
/// </summary>
public sealed class QuizQuestionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string QuizId { get; set; } = string.Empty;
    public int OrderIndex { get; set; }
    public QuizQuestionKind Kind { get; set; } = QuizQuestionKind.MultipleChoice;
    public CognitiveLevel CognitiveLevel { get; set; } = CognitiveLevel.Comprehension;
    public int Difficulty { get; set; } = 3;
    public string TopicTag { get; set; } = "general";
    public string Prompt { get; set; } = string.Empty;
    public List<string> Options { get; set; } = [];
    public string CorrectAnswer { get; set; } = string.Empty;
    public List<string> AcceptedAliases { get; set; } = [];
    public string Explanation { get; set; } = string.Empty;

    // Grounding to Master / Handout PDF
    public string SourcePdfPath { get; set; } = string.Empty;
    public int SourcePage { get; set; } = 1;
    public string SectionTitle { get; set; } = string.Empty;
    public string VerbatimQuote { get; set; } = string.Empty;
    public List<NormalizedHighlightRect> HighlightRects { get; set; } = [];

    // Spaced Repetition (SM-2 / FSRS hybrid) & Attempt History
    public double SrsIntervalDays { get; set; }
    public double SrsEaseFactor { get; set; } = 2.5;
    public DateOnly? SrsDueDate { get; set; }
    public int SrsReps { get; set; }
    public int SrsLapses { get; set; }
    public bool? LastAnsweredCorrect { get; set; }

    public bool EvaluateAnswer(string? userAnswer)
    {
        if (string.IsNullOrWhiteSpace(userAnswer))
            return false;

        var cleanUser = NormalizeAnswerToken(userAnswer);
        var cleanCorrect = NormalizeAnswerToken(CorrectAnswer);

        if (string.Equals(cleanUser, cleanCorrect, StringComparison.OrdinalIgnoreCase))
            return true;

        if (Kind == QuizQuestionKind.Ordering)
        {
            var expectedOrder = Options.Count >= 2
                ? string.Join(" -> ", Options.Select(NormalizeAnswerToken))
                : cleanCorrect;
            var userParts = userAnswer
                .Split(["->", "|", "\n", ";"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(NormalizeAnswerToken)
                .Where(s => s.Length > 0)
                .ToList();
            if (userParts.Count >= 2)
            {
                var normalizedUserOrder = string.Join(" -> ", userParts);
                if (string.Equals(normalizedUserOrder, expectedOrder, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        // If MCQ option starts with "A) ..." or user passed option text matching CorrectAnswer
        if (Kind is QuizQuestionKind.MultipleChoice or QuizQuestionKind.Situational)
        {
            foreach (var opt in Options)
            {
                if (string.Equals(NormalizeAnswerToken(opt), cleanUser, StringComparison.OrdinalIgnoreCase))
                {
                    var strippedOpt = StripOptionPrefix(opt);
                    var strippedCorrect = StripOptionPrefix(CorrectAnswer);
                    if (string.Equals(NormalizeAnswerToken(strippedOpt), NormalizeAnswerToken(strippedCorrect), StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }

        foreach (var alias in AcceptedAliases)
        {
            if (!string.IsNullOrWhiteSpace(alias) &&
                string.Equals(cleanUser, NormalizeAnswerToken(alias), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string StripOptionPrefix(string text)
    {
        var t = text.Trim();
        if (t.Length >= 3 && char.IsLetter(t[0]) && (t[1] is ')' or '.' or ':') && char.IsWhiteSpace(t[2]))
            return t[3..].Trim();
        return t;
    }

    private static string NormalizeAnswerToken(string raw)
    {
        var chars = raw.Trim()
            .Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))
            .ToArray();
        return string.Join(' ', new string(chars).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }
}

public sealed record QuizInProgressAnswer(string Answer, bool IsCorrect);

/// <summary>
/// Persistent quiz session stored in SQLite (scholarflow.db).
/// </summary>
public sealed class QuizSessionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string CourseFolder { get; set; } = string.Empty;
    public string CourseTitle { get; set; } = string.Empty;
    public string? Term { get; set; }
    public string Title { get; set; } = string.Empty;
    public string SourcePdfPath { get; set; } = string.Empty;
    public long? SourcePdfMtimeTicks { get; set; }
    public string ModelUsed { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public int AttemptCount { get; set; }
    public double? LastScorePercent { get; set; }
    public int? InProgressQuestionIndex { get; set; }
    public Dictionary<string, QuizInProgressAnswer> InProgressAnswers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<QuizQuestionRecord> Questions { get; set; } = [];

    public bool HasInProgressAttempt =>
        Questions.Count > 0 && (InProgressAnswers.Count > 0 || (InProgressQuestionIndex ?? 0) > 0);

    public bool IsSourcePdfStale()
    {
        if (string.IsNullOrWhiteSpace(SourcePdfPath) || !File.Exists(SourcePdfPath))
            return false;

        var currentTicks = File.GetLastWriteTimeUtc(SourcePdfPath).Ticks;
        if (SourcePdfMtimeTicks.HasValue)
            return SourcePdfMtimeTicks.Value != currentTicks;

        // For legacy sessions without SourcePdfMtimeTicks, compare PDF write time vs quiz creation time
        return File.GetLastWriteTimeUtc(SourcePdfPath) > CreatedAtUtc.UtcDateTime.AddSeconds(5);
    }
}

public sealed record PdfPageRange(int StartPage, int EndPage)
{
    public bool Contains(int page) => page >= StartPage && page <= EndPage;
}

public sealed record PdfOutlineSectionInfo(
    string Title,
    string? Term,
    int Level,
    int StartPage,
    int EndPage)
{
    public int PageCount => Math.Max(1, EndPage - StartPage + 1);
}

public sealed class QuizGenerationRequest
{
    public string BasinRoot { get; set; } = string.Empty;
    public string CourseFolder { get; set; } = string.Empty;
    public string CourseTitle { get; set; } = string.Empty;
    public string? Term { get; set; }
    public string SourcePdfPath { get; set; } = string.Empty;
    public string? SectionFilter { get; set; }
    public int? StartPage { get; set; }
    public int? EndPage { get; set; }
    public List<PdfPageRange> SelectedRanges { get; set; } = [];
    public int QuestionCount { get; set; } = 10;
    public QuizPresetMode Preset { get; set; } = QuizPresetMode.ExamSimulator;
    public QuestionAllocationMode AllocationMode { get; set; } = QuestionAllocationMode.Percentages;
    public Dictionary<QuizQuestionKind, int> KindAllocations { get; set; } = GetDefaultAllocationsForPreset(QuizPresetMode.ExamSimulator);
    public List<string> ExcludePromptsOrTopics { get; set; } = [];
    public string? AppendToQuizId { get; set; }
    public string ProviderBaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
    public string? ApiKey { get; set; }
    public string ModelName { get; set; } = "gemini-2.5-flash";
    public bool UseOfflineHeuristicFallback { get; set; }

    public static Dictionary<QuizQuestionKind, int> GetDefaultAllocationsForPreset(
        QuizPresetMode preset,
        QuestionAllocationMode mode = QuestionAllocationMode.Percentages)
    {
        if (mode == QuestionAllocationMode.ExactCounts)
        {
            return preset switch
            {
                QuizPresetMode.QuickReview => new()
                {
                    [QuizQuestionKind.MultipleChoice] = 5,
                    [QuizQuestionKind.Situational] = 0,
                    [QuizQuestionKind.Identification] = 3,
                    [QuizQuestionKind.TrueFalse] = 2,
                    [QuizQuestionKind.FillInTheBlank] = 0,
                    [QuizQuestionKind.Ordering] = 0
                },
                QuizPresetMode.WeakTopicDrill => new()
                {
                    [QuizQuestionKind.MultipleChoice] = 2,
                    [QuizQuestionKind.Situational] = 3,
                    [QuizQuestionKind.Identification] = 3,
                    [QuizQuestionKind.TrueFalse] = 0,
                    [QuizQuestionKind.FillInTheBlank] = 2,
                    [QuizQuestionKind.Ordering] = 0
                },
                _ => new()
                {
                    [QuizQuestionKind.MultipleChoice] = 3,
                    [QuizQuestionKind.Situational] = 2,
                    [QuizQuestionKind.Identification] = 2,
                    [QuizQuestionKind.TrueFalse] = 1,
                    [QuizQuestionKind.FillInTheBlank] = 1,
                    [QuizQuestionKind.Ordering] = 1
                }
            };
        }

        return preset switch
        {
            QuizPresetMode.QuickReview => new()
            {
                [QuizQuestionKind.MultipleChoice] = 50,
                [QuizQuestionKind.Situational] = 0,
                [QuizQuestionKind.Identification] = 30,
                [QuizQuestionKind.TrueFalse] = 20,
                [QuizQuestionKind.FillInTheBlank] = 0,
                [QuizQuestionKind.Ordering] = 0
            },
            QuizPresetMode.WeakTopicDrill => new()
            {
                [QuizQuestionKind.MultipleChoice] = 20,
                [QuizQuestionKind.Situational] = 30,
                [QuizQuestionKind.Identification] = 30,
                [QuizQuestionKind.TrueFalse] = 0,
                [QuizQuestionKind.FillInTheBlank] = 20,
                [QuizQuestionKind.Ordering] = 0
            },
            _ => new()
            {
                [QuizQuestionKind.MultipleChoice] = 30,
                [QuizQuestionKind.Situational] = 20,
                [QuizQuestionKind.Identification] = 20,
                [QuizQuestionKind.TrueFalse] = 10,
                [QuizQuestionKind.FillInTheBlank] = 10,
                [QuizQuestionKind.Ordering] = 10
            }
        };
    }

    /// <summary>
    /// Resolves either exact counts or percentage weights into deterministic per-kind item counts summing to totalCount.
    /// </summary>
    public Dictionary<QuizQuestionKind, int> ResolveExactKindCounts(int? targetTotalOverride = null)
    {
        var allKinds = Enum.GetValues<QuizQuestionKind>();
        var result = allKinds.ToDictionary(k => k, _ => 0);

        if (AllocationMode == QuestionAllocationMode.ExactCounts)
        {
            int explicitSum = 0;
            foreach (var k in allKinds)
            {
                int val = Math.Max(0, KindAllocations.GetValueOrDefault(k, 0));
                result[k] = val;
                explicitSum += val;
            }

            if (explicitSum == 0)
            {
                int fallbackTotal = Math.Clamp(targetTotalOverride ?? QuestionCount, 1, 150);
                result[QuizQuestionKind.MultipleChoice] = fallbackTotal;
                return result;
            }

            if (targetTotalOverride.HasValue && targetTotalOverride.Value > 0 && targetTotalOverride.Value != explicitSum)
            {
                return ScaleAllocationsToTarget(result, targetTotalOverride.Value);
            }

            return result;
        }

        int targetTotal = Math.Clamp(targetTotalOverride ?? QuestionCount, 1, 150);
        return ScaleAllocationsToTarget(KindAllocations, targetTotal);
    }

    private static Dictionary<QuizQuestionKind, int> ScaleAllocationsToTarget(
        IReadOnlyDictionary<QuizQuestionKind, int> weights,
        int targetTotal)
    {
        var allKinds = Enum.GetValues<QuizQuestionKind>();
        var result = allKinds.ToDictionary(k => k, _ => 0);

        int weightSum = allKinds.Sum(k => Math.Max(0, weights.GetValueOrDefault(k, 0)));
        if (weightSum <= 0)
        {
            result[QuizQuestionKind.MultipleChoice] = targetTotal;
            return result;
        }

        var remainders = new List<(QuizQuestionKind Kind, double Remainder, int Weight)>();
        int allocated = 0;

        foreach (var k in allKinds)
        {
            int w = Math.Max(0, weights.GetValueOrDefault(k, 0));
            if (w == 0) continue;

            double exact = (double)w * targetTotal / weightSum;
            int floor = (int)Math.Floor(exact);
            result[k] = floor;
            allocated += floor;
            remainders.Add((k, exact - floor, w));
        }

        int leftover = targetTotal - allocated;
        foreach (var item in remainders
                     .OrderByDescending(r => r.Remainder)
                     .ThenByDescending(r => r.Weight)
                     .ThenBy(r => (int)r.Kind)
                     .Take(Math.Max(0, leftover)))
        {
            result[item.Kind]++;
        }

        return result;
    }
}
