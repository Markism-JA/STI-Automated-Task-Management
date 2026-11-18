namespace ScholarFlow.Elms.Models;

/// <summary>
/// Controls which file types and links are eligible for download during sync.
/// </summary>
public sealed record ResourceFilter
{
    public static ResourceFilter AcademicDefault { get; } = new();

    public IReadOnlySet<string> AllowedExtensions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".doc", ".pptx", ".ppt", ".xlsx", ".xls",
        ".zip", ".rar", ".7z", ".txt", ".md", ".csv", ".json"
    };

    public IReadOnlySet<string> AllowedMimePrefixes { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "application/vnd.openxmlformats-officedocument",
        "application/msword",
        "application/vnd.ms-",
        "application/zip",
        "application/x-zip",
        "text/plain",
        "text/markdown",
        "text/csv"
    };

    public bool AllowUnknownExtensionWhenMimeMatches { get; init; } = true;
}

/// <summary>
/// Action determined by refresh resolution for a single remote resource.
/// </summary>
public enum SyncActionKind
{
    New,
    Updated,
    MovedLocally,
    Unchanged,
    SkippedExternal
}

public sealed record SyncAction(
    ElmsResource Resource,
    SyncActionKind Kind,
    string TargetRelativePath,
    string? ExistingRelocatedPath = null,
    string? Reason = null);

public sealed record SyncPlan(
    ElmsCourse Course,
    IReadOnlyList<SyncAction> Actions,
    DateTimeOffset CreatedAt)
{
    public int DownloadCount => Actions.Count(a => a.Kind is SyncActionKind.New or SyncActionKind.Updated);
    public int RelocatedCount => Actions.Count(a => a.Kind == SyncActionKind.MovedLocally);
    public int UnchangedCount => Actions.Count(a => a.Kind == SyncActionKind.Unchanged);
    public int SkippedCount => Actions.Count(a => a.Kind == SyncActionKind.SkippedExternal);
}

public sealed record SyncOptions
{
    public required string OutputRootDirectory { get; init; }
    public int MaxConcurrentDownloads { get; init; } = 4;
    public bool ForceRedownload { get; init; }
    public bool DryRun { get; init; }
    public ResourceFilter Filter { get; init; } = ResourceFilter.AcademicDefault;
}

public sealed record SyncProgress(
    string Stage,
    string Message,
    int? Completed = null,
    int? Total = null,
    string? CourseTitle = null);

public sealed record SyncResult(
    ElmsCourse Course,
    int Downloaded,
    int Updated,
    int Relocated,
    int Unchanged,
    int Skipped,
    int Failed,
    IReadOnlyList<string> Errors,
    TimeSpan Duration);

/// <summary>
/// Persistent metadata for a single downloaded resource, keyed by canonical URL.
/// </summary>
public sealed class SyncedResourceRecord
{
    public string Url { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string? CourseId { get; set; }
    public string? ETag { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public long? ContentLength { get; set; }
    public string? Sha256 { get; set; }
    public DateTimeOffset LastSyncedAt { get; set; }
}

/// <summary>
/// Persistent sync state stored per Basin/output directory (e.g., .scholarflow/sync_state.json).
/// </summary>
public sealed class LocalSyncState
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset? LastFullSyncAt { get; set; }
    public Dictionary<string, SyncedResourceRecord> ResourcesByUrl { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
