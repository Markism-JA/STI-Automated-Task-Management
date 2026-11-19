namespace ScholarFlow.Desktop.Services;

/// <summary>
/// Manages cross-platform autostart registration (Windows HKCU Run key, Linux XDG autostart .desktop).
/// </summary>
public static class StartupRegistrationService
{
    private const string AppName = "ScholarFlow";

    public static void SetEnabled(bool enabled)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
            return;

        if (OperatingSystem.IsWindows())
        {
#pragma warning disable CA1416
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run",
                    writable: true);
                if (key is null)
                    return;

                if (enabled)
                    key.SetValue(AppName, $"\"{exePath}\" --minimized");
                else
                    key.DeleteValue(AppName, throwOnMissingValue: false);
            }
            catch
            {
                // Non-fatal if registry access is restricted
            }
#pragma warning restore CA1416
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                if (string.IsNullOrWhiteSpace(configHome))
                    configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

                var autostartDir = Path.Combine(configHome, "autostart");
                var desktopFile = Path.Combine(autostartDir, "scholarflow.desktop");

                if (enabled)
                {
                    Directory.CreateDirectory(autostartDir);
                    var content = $"""
                        [Desktop Entry]
                        Type=Application
                        Name=ScholarFlow
                        Comment=Academic Workspace & Study Studio
                        Exec="{exePath}" --minimized
                        Icon=scholarflow
                        Terminal=false
                        Categories=Education;Office;
                        X-GNOME-Autostart-enabled=true
                        """;
                    File.WriteAllText(desktopFile, content);
                }
                else if (File.Exists(desktopFile))
                {
                    File.Delete(desktopFile);
                }
            }
            catch
            {
                // Non-fatal if autostart dir is read-only
            }
        }
    }
}
