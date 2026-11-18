namespace ScholarFlow.Core.Models;

public enum TaskPriority
{
    P1 = 1,
    P2 = 2,
    P3 = 3,
    P4 = 4
}

public sealed record TaskSubItem(
    string Title,
    bool IsCompleted,
    DateOnly? CompletedDate = null);

public sealed class TaskItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public DateOnly? DueDate { get; set; }
    public TimeOnly? DueTime { get; set; }
    public DateOnly? CompletedDate { get; set; }
    public TaskPriority Priority { get; set; } = TaskPriority.P4;
    public int Difficulty { get; set; } = 1;
    public string? CourseFolder { get; set; }
    public string? CourseAlias { get; set; }
    public List<string> Labels { get; set; } = [];
    public List<string> LinkedFiles { get; set; } = [];
    public string? NotePath { get; set; }
    public string? LmsId { get; set; }
    public string? LmsStatus { get; set; }
    public string? LmsScore { get; set; }
    public string? LmsUrl { get; set; }
    public List<TaskSubItem> SubTasks { get; set; } = [];
    public string SourceMarkdownPath { get; set; } = string.Empty;
    public int LineNumber { get; set; }

    public bool IsInbox => string.IsNullOrWhiteSpace(CourseFolder);
}

public sealed record UnregisteredOngoingFile(
    string CourseFolder,
    string CourseTitle,
    string FileName,
    string RelativePath,
    string FullPath,
    long SizeBytes,
    DateTimeOffset LastModifiedAt);
