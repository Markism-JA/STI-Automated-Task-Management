using ScholarFlow.Elms.Abstractions;
using ScholarFlow.Elms.Models;

namespace ScholarFlow.Elms.Sync;

/// <summary>
/// Resolves remote course snapshots against <see cref="LocalSyncState"/> and the local filesystem,
/// detecting new, updated, unchanged, and locally moved/renamed files without duplicate downloads.
/// Uses <see cref="ElmsResource.CanonicalUrl"/> so rotating ?lmsauth= tokens never trigger duplicate downloads.
/// </summary>
public sealed class SyncResolver : ISyncResolver
{
    public SyncPlan Resolve(
        ElmsCourseSnapshot snapshot,
        LocalSyncState localState,
        string outputRootDirectory,
        bool forceRedownload = false
    )
    {
        var actions = new List<SyncAction>();
        var courseRoot = Path.Combine(outputRootDirectory, snapshot.Course.SanitizedFolderName);
        var localFilesByName = BuildCourseFileIndex(courseRoot);

        foreach (
            var resource in snapshot.AllResources.DistinctBy(
                r => r.CanonicalUrl,
                StringComparer.OrdinalIgnoreCase
            )
        )
        {
            var targetRelPath = Path.Combine(
                    snapshot.Course.SanitizedFolderName,
                    resource.RelativeTargetPath
                )
                .Replace('\\', '/');

            if (resource.Kind is ElmsResourceKind.ExternalLink or ElmsResourceKind.ScormPackage)
            {
                actions.Add(
                    new SyncAction(
                        resource,
                        SyncActionKind.SkippedExternal,
                        targetRelPath,
                        Reason: "External or SCORM resource"
                    )
                );
                continue;
            }

            var hasRecord =
                localState.ResourcesByUrl.TryGetValue(resource.CanonicalUrl, out var record)
                || localState.ResourcesByUrl.TryGetValue(resource.Url, out record);

            if (forceRedownload || !hasRecord || record is null)
            {
                actions.Add(
                    new SyncAction(
                        resource,
                        SyncActionKind.New,
                        targetRelPath,
                        Reason: forceRedownload ? "Force redownload" : "Not previously synced"
                    )
                );
                continue;
            }

            if (HasRemoteChanged(resource, record))
            {
                actions.Add(
                    new SyncAction(
                        resource,
                        SyncActionKind.Updated,
                        targetRelPath,
                        Reason: "Remote metadata (ETag/LastModified/Size) changed"
                    )
                );
                continue;
            }

            var recordedFullPath = Path.Combine(outputRootDirectory, record.RelativePath);
            if (File.Exists(recordedFullPath))
            {
                actions.Add(
                    new SyncAction(
                        resource,
                        SyncActionKind.Unchanged,
                        record.RelativePath,
                        Reason: "Up to date on disk"
                    )
                );
                continue;
            }

            var expectedFullPath = Path.Combine(outputRootDirectory, targetRelPath);
            if (File.Exists(expectedFullPath))
            {
                actions.Add(
                    new SyncAction(
                        resource,
                        SyncActionKind.Unchanged,
                        targetRelPath,
                        Reason: "Exists at target path"
                    )
                );
                continue;
            }

            // Check if the user moved the file locally (e.g., from Lesson/ or ongoing/ to completed/)
            var lookupName = string.IsNullOrEmpty(record.FileName)
                ? resource.SanitizedFileName
                : record.FileName;
            if (localFilesByName.TryGetValue(lookupName, out var relocatedFullPath))
            {
                var relocatedRel = Path.GetRelativePath(outputRootDirectory, relocatedFullPath)
                    .Replace('\\', '/');
                actions.Add(
                    new SyncAction(
                        resource,
                        SyncActionKind.MovedLocally,
                        targetRelPath,
                        ExistingRelocatedPath: relocatedRel,
                        Reason: $"Relocated locally to '{relocatedRel}'"
                    )
                );
                continue;
            }

            actions.Add(
                new SyncAction(
                    resource,
                    SyncActionKind.New,
                    targetRelPath,
                    Reason: "Previously synced file missing locally"
                )
            );
        }

        return new SyncPlan(snapshot.Course, actions, DateTimeOffset.UtcNow);
    }

    private static bool HasRemoteChanged(ElmsResource remote, SyncedResourceRecord local)
    {
        if (
            !string.IsNullOrEmpty(remote.ETag)
            && !string.IsNullOrEmpty(local.ETag)
            && !string.Equals(remote.ETag, local.ETag, StringComparison.Ordinal)
        )
        {
            return true;
        }

        if (
            remote.LastModified.HasValue
            && local.LastModified.HasValue
            && remote.LastModified.Value > local.LastModified.Value
        )
        {
            return true;
        }

        if (
            remote.ContentLength.HasValue
            && local.ContentLength.HasValue
            && remote.ContentLength.Value != local.ContentLength.Value
        )
        {
            return true;
        }

        return false;
    }

    private static Dictionary<string, string> BuildCourseFileIndex(string courseRoot)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(courseRoot))
            return index;

        foreach (var file in Directory.EnumerateFiles(courseRoot, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            index.TryAdd(name, file);
        }

        return index;
    }
}
