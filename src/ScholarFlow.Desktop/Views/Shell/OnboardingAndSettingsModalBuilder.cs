using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Material.Icons;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views.Shell;

/// <summary>
/// Pure-C# builders for the First-Run Onboarding Wizard and Desktop Settings Modal
/// (Tray, OS Notifications, Startup Registration, and Study Vault Setup).
/// </summary>
public static class OnboardingAndSettingsModalBuilder
{
    public static Control BuildOnboardingModal(Window owner, MainWindowViewModel vm)
    {
        var stack = new StackPanel { Spacing = 14 };

        var header = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        header.Children.Add(new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock
                {
                    Text = "Welcome to ScholarFlow",
                    FontSize = 18,
                    FontWeight = FontWeight.Bold,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary)
                },
                new TextBlock
                {
                    Text = "Set up your local Study Vault and optionally link your STI eLMS account.",
                    FontSize = 12.5,
                    Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                    TextWrapping = TextWrapping.Wrap
                }
            }
        });

        if (!string.IsNullOrWhiteSpace(vm.BasinRoot))
        {
            var closeBtn = UiBuilder.IconButton(
                MaterialIconKind.Close,
                () => vm.CloseTopOverlayOrDrawer(),
                16);
            Grid.SetColumn(closeBtn, 1);
            header.Children.Add(closeBtn);
        }
        stack.Children.Add(header);

        // Step 1: Study Vault Folder
        var vaultCard = new StackPanel { Spacing = 8 };
        vaultCard.Children.Add(UiBuilder.SectionHeader("1. Study Vault Location (courses, PDFs, tasks & quizzes)"));

        var pathGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 8 };
        var pathBox = new TextBox
        {
            Text = vm.OnboardingVaultPath,
            Watermark = "/path/to/Study-Vault",
            FontSize = 12.5
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
                var picked = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Select or Create ScholarFlow Study Vault Folder",
                    AllowMultiple = false
                });
                if (picked.Count > 0)
                {
                    var local = picked[0].TryGetLocalPath();
                    if (!string.IsNullOrWhiteSpace(local))
                    {
                        vm.OnboardingVaultPath = local;
                        pathBox.Text = local;
                    }
                }
            });
        Grid.SetColumn(browseBtn, 1);
        pathGrid.Children.Add(browseBtn);
        vaultCard.Children.Add(pathGrid);
        stack.Children.Add(UiBuilder.Card(vaultCard, padding: new Thickness(12, 10)));

        // Step 2: STI eLMS Account
        var elmsCard = new StackPanel { Spacing = 6 };
        elmsCard.Children.Add(UiBuilder.SectionHeader("2. STI eLMS Connection (Optional)"));
        elmsCard.Children.Add(new TextBlock
        {
            Text = "Link your STI eLMS account to automatically sync courses, deadlines, handouts, and syllabus term schedules — or start offline and connect later.",
            FontSize = 12,
            Foreground = ThemeService.Brush(ThemeService.TextSecondary),
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(UiBuilder.Card(elmsCard, padding: new Thickness(12, 10)));

        // Action Buttons
        var actions = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"), ColumnSpacing = 8 };
        var offlineBtn = UiBuilder.GhostButton(
            "Start Offline (Vault Only)",
            MaterialIconKind.FolderCheckOutline,
            async () => await vm.CompleteOnboardingAsync(vm.OnboardingVaultPath, runElmsAuth: false, runInitialSync: false));
        Grid.SetColumn(offlineBtn, 1);
        actions.Children.Add(offlineBtn);

        var connectBtn = UiBuilder.PrimaryButton(
            "Connect STI eLMS & Sync",
            MaterialIconKind.CloudSyncOutline,
            async () => await vm.CompleteOnboardingAsync(vm.OnboardingVaultPath, runElmsAuth: true, runInitialSync: true));
        Grid.SetColumn(connectBtn, 2);
        actions.Children.Add(connectBtn);
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
            Child = stack
        };
    }

    public static Control BuildSettingsModal(MainWindowViewModel vm)
    {
        var stack = new StackPanel { Spacing = 12 };

        var header = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        header.Children.Add(new TextBlock
        {
            Text = "Desktop Settings & Integration",
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center
        });
        var closeBtn = UiBuilder.IconButton(
            MaterialIconKind.Close,
            () => vm.ToggleSettingsModal(),
            16);
        Grid.SetColumn(closeBtn, 1);
        header.Children.Add(closeBtn);
        stack.Children.Add(header);
        stack.Children.Add(UiBuilder.HairlineDivider());

        Control BuildToggleRow(string title, string subtitle, bool isEnabled, Func<Task> onToggle)
        {
            var row = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 12 };
            row.Children.Add(new StackPanel
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
                        Foreground = ThemeService.Brush(ThemeService.TextPrimary)
                    },
                    new TextBlock
                    {
                        Text = subtitle,
                        FontSize = 11.5,
                        Foreground = ThemeService.Brush(ThemeService.TextMuted),
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            });

            var btn = UiBuilder.GhostButton(
                isEnabled ? "Enabled" : "Disabled",
                isEnabled ? MaterialIconKind.CheckCircleOutline : MaterialIconKind.CircleOutline,
                async () => await onToggle(),
                isActive: isEnabled);
            Grid.SetColumn(btn, 1);
            row.Children.Add(btn);
            return UiBuilder.Card(row, padding: new Thickness(12, 9));
        }

        stack.Children.Add(BuildToggleRow(
            "STI eLMS Integration & Background Sync",
            "Enable automatic STI eLMS assignment polling and handout syncing. Turn off for a pure offline/local study vault.",
            vm.Settings.EnableElmsIntegration,
            () => vm.ToggleElmsIntegrationAsync()));

        stack.Children.Add(BuildToggleRow(
            "Minimize to System Tray on Close",
            "Keep ScholarFlow running in the background when closing the window so eLMS polling and reminders stay active.",
            vm.Settings.MinimizeToTrayOnClose,
            () => vm.ToggleMinimizeToTrayAsync()));

        stack.Children.Add(BuildToggleRow(
            "OS Notification Tray Alerts",
            "Dispatch native Linux (notify-send) or Windows Toast notifications for new assignments and deadlines.",
            vm.Settings.EnableDesktopNotifications,
            () => vm.ToggleDesktopNotificationsAsync()));

        stack.Children.Add(BuildToggleRow(
            "Launch on System Startup (--minimized)",
            "Automatically start ScholarFlow minimized in the system tray when signing into Windows or Linux.",
            vm.Settings.LaunchOnStartup,
            () => vm.ToggleLaunchOnStartupAsync()));

        // Study Vault & Setup Wizard Footer
        var vaultRow = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 10 };
        vaultRow.Children.Add(new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock
                {
                    Text = "Active Study Vault",
                    FontSize = 12.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary)
                },
                new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(vm.BasinRoot) ? "Not configured" : vm.BasinRoot,
                    FontSize = 11.5,
                    Foreground = ThemeService.Brush(ThemeService.TextMuted),
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            }
        });
        var wizardBtn = UiBuilder.GhostButton(
            "Setup Wizard",
            MaterialIconKind.WizardHat,
            () => vm.OpenOnboardingWizard());
        Grid.SetColumn(wizardBtn, 1);
        vaultRow.Children.Add(wizardBtn);
        stack.Children.Add(UiBuilder.Card(vaultRow, padding: new Thickness(12, 9)));

        return new Border
        {
            Width = 540,
            Background = ThemeService.Brush(ThemeService.BgElevated),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20, 18),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = stack
        };
    }

    public static Control BuildAddCourseModal(MainWindowViewModel vm)
    {
        var stack = new StackPanel { Spacing = 12 };

        var header = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        header.Children.Add(new TextBlock
        {
            Text = "Create Local Course / Subject",
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center
        });
        var closeBtn = UiBuilder.IconButton(MaterialIconKind.Close, () => vm.ToggleAddCourseModal(), 16);
        Grid.SetColumn(closeBtn, 1);
        header.Children.Add(closeBtn);
        stack.Children.Add(header);
        stack.Children.Add(UiBuilder.HairlineDivider());

        var titleBox = new TextBox
        {
            Watermark = "Course Title (e.g. Linear Algebra, Organic Chemistry, Ethics)",
            FontSize = 13
        };
        stack.Children.Add(titleBox);

        var row2 = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,*"), ColumnSpacing = 8 };
        var aliasBox = new TextBox
        {
            Watermark = "Short #alias (optional, e.g. math)",
            FontSize = 12.5
        };
        row2.Children.Add(aliasBox);

        var codeBox = new TextBox
        {
            Watermark = "Course Code (optional, e.g. MATH101)",
            FontSize = 12.5
        };
        Grid.SetColumn(codeBox, 1);
        row2.Children.Add(codeBox);
        stack.Children.Add(row2);

        var actions = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"), ColumnSpacing = 8 };
        var cancelBtn = UiBuilder.GhostButton("Cancel", null, () => vm.ToggleAddCourseModal());
        Grid.SetColumn(cancelBtn, 1);
        actions.Children.Add(cancelBtn);

        var createBtn = UiBuilder.PrimaryButton(
            "Create Course",
            MaterialIconKind.Plus,
            async () =>
            {
                if (!string.IsNullOrWhiteSpace(titleBox.Text))
                    await vm.CreateLocalCourseAsync(titleBox.Text, aliasBox.Text, codeBox.Text);
            });
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
            Child = stack
        };
    }
}
