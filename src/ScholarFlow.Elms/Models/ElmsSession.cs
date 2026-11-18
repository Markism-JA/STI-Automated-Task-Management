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
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Represents an active authenticated session against STI eLMS.
/// </summary>
public sealed record ElmsSession(
    string BaseUrl,
    string UserAgent,
    IReadOnlyList<ElmsCookie> Cookies,
    DateTimeOffset AuthenticatedAt)
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
    public string BrowserProfileDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "scholarflow",
        "browser_session");

    /// <summary>
    /// Cached cookie session file path for fast HTTP-only validation without launching Chromium.
    /// </summary>
    public string SessionFilePath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "scholarflow",
        "session.json");

    /// <summary>
    /// Optional credentials; if omitted, checks ELMS_USERNAME and ELMS_PASSWORD environment variables.
    /// </summary>
    public ElmsCredentials? Credentials { get; init; }

    /// <summary>
    /// When true, skips cached session &amp; headless probes and opens an interactive browser window immediately.
    /// </summary>
    public bool ForceInteractive { get; init; }

    /// <summary>
    /// Maximum time to wait for manual login when interactive mode is triggered.
    /// </summary>
    public TimeSpan LoginTimeout { get; init; } = TimeSpan.FromMinutes(5);
}
