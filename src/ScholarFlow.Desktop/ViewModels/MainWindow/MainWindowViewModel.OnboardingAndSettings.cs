using Avalonia.Threading;
using ScholarFlow.Core.Database;
using ScholarFlow.Core.Markdown;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Pdf;
using ScholarFlow.Core.Services;
using ScholarFlow.Core.Sync;
using ScholarFlow.Core.Tasks;
using ScholarFlow.Desktop.Services;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Elms.Client;
using ScholarFlow.Elms.Models;

namespace ScholarFlow.Desktop.ViewModels;


public sealed partial class MainWindowViewModel
{
    public void OpenOnboardingWizard()
    {
        OnboardingVaultPath = !string.IsNullOrWhiteSpace(BasinRoot)
            ? BasinRoot
            : OnboardingVaultPath;
        IsSettingsModalOpen = false;
        IsOnboardingOpen = true;
        NotifyStateChanged(UiChangeKind.OverlayOnly);
    }

    public async Task CompleteOnboardingAsync(string vaultPath, bool runElmsAuth, bool runInitialSync)
    {
        var resolvedPath = string.IsNullOrWhiteSpace(vaultPath) ? OnboardingVaultPath : vaultPath.Trim();
        IsOnboardingOpen = false;
        Settings.HasCompletedOnboarding = true;
        Settings.EnableElmsIntegration = runElmsAuth || runInitialSync;
        await AppSettingsStore.SaveAsync(Settings);

        await OpenBasinAsync(resolvedPath);

        if (runElmsAuth || runInitialSync)
            await RunFullSyncAsync();
    }

    public void ToggleAddCourseModal()
    {
        IsAddCourseModalOpen = !IsAddCourseModalOpen;
        IsSettingsModalOpen = false;
        IsSearchModalOpen = false;
        IsShortcutsModalOpen = false;
        NotifyStateChanged(UiChangeKind.OverlayOnly);
    }

    public async Task CreateLocalCourseAsync(string title, string? alias, string? courseCode)
    {
        if (string.IsNullOrWhiteSpace(BasinRoot) || string.IsNullOrWhiteSpace(title))
            return;

        var created = await _basinService.CreateLocalCourseAsync(BasinRoot, title, alias, courseCode);
        IsAddCourseModalOpen = false;
        await ReloadBasinDataAsync();
        SelectCourse(created, showMaterials: true);
        StatusText = $"Created local course '{created.Title}' (#{created.Alias})";
        NotifyStateChanged();
    }

    public async Task ImportPdfsToCourseAsync(BasinCourseEntry course, IReadOnlyList<string> sourcePdfPaths)
    {
        if (string.IsNullOrWhiteSpace(BasinRoot) || sourcePdfPaths.Count == 0)
            return;

        var courseDir = Path.Combine(BasinRoot, course.FolderName);
        Directory.CreateDirectory(courseDir);

        int existingModules = Directory.GetDirectories(courseDir)
            .Select(Path.GetFileName)
            .Count(n => n is not null && n.Length >= 3 && char.IsDigit(n[0]) && char.IsDigit(n[1]) && n[2] == '_');

        int copied = 0;
        foreach (var src in sourcePdfPaths)
        {
            if (string.IsNullOrWhiteSpace(src) || !File.Exists(src))
                continue;

            existingModules++;
            var stem = Path.GetFileNameWithoutExtension(src);
            var folderName = $"{existingModules:00}_{ScholarFlow.Elms.Parsing.FilenameSanitizer.SanitizeFileName(stem, "").TrimEnd('.')}";
            var destDir = Path.Combine(courseDir, folderName);
            Directory.CreateDirectory(destDir);
            var destPath = Path.Combine(destDir, Path.GetFileName(src));
            File.Copy(src, destPath, overwrite: true);
            copied++;
        }

        if (copied > 0)
        {
            await ReloadBasinDataAsync();
            StatusText = $"Imported {copied} PDF(s) into {course.Title}";
            NotifyStateChanged();
        }
    }

    public async Task ToggleElmsIntegrationAsync()
    {
        Settings.EnableElmsIntegration = !Settings.EnableElmsIntegration;
        await AppSettingsStore.SaveAsync(Settings);
        if (Settings.EnableElmsIntegration && !string.IsNullOrWhiteSpace(BasinRoot))
            _pollingService.Start(BasinRoot, BasinConfig);
        else
            _pollingService.Stop();
        NotifyStateChanged();
    }

    public void ToggleSettingsModal()
    {
        IsSettingsModalOpen = !IsSettingsModalOpen;
        IsSearchModalOpen = false;
        IsShortcutsModalOpen = false;
        NotifyStateChanged(UiChangeKind.OverlayOnly);
    }

    public async Task ToggleMinimizeToTrayAsync()
    {
        Settings.MinimizeToTrayOnClose = !Settings.MinimizeToTrayOnClose;
        await AppSettingsStore.SaveAsync(Settings);
        NotifyStateChanged(UiChangeKind.OverlayOnly);
    }

    public async Task ToggleDesktopNotificationsAsync()
    {
        Settings.EnableDesktopNotifications = !Settings.EnableDesktopNotifications;
        await AppSettingsStore.SaveAsync(Settings);
        if (Settings.EnableDesktopNotifications)
            DesktopNotificationService.TryShow("ScholarFlow", "Desktop notifications enabled.");
        NotifyStateChanged(UiChangeKind.OverlayOnly);
    }

    public async Task ToggleLaunchOnStartupAsync()
    {
        Settings.LaunchOnStartup = !Settings.LaunchOnStartup;
        Services.StartupRegistrationService.SetEnabled(Settings.LaunchOnStartup);
        await AppSettingsStore.SaveAsync(Settings);
        NotifyStateChanged(UiChangeKind.OverlayOnly);
    }

}
