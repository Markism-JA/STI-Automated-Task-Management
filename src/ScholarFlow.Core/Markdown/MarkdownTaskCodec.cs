using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ScholarFlow.Core.Models;

namespace ScholarFlow.Core.Markdown;

/// <summary>
/// Losslessly parses and serializes Obsidian-compatible Markdown task lists (tasks.md and inbox.md),
/// using clean, emoji-free Dataview inline fields ([due::], [done::], [p::], [diff::], [file::], [lms_id::])
/// while retaining backward-compatible parsing for legacy emoji markers.
/// </summary>
public static partial class MarkdownTaskCodec
{
    [GeneratedRegex(@"^(\s*)- \[([ xX])\]\s+(.*)$", RegexOptions.Compiled)]
    private static partial Regex TaskLineRegex();

    [GeneratedRegex(@"📅\s*(\d{4}-\d{2}-\d{2})", RegexOptions.Compiled)]
    private static partial Regex LegacyDueDateRegex();

    [GeneratedRegex(@"⏰\s*(\d{2}:\d{2})", RegexOptions.Compiled)]
    private static partial Regex LegacyDueTimeRegex();

    [GeneratedRegex(@"✅\s*(\d{4}-\d{2}-\d{2})", RegexOptions.Compiled)]
    private static partial Regex LegacyCompletedDateRegex();

    [GeneratedRegex(@"\[([a-z_]+)::\s*([^\]]+)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex InlineFieldRegex();

    [GeneratedRegex(@"(?<!\S)#([A-Za-z0-9_\-]+)", RegexOptions.Compiled)]
    private static partial Regex CourseTagRegex();

    [GeneratedRegex(@"(?<!\S)@([A-Za-z0-9_\-]+)", RegexOptions.Compiled)]
    private static partial Regex LabelTagRegex();

    public static IReadOnlyList<TaskItem> Parse(string markdown, string sourcePath, string? defaultCourseFolder = null)
    {
        var tasks = new List<TaskItem>();
        if (string.IsNullOrEmpty(markdown))
            return tasks;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        TaskItem? currentParent = null;

        for (int i = 0; i < lines.Length; i++)
        {
            var match = TaskLineRegex().Match(lines[i]);
            if (!match.Success)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]))
                    currentParent = null;
                continue;
            }

            var indent = match.Groups[1].Value.Length;
            var isCompleted = match.Groups[2].Value is "x" or "X";
            var body = match.Groups[3].Value.Trim();

            if (indent >= 2 && currentParent is not null)
            {
                var subCompletedDate = ExtractCompletedDate(body, out var cleanSub);
                currentParent.SubTasks.Add(new TaskSubItem(cleanSub.Trim(), isCompleted, subCompletedDate));
                continue;
            }

