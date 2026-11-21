using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Material.Icons;
using ScholarFlow.Core.Models;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views.Shell;

/// <summary>
/// Pure-C# builders for the First-Run Onboarding Wizard and Desktop Settings Modal
/// (Pluggable Basin Sync Providers, Per-Basin Account/Config Fields, Tray, OS Notifications, and Startup).
/// </summary>
public static class OnboardingAndSettingsModalBuilder
{
    public static Control BuildOnboardingModal(Window owner, MainWindowViewModel vm)
    {
        var stack = new StackPanel { Spacing = 14 };

        var header = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        header.Children.Add(
            new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Welcome to ScholarFlow",
                        FontSize = 18,
                        FontWeight = FontWeight.Bold,
                        Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                    },
                    new TextBlock
                    {
                        Text =
                            "Set up a local Study Basin and choose its sync provider (Local Knowledge Hub or an LMS integration).",
                        FontSize = 12.5,
                        Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            }
        );

        if (!string.IsNullOrWhiteSpace(vm.BasinRoot))
        {
            var closeBtn = UiBuilder.IconButton(
                MaterialIconKind.Close,
                () => vm.CloseTopOverlayOrDrawer(),
                16
            );
            Grid.SetColumn(closeBtn, 1);
            header.Children.Add(closeBtn);
        }
        stack.Children.Add(header);

        // Step 1: Study Basin Folder
        var vaultCard = new StackPanel { Spacing = 8 };
        vaultCard.Children.Add(
            UiBuilder.SectionHeader("1. Basin Folder Location (courses, PDFs, tasks & quizzes)")
        );

