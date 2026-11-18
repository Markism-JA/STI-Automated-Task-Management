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

        // 1. Write term.json when at least one module has a recognized term & prefix
        var termMap = snapshot.BuildTermMap();
        if (termMap.Values.Any(v => v.Count > 0))
        {
            var termJsonPath = Path.Combine(courseRoot, "term.json");
            var json = JsonSerializer.Serialize(termMap, JsonOptions);
            await File.WriteAllTextAsync(termJsonPath, json + "\n", ct);
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
            await File.WriteAllTextAsync(targetPath, markdown, ct);

            // If an assignment transitioned from ongoing -> completed on eLMS, remove the stale note in ongoing/
            if (isDone && File.Exists(staleOtherPath))
            {
                try { File.Delete(staleOtherPath); } catch { /* ignore */ }
            }
        }
    }
}
