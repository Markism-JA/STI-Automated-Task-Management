using System.Text.RegularExpressions;

namespace ScholarFlow.Elms.Parsing;

/// <summary>
/// Produces clean, deterministic, Linux/Windows-safe filenames and directory names.
/// </summary>
public static partial class FilenameSanitizer
{
    [GeneratedRegex(@"[^\w\s\-\(\)]", RegexOptions.Compiled)]
    private static partial Regex InvalidCharsRegex();

    [GeneratedRegex(@"[\s_]+", RegexOptions.Compiled)]
    private static partial Regex MultiUnderscoreRegex();

    /// <summary>
    /// Sanitizes a directory or course/lesson title into an underscore-separated folder name.
    /// </summary>
    public static string SanitizeFolderName(string rawName, string fallback = "Untitled")
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return fallback;

        var trimmed = rawName.Trim();
        var cleaned = InvalidCharsRegex().Replace(trimmed, " ");
        cleaned = MultiUnderscoreRegex().Replace(cleaned, "_").Trim('_');

        return string.IsNullOrEmpty(cleaned) ? fallback : cleaned;
    }

    /// <summary>
    /// Sanitizes a file display name while preserving or appending its extension cleanly.
    /// </summary>
    public static string SanitizeFileName(
        string rawName,
        string? fallbackExtension = null,
        string fallback = "file"
    )
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return string.IsNullOrEmpty(fallbackExtension)
                ? fallback
                : $"{fallback}{NormalizeExt(fallbackExtension)}";

        var trimmed = rawName.Trim();
        var ext = Path.GetExtension(trimmed);
        var stem = string.IsNullOrEmpty(ext) ? trimmed : Path.GetFileNameWithoutExtension(trimmed);

        // Guard against bogus "extensions" like ".1" or overly long fragments from titles such as "01. Introduction"
        if (ext.Length > 10 || (ext.Length > 1 && ext[1..].All(char.IsDigit)))
        {
            stem = trimmed;
            ext = string.Empty;
        }

        var cleanStem = SanitizeFolderName(stem, fallback);
        var finalExt = !string.IsNullOrEmpty(ext)
            ? ext.ToLowerInvariant()
            : NormalizeExt(fallbackExtension);

        return string.IsNullOrEmpty(finalExt) ? cleanStem : $"{cleanStem}{finalExt}";
    }

    private static string NormalizeExt(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext))
            return string.Empty;

        var trimmed = ext.Trim().ToLowerInvariant();
        return trimmed.StartsWith('.') ? trimmed : $".{trimmed}";
    }
}