        var pathGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ColumnSpacing = 8,
        };
        var pathBox = new TextBox
        {
            Text = vm.OnboardingVaultPath,
            Watermark = "/path/to/Study-Basin",
            FontSize = 12.5,
        };
        pathBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                vm.OnboardingVaultPath = pathBox.Text ?? string.Empty;
        };
        pathGrid.Children.Add(pathBox);

        var browseBtn = UiBuilder.GhostButton(
            "Browse...",
            MaterialIconKind.FolderOpenOutline,
            async () =>
            {
                var picked = await owner.StorageProvider.OpenFolderPickerAsync(
                    new FolderPickerOpenOptions
                    {
                        Title = "Select or Create ScholarFlow Study Basin Folder",
                        AllowMultiple = false,
                    }
                );
                if (picked.Count > 0)
                {
                    var local = picked[0].TryGetLocalPath();
                    if (!string.IsNullOrWhiteSpace(local))
                    {
                        vm.OnboardingVaultPath = local;
                        pathBox.Text = local;
                    }
                }
            }
        );
        Grid.SetColumn(browseBtn, 1);
        pathGrid.Children.Add(browseBtn);
        vaultCard.Children.Add(pathGrid);
        stack.Children.Add(UiBuilder.Card(vaultCard, padding: new Thickness(12, 10)));

        // Step 2: Pluggable Sync Provider Selector & Dynamic Config Fields
        var providerCard = new StackPanel { Spacing = 8 };
        providerCard.Children.Add(UiBuilder.SectionHeader("2. Basin Sync Provider"));

        var providerPills = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var provider in vm.AvailableSyncProviders)
        {
            var captured = provider;
            bool isSelected = string.Equals(
                vm.OnboardingProviderId,
                captured.ProviderId,
                StringComparison.OrdinalIgnoreCase
            );
            var pill = UiBuilder.GhostButton(
                captured.DisplayName,
                captured.Capabilities.SupportsSync
                    ? MaterialIconKind.CloudSyncOutline
                    : MaterialIconKind.BookOpenPageVariantOutline,
                () => vm.SelectOnboardingProvider(captured.ProviderId),
                isActive: isSelected
            );
            pill.Margin = new Thickness(0, 0, 6, 6);
            providerPills.Children.Add(pill);
        }
        providerCard.Children.Add(providerPills);

        var selectedProvider = vm.SyncCoordinator.ResolveProvider(
            new BasinConfig { ProviderId = vm.OnboardingProviderId }
        );

        if (selectedProvider.Capabilities.SupportsSync)
        {
            var acctBox = new TextBox
            {
                Text = vm.OnboardingAccountKey,
                Watermark = "Account Profile Key (e.g. default, school-main)",
                FontSize = 12,
            };
            acctBox.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                    vm.OnboardingAccountKey = acctBox.Text ?? "default";
            };
            providerCard.Children.Add(acctBox);

            foreach (var field in selectedProvider.Capabilities.ConfigFields ?? [])
            {
                var capturedField = field;
                var initialVal = vm.OnboardingProviderSettings.TryGetValue(
                    capturedField.Key,
                    out var existing
                )
                    ? existing
                    : capturedField.DefaultValue;
                var fieldBox = new TextBox
                {
                    Text = initialVal,
                    Watermark = capturedField.Placeholder ?? capturedField.Label,
                    PasswordChar = capturedField.IsSecret ? '•' : '\0',
                    FontSize = 12,
                };
                fieldBox.PropertyChanged += (_, e) =>
                {
                    if (e.Property == TextBox.TextProperty)
                        vm.OnboardingProviderSettings[capturedField.Key] =
                            fieldBox.Text ?? string.Empty;
                };
                providerCard.Children.Add(fieldBox);
            }
        }
        else
        {
            providerCard.Children.Add(
                new TextBlock
                {
                    Text =
                        "Local Knowledge Hub basins store subjects, books, PDFs, notes, and recall decks purely offline with zero background LMS polling.",
                    FontSize = 12,
                    Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                    TextWrapping = TextWrapping.Wrap,
                }
            );
        }

        stack.Children.Add(UiBuilder.Card(providerCard, padding: new Thickness(12, 10)));

        // Action Buttons
        var actions = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"),
            ColumnSpacing = 8,
        };
        var offlineBtn = UiBuilder.GhostButton(
            "Create as Local Basin",
            MaterialIconKind.FolderCheckOutline,
            async () =>
                await vm.CompleteOnboardingAsync(
                    vm.OnboardingVaultPath,
                    runElmsAuth: false,
                    runInitialSync: false
                )
        );
        Grid.SetColumn(offlineBtn, 1);
        actions.Children.Add(offlineBtn);

        if (selectedProvider.Capabilities.SupportsSync)
        {
            var connectBtn = UiBuilder.PrimaryButton(
                $"Connect {selectedProvider.DisplayName} & Sync",
                MaterialIconKind.CloudSyncOutline,
                async () =>
                    await vm.CompleteOnboardingAsync(
                        vm.OnboardingVaultPath,
                        runElmsAuth: true,
                        runInitialSync: true
                    )
            );
            Grid.SetColumn(connectBtn, 2);
            actions.Children.Add(connectBtn);
        }

        stack.Children.Add(actions);

        return new Border
        {
            Width = 580,
            Background = ThemeService.Brush(ThemeService.BgElevated),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20, 18),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = stack,
        };
    }

    public static Control BuildSettingsModal(MainWindowViewModel vm)
    {
        var stack = new StackPanel { Spacing = 10 };

        var header = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        header.Children.Add(
            new TextBlock
            {
                Text = "Basin Sync Provider & Desktop Settings",
                FontSize = 16,
                FontWeight = FontWeight.Bold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        var closeBtn = UiBuilder.IconButton(MaterialIconKind.Close, vm.ToggleSettingsModal, 16);
        Grid.SetColumn(closeBtn, 1);
        header.Children.Add(closeBtn);
        stack.Children.Add(header);
        stack.Children.Add(UiBuilder.HairlineDivider());

        // 1. Active Basin & Pluggable Sync Provider Configuration Card
        var basinCard = new StackPanel { Spacing = 8 };
        basinCard.Children.Add(
            UiBuilder.SectionHeader($"Active Basin: {vm.BasinConfig.Name}")
        );

        var providerRow = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var provider in vm.AvailableSyncProviders)
        {
            var captured = provider;
            bool isCurrent = string.Equals(
                vm.ActiveSyncProvider.ProviderId,
                captured.ProviderId,
                StringComparison.OrdinalIgnoreCase
            );
            var btn = UiBuilder.GhostButton(
                captured.DisplayName,
                captured.Capabilities.SupportsSync
                    ? MaterialIconKind.CloudSyncOutline
                    : MaterialIconKind.BookOpenPageVariantOutline,
                async () => await vm.SetBasinProviderAsync(captured.ProviderId),
                isActive: isCurrent
            );
            btn.Margin = new Thickness(0, 0, 6, 4);
            providerRow.Children.Add(btn);
        }
        basinCard.Children.Add(providerRow);

        if (vm.SupportsActiveBasinSync)
        {
            // Dynamic Config Fields + Account Key
            var fieldsGrid = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("160,*"),
                ColumnSpacing = 8,
            };

            var acctBox = new TextBox
            {
                Text = vm.BasinConfig.AccountKey,
                Watermark = "Account Key (default)",
                FontSize = 12,
            };
            acctBox.LostFocus += async (_, _) =>
                await vm.SetBasinAccountKeyAsync(acctBox.Text ?? "default");
            fieldsGrid.Children.Add(acctBox);

            var configFields = vm.ActiveCapabilities.ConfigFields ?? [];
            if (configFields.Count > 0)
            {
                var firstField = configFields[0];
                var val = vm.BasinConfig.GetProviderSetting(
                    firstField.Key,
                    firstField.DefaultValue
                );
                var fieldBox = new TextBox
                {
                    Text = val,
                    Watermark = firstField.Placeholder ?? firstField.Label,
                    PasswordChar = firstField.IsSecret ? '•' : '\0',
                    FontSize = 12,
                };
                fieldBox.LostFocus += async (_, _) =>
                    await vm.SetBasinProviderSettingAsync(
                        firstField.Key,
                        fieldBox.Text ?? firstField.DefaultValue
                    );
                Grid.SetColumn(fieldBox, 1);
                fieldsGrid.Children.Add(fieldBox);
            }
            basinCard.Children.Add(fieldsGrid);

            foreach (var extraField in configFields.Skip(1))
            {
                var capturedField = extraField;
                var val = vm.BasinConfig.GetProviderSetting(
                    capturedField.Key,
                    capturedField.DefaultValue
                );
                var extraBox = new TextBox
                {
                    Text = val,
                    Watermark = capturedField.Placeholder ?? capturedField.Label,
                    PasswordChar = capturedField.IsSecret ? '•' : '\0',
                    FontSize = 12,
                };
                extraBox.LostFocus += async (_, _) =>
                    await vm.SetBasinProviderSettingAsync(
                        capturedField.Key,
                        extraBox.Text ?? capturedField.DefaultValue
                    );
                basinCard.Children.Add(extraBox);
            }

            // Per-Basin Background Polling & Auth Action Strip
            var providerActions = new WrapPanel { Orientation = Orientation.Horizontal };
            var bgSyncBtn = UiBuilder.GhostButton(
                vm.BasinConfig.BackgroundSyncEnabled
                    ? "Background Polling: ON"
                    : "Background Polling: OFF",
                vm.BasinConfig.BackgroundSyncEnabled
                    ? MaterialIconKind.CheckCircleOutline
                    : MaterialIconKind.PauseCircleOutline,
                async () => await vm.ToggleBasinBackgroundSyncAsync(),
                isActive: vm.BasinConfig.BackgroundSyncEnabled
            );
            bgSyncBtn.Margin = new Thickness(0, 2, 6, 2);
            providerActions.Children.Add(bgSyncBtn);

            var authBtn = UiBuilder.GhostButton(
                "Connect / Verify Session",
                MaterialIconKind.AccountCheckOutline,
                async () => await vm.AuthenticateActiveProviderAsync()
            );
            authBtn.Margin = new Thickness(0, 2, 6, 2);
            providerActions.Children.Add(authBtn);

            var signOutBtn = UiBuilder.GhostButton(
                "Sign Out",
                MaterialIconKind.LogoutVariant,
                async () => await vm.DisconnectActiveProviderAsync()
            );
            signOutBtn.Margin = new Thickness(0, 2, 0, 2);
            providerActions.Children.Add(signOutBtn);

            basinCard.Children.Add(providerActions);

            // Excluded Courses Quick-Restore Chips
            var excludedCourses = vm
                .Courses.Where(vm.IsCourseSyncExcluded)
                .OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (excludedCourses.Count > 0)
            {
                var excludedWrap = new WrapPanel { Orientation = Orientation.Horizontal };
                foreach (var course in excludedCourses)
                {
                    var capturedCourse = course;
                    var chip = UiBuilder.GhostButton(
                        $"Excluded: {capturedCourse.Title} (Click to Include)",
                        MaterialIconKind.EyeOffOutline,
                        async () => await vm.ToggleCourseSyncExclusionAsync(capturedCourse)
                    );
                    chip.Margin = new Thickness(0, 2, 6, 2);
                    excludedWrap.Children.Add(chip);
                }
                basinCard.Children.Add(excludedWrap);
            }
        }

        stack.Children.Add(UiBuilder.Card(basinCard, padding: new Thickness(12, 10)));

        Control BuildToggleRow(string title, string subtitle, bool isEnabled, Func<Task> onToggle)
        {
            var row = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
                ColumnSpacing = 12,
            };
            row.Children.Add(
                new StackPanel
                {
                    Spacing = 2,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = title,
                            FontSize = 13,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                        },
                        new TextBlock
                        {
                            Text = subtitle,
                            FontSize = 11.5,
                            Foreground = ThemeService.Brush(ThemeService.TextMuted),
                            TextWrapping = TextWrapping.Wrap,
                        },
                    },
                }
            );

            var btn = UiBuilder.GhostButton(
                isEnabled ? "Enabled" : "Disabled",
                isEnabled ? MaterialIconKind.CheckCircleOutline : MaterialIconKind.CircleOutline,
                async () => await onToggle(),
                isActive: isEnabled
            );
            Grid.SetColumn(btn, 1);
            row.Children.Add(btn);
            return UiBuilder.Card(row, padding: new Thickness(12, 9));
        }

        stack.Children.Add(
            BuildToggleRow(
                "Minimize to System Tray on Close",
                "Keep ScholarFlow running in the background when closing the window so multi-basin polling and reminders stay active.",
                vm.Settings.MinimizeToTrayOnClose,
                vm.ToggleMinimizeToTrayAsync
            )
        );

        stack.Children.Add(
            BuildToggleRow(
                "OS Notification Tray Alerts",
                "Dispatch native Linux (notify-send) or Windows Toast notifications for new assignments and deadlines across all synced basins.",
                vm.Settings.EnableDesktopNotifications,
                vm.ToggleDesktopNotificationsAsync
            )
        );

        stack.Children.Add(
            BuildToggleRow(
                "Launch on System Startup (--minimized)",
                "Automatically start ScholarFlow minimized in the system tray when signing into Windows or Linux.",
                vm.Settings.LaunchOnStartup,
                vm.ToggleLaunchOnStartupAsync
            )
        );

        // Study Basin & Setup Wizard Footer
        var vaultRow = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ColumnSpacing = 10,
        };
        vaultRow.Children.Add(
            new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Active Basin Path",
                        FontSize = 12.5,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                    },
                    new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(vm.BasinRoot)
                            ? "Not configured"
                            : vm.BasinRoot,
                        FontSize = 11.5,
                        Foreground = ThemeService.Brush(ThemeService.TextMuted),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                },
            }
        );
        var wizardBtn = UiBuilder.GhostButton(
            "New Basin Wizard",
            MaterialIconKind.WizardHat,
            vm.OpenOnboardingWizard
        );
        Grid.SetColumn(wizardBtn, 1);
        vaultRow.Children.Add(wizardBtn);
        stack.Children.Add(UiBuilder.Card(vaultRow, padding: new Thickness(12, 9)));

        return new Border
        {
            Width = 560,
            Background = ThemeService.Brush(ThemeService.BgElevated),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20, 18),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = stack,
        };
    }

    public static Control BuildAddCourseModal(MainWindowViewModel vm)
    {
        var stack = new StackPanel { Spacing = 12 };

        var header = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        header.Children.Add(
            new TextBlock
            {
                Text = "Create Local Course / Subject",
                FontSize = 16,
                FontWeight = FontWeight.Bold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        var closeBtn = UiBuilder.IconButton(MaterialIconKind.Close, vm.ToggleAddCourseModal, 16);
        Grid.SetColumn(closeBtn, 1);
        header.Children.Add(closeBtn);
        stack.Children.Add(header);
        stack.Children.Add(UiBuilder.HairlineDivider());

        var titleBox = new TextBox
        {
            Watermark = "Course Title (e.g. Linear Algebra, Organic Chemistry, Ethics)",
            FontSize = 13,
        };
        stack.Children.Add(titleBox);

        var row2 = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,*"),
            ColumnSpacing = 8,
        };
        var aliasBox = new TextBox
        {
            Watermark = "Short #alias (optional, e.g. math)",
            FontSize = 12.5,
        };
        row2.Children.Add(aliasBox);

        var codeBox = new TextBox
        {
            Watermark = "Course Code (optional, e.g. MATH101)",
            FontSize = 12.5,
        };
        Grid.SetColumn(codeBox, 1);
        row2.Children.Add(codeBox);
        stack.Children.Add(row2);

        var actions = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"),
            ColumnSpacing = 8,
        };
        var cancelBtn = UiBuilder.GhostButton("Cancel", null, vm.ToggleAddCourseModal);
        Grid.SetColumn(cancelBtn, 1);
        actions.Children.Add(cancelBtn);

        var createBtn = UiBuilder.PrimaryButton(
            "Create Course",
            MaterialIconKind.Plus,
            async () =>
            {
                if (!string.IsNullOrWhiteSpace(titleBox.Text))
                    await vm.CreateLocalCourseAsync(titleBox.Text, aliasBox.Text, codeBox.Text);
            }
        );
        Grid.SetColumn(createBtn, 2);
        actions.Children.Add(createBtn);
        stack.Children.Add(actions);

        return new Border
        {
            Width = 480,
            Background = ThemeService.Brush(ThemeService.BgElevated),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20, 18),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = stack,
        };
    }
}
