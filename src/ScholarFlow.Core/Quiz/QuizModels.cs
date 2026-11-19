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
    WeakTopicDrill
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
    public string ModelUsed { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public int AttemptCount { get; set; }
    public double? LastScorePercent { get; set; }
    public List<QuizQuestionRecord> Questions { get; set; } = [];
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
    public string ProviderBaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
    public string? ApiKey { get; set; }
    public string ModelName { get; set; } = "gemini-2.5-flash";
    public bool UseOfflineHeuristicFallback { get; set; }
}
