namespace ScholarFlow.Elms.Parsing;

/// <summary>
/// Normalizes STI eLMS URLs by stripping ephemeral per-session tokens (such as ?lmsauth=...)
/// while preserving structural query parameters like lesson_id and section_id.
/// </summary>
public static class ElmsUrlCanonicalizer
{
    public static string GetCanonicalUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return url.Trim();

        if (string.IsNullOrEmpty(uri.Query))
            return uri.GetLeftPart(UriPartial.Path);

        var query = uri.Query.TrimStart('?');
        var keptParams = new List<string>();

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eqIdx = part.IndexOf('=');
            var key = eqIdx >= 0 ? part[..eqIdx] : part;

            if (key.Equals("lmsauth", StringComparison.OrdinalIgnoreCase))
                continue;

            keptParams.Add(part);
        }

        var basePath = uri.GetLeftPart(UriPartial.Path);
        return keptParams.Count == 0 ? basePath : $"{basePath}?{string.Join('&', keptParams)}";
    }

    public static bool IsSameOrSubdomain(string candidateDomain, string targetHost)
    {
        if (string.IsNullOrWhiteSpace(candidateDomain) || string.IsNullOrWhiteSpace(targetHost))
            return false;

        var cleanDomain = candidateDomain.Trim().TrimStart('.');
        var cleanHost = targetHost.Trim().TrimStart('.');

        if (cleanDomain.Equals(cleanHost, StringComparison.OrdinalIgnoreCase))
            return true;

        if (cleanHost.EndsWith("." + cleanDomain, StringComparison.OrdinalIgnoreCase))
            return true;

        if (cleanDomain.EndsWith("." + cleanHost, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
