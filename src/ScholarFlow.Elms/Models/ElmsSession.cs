using ScholarFlow.Core.Sync;

namespace ScholarFlow.Elms.Models;

/// <summary>
/// Represents an authenticated browser cookie exported from Playwright for HTTP scraping.
/// </summary>
public sealed record ElmsCookie(
    string Name,
    string Value,
    string Domain,
    string Path,
    bool Secure,
    bool HttpOnly,
    DateTimeOffset? ExpiresAt
);

/// <summary>
/// Represents an active authenticated session against STI eLMS.
/// </summary>
public sealed record ElmsSession(
    string BaseUrl,
    string UserAgent,
    IReadOnlyList<ElmsCookie> Cookies,
    DateTimeOffset AuthenticatedAt
)
{
    public bool HasCookies => Cookies.Count > 0;
}

/// <summary>
/// Optional credentials for automated headless login when session cookies expire.
/// </summary>
public sealed record ElmsCredentials(string Username, string Password);

/// <summary>
/// Configuration for authenticating with STI eLMS via Playwright.
/// </summary>
public sealed record AuthOptions
{
    public string BaseUrl { get; init; } = "https://elms.sti.edu";

    /// <summary>
    /// Directory storing persistent Chromium profile data (cookies, localStorage, session).
    /// </summary>
    public string BrowserProfileDirectory { get; init; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "scholarflow",
            "browser_session"
        );

    /// <summary>
    /// Cached cookie session file path for fast HTTP-only validation without launching Chromium.
    /// </summary>
    public string SessionFilePath { get; init; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "scholarflow",
            "session.json"
        );

    /// <summary>
    /// Optional credentials; if omitted, checks ELMS_USERNAME and ELMS_PASSWORD environment variables.
    /// </summary>
    public ElmsCredentials? Credentials { get; init; }

    /// <summary>
    /// When true, skips cached session &amp; headless probes and opens an interactive browser window immediately.
    /// </summary>
    public bool ForceInteractive { get; init; }

    /// <summary>
    /// When false (e.g., background polling), never launches a visible browser window if silent/headless auth fails.
    /// </summary>
    public bool AllowInteractiveBrowser { get; init; } = true;

    /// <summary>
    /// Maximum time to wait for manual login when interactive mode is triggered.
    /// </summary>
    public TimeSpan LoginTimeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Thrown when an authenticated eLMS session cannot be established silently and interactive browser login is disabled.
/// </summary>
public sealed class ElmsAuthenticationRequiredException(string message)
    : ProviderAuthenticationRequiredException(message) { }
