using System.Text.Json;
using ScholarFlow.Elms.Models;

namespace ScholarFlow.Elms.Sync;

/// <summary>
/// Persists <see cref="ElmsSession"/> cookies to disk so subsequent runs skip launching Playwright
/// whenever the cached session is still valid.
/// </summary>
public static class ElmsSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string GetDefaultSessionPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "scholarflow",
            "session.json"
        );

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
