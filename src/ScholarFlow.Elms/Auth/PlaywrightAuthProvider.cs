using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using ScholarFlow.Elms.Abstractions;
using ScholarFlow.Elms.Models;
using ScholarFlow.Elms.Parsing;
using ScholarFlow.Elms.Sync;

namespace ScholarFlow.Elms.Auth;

/// <summary>
/// Authenticates against STI eLMS using cached HTTP cookies first, then persistent Playwright
/// headless profile + optional automated login, falling back to interactive Chromium only if needed.
/// </summary>
public sealed class PlaywrightAuthProvider : IElmsAuthProvider
{
    private readonly ILogger<PlaywrightAuthProvider>? _logger;

    public PlaywrightAuthProvider(ILogger<PlaywrightAuthProvider>? logger = null)
    {
        _logger = logger;
    }

    public async Task<ElmsSession> EnsureAuthenticatedAsync(AuthOptions options, CancellationToken ct = default)
    {
        Directory.CreateDirectory(options.BrowserProfileDirectory);

        if (!options.ForceInteractive)
        {
            // 1. Fast path: validate cached session.json via HttpClient without launching Chromium
            var cached = await ElmsSessionStore.LoadAsync(options.SessionFilePath, ct);
            if (cached is not null && await ValidateSessionAsync(cached, ct))
            {
                _logger?.LogInformation("Reused cached HTTP session ({CookieCount} cookies).", cached.Cookies.Count);
                return cached;
            }

            // 2. Headless Playwright persistent profile (+ optional automated credential login)
            var credentials = ResolveCredentials(options);
            var headlessSession = await TryCaptureSessionAsync(options, headless: true, credentials, ct);
            if (headlessSession is not null)
            {
                await ElmsSessionStore.SaveAsync(headlessSession, options.SessionFilePath, ct);
                _logger?.LogInformation("Established headless Playwright session ({CookieCount} cookies).", headlessSession.Cookies.Count);
                return headlessSession;
            }

            _logger?.LogInformation("Headless session unavailable; launching interactive login window.");
        }

        var interactiveSession = await TryCaptureSessionAsync(options, headless: false, ResolveCredentials(options), ct);
        if (interactiveSession is null)
            throw new InvalidOperationException("Failed to establish an authenticated eLMS session.");

        await ElmsSessionStore.SaveAsync(interactiveSession, options.SessionFilePath, ct);
        return interactiveSession;
    }

    public async Task<bool> ValidateSessionAsync(ElmsSession session, CancellationToken ct = default)
    {
        if (!session.HasCookies)
            return false;

        try
        {
            using var handler = CreateCookieHandler(session);
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(session.UserAgent);

            var dashboardUrl = $"{session.BaseUrl.TrimEnd('/')}/user_dashboard";
            using var response = await client.GetAsync(dashboardUrl, ct);
            if (!response.IsSuccessStatusCode)
                return false;

            var html = await response.Content.ReadAsStringAsync(ct);
            return !HtmlParsingHelpers.IsLoginRedirectHtml(html, response.RequestMessage?.RequestUri);
        }
        catch
        {
            return false;
        }
    }

    public Task<ElmsSession> RefreshSessionAsync(AuthOptions options, CancellationToken ct = default)
        => EnsureAuthenticatedAsync(options, ct);

