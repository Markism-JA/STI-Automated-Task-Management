namespace ScholarFlow.Elms.Models;

/// <summary>
/// Categorizes a child section row inside an eLMS module table.
/// </summary>
public enum ElmsSectionKind
{
    Handout,
    Presentation,
    Assignment,
    Quiz,
    Other,
}

/// <summary>
/// Represents an individual child section inside a course module (e.g. 01 Handout 1, 01 Online Presentation).
/// </summary>
public sealed record ElmsModuleSection(
    string SectionId,
    string Title,
    string SanitizedFolderName,
    string Url,
    ElmsSectionKind Kind,
    bool IsCompleted = false,
    string? DueText = null,
    string? ScoreText = null
);

/// <summary>
/// Represents an enrolled course discovered from the eLMS dashboard.
/// </summary>
public sealed record ElmsCourse(
    string CourseId,
    string Title,
    string CourseUrl,
    string SanitizedFolderName,
    string? CourseCode = null,
    string? TermOrSection = null,
    string? RawTitle = null,
    string? TermSuffix = null,
    bool IsCompleted = false,
    bool IsAdministrativeShell = false
);

/// <summary>
/// Represents a lesson or module entry inside a course.
/// </summary>
public sealed record ElmsModule(
    string Title,
    string SanitizedFolderName,
    string LessonUrl,
    int OrderIndex,
    string? TermHeader = null,
    bool IsScormOrExternal = false,
    string? LessonId = null,
    string? ModuleNumberPrefix = null,
    IReadOnlyList<ElmsModuleSection>? Sections = null
)
{
    public IReadOnlyList<ElmsModuleSection> ModuleSections => Sections ?? [];
}

/// <summary>
/// Detailed content scraped from an individual lesson or handout section page.
/// </summary>
public sealed record ElmsLesson(
    string LessonUrl,
    string Title,
    string SanitizedFolderName,
    string? TermHeader,
    string? HtmlContent,
    IReadOnlyList<ElmsResource> Resources,
    bool HasScormOrEmbeddedPlayer = false,
    string? MarkdownContent = null
);

/// <summary>
/// Aggregated remote state for a single course at a point in time.
/// </summary>
public sealed record ElmsCourseSnapshot(
    ElmsCourse Course,
    IReadOnlyList<ElmsResource> GeneralResources,
    IReadOnlyList<ElmsModule> Modules,
    IReadOnlyList<ElmsLesson> Lessons,
    IReadOnlyList<ElmsAssignment> Assignments,
    IReadOnlyList<string> Warnings,
    DateTimeOffset CapturedAt
)
{
    public IEnumerable<ElmsResource> AllResources =>
        GeneralResources
            .Concat(Lessons.SelectMany(l => l.Resources))
            .Concat(Assignments.SelectMany(a => a.AllResources));

    /// <summary>
    /// Builds a Compile_School-compatible term.json dictionary mapping Prelims/Midterms/Prefinals/Finals to 2-digit module prefixes.
    /// </summary>
    public Dictionary<string, List<string>> BuildTermMap()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Prelims"] = [],
            ["Midterms"] = [],
            ["Prefinals"] = [],
            ["Finals"] = [],
        };

        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in Modules)
        {
            if (
                string.IsNullOrWhiteSpace(mod.TermHeader)
                || string.IsNullOrWhiteSpace(mod.ModuleNumberPrefix)
            )
                continue;

            if (!map.TryGetValue(mod.TermHeader, out var list))
            {
                list = [];
                map[mod.TermHeader] = list;
            }

            if (!list.Contains(mod.ModuleNumberPrefix, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(mod.ModuleNumberPrefix);
                assigned.Add(mod.ModuleNumberPrefix);
            }
        }

        // Standard STI 4-term fallback for numbered modules when instructor omitted (Prelim)/(Midterm) from module title
        foreach (var mod in Modules)
        {
            if (
                string.IsNullOrWhiteSpace(mod.ModuleNumberPrefix)
                || assigned.Contains(mod.ModuleNumberPrefix)
            )
                continue;

            if (!int.TryParse(mod.ModuleNumberPrefix, out int num))
                continue;

            var inferredTerm = num switch
            {
                <= 2 => "Prelims",
                <= 4 => "Midterms",
                <= 6 => "Prefinals",
                _ => "Finals",
            };

            if (
                !map[inferredTerm]
                    .Contains(mod.ModuleNumberPrefix, StringComparer.OrdinalIgnoreCase)
            )
            {
                map[inferredTerm].Add(mod.ModuleNumberPrefix);
                assigned.Add(mod.ModuleNumberPrefix);
            }
        }

        return map;
    }
}
