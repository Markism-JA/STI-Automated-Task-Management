using ScholarFlow.Elms.Parsing;

namespace ScholarFlow.Elms.Models;

/// <summary>
/// Categorizes how a remote eLMS resource was discovered and should be handled.
/// </summary>
public enum ElmsResourceKind
{
    CourseFile,
    LessonAttachment,
    AssignmentAttachment,
    SubmittedArtifact,
    ExternalLink,
    ScormPackage,
}

/// <summary>
/// Represents a downloadable file or external reference discovered on eLMS.
/// </summary>
public sealed record ElmsResource(
    string Url,
    string DisplayName,
    string SanitizedFileName,
    string RelativeTargetFolder,
    ElmsResourceKind Kind,
    string? CourseId = null,
    string? SourceTitle = null,
    string? MimeType = null,
    long? ContentLength = null,
    string? ETag = null,
    DateTimeOffset? LastModified = null
)
{
    /// <summary>
    /// Stable URL key with ephemeral query tokens (e.g. ?lmsauth=...) removed.
    /// </summary>
    public string CanonicalUrl => ElmsUrlCanonicalizer.GetCanonicalUrl(Url);

    public string RelativeTargetPath =>
        string.IsNullOrEmpty(RelativeTargetFolder)
            ? SanitizedFileName
            : $"{RelativeTargetFolder}/{SanitizedFileName}";
}
