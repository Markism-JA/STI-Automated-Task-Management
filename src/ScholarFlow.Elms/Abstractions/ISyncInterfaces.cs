using ScholarFlow.Elms.Models;

namespace ScholarFlow.Elms.Abstractions;

/// <summary>
/// Resolves differences between a remote course snapshot and local filesystem/sync state.
/// </summary>
public interface ISyncResolver
{
    /// <summary>
    /// Produces a deterministic <see cref="SyncPlan"/> classifying every resource as New, Updated, MovedLocally, Unchanged, or SkippedExternal.
    /// </summary>
    SyncPlan Resolve(
        ElmsCourseSnapshot snapshot,
        LocalSyncState localState,
        string outputRootDirectory,
        bool forceRedownload = false
    );
}

/// <summary>
/// Executes a <see cref="SyncPlan"/> with bounded concurrency, hashing, and state updates.
/// </summary>
public interface ISyncExecutor
{
    Task<SyncResult> ExecuteAsync(
        SyncPlan plan,
        SyncOptions options,
        LocalSyncState localState,
        IProgress<SyncProgress>? progress = null,
        CancellationToken ct = default
    );
}
