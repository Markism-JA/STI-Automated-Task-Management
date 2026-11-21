using ScholarFlow.Core.Models;
using ScholarFlow.Core.Services;
using ScholarFlow.Desktop.Services;

namespace ScholarFlow.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    public void OpenOnboardingWizard()
    {
        OnboardingVaultPath = !string.IsNullOrWhiteSpace(BasinRoot)
            ? BasinRoot
            : OnboardingVaultPath;
        OnboardingProviderId = !string.IsNullOrWhiteSpace(BasinConfig.ProviderId)
            ? BasinConfig.ProviderId
            : SyncProviderIds.StiElms;
        OnboardingAccountKey = string.IsNullOrWhiteSpace(BasinConfig.AccountKey)
            ? "default"
            : BasinConfig.AccountKey;
        OnboardingProviderSettings.Clear();
        foreach (var kv in BasinConfig.ProviderSettings)
            OnboardingProviderSettings[kv.Key] = kv.Value;

        IsSettingsModalOpen = false;
        IsOnboardingOpen = true;
        NotifyStateChanged(UiChangeKind.OverlayOnly);
    }

    public void SelectOnboardingProvider(string providerId)
    {
        OnboardingProviderId = providerId;
        NotifyStateChanged(UiChangeKind.OverlayOnly);
    }

    public async Task CompleteOnboardingAsync(
        string vaultPath,
        bool runElmsAuth,
        bool runInitialSync
    )
    {
        var resolvedPath = string.IsNullOrWhiteSpace(vaultPath)
            ? OnboardingVaultPath
            : vaultPath.Trim();
        IsOnboardingOpen = false;
        Settings.HasCompletedOnboarding = true;

        var targetProviderId =
            runElmsAuth || runInitialSync ? OnboardingProviderId : SyncProviderIds.None;
        var provider = SyncCoordinator.ResolveProvider(
            new BasinConfig { ProviderId = targetProviderId }
        );

        Settings.EnableElmsIntegration = provider.Capabilities.SupportsSync;
        await AppSettingsStore.SaveAsync(Settings);

        await OpenBasinAsync(resolvedPath);

        BasinConfig.SchemaVersion = Math.Max(BasinConfig.SchemaVersion, 2);
        BasinConfig.ProviderId = provider.ProviderId;
        BasinConfig.AccountKey = string.IsNullOrWhiteSpace(OnboardingAccountKey)
            ? "default"
            : OnboardingAccountKey.Trim();
        BasinConfig.BackgroundSyncEnabled = provider.Capabilities.SupportsSync;
        BasinConfig.Terms = [.. provider.Capabilities.DefaultTerms];
        foreach (var kv in OnboardingProviderSettings)
        {
            if (!string.IsNullOrWhiteSpace(kv.Value))
                BasinConfig.ProviderSettings[kv.Key] = kv.Value.Trim();
        }
        if (BasinConfig.ProviderSettings.TryGetValue("baseUrl", out var baseUrl))
            BasinConfig.ElmsBaseUrl = baseUrl;

        await _basinService.SaveBasinConfigAsync(BasinRoot, BasinConfig);
        await SyncCoordinator.RegisterOrUpdateBasinAsync(
            BasinRoot,
            BasinConfig,
            startLoop: BasinConfig.BackgroundSyncEnabled
        );
        NotifyStateChanged(UiChangeKind.FullLayout);

        if (provider.Capabilities.SupportsSync && (runElmsAuth || runInitialSync))
            await RunFullSyncAsync();
    }

    public async Task SetBasinProviderAsync(string providerId)
    {
        if (string.IsNullOrWhiteSpace(BasinRoot))
            return;

        var nextProvider = SyncCoordinator.ResolveProvider(
            new BasinConfig { ProviderId = providerId }
        );
        BasinConfig.SchemaVersion = Math.Max(BasinConfig.SchemaVersion, 2);
        BasinConfig.ProviderId = nextProvider.ProviderId;
        BasinConfig.BackgroundSyncEnabled = nextProvider.Capabilities.SupportsSync;
        if (
            !nextProvider.Capabilities.SupportsSync
            || BasinConfig.Terms.Count == 0
        )
        {
            BasinConfig.Terms = [.. nextProvider.Capabilities.DefaultTerms];
        }

        Settings.EnableElmsIntegration = nextProvider.Capabilities.SupportsSync;
        await AppSettingsStore.SaveAsync(Settings);
        await _basinService.SaveBasinConfigAsync(BasinRoot, BasinConfig);
        await SyncCoordinator.RegisterOrUpdateBasinAsync(
            BasinRoot,
            BasinConfig,
            startLoop: BasinConfig.BackgroundSyncEnabled
        );

        StatusText = $"Basin sync provider set to {nextProvider.DisplayName}";
        NotifyStateChanged(UiChangeKind.FullLayout);
    }

    public async Task SetBasinAccountKeyAsync(string accountKey)
    {
        if (string.IsNullOrWhiteSpace(BasinRoot))
            return;

        var normalized = string.IsNullOrWhiteSpace(accountKey) ? "default" : accountKey.Trim();
        if (string.Equals(BasinConfig.AccountKey, normalized, StringComparison.Ordinal))
            return;

        BasinConfig.AccountKey = normalized;
        await _basinService.SaveBasinConfigAsync(BasinRoot, BasinConfig);
        await SyncCoordinator.RegisterOrUpdateBasinAsync(
            BasinRoot,
            BasinConfig,
            startLoop: BasinConfig.BackgroundSyncEnabled
        );
        StatusText = $"Account profile key updated to '{normalized}'";
        NotifyStateChanged(UiChangeKind.StatusOnly);
    }

    public async Task SetBasinProviderSettingAsync(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(BasinRoot) || string.IsNullOrWhiteSpace(key))
            return;

        var trimmed = value?.Trim() ?? string.Empty;
        BasinConfig.ProviderSettings[key] = trimmed;
        if (
            string.Equals(key, "baseUrl", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(trimmed)
        )
        {
            BasinConfig.ElmsBaseUrl = trimmed;
        }

        await _basinService.SaveBasinConfigAsync(BasinRoot, BasinConfig);
        await SyncCoordinator.RegisterOrUpdateBasinAsync(
            BasinRoot,
            BasinConfig,
            startLoop: BasinConfig.BackgroundSyncEnabled
        );
    }

    public async Task ToggleBasinBackgroundSyncAsync()
    {
        if (string.IsNullOrWhiteSpace(BasinRoot))
            return;

        BasinConfig.BackgroundSyncEnabled = !BasinConfig.BackgroundSyncEnabled;
        await _basinService.SaveBasinConfigAsync(BasinRoot, BasinConfig);
        await SyncCoordinator.RegisterOrUpdateBasinAsync(
            BasinRoot,
            BasinConfig,
            startLoop: BasinConfig.BackgroundSyncEnabled
        );
        StatusText = BasinConfig.BackgroundSyncEnabled
            ? $"Background {ActiveProviderName} polling enabled for {BasinConfig.Name}"
            : $"Background {ActiveProviderName} polling disabled for {BasinConfig.Name}";
        NotifyStateChanged(UiChangeKind.FullLayout);
    }

    public async Task AuthenticateActiveProviderAsync()
    {
        if (string.IsNullOrWhiteSpace(BasinRoot) || !SupportsActiveBasinSync)
            return;

        StatusText = $"Authenticating with {ActiveProviderName}...";
        NotifyStateChanged(UiChangeKind.StatusOnly);
        try
        {
            var res = await SyncCoordinator.AuthenticateBasinAsync(BasinRoot, allowInteractive: true);
            StatusText = res.Message;
        }
        catch (Exception ex)
        {
            StatusText = $"Authentication failed: {ex.Message}";
        }
        NotifyStateChanged(UiChangeKind.FullLayout);
    }

    public async Task DisconnectActiveProviderAsync()
    {
        if (string.IsNullOrWhiteSpace(BasinRoot) || !SupportsActiveBasinSync)
            return;

        await SyncCoordinator.DisconnectBasinAsync(BasinRoot);
        StatusText = $"Signed out of {ActiveProviderName} ({BasinConfig.AccountKey})";
        NotifyStateChanged(UiChangeKind.FullLayout);
    }

    public bool IsCourseSyncExcluded(BasinCourseEntry course) =>
        !string.IsNullOrWhiteSpace(course.CourseId)
        && BasinConfig.ExcludedCourseIds.Contains(
            course.CourseId,
            StringComparer.OrdinalIgnoreCase
        );

    public async Task ToggleCourseSyncExclusionAsync(BasinCourseEntry course)
    {
        if (string.IsNullOrWhiteSpace(BasinRoot) || string.IsNullOrWhiteSpace(course.CourseId))
            return;

        var existingIdx = BasinConfig.ExcludedCourseIds.FindIndex(id =>
            string.Equals(id, course.CourseId, StringComparison.OrdinalIgnoreCase)
        );
        bool excluded;
        if (existingIdx >= 0)
        {
            BasinConfig.ExcludedCourseIds.RemoveAt(existingIdx);
            excluded = false;
        }
        else
        {
            BasinConfig.ExcludedCourseIds.Add(course.CourseId);
            excluded = true;
        }

        await _basinService.SaveBasinConfigAsync(BasinRoot, BasinConfig);
        await SyncCoordinator.RegisterOrUpdateBasinAsync(
            BasinRoot,
            BasinConfig,
            startLoop: BasinConfig.BackgroundSyncEnabled
        );
        StatusText = excluded
            ? $"Excluded '{course.Title}' from {ActiveProviderName} sync"
            : $"Included '{course.Title}' in {ActiveProviderName} sync";
        NotifyStateChanged(UiChangeKind.FullLayout);
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

        var created = await _basinService.CreateLocalCourseAsync(
            BasinRoot,
            title,
            alias,
            courseCode
        );
        IsAddCourseModalOpen = false;
        await ReloadBasinDataAsync();
        SelectCourse(created, showMaterials: true);
        StatusText = $"Created local course '{created.Title}' (#{created.Alias})";
        NotifyStateChanged();
    }

    public async Task ImportPdfsToCourseAsync(
        BasinCourseEntry course,
        IReadOnlyList<string> sourcePdfPaths
    )
    {
        if (string.IsNullOrWhiteSpace(BasinRoot) || sourcePdfPaths.Count == 0)
            return;

        var courseDir = Path.Combine(BasinRoot, course.FolderName);
        Directory.CreateDirectory(courseDir);

        int existingModules = Directory
            .GetDirectories(courseDir)
            .Select(Path.GetFileName)
            .Count(n =>
                n is not null
                && n.Length >= 3
                && char.IsDigit(n[0])
                && char.IsDigit(n[1])
                && n[2] == '_'
            );

        int copied = 0;
        foreach (var src in sourcePdfPaths)
        {
            if (string.IsNullOrWhiteSpace(src) || !File.Exists(src))
                continue;

            existingModules++;
            var stem = Path.GetFileNameWithoutExtension(src);
            var folderName =
                $"{existingModules:00}_{FilenameSanitizer.SanitizeFileName(stem, "").TrimEnd('.')}";
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
        var nextProvider = SupportsActiveBasinSync
            ? SyncProviderIds.None
            : SyncProviderIds.StiElms;
        await SetBasinProviderAsync(nextProvider);
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
        StartupRegistrationService.SetEnabled(Settings.LaunchOnStartup);
        await AppSettingsStore.SaveAsync(Settings);
        NotifyStateChanged(UiChangeKind.OverlayOnly);
    }
}
