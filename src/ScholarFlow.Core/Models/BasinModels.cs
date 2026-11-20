namespace ScholarFlow.Core.Models;

public sealed class BasinConfig
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "School Basin";
    public string ElmsBaseUrl { get; set; } = "https://elms.sti.edu";
    public int FastPollIntervalMinutes { get; set; } = 15;
    public int FullSyncIntervalMinutes { get; set; } = 60;
    public bool AutoCompileMasterPdf { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class BasinCourseEntry
{
    public string? CourseId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public string? CourseCode { get; set; }
    public string? SectionCode { get; set; }
    public string? TermSuffix { get; set; }
    public string? CourseUrl { get; set; }
    public bool IsArchived { get; set; }
}

public static class CourseAliasGenerator
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and",
        "or",
        "in",
        "of",
        "the",
        "for",
        "to",
        "a",
        "an",
        "with",
    };

    /// <summary>
    /// Generates a concise, unique hashtag alias (e.g. "sqa", "os", "net1", "ias") from a course title.
    /// </summary>
    public static string Generate(string titleOrFolder, IEnumerable<string>? existingAliases = null)
    {
        var used = new HashSet<string>(existingAliases ?? [], StringComparer.OrdinalIgnoreCase);

        // Well-known compact acronym/word heuristics
        var clean = titleOrFolder.Replace('_', ' ').Replace('-', ' ');
        var words = clean
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string([.. w.Where(char.IsLetterOrDigit)]))
            .Where(w => !string.IsNullOrEmpty(w) && !StopWords.Contains(w))
            .ToList();

        string candidate;
        if (words.Count == 0)
        {
            candidate = "course";
        }
        else if (words.Count == 1)
        {
            var w = words[0].ToLowerInvariant();
            candidate = w.Length <= 6 ? w : w[..Math.Min(5, w.Length)];
        }
        else
        {
            candidate = string.Concat(
                words.Select(w => char.IsDigit(w[0]) ? w : char.ToLowerInvariant(w[0]).ToString())
            );

            if (candidate.Length < 2)
                candidate = words[0].ToLowerInvariant()[..Math.Min(4, words[0].Length)];
        }

        if (!used.Contains(candidate))
            return candidate;

        for (int i = 2; ; i++)
        {
            var numbered = $"{candidate}{i}";
            if (!used.Contains(numbered))
                return numbered;
        }
    }
}
