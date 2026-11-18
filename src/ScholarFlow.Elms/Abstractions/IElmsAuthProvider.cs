using ScholarFlow.Elms.Models;

namespace ScholarFlow.Elms.Abstractions;

/// <summary>
/// Manages Playwright-backed authentication and session cookie extraction for STI eLMS.
/// </summary>
public interface IElmsAuthProvider
{
    /// <summary>
    /// Ensures an authenticated session exists, launching interactive login only if required.
    /// </summary>
    Task<ElmsSession> EnsureAuthenticatedAsync(AuthOptions options, CancellationToken ct = default);

    /// <summary>
    /// Checks whether an existing session's cookies are still accepted by the eLMS dashboard.
    /// </summary>
    Task<bool> ValidateSessionAsync(ElmsSession session, CancellationToken ct = default);

    /// <summary>
    /// Re-authenticates using the configured browser profile when a session expires mid-sync.
    /// </summary>
    Task<ElmsSession> RefreshSessionAsync(AuthOptions options, CancellationToken ct = default);
}
