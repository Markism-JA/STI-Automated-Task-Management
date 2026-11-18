using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ScholarFlow.Core.Models;

namespace ScholarFlow.Core.Markdown;

/// <summary>
/// Losslessly parses and serializes Obsidian-compatible Markdown task lists (tasks.md and inbox.md),
/// supporting subtasks, Todoist priorities, due/completion dates, file links, and LMS tracking metadata.
/// </summary>
public static partial class MarkdownTaskCodec
{
    [GeneratedRegex(@"^(\s*)- \[([ xX])\]\s+(.*)$", RegexOptions.Compiled)]
    private static partial Regex TaskLineRegex();

    [GeneratedRegex(@"📅\s*(\d{4}-\d{2}-\d{2})", RegexOptions.Compiled)]
    private static partial Regex DueDateRegex();

    [GeneratedRegex(@"⏰\s*(\d{2}:\d{2})", RegexOptions.Compiled)]
    private static partial Regex DueTimeRegex();

    [GeneratedRegex(@"✅\s*(\d{4}-\d{2}-\d{2})", RegexOptions.Compiled)]
    private static partial Regex CompletedDateRegex();

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
            sb.Append($" 📅 {task.DueDate.Value:yyyy-MM-dd}");

        if (task.DueTime.HasValue)
            sb.Append($" ⏰ {task.DueTime.Value:HH:mm}");

        if (task.IsCompleted && task.CompletedDate.HasValue)
            sb.Append($" ✅ {task.CompletedDate.Value:yyyy-MM-dd}");

        var priorityEmoji = task.Priority switch
        {
            TaskPriority.P1 => " 🔺",
            TaskPriority.P2 => " ⏫",
            TaskPriority.P3 => " 🔼",
            _ => string.Empty
        };
        sb.Append(priorityEmoji);

        if (!string.IsNullOrWhiteSpace(task.CourseAlias))
            sb.Append($" #{task.CourseAlias.Trim().TrimStart('#')}");

        foreach (var label in task.Labels.Where(l => !string.IsNullOrWhiteSpace(l)).Distinct(StringComparer.OrdinalIgnoreCase))
            sb.Append($" @{label.Trim().TrimStart('@')}");

        if (task.Difficulty is > 1 and <= 5)
            sb.Append($" [diff:: {task.Difficulty}]");

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
                sb.Append($" ✅ {sub.CompletedDate.Value:yyyy-MM-dd}");
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

                // Mark completed if LMS reports Submitted/Graded; never un-complete a task the user manually checked off
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
        var dueMatch = DueDateRegex().Match(working);
        if (dueMatch.Success && DateOnly.TryParseExact(dueMatch.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dd))
        {
            dueDate = dd;
            working = working.Remove(dueMatch.Index, dueMatch.Length);
        }

        TimeOnly? dueTime = null;
        var timeMatch = DueTimeRegex().Match(working);
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
        var match = CompletedDateRegex().Match(text);
        if (match.Success && DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var cd))
        {
            cleaned = text.Remove(match.Index, match.Length);
            return cd;
        }

        cleaned = text;
        return null;
    }
}
