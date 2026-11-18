using System.Diagnostics;
using ScholarFlow.Core.Models;

namespace ScholarFlow.Desktop.Services;

/// <summary>
/// Dispatches AppNotifications to both the in-app notification center and Linux notify-send.
/// </summary>
public sealed class DesktopNotificationService
{
    private readonly HashSet<string> _dispatchedKeys = new(StringComparer.OrdinalIgnoreCase);

    public void SendSystemNotification(AppNotification notification)
    {
        if (!OperatingSystem.IsLinux())
            return;

        var dedupKey = $"{notification.Kind}|{notification.Title}|{notification.Message}";
        if (!_dispatchedKeys.Add(dedupKey))
            return;

        try
        {
            var urgency = notification.Kind switch
            {
                NotificationKind.DueSoon or NotificationKind.SessionExpired or NotificationKind.Error => "critical",
                NotificationKind.MaterialDownloaded or NotificationKind.MasterPdfCompiled => "low",
                _ => "normal"
            };

            var psi = new ProcessStartInfo
            {
                FileName = "notify-send",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add("ScholarFlow");
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add(urgency);
            psi.ArgumentList.Add(notification.Title);
            psi.ArgumentList.Add(notification.Message);

            Process.Start(psi);
        }
        catch
        {
            // notify-send is optional on headless/minimal systems
        }
    }
}