            var task = ParseTaskBody(body, isCompleted, sourcePath, i + 1, defaultCourseFolder);
            tasks.Add(task);
            currentParent = task;
        }

        return tasks;
    }

    public static async Task<IReadOnlyList<TaskItem>> LoadFileAsync(
        string filePath,
        string? defaultCourseFolder = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            return [];

        var text = await File.ReadAllTextAsync(filePath, ct);
        return Parse(text, filePath, defaultCourseFolder);
    }

    public static string SerializeTask(TaskItem task)
    {
        var sb = new StringBuilder();
        sb.Append(task.IsCompleted ? "- [x] " : "- [ ] ");
        sb.Append(task.Title.Trim());

        if (task.DueDate.HasValue)
            sb.Append($" [due:: {task.DueDate.Value:yyyy-MM-dd}]");

        if (task.DueTime.HasValue)
            sb.Append($" [time:: {task.DueTime.Value:HH:mm}]");

        if (task.IsCompleted && task.CompletedDate.HasValue)
            sb.Append($" [done:: {task.CompletedDate.Value:yyyy-MM-dd}]");

        if (task.Priority != TaskPriority.P4)
            sb.Append($" [p:: {(int)task.Priority}]");

        if (task.Difficulty is > 1 and <= 5)
            sb.Append($" [diff:: {task.Difficulty}]");

        if (!string.IsNullOrWhiteSpace(task.CourseAlias))
            sb.Append($" #{task.CourseAlias.Trim().TrimStart('#')}");

        foreach (var label in task.Labels.Where(l => !string.IsNullOrWhiteSpace(l)).Distinct(StringComparer.OrdinalIgnoreCase))
            sb.Append($" @{label.Trim().TrimStart('@')}");

        foreach (var file in task.LinkedFiles.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
            sb.Append($" [file:: {file}]");

        if (!string.IsNullOrWhiteSpace(task.NotePath))
            sb.Append($" [note:: {task.NotePath}]");

        if (!string.IsNullOrWhiteSpace(task.LmsId))
            sb.Append($" [lms_id:: {task.LmsId}]");

        if (!string.IsNullOrWhiteSpace(task.LmsStatus))
            sb.Append($" [lms_status:: {task.LmsStatus}]");

        if (!string.IsNullOrWhiteSpace(task.LmsScore))
            sb.Append($" [lms_score:: {task.LmsScore}]");

        if (!string.IsNullOrWhiteSpace(task.LmsUrl))
            sb.Append($" [lms_url:: {task.LmsUrl}]");

        foreach (var sub in task.SubTasks)
        {
            sb.AppendLine();
            sb.Append(sub.IsCompleted ? "  - [x] " : "  - [ ] ");
            sb.Append(sub.Title.Trim());
            if (sub.IsCompleted && sub.CompletedDate.HasValue)
                sb.Append($" [done:: {sub.CompletedDate.Value:yyyy-MM-dd}]");
        }

        return sb.ToString();
    }

    public static string RenderDocument(IEnumerable<TaskItem> tasks, string? headerTitle = null)
    {
        var list = tasks.ToList();
        var ongoing = list.Where(t => !t.IsCompleted).ToList();
        var completed = list.Where(t => t.IsCompleted).ToList();

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(headerTitle))
        {
            sb.AppendLine($"# {headerTitle}");
            sb.AppendLine();
        }

        sb.AppendLine("## Ongoing");
        sb.AppendLine();
        foreach (var task in ongoing)
            sb.AppendLine(SerializeTask(task));

        if (completed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Completed");
            sb.AppendLine();
            foreach (var task in completed)
                sb.AppendLine(SerializeTask(task));
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    public static async Task SaveDocumentAsync(
        string filePath,
        IEnumerable<TaskItem> tasks,
        string? headerTitle = null,
        CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var content = RenderDocument(tasks, headerTitle);
        var temp = $"{filePath}.tmp.{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(temp, content, ct);
        File.Move(temp, filePath, overwrite: true);
    }

    public static async Task UpdateTaskInFileAsync(
        string basinRoot,
        TaskItem updatedTask,
        CancellationToken ct = default)
    {
        var sourcePath = !string.IsNullOrWhiteSpace(updatedTask.SourceMarkdownPath)
            ? updatedTask.SourceMarkdownPath
            : string.IsNullOrWhiteSpace(updatedTask.CourseFolder)
                ? Path.Combine(basinRoot, ".scholarflow", "inbox.md")
                : Path.Combine(basinRoot, updatedTask.CourseFolder, "tasks.md");

        var tasks = (await LoadFileAsync(sourcePath, updatedTask.CourseFolder, ct)).ToList();
        var idx = tasks.FindIndex(t =>
            (!string.IsNullOrWhiteSpace(updatedTask.LmsId) && string.Equals(t.LmsId, updatedTask.LmsId, StringComparison.OrdinalIgnoreCase)) ||
            (t.LineNumber == updatedTask.LineNumber && t.LineNumber > 0) ||
            t.Id == updatedTask.Id);

        if (idx >= 0)
            tasks[idx] = updatedTask;
        else
            tasks.Add(updatedTask);

        var header = string.IsNullOrWhiteSpace(updatedTask.CourseFolder)
            ? "Inbox"
            : updatedTask.CourseFolder.Replace('_', ' ');
        await SaveDocumentAsync(sourcePath, tasks, header, ct);
    }

    public static async Task DeleteTaskFromFileAsync(
        string basinRoot,
        TaskItem targetTask,
        CancellationToken ct = default)
    {
        var sourcePath = !string.IsNullOrWhiteSpace(targetTask.SourceMarkdownPath)
            ? targetTask.SourceMarkdownPath
            : string.IsNullOrWhiteSpace(targetTask.CourseFolder)
                ? Path.Combine(basinRoot, ".scholarflow", "inbox.md")
                : Path.Combine(basinRoot, targetTask.CourseFolder, "tasks.md");

        var tasks = (await LoadFileAsync(sourcePath, targetTask.CourseFolder, ct)).ToList();
        tasks.RemoveAll(t =>
            (!string.IsNullOrWhiteSpace(targetTask.LmsId) && string.Equals(t.LmsId, targetTask.LmsId, StringComparison.OrdinalIgnoreCase)) ||
            (t.LineNumber == targetTask.LineNumber && t.Title.Equals(targetTask.Title, StringComparison.OrdinalIgnoreCase)) ||
            t.Id == targetTask.Id);

        var header = string.IsNullOrWhiteSpace(targetTask.CourseFolder)
            ? "Inbox"
            : targetTask.CourseFolder.Replace('_', ' ');
        await SaveDocumentAsync(sourcePath, tasks, header, ct);
    }

    /// <summary>
    /// Idempotently merges incoming LMS tasks into an existing tasks list by [lms_id:: ...],
    /// preserving user-added subtasks, difficulty, labels, and manual [x] completion state.
    /// </summary>
    public static List<TaskItem> MergeLmsTasks(
        IEnumerable<TaskItem> existingTasks,
        IEnumerable<TaskItem> incomingLmsTasks)
    {
        var list = existingTasks.ToList();
        var byLmsId = list
            .Where(t => !string.IsNullOrWhiteSpace(t.LmsId))
            .ToDictionary(t => t.LmsId!, StringComparer.OrdinalIgnoreCase);

        foreach (var incoming in incomingLmsTasks)
        {
            if (!string.IsNullOrWhiteSpace(incoming.LmsId) &&
                byLmsId.TryGetValue(incoming.LmsId, out var existing))
            {
                existing.Title = incoming.Title;
                existing.DueDate = incoming.DueDate ?? existing.DueDate;
                existing.DueTime = incoming.DueTime ?? existing.DueTime;
                existing.LmsStatus = incoming.LmsStatus;
                existing.LmsScore = incoming.LmsScore;
                existing.LmsUrl = incoming.LmsUrl ?? existing.LmsUrl;
                existing.NotePath = incoming.NotePath ?? existing.NotePath;
                existing.CourseAlias ??= incoming.CourseAlias;
                existing.CourseFolder ??= incoming.CourseFolder;

                foreach (var f in incoming.LinkedFiles)
                {
                    if (!existing.LinkedFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
                        existing.LinkedFiles.Add(f);
                }

                if (incoming.IsCompleted && !existing.IsCompleted)
                {
                    existing.IsCompleted = true;
                    existing.CompletedDate ??= incoming.CompletedDate ?? DateOnly.FromDateTime(DateTime.Today);
                }
            }
            else
            {
                list.Add(incoming);
                if (!string.IsNullOrWhiteSpace(incoming.LmsId))
                    byLmsId[incoming.LmsId] = incoming;
            }
        }

        return list;
    }

    private static TaskItem ParseTaskBody(
        string body,
        bool isCompleted,
        string sourcePath,
        int lineNumber,
        string? defaultCourseFolder)
    {
        var working = body;

        DateOnly? dueDate = null;
        var dueMatch = LegacyDueDateRegex().Match(working);
        if (dueMatch.Success && DateOnly.TryParseExact(dueMatch.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dd))
        {
            dueDate = dd;
            working = working.Remove(dueMatch.Index, dueMatch.Length);
        }

        TimeOnly? dueTime = null;
        var timeMatch = LegacyDueTimeRegex().Match(working);
        if (timeMatch.Success && TimeOnly.TryParseExact(timeMatch.Groups[1].Value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            dueTime = dt;
            working = working.Remove(timeMatch.Index, timeMatch.Length);
        }

        var completedDate = ExtractCompletedDate(working, out working);

        var priority = TaskPriority.P4;
        if (working.Contains("🔺")) { priority = TaskPriority.P1; working = working.Replace("🔺", ""); }
        else if (working.Contains("⏫")) { priority = TaskPriority.P2; working = working.Replace("⏫", ""); }
        else if (working.Contains("🔼")) { priority = TaskPriority.P3; working = working.Replace("🔼", ""); }
        else if (working.Contains("🔽")) { priority = TaskPriority.P4; working = working.Replace("🔽", ""); }

        int difficulty = 1;
        string? lmsId = null, lmsStatus = null, lmsScore = null, lmsUrl = null, notePath = null;
        var linkedFiles = new List<string>();

        working = InlineFieldRegex().Replace(working, m =>
        {
            var key = m.Groups[1].Value.ToLowerInvariant();
            var val = m.Groups[2].Value.Trim();
            switch (key)
            {
                case "due" when DateOnly.TryParseExact(val, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDue):
                    dueDate = parsedDue;
                    break;
                case "time" when TimeOnly.TryParseExact(val, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedTime):
                    dueTime = parsedTime;
                    break;
                case "done" when DateOnly.TryParseExact(val, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDone):
                    completedDate = parsedDone;
                    break;
                case "p" or "priority" when int.TryParse(val.TrimStart('p', 'P'), out var pVal) && pVal is >= 1 and <= 4:
                    priority = (TaskPriority)pVal;
                    break;
                case "diff" when int.TryParse(val, out var d):
                    difficulty = Math.Clamp(d, 1, 5);
                    break;
                case "file":
                    linkedFiles.Add(val);
                    break;
                case "note":
                    notePath = val;
                    break;
                case "lms_id":
                    lmsId = val;
                    break;
                case "lms_status":
                    lmsStatus = val;
                    break;
                case "lms_score":
                    lmsScore = val;
                    break;
                case "lms_url":
                    lmsUrl = val;
                    break;
            }
            return string.Empty;
        });

        string? courseAlias = null;
        working = CourseTagRegex().Replace(working, m =>
        {
            courseAlias ??= m.Groups[1].Value;
            return string.Empty;
        });

        var labels = new List<string>();
        working = LabelTagRegex().Replace(working, m =>
        {
            labels.Add(m.Groups[1].Value);
            return string.Empty;
        });

        var cleanTitle = string.Join(' ', working.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

        return new TaskItem
        {
            Id = !string.IsNullOrWhiteSpace(lmsId) ? $"lms-{lmsId}" : $"{Path.GetFileName(sourcePath)}:{lineNumber}",
            Title = cleanTitle,
            IsCompleted = isCompleted,
            DueDate = dueDate,
            DueTime = dueTime,
            CompletedDate = completedDate,
            Priority = priority,
            Difficulty = difficulty,
            CourseFolder = defaultCourseFolder,
            CourseAlias = courseAlias,
            Labels = labels,
            LinkedFiles = linkedFiles,
            NotePath = notePath,
            LmsId = lmsId,
            LmsStatus = lmsStatus,
            LmsScore = lmsScore,
            LmsUrl = lmsUrl,
            SourceMarkdownPath = sourcePath,
            LineNumber = lineNumber
        };
    }

    private static DateOnly? ExtractCompletedDate(string text, out string cleaned)
    {
        var match = LegacyCompletedDateRegex().Match(text);
        if (match.Success && DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var cd))
        {
            cleaned = text.Remove(match.Index, match.Length);
            return cd;
        }

        DateOnly? inlineDone = null;
        cleaned = InlineFieldRegex().Replace(text, m =>
        {
            if (m.Groups[1].Value.Equals("done", StringComparison.OrdinalIgnoreCase) &&
                DateOnly.TryParseExact(m.Groups[2].Value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDone))
            {
                inlineDone = parsedDone;
                return string.Empty;
            }
            return m.Value;
        });

        return inlineDone;
    }
}
