using System.Text.Json;
using ScholarFlow.Core.Services;
using ScholarFlow.Elms.Models;

namespace ScholarFlow.Elms.Sync;

/// <summary>
/// Persists <see cref="ElmsSession"/> cookies and browser profiles on disk, scoped by optional
/// <c>accountKey</c> so multi-account STI eLMS basins remain isolated.
/// </summary>
public static class ElmsSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string AppLocalDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "scholarflow"
        );

    public static string GetDefaultSessionPath() => GetSessionPathForAccount(null);

    public static string GetSessionPathForAccount(string? accountKey)
    {
        if (
            string.IsNullOrWhiteSpace(accountKey)
            || string.Equals(accountKey.Trim(), "default", StringComparison.OrdinalIgnoreCase)
        )
            return Path.Combine(AppLocalDir, "session.json");

        var safe = FilenameSanitizer.SanitizeFolderName(accountKey, "account");
        return Path.Combine(AppLocalDir, "sessions", safe, "session.json");
    }

    public static string GetBrowserProfileForAccount(string? accountKey)
    {
        if (
            string.IsNullOrWhiteSpace(accountKey)
            || string.Equals(accountKey.Trim(), "default", StringComparison.OrdinalIgnoreCase)
        )
            return Path.Combine(AppLocalDir, "browser_session");

        var safe = FilenameSanitizer.SanitizeFolderName(accountKey, "account");
        return Path.Combine(AppLocalDir, "sessions", safe, "browser_session");
    }

    public static AuthOptions BuildAuthOptions(
        string? accountKey,
        string? baseUrl = null,
        bool allowInteractiveBrowser = true
    ) =>
        new()
        {
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://elms.sti.edu" : baseUrl.Trim(),
            SessionFilePath = GetSessionPathForAccount(accountKey),
            BrowserProfileDirectory = GetBrowserProfileForAccount(accountKey),
            AllowInteractiveBrowser = allowInteractiveBrowser,
        };

    public static async Task<ElmsSession?> LoadAsync(
        string? path = null,
        CancellationToken ct = default
    )
    {
        var target = path ?? GetDefaultSessionPath();
        if (!File.Exists(target))
            return null;

        try
        {
            await using var stream = File.OpenRead(target);
            return await JsonSerializer.DeserializeAsync<ElmsSession>(stream, JsonOptions, ct);
        }
        catch
        {
            return null;
        }
    }

    public static async Task SaveAsync(
        ElmsSession session,
        string? path = null,
        CancellationToken ct = default
    )
    {
        var target = path ?? GetDefaultSessionPath();
        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var temp = $"{target}.tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, session, JsonOptions, ct);
        }

        File.Move(temp, target, overwrite: true);
        TryRestrictPermissions(target);
    }

    private static void TryRestrictPermissions(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Ignore on filesystems without POSIX mode support
        }
    }
}
