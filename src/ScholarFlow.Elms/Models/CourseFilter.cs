using System.Text.RegularExpressions;

namespace ScholarFlow.Elms.Models;

/// <summary>
/// Controls which discovered eLMS courses are ignored (e.g., administrative/ad-hoc info shells).
/// </summary>
public sealed class CourseFilter
{
    public HashSet<string> IgnoredCourseIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> IgnoredTitlePatterns { get; set; } = [];

    public bool IsIgnored(ElmsCourse course)
    {
        if (IgnoredCourseIds.Contains(course.CourseId))
            return true;

        foreach (var pattern in IgnoredTitlePatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                continue;

            if (MatchesWildcardOrSubstring(course.Title, pattern))
                return true;
        }

        return false;
    }

    public IReadOnlyList<ElmsCourse> Apply(IEnumerable<ElmsCourse> courses)
        => courses.Where(c => !IsIgnored(c)).ToList();

    private static bool MatchesWildcardOrSubstring(string text, string pattern)
    {
        if (pattern.Contains('*') || pattern.Contains('?'))
        {
            var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(text, regexPattern, RegexOptions.IgnoreCase);
        }

        return text.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }
}
