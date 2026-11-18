namespace ScholarFlow.Core.Models;

public enum NotificationKind
{
    NewAssignment,
    DueDateChanged,
    DueSoon,
    GradePosted,
    MaterialDownloaded,
    MasterPdfCompiled,
    SessionExpired,
    Error
}

public sealed record AppNotification(
    NotificationKind Kind,
    string Title,
    string Message,
    string? CourseFolder = null,
    string? LmsUrl = null,
    DateTimeOffset? Timestamp = null)
{
    public DateTimeOffset OccurredAt { get; init; } = Timestamp ?? DateTimeOffset.UtcNow;
}

public sealed class PollAssignmentSnapshot
{
    public string LmsId { get; set; } = string.Empty;
    public string CourseId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Score { get; set; }
    public DateTimeOffset? DueDate { get; set; }
    public string Url { get; set; } = string.Empty;
}

public sealed class PollState
{
    public DateTimeOffset? LastFastPollAt { get; set; }
    public DateTimeOffset? LastFullSyncAt { get; set; }
    public Dictionary<string, PollAssignmentSnapshot> AssignmentsByLmsId { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
