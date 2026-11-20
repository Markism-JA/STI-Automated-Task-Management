namespace ScholarFlow.Core.Models;

public enum TaskPriority
{
    P1 = 1,
    P2 = 2,
    P3 = 3,
    P4 = 4,
}

public sealed record TaskSubItem(string Title, bool IsCompleted, DateOnly? CompletedDate = null);

public sealed class TaskItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string? ParentTaskId { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public DateOnly? DueDate { get; set; }
    public TimeOnly? DueTime { get; set; }
    public DateOnly? CompletedDate { get; set; }
    public TaskPriority Priority { get; set; } = TaskPriority.P4;
    public int Difficulty { get; set; } = 1;
    public string? CourseFolder { get; set; }
    public string? CourseAlias { get; set; }

    /// <summary>
    /// Academic term this task belongs to: "Prelims", "Midterms", "Prefinals", or "Finals".
    /// </summary>
    public string? Term { get; set; }

    /// <summary>
    /// Optional module or Todoist section grouping within a course or project (e.g. "05 Handout 1", "Laboratory Exercises").
    /// </summary>
    public string? Section { get; set; }

    /// <summary>
    /// eLMS assignment type (e.g. "Quiz", "Dropbox", "Freeform", "Discussion").
    /// </summary>
    public string? LmsKind { get; set; }

    /// <summary>
    /// Optional grading weight percentage scraped from eLMS.
    /// </summary>
    public double? WeightPercent { get; set; }

    public List<string> Labels { get; set; } = [];

    /// <summary>
    /// Instructor-provided assignment worksheet/prompt PDFs and attachments ([attachment:: ...]).
    /// </summary>
    public List<string> InstructionFiles { get; set; } = [];

    /// <summary>
    /// Student working drafts (ongoing/) or submitted deliverables (completed/) ([submission:: ...]).
    /// </summary>
    public List<string> SubmissionFiles { get; set; } = [];

    /// <summary>
    /// Legacy combined file links ([file:: ...]). Kept in sync with InstructionFiles and SubmissionFiles.
    /// </summary>
    public List<string> LinkedFiles { get; set; } = [];

    public string? NotePath { get; set; }
    public string? LmsId { get; set; }
    public string? LmsStatus { get; set; }
    public string? LmsScore { get; set; }
    public string? LmsUrl { get; set; }

    /// <summary>
    /// First-class Todoist nested subtasks (each is a full TaskItem with its own DueDate, Priority, Labels, Attachments, etc.).
    /// </summary>
    public List<TaskItem> ChildTasks { get; set; } = [];

    /// <summary>
    /// Lightweight subtask view kept synchronized with ChildTasks for backwards compatibility.
    /// </summary>
    public List<TaskSubItem> SubTasks { get; set; } = [];

    public bool IsSubtasksExpanded { get; set; } = true;
    public string SourceMarkdownPath { get; set; } = string.Empty;
    public int LineNumber { get; set; }

    public bool IsInbox => string.IsNullOrWhiteSpace(CourseFolder);

    public void EnsureChildTasksSynced()
    {
        if (ChildTasks.Count == 0 && SubTasks.Count > 0)
        {
            for (int i = 0; i < SubTasks.Count; i++)
            {
                var sub = SubTasks[i];
                ChildTasks.Add(
                    new TaskItem
                    {
                        Id = $"{Id}:sub-{i + 1}",
                        ParentTaskId = Id,
                        Title = sub.Title,
                        IsCompleted = sub.IsCompleted,
                        CompletedDate = sub.CompletedDate,
                        CourseFolder = CourseFolder,
                        CourseAlias = CourseAlias,
                        Term = Term,
                        Section = Section,
                        SourceMarkdownPath = SourceMarkdownPath,
                    }
                );
            }
        }
        else if (ChildTasks.Count > 0)
        {
            foreach (var child in ChildTasks)
            {
                child.ParentTaskId = Id;
                child.CourseFolder ??= CourseFolder;
                child.CourseAlias ??= CourseAlias;
                child.Term ??= Term;
                if (string.IsNullOrWhiteSpace(child.SourceMarkdownPath))
                    child.SourceMarkdownPath = SourceMarkdownPath;
                child.EnsureChildTasksSynced();
            }

            SubTasks =
            [
                .. ChildTasks.Select(c => new TaskSubItem(c.Title, c.IsCompleted, c.CompletedDate)),
            ];
        }
    }

    public static TaskItem? FindById(IEnumerable<TaskItem> roots, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        foreach (var t in roots)
        {
            if (string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
                return t;
            var foundChild = FindById(t.ChildTasks, id);
            if (foundChild is not null)
                return foundChild;
        }

        return null;
    }

    public static TaskItem? FindParentOf(IEnumerable<TaskItem> roots, string? childId)
    {
        if (string.IsNullOrWhiteSpace(childId))
            return null;

        foreach (var t in roots)
        {
            if (
                t.ChildTasks.Any(c =>
                    string.Equals(c.Id, childId, StringComparison.OrdinalIgnoreCase)
                )
            )
                return t;
            var nested = FindParentOf(t.ChildTasks, childId);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    public IReadOnlyList<string> AllFiles =>
        [
            .. InstructionFiles
                .Concat(SubmissionFiles)
                .Concat(LinkedFiles)
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];

    public string? PrimaryPdfFile =>
        InstructionFiles.FirstOrDefault(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        ?? SubmissionFiles.FirstOrDefault(f =>
            f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
        )
        ?? LinkedFiles.FirstOrDefault(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

    public static string? NormalizeTerm(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var lower = raw.Trim().ToLowerInvariant();
        if (lower.Contains("prelim"))
            return "Prelims";
        if (lower.Contains("midterm"))
            return "Midterms";
        if (lower.Contains("prefinal") || lower.Contains("pre-final"))
            return "Prefinals";
        if (lower.Contains("final"))
            return "Finals";

        return null;
    }
}

public sealed record UnregisteredOngoingFile(
    string CourseFolder,
    string CourseTitle,
    string FileName,
    string RelativePath,
    string FullPath,
    long SizeBytes,
    DateTimeOffset LastModifiedAt
);
