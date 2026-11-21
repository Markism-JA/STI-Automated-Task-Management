using ScholarFlow.Core.Models;

namespace ScholarFlow.Core.Tasks;

public enum SmartViewKind
{
    Inbox,
    Today,
    Upcoming,
    All,
    Completed,
    Course,
    Label,
    Term,
}

/// <summary>
/// Provides Todoist-style smart views, term/label filtering, and scans for unregistered files inside ongoing/ folders.
/// </summary>
public static class TaskQueryService
{
    public static IReadOnlyList<TaskItem> Filter(
        IEnumerable<TaskItem> allTasks,
        SmartViewKind view,
        string? targetValue = null,
        string? searchQuery = null,
        DateOnly? todayOverride = null,
        string? termFilter = null
    )
    {
        var today = todayOverride ?? DateOnly.FromDateTime(DateTime.Today);

        IEnumerable<TaskItem> query = view switch
        {
            SmartViewKind.Inbox => allTasks.Where(t => !t.IsCompleted && t.IsInbox),
            SmartViewKind.Today => allTasks.Where(t =>
                !t.IsCompleted && t.DueDate.HasValue && t.DueDate.Value <= today
            ),
            SmartViewKind.Upcoming => allTasks.Where(t =>
                !t.IsCompleted && t.DueDate.HasValue && t.DueDate.Value > today
            ),
            SmartViewKind.Completed => string.IsNullOrWhiteSpace(targetValue)
                ? allTasks.Where(t => t.IsCompleted)
                : allTasks.Where(t =>
                    t.IsCompleted
                    && string.Equals(
                        t.CourseFolder,
                        targetValue,
                        StringComparison.OrdinalIgnoreCase
                    )
                ),
            SmartViewKind.Course => allTasks.Where(t =>
                !t.IsCompleted
                && string.Equals(t.CourseFolder, targetValue, StringComparison.OrdinalIgnoreCase)
            ),
            SmartViewKind.Label => allTasks.Where(t =>
                !t.IsCompleted
                && t.Labels.Contains(targetValue ?? "", StringComparer.OrdinalIgnoreCase)
            ),
            SmartViewKind.Term => allTasks.Where(t =>
                !t.IsCompleted && MatchesTerm(t, targetValue)
            ),
            _ => allTasks.Where(t => !t.IsCompleted),
        };

        if (!string.IsNullOrWhiteSpace(termFilter))
        {
            query = query.Where(t => MatchesTerm(t, termFilter));
        }

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var term = searchQuery.Trim();
            query = query.Where(t =>
                t.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (t.CourseFolder?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || (
                    t.CourseAlias?.Contains(term.TrimStart('#'), StringComparison.OrdinalIgnoreCase)
                    ?? false
                )
                || (t.Term?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || (t.Section?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || t.Labels.Any(l =>
                    l.Contains(term.TrimStart('@'), StringComparison.OrdinalIgnoreCase)
                )
            );
        }

        return view == SmartViewKind.Completed
                ? [.. query.OrderByDescending(t => t.CompletedDate).ThenBy(t => t.Title)]
            : view == SmartViewKind.Upcoming
                ?
                [
                    .. query
                        .OrderBy(t => t.DueDate)
                        .ThenByDescending(t => UrgencyCalculator.CalculateScore(t, today)),
                ]
            : query
                .OrderByDescending(t => UrgencyCalculator.CalculateScore(t, today))
                .ThenBy(t => t.DueDate)
                .ToList();
    }

    public static bool MatchesTerm(TaskItem task, string? expectedTerm)
    {
        if (string.IsNullOrWhiteSpace(expectedTerm))
            return true;

        var normalizedExpected = TaskItem.NormalizeTerm(expectedTerm) ?? expectedTerm.Trim();
        var taskTerm = TaskItem.NormalizeTerm(task.Term) ?? task.Term?.Trim();
        if (string.Equals(taskTerm, normalizedExpected, StringComparison.OrdinalIgnoreCase))
            return true;

        return task.Labels.Any(l =>
            string.Equals(
                TaskItem.NormalizeTerm(l) ?? l.Trim(),
                normalizedExpected,
                StringComparison.OrdinalIgnoreCase
            )
        );
    }

    public static IReadOnlyList<UnregisteredOngoingFile> FindUnregisteredOngoingFiles(
        string basinRoot,
        IReadOnlyList<BasinCourseEntry> courses,
        IReadOnlyList<TaskItem> allTasks
    )
    {
        var trackedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in allTasks)
        {
            foreach (var f in t.AllFiles)
                trackedFiles.Add(Path.GetFileName(f));
            if (!string.IsNullOrWhiteSpace(t.NotePath))
                trackedFiles.Add(Path.GetFileName(t.NotePath));
        }

        var results = new List<UnregisteredOngoingFile>();
        foreach (var course in courses.Where(c => !c.IsArchived))
        {
            var ongoingDir = Path.Combine(basinRoot, course.FolderName, "ongoing");
            if (!Directory.Exists(ongoingDir))
                continue;

            foreach (var filePath in Directory.EnumerateFiles(ongoingDir))
            {
                var name = Path.GetFileName(filePath);
                if (
                    name.StartsWith('.') || name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                )
                    continue;

                if (trackedFiles.Contains(name))
                    continue;

                var info = new FileInfo(filePath);
                results.Add(
                    new UnregisteredOngoingFile(
                        CourseFolder: course.FolderName,
                        CourseTitle: course.Title,
                        FileName: name,
                        RelativePath: $"ongoing/{name}",
                        FullPath: filePath,
                        SizeBytes: info.Length,
                        DateTimeOffset.FromFileTime(info.LastWriteTimeUtc.ToFileTimeUtc())
                    )
                );
            }
        }

        return results;
    }
}
