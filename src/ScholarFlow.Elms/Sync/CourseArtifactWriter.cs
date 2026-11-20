using System.Text.Json;
using ScholarFlow.Elms.Models;
using ScholarFlow.Elms.Parsing;

namespace ScholarFlow.Elms.Sync;

/// <summary>
/// Writes auto-generated course artifacts (term.json for Compile_School and Markdown notes for assignments)
/// directly into the course folder inside the Basin.
/// </summary>
public static class CourseArtifactWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static async Task WriteCourseArtifactsAsync(
        ElmsCourseSnapshot snapshot,
        string outputRootDirectory,
        CancellationToken ct = default)
    {
        var courseRoot = Path.Combine(outputRootDirectory, snapshot.Course.SanitizedFolderName);
        Directory.CreateDirectory(courseRoot);

        // 1. Write term.json when at least one module has a recognized term & prefix, preserving existing custom mappings
        var termMap = snapshot.BuildTermMap();
        var termJsonPath = Path.Combine(courseRoot, "term.json");
        if (File.Exists(termJsonPath))
        {
            try
            {
                var existingJson = await File.ReadAllTextAsync(termJsonPath, ct);
                var existingMap = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(existingJson);
                if (existingMap is not null && existingMap.Values.Any(v => v is { Count: > 0 }))
                {
                    var alreadyMapped = existingMap.Values.SelectMany(v => v).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var (term, prefixes) in termMap)
                    {
                        if (!existingMap.TryGetValue(term, out var targetList))
                        {
                            targetList = [];
                            existingMap[term] = targetList;
                        }
                        foreach (var p in prefixes)
                        {
                            if (alreadyMapped.Add(p))
                                targetList.Add(p);
                        }
                    }
                    termMap = existingMap;
                }
            }
            catch
            {
                // Overwrite corrupted term.json with freshly built termMap
            }
        }

        if (termMap.Values.Any(v => v.Count > 0))
        {
            var json = JsonSerializer.Serialize(termMap, JsonOptions);
            await WriteIfChangedAsync(termJsonPath, json + "\n", ct);
        }

        // 2. Ensure ongoing/ and completed/ folders exist and write Markdown notes for assignments
        var ongoingDir = Path.Combine(courseRoot, "ongoing");
        var completedDir = Path.Combine(courseRoot, "completed");
        Directory.CreateDirectory(ongoingDir);
        Directory.CreateDirectory(completedDir);

        foreach (var assignment in snapshot.Assignments)
        {
            var isDone = assignment.Status is AssignmentStatus.Submitted or AssignmentStatus.Graded;
            var targetDir = isDone ? completedDir : ongoingDir;
            var otherDir = isDone ? ongoingDir : completedDir;

            var noteFileName = FilenameSanitizer.SanitizeFileName(assignment.Title, ".md");
            var targetPath = Path.Combine(targetDir, noteFileName);
            var staleOtherPath = Path.Combine(otherDir, noteFileName);

            var markdown = HtmlToMarkdownConverter.FormatAssignmentNote(assignment);
            await WriteIfChangedAsync(targetPath, markdown, ct);

            // If an assignment transitioned from ongoing -> completed on eLMS, remove the stale note in ongoing/
            if (isDone && File.Exists(staleOtherPath))
            {
                try { File.Delete(staleOtherPath); } catch { /* ignore */ }
            }
        }
    }

    public static async Task WriteIfChangedAsync(string path, string content, CancellationToken ct = default)
    {
        if (File.Exists(path))
        {
            try
            {
                var existing = await File.ReadAllTextAsync(path, ct);
                if (string.Equals(existing, content, StringComparison.Ordinal))
                    return;
            }
            catch
            {
                // Fall through and rewrite if unreadable
            }
        }

        await File.WriteAllTextAsync(path, content, ct);
    }
}