    private async Task<ElmsSession?> TryCaptureSessionAsync(
        AuthOptions options,
        bool headless,
        ElmsCredentials? credentials,
        CancellationToken ct)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var context = await playwright.Chromium.LaunchPersistentContextAsync(
            options.BrowserProfileDirectory,
            new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = headless,
                Args = ["--no-sandbox", "--disable-setuid-sandbox"]
            });

        var page = context.Pages.Count > 0 ? context.Pages[0] : await context.NewPageAsync();
        var dashboardUrl = $"{options.BaseUrl.TrimEnd('/')}/user_dashboard";

        await page.GotoAsync(dashboardUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 30000
        });

        if (IsOnDashboard(page.Url))
            return await ExtractSessionAsync(context, page, options.BaseUrl);

        if (credentials is not null)
        {
            var loggedIn = await TryAutomatedLoginAsync(page, credentials, ct);
            if (loggedIn && IsOnDashboard(page.Url))
                return await ExtractSessionAsync(context, page, options.BaseUrl);
        }

        if (headless)
            return null;

        _logger?.LogInformation("Waiting for manual login on {BaseUrl} (timeout: {Timeout})...", options.BaseUrl, options.LoginTimeout);

        await page.WaitForURLAsync(
            url => IsOnDashboard(url),
            new PageWaitForURLOptions { Timeout = (float)options.LoginTimeout.TotalMilliseconds });

        return await ExtractSessionAsync(context, page, options.BaseUrl);
    }

    private async Task<bool> TryAutomatedLoginAsync(IPage page, ElmsCredentials credentials, CancellationToken ct)
    {
        try
        {
            // Case A: Microsoft / Office 365 SSO button on NEO LMS login page
            var o365Button = await page.QuerySelectorAsync("a[href*=\"office365\"], a[href*=\"microsoft\"], a:has-text(\"Office 365\"), button:has-text(\"Microsoft\")");
            if (o365Button is not null)
            {
                await o365Button.ClickAsync();
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            }

            // Case B: Microsoft Entra / O365 login flow
            if (page.Url.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
            {
                var emailInput = await page.WaitForSelectorAsync("input[name=\"loginfmt\"], #i0116", new PageWaitForSelectorOptions { Timeout = 8000 });
                if (emailInput is not null)
                {
                    await emailInput.FillAsync(credentials.Username);
                    await page.ClickAsync("#idSIButton9, input[type=\"submit\"]");
                }

                var passInput = await page.WaitForSelectorAsync("input[name=\"passwd\"], #i0118", new PageWaitForSelectorOptions { Timeout = 8000 });
                if (passInput is not null)
                {
                    await passInput.FillAsync(credentials.Password);
                    await page.ClickAsync("#idSIButton9, input[type=\"submit\"]");
                }

                // Handle "Stay signed in?" prompt if it appears (short timeout; MFA will time out cleanly)
                try
                {
                    var staySignedIn = await page.WaitForSelectorAsync("#idSIButton9", new PageWaitForSelectorOptions { Timeout = 5000 });
                    if (staySignedIn is not null && page.Url.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
                        await staySignedIn.ClickAsync();
                }
                catch
                {
                    // Ignore if redirected immediately
                }

                await page.WaitForURLAsync(url => IsOnDashboard(url), new PageWaitForURLOptions { Timeout = 15000 });
                return true;
            }

            // Case C: Standard NEO LMS username/password form
            var userField = await page.QuerySelectorAsync("input[name=\"userid\"], input[name=\"username\"], input[type=\"email\"]");
            var passwordField = await page.QuerySelectorAsync("input[name=\"password\"]");
            if (userField is not null && passwordField is not null)
            {
                await userField.FillAsync(credentials.Username);
                await passwordField.FillAsync(credentials.Password);
                var submit = await page.QuerySelectorAsync("button[type=\"submit\"], input[type=\"submit\"]");
                if (submit is not null)
                    await submit.ClickAsync();
                await page.WaitForURLAsync(url => IsOnDashboard(url), new PageWaitForURLOptions { Timeout = 15000 });
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug("Automated login did not complete: {Message}", ex.Message);
        }

        return false;
    }

    private static ElmsCredentials? ResolveCredentials(AuthOptions options)
    {
        if (options.Credentials is not null)
            return options.Credentials;

        var user = Environment.GetEnvironmentVariable("ELMS_USERNAME");
        var pass = Environment.GetEnvironmentVariable("ELMS_PASSWORD");
        if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pass))
            return new ElmsCredentials(user, pass);

        return null;
    }

    private static bool IsOnDashboard(string url) =>
        url.Contains("/user_dashboard", StringComparison.OrdinalIgnoreCase) &&
        !url.Contains("login", StringComparison.OrdinalIgnoreCase);

    private static async Task<ElmsSession> ExtractSessionAsync(IBrowserContext context, IPage page, string baseUrl)
    {
        var pwCookies = await context.CookiesAsync();
        var ua = await page.EvaluateAsync<string>("() => navigator.userAgent");

        var cookies = pwCookies
            .Select(c => new ElmsCookie(
                Name: c.Name,
                Value: c.Value,
                Domain: c.Domain,
                Path: c.Path,
                Secure: c.Secure,
                HttpOnly: c.HttpOnly,
                ExpiresAt: c.Expires > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)c.Expires) : null))
            .ToList();

        return new ElmsSession(baseUrl, ua, cookies, DateTimeOffset.UtcNow);
    }

    internal static HttpClientHandler CreateCookieHandler(ElmsSession session)
    {
        var container = new CookieContainer();
        var baseUri = new Uri(session.BaseUrl);

        foreach (var c in session.Cookies)
        {
            if (!ElmsUrlCanonicalizer.IsSameOrSubdomain(c.Domain, baseUri.Host))
                continue;

            try
            {
                var cookie = new System.Net.Cookie(c.Name, c.Value, string.IsNullOrEmpty(c.Path) ? "/" : c.Path, c.Domain.TrimStart('.'))
                {
                    Secure = c.Secure,
                    HttpOnly = c.HttpOnly
                };
                container.Add(baseUri, cookie);
            }
            catch (CookieException)
            {
                // Ignore third-party cookies incompatible with baseUri
            }
        }

        return new HttpClientHandler
        {
            CookieContainer = container,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All
        };
    }
}
