using System.Diagnostics;
using ScholarFlow.Elms.Auth;
using ScholarFlow.Elms.Models;

namespace ScholarFlow.Desktop.Services;

/// <summary>
/// Opens local files in Neovim (kitty --detach nvim) or system viewer (xdg-open),
/// and launches authenticated standalone Chromium --app windows for eLMS assignments.
/// </summary>
public static class ExternalFileLauncher
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".json", ".yaml", ".yml", ".py", ".sh", ".cs", ".toml", ".conf"
    };

    public static void OpenLocalFile(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            return;

        var ext = Path.GetExtension(fullPath);
        if (TextExtensions.Contains(ext) && File.Exists("/usr/bin/kitty"))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "/usr/bin/kitty",
                    ArgumentList = { "--detach", "nvim", fullPath },
                    UseShellExecute = false
                });
                return;
            }
            catch
            {
                // Fall through to xdg-open
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                ArgumentList = { fullPath },
                UseShellExecute = false
            });
        }
        catch
        {
            // Ignore if xdg-open unavailable
        }
    }

    /// <summary>
    /// Launches an authenticated Chromium --app window for an eLMS URL and invokes onWindowClosed when the user closes it.
    /// </summary>
    public static async Task OpenInElmsAppAndWatchAsync(
        string urlOrPath,
        Func<Task>? onWindowClosed = null,
        AuthOptions? options = null)
    {
        var proc = ElmsBrowserLauncher.LaunchAppWindow(urlOrPath, options);
        if (proc is null || onWindowClosed is null)
            return;

        try
        {
            await proc.WaitForExitAsync();
            await onWindowClosed();
        }
        catch
        {
            // Ignore process watch errors
        }
    }
}
