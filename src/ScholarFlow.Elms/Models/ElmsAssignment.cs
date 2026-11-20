namespace ScholarFlow.Elms.Models;

public enum AssignmentStatus
{
    Unknown,
    Pending,
    Submitted,
    Missing,
    Graded,
}

public enum ElmsAssignmentKind
{
    Unknown,
    Freeform,
    Dropbox,
    Quiz,
    Discussion,
    Survey,
    Offline,
}

/// <summary>
/// Summary row parsed from /student_assignments/list/{courseId}.
/// </summary>
public sealed record ElmsAssignmentSummary(
    string LmsId,
    string CourseId,
    string Title,
    string Url,
    ElmsAssignmentKind Kind,
    string? ParentModuleTitle,
    string? TermHeader,
    DateTimeOffset? DueDate,
    string? DueText,
    double? WeightPercent,
    AssignmentStatus Status,
    string? ScoreText,
    string? GradeText
);

/// <summary>
/// Detailed submission and grading metadata scraped from an assignment's right column and /submissions/{id} page.
/// </summary>
public sealed record ElmsSubmissionInfo(
    bool IsSubmitted,
    string? SubmittedAtText,
    int Attempts,
    int? MaxAttempts,
    bool AllowLateSubmissions,
    bool IsClosed,
    string? SubmissionDetailsUrl,
    IReadOnlyList<string> Comments,
    IReadOnlyList<ElmsResource> SubmittedFiles
)
{
    public static ElmsSubmissionInfo Empty { get; } =
        new(false, null, 0, null, true, false, null, [], []);
}

/// <summary>
/// Represents an assignment, quiz, or activity scraped from a course.
/// </summary>
public sealed record ElmsAssignment(
    string LmsId,
    string CourseId,
    string Title,
    string Url,
    DateTimeOffset? DueDate,
    AssignmentStatus Status,
    string? Score,
    string? InstructionsHtml,
    IReadOnlyList<ElmsResource> Attachments,
    ElmsAssignmentKind Kind = ElmsAssignmentKind.Unknown,
    string? ParentModuleTitle = null,
    string? TermHeader = null,
    int? MaxScore = null,
    double? WeightPercent = null,
    string? InstructionsMarkdown = null,
    ElmsSubmissionInfo? Submission = null,
    DateTimeOffset? StartDate = null,
    string? TimerText = null
)
{
    public ElmsSubmissionInfo SubmissionInfo => Submission ?? ElmsSubmissionInfo.Empty;

    public IEnumerable<ElmsResource> AllResources =>
        Attachments.Concat(SubmissionInfo.SubmittedFiles);
}
